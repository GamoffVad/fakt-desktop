# -*- coding: utf-8 -*-
"""Streaming readers: ``open_reader``, ``read_chunk``, ``close_reader``
(PROTOCOL.md §3.4-3.6).

Resume is a safe re-read: the file is parsed from the start by the same
parser and records with ``ordinal <= start_after_ordinal`` are skipped
without hashing or output. The file fingerprint (size + st_mtime_ns) is
checked on open against the client's expectation and again before every
chunk against the values seen on open."""
import logging
import os
from typing import Any, Dict, Iterator, List, Optional, Tuple

from . import encoding_detect, structure
from .parsers import NEWLINE, BaseParser, create_parser
from .parsers.xml_stream import declared_encoding
from .protocol import (FILE_CHANGED, INTERNAL_ERROR, READER_NOT_FOUND, WorkerError, arg_int,
                       arg_obj, arg_path, arg_str, format_traceback, map_os_error, open_binary,
                       stat_file)
from .records import ChunkBuilder, Prepared, prepare, replacement_count

log = logging.getLogger("fakt_worker.readers")

HEAD_BYTES = 65536


def resolve_encoding(eff, head, eof):
    # type: (Dict[str, Any], bytes, bool) -> Tuple[str, str, int]
    """(canonical, codec, BOM bytes to skip) for reading a file whose first
    bytes are ``head``. An explicit structure encoding wins; otherwise XML
    uses BOM > declaration > detection and other formats use detection
    (which starts with the BOM)."""
    encoding = eff.get("encoding")
    if not encoding:
        if eff["format"] == "xml":
            bom, _ = encoding_detect.detect_bom(head)
            declared = declared_encoding(head) if bom is None else None
            canon = encoding_detect.normalize_name(declared) if declared else None
            encoding = canon or encoding_detect.detect_encoding(head, eof).encoding
        else:
            encoding = encoding_detect.detect_encoding(head, eof).encoding
    return encoding_detect.resolve_for_file(encoding, head[:4])


def open_stream(raw, eff):
    # type: (Any, Dict[str, Any]) -> Tuple[Any, str]
    """Text stream over an open binary file; returns (stream, canonical)."""
    head = raw.read(HEAD_BYTES)
    eof = len(head) < HEAD_BYTES
    canonical, codec, skip = resolve_encoding(eff, head, eof)
    return encoding_detect.open_text(raw, codec, skip, NEWLINE[eff["format"]]), canonical


class Reader(object):

    def __init__(self, reader_id, raw, parser, eff, chunk_size, start_after, max_chunk_bytes):
        # type: (str, Any, BaseParser, Dict[str, Any], int, int, int) -> None
        self.id = reader_id
        self.raw = raw
        self.parser = parser
        self.fixed_columns = None if parser.dynamic_columns else list(eff["columns"])
        self.chunk_size = chunk_size
        self.start_after = start_after
        self.max_chunk_bytes = max_chunk_bytes
        st = os.fstat(raw.fileno())
        self.fingerprint = (st.st_size, st.st_mtime_ns)
        self._iter = parser.records()  # type: Optional[Iterator[Any]]
        self._pending = None  # type: Optional[Prepared]
        self.ordinal = 0
        self.eof = False
        self.error = None  # type: Optional[WorkerError]
        self.stats = {"records_emitted": 0, "records_skipped": 0, "bad_records": 0,
                      "blank_lines": 0, "replacement_count": 0}

    def _check_fingerprint(self):
        try:
            st = os.fstat(self.raw.fileno())
        except OSError as exc:
            raise map_os_error(exc)
        if (st.st_size, st.st_mtime_ns) != self.fingerprint:
            raise WorkerError(FILE_CHANGED, "Файл изменился во время чтения",
                              {"size": st.st_size, "mtime_ns": st.st_mtime_ns,
                               "expected_size": self.fingerprint[0],
                               "expected_mtime_ns": self.fingerprint[1]})

    def _fail(self, exc):
        # type: (BaseException) -> WorkerError
        if isinstance(exc, WorkerError):
            error = exc
        elif isinstance(exc, OSError):
            error = map_os_error(exc)
        else:
            log.error("reader %s failed: %s", self.id, format_traceback(exc))
            error = WorkerError(INTERNAL_ERROR, "Внутренняя ошибка при чтении файла",
                                {"exception_type": type(exc).__name__})
        self.error = error
        self._close_iter()
        return error

    def _next_item(self):
        # type: () -> Optional[Prepared]
        """Next record to output (skipping resumed ones); None at the end."""
        assert self._iter is not None
        while True:
            record = next(self._iter, None)
            if record is None:
                return None
            self.ordinal += 1
            if self.ordinal <= self.start_after:
                self.stats["records_skipped"] += 1
                continue
            return prepare(self.ordinal, record, self.fixed_columns, self.max_chunk_bytes)

    def read_chunk(self):
        # type: () -> Dict[str, Any]
        if self.error is not None:
            raise self.error
        builder = ChunkBuilder(self.fixed_columns, self.max_chunk_bytes)
        if not self.eof:
            try:
                self._check_fingerprint()
            except WorkerError as exc:
                raise self._fail(exc)
            while len(builder.items) < self.chunk_size:
                item = self._pending
                if item is None:
                    try:
                        item = self._next_item()
                    except Exception as exc:  # noqa: B902 - reported to the client
                        error = self._fail(exc)
                        if not builder.items:
                            raise error
                        break
                    if item is None:
                        self.eof = True
                        self._close_iter()
                        break
                if not builder.try_add(item):
                    self._pending = item
                    break
                self._pending = None
                self._count(item)
        self.stats["blank_lines"] = self.parser.blank_lines
        return {"columns": builder.columns(), "records": builder.output(), "eof": self.eof,
                "stats": dict(self.stats), "warnings": self.parser.drain_warnings()}

    def _count(self, item):
        # type: (Prepared) -> None
        stats = self.stats
        stats["records_emitted"] += 1
        record = item.record
        if record.error is not None:
            stats["bad_records"] += 1
        elif record.values:
            stats["replacement_count"] += replacement_count(record.values)

    def _close_iter(self):
        iterator, self._iter = self._iter, None
        if iterator is not None:
            try:
                iterator.close()
            except Exception:  # noqa: B902 - closing must not fail
                log.warning("reader %s: parser close failed", self.id)

    def close(self):
        self._close_iter()
        try:
            self.raw.close()
        except OSError:
            pass


