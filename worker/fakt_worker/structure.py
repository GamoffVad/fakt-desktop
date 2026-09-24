# -*- coding: utf-8 -*-
"""Normalization of the ``structure`` object (PROTOCOL.md §5) into
``effective_structure``: defaults, canonical encoding, unique column names.

``normalize`` never raises for bad parameters: it returns issues with the
validate codes of §3.3. ``for_reader`` converts issues into protocol errors
for ``open_reader``."""
import re
from typing import Any, Dict, List, Optional, Tuple

from . import encoding_detect
from .protocol import (INVALID_STRUCTURE, UNSUPPORTED_ENCODING, UNSUPPORTED_FORMAT,
                       WorkerError)

FORMATS = ("delimited", "fixed_width", "xml", "jsonl", "json_array")
TEXT_FORMATS = ("delimited", "fixed_width")
DATA_FORMATS = ("xml", "jsonl", "json_array")  # columns come from the data

MAX_COLUMNS = 100000
MAX_SKIP_ROWS = 100000000
MAX_TOTAL_WIDTH = 1000000
XML_NS = "http://www.w3.org/XML/1998/namespace"

_XML_NAME = re.compile(r"^[^\s/:<>&'\"=\[\]*@]+$")
_KEYS = ("classification", "format", "encoding", "has_header", "header_row", "skip_rows",
         "delimiter", "quote_char", "escape_char", "columns", "fixed_widths",
         "xml_record_path", "xml_namespaces", "json_record_path", "confidence", "reason")


def issue(code, message, ordinal=None):
    # type: (str, str, Optional[int]) -> Dict[str, Any]
    return {"code": code, "message": message, "ordinal": ordinal}


def auto_column(index):
    # type: (int) -> str
    return "Колонка %d" % (index + 1)


def dedupe_columns(names):
    # type: (List[Any]) -> Tuple[List[str], bool]
    """Unique, stripped column names: "X", "X (2)", "X (3)"; empty or null
    names become "Колонка N". Uniqueness is case-insensitive."""
    used = set()
    result = []
    renamed = False
    for index, name in enumerate(names):
        base = name.strip() if isinstance(name, str) else ""
        if not base:
            base = auto_column(index)
        candidate = base
        number = 1
        while candidate.casefold() in used:
            number += 1
            candidate = "%s (%d)" % (base, number)
        if candidate != base:
            renamed = True
        used.add(candidate.casefold())
        result.append(candidate)
    return result, renamed


def normalize_header_name(name):
    # type: (Any) -> str
    return name.strip().casefold() if isinstance(name, str) else ""


def parse_xml_path(path, namespaces):
    # type: (str, Dict[str, str]) -> Tuple[bool, List[Tuple[Optional[str], str]]]
    """(absolute, [(namespace uri or None, local name)]). A segment without a
    prefix matches by local name in any namespace. Raises ValueError."""
    text = path.strip()
    absolute = text.startswith("/")
    body = text[1:] if absolute else text
    if body.endswith("/"):
        body = body[:-1]
    if not body:
        raise ValueError("пустой путь")
    segments = []
    for part in body.split("/"):
        part = part.strip()
        if not part:
            raise ValueError("пустой сегмент пути")
        if ":" in part:
            prefix, local = part.split(":", 1)
            if prefix == "xml":
                uri = XML_NS
            elif prefix in namespaces:
                uri = namespaces[prefix]
            else:
                raise ValueError("префикс «%s» не объявлен в xml_namespaces" % prefix)
        else:
            uri, local = None, part
        if not _XML_NAME.match(local):
            raise ValueError("недопустимое имя элемента «%s»" % local[:50])
        segments.append((uri, local))
    return absolute, segments


class Normalized(object):
    """Result of ``normalize``: effective structure (None if unusable) and
    issues. ``protocol_code`` is the open_reader error for the first issue."""

    def __init__(self):
        self.effective = None  # type: Optional[Dict[str, Any]]
        self.errors = []  # type: List[Dict[str, Any]]
        self.warnings = []  # type: List[Dict[str, Any]]
        self.protocol_code = INVALID_STRUCTURE

    def fail(self, code, message, protocol_code=None):
        # type: (str, str, Optional[str]) -> None
        if not self.errors and protocol_code:
            self.protocol_code = protocol_code
        self.errors.append(issue(code, message))


