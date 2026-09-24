# -*- coding: utf-8 -*-
"""Streaming XML through ``defusedxml.ElementTree.iterparse``.

The file is decoded by the worker (BOM > structure encoding > declaration,
invalid bytes -> U+FFFD) and fed to expat as UTF-8 with an explicit
encoding override, so any supported encoding works the same way. Entity
declarations and external references are forbidden (defusedxml); a DOCTYPE
without them is ignored with a ``dtd_ignored`` warning.

A record element is matched by ``xml_record_path`` (absolute: exact path
from the root; relative: suffix of the element path; a segment without a
prefix matches the local name in any namespace). Nested matches inside a
record belong to that record. After a record is flattened (§5.1) it is
cleared and removed from its parent; elements outside records are released
the same way, so memory does not grow with the file size."""
import re
from collections import Counter
from typing import Any, Dict, Iterator, List, Optional, Tuple

from ..protocol import DECODE_ERROR, WorkerError
from ..records import Record, error_record
from ..structure import XML_NS, parse_xml_path
from . import BaseParser

_DECLARATION = re.compile(br"^<\?xml[^>]*?encoding\s*=\s*[\"']([A-Za-z0-9._-]+)[\"']")
_FEED_CHARS = 8192


def declared_encoding(head):
    # type: (bytes) -> Optional[str]
    """Encoding name from the XML declaration of an ASCII-compatible file."""
    match = _DECLARATION.match(head.lstrip(b"\xef\xbb\xbf")[:512])
    return match.group(1).decode("ascii") if match else None


class _Utf8Feed(object):
    """Binary file-like object: the decoded text stream re-encoded as UTF-8."""

    def __init__(self, stream):
        self._stream = stream
        self.exhausted = False

    def read(self, size=-1):
        # type: (int) -> bytes
        text = self._stream.read(_FEED_CHARS)
        if not text:
            self.exhausted = True
            return b""
        return text.encode("utf-8")


def _split_tag(tag):
    # type: (str) -> Tuple[Optional[str], str]
    if tag[:1] == "{":
        uri, _, local = tag[1:].partition("}")
        return uri, local
    return None, tag