_READERS = {}  # type: Dict[str, Reader]
_NEXT_ID = [0]


def open_reader(args):
    # type: (Dict[str, Any]) -> Dict[str, Any]
    path = arg_path(args)
    eff = structure.for_reader(arg_obj(args, "structure"))
    chunk_size = arg_int(args, "chunk_size", 5000, 1, 100000)
    start_after = arg_int(args, "start_after_ordinal", 0, 0)
    max_chunk_bytes = arg_int(args, "max_chunk_bytes", 16777216, 4096, 1 << 30)
    expected_size = arg_int(args, "expected_size", None, 0, allow_none=True)
    expected_mtime = arg_int(args, "expected_mtime_ns", None, allow_none=True)
    stat_file(path)
    raw = open_binary(path)
    try:
        st = os.fstat(raw.fileno())
        if (expected_size is not None and st.st_size != expected_size) or \
                (expected_mtime is not None and st.st_mtime_ns != expected_mtime):
            raise WorkerError(FILE_CHANGED, "Файл изменился: отпечаток не совпадает",
                              {"size": st.st_size, "mtime_ns": st.st_mtime_ns,
                               "expected_size": expected_size,
                               "expected_mtime_ns": expected_mtime})
        try:
            stream, canonical = open_stream(raw, eff)
        except OSError as exc:
            raise map_os_error(exc)
        eff = dict(eff, encoding=canonical)
        parser = create_parser(stream, eff, chunk_size=chunk_size)
        _NEXT_ID[0] += 1
        reader_id = "r%d" % _NEXT_ID[0]
        reader = Reader(reader_id, raw, parser, eff, chunk_size, start_after, max_chunk_bytes)
    except BaseException:
        raw.close()
        raise
    _READERS[reader_id] = reader
    return {"reader_id": reader_id}


def _get(args):
    # type: (Dict[str, Any]) -> Reader
    reader_id = arg_str(args, "reader_id")
    reader = _READERS.get(reader_id)  # type: ignore[arg-type]
    if reader is None:
        raise WorkerError(READER_NOT_FOUND, "Читатель не найден", {"reader_id": reader_id})
    return reader


def read_chunk(args):
    # type: (Dict[str, Any]) -> Dict[str, Any]
    return _get(args).read_chunk()


def close_reader(args):
    # type: (Dict[str, Any]) -> Dict[str, Any]
    reader = _get(args)
    del _READERS[reader.id]
    reader.close()
    return {}


def close_all():
    for reader_id in list(_READERS):
        reader = _READERS.pop(reader_id)
        reader.close()


def open_count():
    # type: () -> int
    return len(_READERS)