def _single_char(value, name, norm, allow_none):
    # type: (Any, str, Normalized, bool) -> Optional[str]
    if value is None:
        if not allow_none:
            norm.fail("invalid_parameter", "Параметр %s обязателен" % name)
        return None
    if isinstance(value, str) and value in ("\\t", "tab"):
        value = "\t"
    if not isinstance(value, str) or len(value) != 1:
        norm.fail("invalid_parameter", "Параметр %s должен быть ровно одним символом" % name)
        return None
    if value in ("\r", "\n"):
        norm.fail("invalid_parameter", "Параметр %s не может быть переводом строки" % name)
        return None
    if "" <= value <= "":  # reserved for internal markers
        norm.fail("invalid_parameter", "Параметр %s содержит недопустимый символ" % name)
        return None
    return value


def _int_value(value, name, default, minimum, maximum, norm):
    # type: (Any, str, int, int, int, Normalized) -> int
    if value is None:
        return default
    if isinstance(value, float) and value.is_integer():
        value = int(value)
    if not isinstance(value, int) or isinstance(value, bool) or not minimum <= value <= maximum:
        norm.fail("invalid_parameter", "Параметр %s должен быть целым числом от %d до %d"
                  % (name, minimum, maximum))
        return default
    return value


def _columns(value, norm):
    # type: (Any, Normalized) -> List[str]
    if value is None:
        return []
    if not isinstance(value, list):
        norm.fail("invalid_parameter", "Параметр columns должен быть массивом строк")
        return []
    if len(value) > MAX_COLUMNS:
        norm.fail("invalid_parameter", "Слишком много колонок (%d)" % len(value))
        return []
    raw = []  # type: List[Any]
    for item in value:
        if item is None or isinstance(item, str):
            raw.append(item)
        elif isinstance(item, (int, float)) and not isinstance(item, bool):
            raw.append(str(item))
        else:
            norm.fail("invalid_parameter", "Имена колонок должны быть строками")
            return []
    names, renamed = dedupe_columns(raw)
    if renamed:
        norm.warnings.append(issue("duplicate_columns_renamed",
                                   "Повторяющиеся имена колонок переименованы"))
    return names


def normalize(structure):
    # type: (Any) -> Normalized
    norm = Normalized()
    if not isinstance(structure, dict):
        norm.fail("invalid_parameter", "Структура должна быть объектом")
        return norm
    fmt = structure.get("format")
    if fmt not in FORMATS:
        norm.fail("invalid_parameter", "Неподдерживаемый формат: допустимы %s" % ", ".join(FORMATS),
                  UNSUPPORTED_FORMAT)
        return norm

    eff = dict((key, None) for key in _KEYS)  # type: Dict[str, Any]
    eff["format"] = fmt
    eff["classification"] = structure.get("classification", "structured")
    confidence = structure.get("confidence")
    eff["confidence"] = confidence if isinstance(confidence, (int, float)) \
        and not isinstance(confidence, bool) else None
    reason = structure.get("reason")
    eff["reason"] = reason if isinstance(reason, str) else None
    eff["xml_namespaces"] = {}

    encoding = structure.get("encoding")
    if encoding is not None and encoding != "":
        canon = encoding_detect.normalize_name(encoding) if isinstance(encoding, str) else None
        if canon is None:
            norm.fail("invalid_parameter", "Неподдерживаемая кодировка", UNSUPPORTED_ENCODING)
        eff["encoding"] = canon

    if fmt in ("jsonl", "json_array") and structure.get("json_record_path") is not None:
        norm.fail("invalid_parameter",
                  "Параметр json_record_path в версии 1 не поддерживается (должен быть null)",
                  UNSUPPORTED_FORMAT)

    has_header = structure.get("has_header")
    if has_header is None:
        has_header = False
    if not isinstance(has_header, bool):
        norm.fail("invalid_parameter", "Параметр has_header должен быть true или false")
        has_header = False
    skip_rows = _int_value(structure.get("skip_rows"), "skip_rows", 0, 0, MAX_SKIP_ROWS, norm)
    if fmt in TEXT_FORMATS:
        eff["has_header"] = has_header
        eff["skip_rows"] = skip_rows
        if has_header:
            header_row = structure.get("header_row")
            if header_row is not None and header_row != skip_rows:
                norm.fail("invalid_parameter",
                          "Параметр header_row должен совпадать с skip_rows при has_header=true")
            eff["header_row"] = skip_rows
    else:
        eff["has_header"] = False
        eff["skip_rows"] = skip_rows if fmt == "jsonl" else 0

    eff["columns"] = _columns(structure.get("columns"), norm)

    if fmt == "delimited":
        _normalize_delimited(structure, eff, norm)
    elif fmt == "fixed_width":
        _normalize_fixed_width(structure, eff, norm)
    elif fmt == "xml":
        _normalize_xml(structure, eff, norm)

    if not norm.errors:
        norm.effective = eff
    return norm