class XmlParser(BaseParser):
    dynamic_columns = True

    def __init__(self, stream, eff, chunk_size=5000, fragment=False, collect=False):
        # type: (Any, Dict[str, Any], int, bool, bool) -> None
        BaseParser.__init__(self, stream, eff, chunk_size, fragment, collect)
        namespaces = eff.get("xml_namespaces") or {}
        self.absolute, self.segments = parse_xml_path(eff["xml_record_path"], namespaces)
        self._prefix_by_uri = {XML_NS: "xml"}  # type: Dict[str, str]
        for prefix in sorted(namespaces):
            self._prefix_by_uri.setdefault(namespaces[prefix], prefix)
        self._names = {}  # type: Dict[str, str]
        self._split = {}  # type: Dict[str, Tuple[Optional[str], str]]
        self.dtd_seen = False
        self.truncated_tail = False
        self.path_counts = Counter()  # type: Counter

    # --- names ----------------------------------------------------------------

    def display_name(self, tag):
        # type: (str) -> str
        name = self._names.get(tag)
        if name is None:
            uri, local = _split_tag(tag)
            prefix = self._prefix_by_uri.get(uri) if uri is not None else None
            name = "%s:%s" % (prefix, local) if prefix else local
            self._names[tag] = name
        return name

    def _matches(self, stack):
        # type: (List[Any]) -> bool
        segments = self.segments
        count = len(segments)
        if len(stack) < count or (self.absolute and len(stack) != count):
            return False
        cache = self._split
        for (uri, local), elem in zip(segments, stack[len(stack) - count:]):
            tag = elem.tag
            parts = cache.get(tag)
            if parts is None:
                parts = cache[tag] = _split_tag(tag)
            if parts[1] != local or (uri is not None and parts[0] != uri):
                return False
        return True

    # --- flattening (§5.1) ----------------------------------------------------

    def _walk(self, elem, prefix, keys, values, is_record):
        # type: (Any, str, List[str], List[Optional[str]], bool) -> None
        name = self.display_name
        for attr, value in elem.attrib.items():
            keys.append(prefix + "@" + name(attr))
            values.append(value)
        children = list(elem)
        if not children:
            if is_record and elem.text is not None and elem.text.strip():
                keys.append("#text")
                values.append(elem.text)
            return
        parts = []
        if elem.text and elem.text.strip():
            parts.append(elem.text.strip())
        for child in children:
            if child.tail and child.tail.strip():
                parts.append(child.tail.strip())
        if parts:
            keys.append(prefix + "#text")
            values.append(" ".join(parts))
        seen = {}  # type: Dict[str, int]
        for child in children:
            child_name = name(child.tag)
            number = seen.get(child_name, 0) + 1
            seen[child_name] = number
            key = prefix + (child_name if number == 1 else "%s[%d]" % (child_name, number))
            if len(child):
                self._walk(child, key + "/", keys, values, False)
            else:
                keys.append(key)
                values.append(child.text or "")
                for attr, value in child.attrib.items():
                    keys.append(key + "/@" + name(attr))
                    values.append(value)

    def flatten(self, elem):
        # type: (Any) -> Record
        keys = []  # type: List[str]
        values = []  # type: List[Optional[str]]
        try:
            self._walk(elem, "", keys, values, True)
        except RecursionError:
            return error_record(None, None, "record_too_large",
                                "Слишком глубокая вложенность элементов записи")
        return Record(None, None, keys, values)

    # --- parsing --------------------------------------------------------------

    def _on_doctype(self, name, system_id, public_id, has_internal_subset):
        if not self.dtd_seen:
            self.dtd_seen = True
            self.warn("dtd_ignored", "Объявление DOCTYPE проигнорировано: DTD не применяется и "
                      "внешние ресурсы не загружаются")

    def _make_iterator(self, feed):
        from defusedxml import ElementTree as DET
        parser = DET.DefusedXMLParser(target=None, encoding="utf-8", forbid_dtd=False,
                                      forbid_entities=True, forbid_external=True)
        parser.parser.StartDoctypeDeclHandler = self._on_doctype
        return DET.iterparse(feed, events=("start", "end"), parser=parser)

    def _path(self, stack):
        # type: (List[Any]) -> str
        return "/" + "/".join(self.display_name(elem.tag) for elem in stack)

    def records(self):
        # type: () -> Iterator[Record]
        import defusedxml
        from xml.etree.ElementTree import ParseError
        feed = _Utf8Feed(self.stream)
        events = self._make_iterator(feed)
        stack = []  # type: List[Any]
        record_depth = 0
        collect = self.collect
        try:
            for event, elem in events:
                if event == "start":
                    stack.append(elem)
                    if not record_depth and self._matches(stack):
                        record_depth = len(stack)
                    if collect:
                        self.path_counts[self._path(stack)] += 1
                    continue
                depth = len(stack)
                if record_depth:
                    if depth == record_depth:
                        record_depth = 0
                        record = self.flatten(elem)
                        self._release(elem, stack)
                        stack.pop()
                        self.record_count += 1
                        yield record
                        continue
                else:
                    self._release(elem, stack)
                stack.pop()
        except defusedxml.EntitiesForbidden:
            raise WorkerError(DECODE_ERROR, "XML содержит объявления сущностей (<!ENTITY>): "
                              "они запрещены из соображений безопасности", {"reason": "entities"})
        except defusedxml.ExternalReferenceForbidden:
            raise WorkerError(DECODE_ERROR, "XML содержит ссылки на внешние ресурсы: они "
                              "запрещены", {"reason": "external_reference"})
        except defusedxml.DefusedXmlException as exc:
            raise WorkerError(DECODE_ERROR, "XML содержит запрещённые конструкции",
                              {"reason": type(exc).__name__})
        except ParseError as exc:
            if self.fragment and feed.exhausted:
                self.truncated_tail = True  # the validate fragment ends mid-document
                return
            line, column = getattr(exc, "position", (None, None))
            raise WorkerError(DECODE_ERROR, "Ошибка разбора XML в строке %s, позиции %s"
                              % (line, column), {"line": line, "column": column})

    @staticmethod
    def _release(elem, stack):
        # type: (Any, List[Any]) -> None
        elem.clear()
        if len(stack) >= 2:
            try:
                stack[-2].remove(elem)
            except ValueError:
                pass

    def suggest_record_path(self):
        # type: () -> Optional[str]
        best = None  # type: Optional[Tuple[int, int, str]]
        for path, count in self.path_counts.items():
            if count < 2:
                continue
            key = (count, -path.count("/"), path)
            if best is None or key[:2] > best[:2]:
                best = key
        return best[2] if best else None