def _normalize_delimited(structure, eff, norm):
    # type: (Dict[str, Any], Dict[str, Any], Normalized) -> None
    delimiter = _single_char(structure.get("delimiter"), "delimiter", norm, False)
    quote = _single_char(structure.get("quote_char", '"'), "quote_char", norm, True)
    escape = _single_char(structure.get("escape_char"), "escape_char", norm, True)
    if delimiter is not None and quote is not None and delimiter == quote:
        norm.fail("invalid_parameter", "Разделитель не может совпадать с символом кавычки")
    if escape is not None and escape == delimiter:
        norm.fail("invalid_parameter", "Символ экранирования не может совпадать с разделителем")
    if escape is not None and escape == quote:
        escape = None  # doubled quotes are always supported
    eff["delimiter"] = delimiter
    eff["quote_char"] = quote
    eff["escape_char"] = escape


def _normalize_fixed_width(structure, eff, norm):
    # type: (Dict[str, Any], Dict[str, Any], Normalized) -> None
    widths = structure.get("fixed_widths")
    if not isinstance(widths, list) or not widths:
        norm.fail("widths_out_of_range", "Не заданы ширины колонок (fixed_widths)")
        return
    clean = []
    for width in widths:
        if isinstance(width, float) and width.is_integer():
            width = int(width)
        if not isinstance(width, int) or isinstance(width, bool) or width <= 0:
            norm.fail("widths_out_of_range", "Ширины колонок должны быть положительными целыми")
            return
        clean.append(width)
    if sum(clean) > MAX_TOTAL_WIDTH:
        norm.fail("widths_out_of_range", "Суммарная ширина колонок больше %d" % MAX_TOTAL_WIDTH)
        return
    columns = eff["columns"]
    if not columns:
        eff["columns"] = [auto_column(i) for i in range(len(clean))]
    elif len(columns) != len(clean):
        norm.fail("widths_out_of_range", "Число ширин (%d) не совпадает с числом колонок (%d)"
                  % (len(clean), len(columns)))
        return
    eff["fixed_widths"] = clean


def _normalize_xml(structure, eff, norm):
    # type: (Dict[str, Any], Dict[str, Any], Normalized) -> None
    namespaces = structure.get("xml_namespaces")
    if namespaces is None:
        namespaces = {}
    if not isinstance(namespaces, dict) or not all(
            isinstance(k, str) and isinstance(v, str) for k, v in namespaces.items()):
        norm.fail("invalid_parameter", "Параметр xml_namespaces должен быть объектом {префикс: URI}")
        return
    eff["xml_namespaces"] = dict(namespaces)
    path = structure.get("xml_record_path")
    if not isinstance(path, str) or not path.strip():
        norm.fail("invalid_parameter", "Не задан путь к записи XML (xml_record_path)")
        return
    try:
        parse_xml_path(path, namespaces)
    except ValueError as exc:
        norm.fail("invalid_parameter", "Некорректный xml_record_path: %s" % exc)
        return
    eff["xml_record_path"] = path.strip()


def for_reader(structure):
    # type: (Any) -> Dict[str, Any]
    """Effective structure for ``open_reader``; raises protocol errors."""
    norm = normalize(structure)
    if norm.errors:
        first = norm.errors[0]
        raise WorkerError(norm.protocol_code, first["message"], {"errors": norm.errors})
    eff = norm.effective
    assert eff is not None
    if eff["format"] == "delimited" and not eff["columns"]:
        raise WorkerError(INVALID_STRUCTURE, "Не заданы колонки: передайте effective_structure "
                          "из validate", {})
    return eff
