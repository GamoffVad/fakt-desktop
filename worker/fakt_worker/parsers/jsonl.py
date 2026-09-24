# -*- coding: utf-8 -*-
"""JSON Lines: one JSON object per non-blank line (PROTOCOL.md §5, §5.1).

Also holds the JSON flattening shared with ``json_array``: nested objects
become ``a.b``; arrays (and values inside them) become compact JSON text;
numbers keep their source text; true/false become "true"/"false"; null
stays null."""
import json
from typing import Any, Dict, Iterator, List, Optional, Tuple

from ..records import Record, clean_str, error_record
from . import LINE_LIMIT, BaseParser


class JsonNumber(str):
    """Source text of a JSON number (or NaN/Infinity constant)."""
    __slots__ = ()


def make_decoder():
    # type: () -> json.JSONDecoder
    return json.JSONDecoder(parse_int=JsonNumber, parse_float=JsonNumber,
                            parse_constant=JsonNumber)


def to_json_text(value):
    # type: (Any) -> str
    """Compact JSON text keeping numbers exactly as in the source."""
    if value is None:
        return "null"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, JsonNumber):
        return str.__str__(value)
    if isinstance(value, str):
        return json.dumps(clean_str(value), ensure_ascii=False)
    if isinstance(value, list):
        return "[" + ",".join(to_json_text(item) for item in value) + "]"
    if isinstance(value, dict):
        return "{" + ",".join(json.dumps(clean_str(key), ensure_ascii=False) + ":"
                              + to_json_text(item) for key, item in value.items()) + "}"
    return json.dumps(value)


def _scalar(value):
    # type: (Any) -> Optional[str]
    if value is None:
        return None
    if value is True:
        return "true"
    if value is False:
        return "false"
    if isinstance(value, JsonNumber):
        return str.__str__(value)
    if isinstance(value, str):
        return clean_str(value)
    return to_json_text(value)


def _flatten_into(obj, prefix, keys, values):
    # type: (Dict[str, Any], str, List[str], List[Optional[str]]) -> None
    for key, value in obj.items():
        name = prefix + clean_str(key)
        if isinstance(value, dict):
            if value:
                _flatten_into(value, name + ".", keys, values)
                continue
            keys.append(name)
            values.append("{}")
        elif isinstance(value, list):
            keys.append(name)
            values.append(to_json_text(value))
        else:
            keys.append(name)
            values.append(_scalar(value))


def flatten_json(obj):
    # type: (Dict[str, Any]) -> Tuple[List[str], List[Optional[str]]]
    keys = []  # type: List[str]
    values = []  # type: List[Optional[str]]
    _flatten_into(obj, "", keys, values)
    if len(set(keys)) != len(keys):  # e.g. {"a.b": 1, "a": {"b": 2}}
        seen = {}  # type: Dict[str, int]
        unique = []
        for key in keys:
            number = seen.get(key, 0) + 1
            seen[key] = number
            unique.append(key if number == 1 else "%s (%d)" % (key, number))
        keys = unique
    return keys, values


def json_record(obj, line, end_line):
    # type: (Any, Optional[int], Optional[int]) -> Record
    """Record for a decoded JSON value: an object or a not_json_object error."""
    if not isinstance(obj, dict):
        kind = "массив" if isinstance(obj, list) else "скалярное значение"
        return error_record(line, end_line, "not_json_object",
                            "Ожидался JSON-объект, получено: %s" % kind)
    try:
        keys, values = flatten_json(obj)
    except RecursionError:
        return error_record(line, end_line, "record_too_large",
                            "Слишком глубокая вложенность JSON")
    return Record(line, end_line, keys, values)


class JsonlParser(BaseParser):
    dynamic_columns = True

    def _read_line(self):
        # type: () -> Tuple[str, bool]
        """(line, oversized). An oversized line is consumed but not kept."""
        line = self.stream.readline(LINE_LIMIT)
        if len(line) >= LINE_LIMIT and not line.endswith("\n"):
            while True:
                more = self.stream.readline(LINE_LIMIT)
                if not more or more.endswith("\n"):
                    break
            return "\n", True
        return line, False

    def records(self):
        # type: () -> Iterator[Record]
        decoder = make_decoder()
        line_no = 0
        for _ in range(self.eff["skip_rows"]):
            line, _oversized = self._read_line()
            if not line:
                return
            line_no += 1
        while True:
            line, oversized = self._read_line()
            if not line:
                return
            line_no += 1
            if oversized:
                record = error_record(line_no, line_no, "record_too_large",
                                      "Строка длиннее 16 MiB символов")
            else:
                text = line.strip()
                if text.startswith("﻿"):
                    text = text[1:].strip()
                if not text:
                    self.blank_lines += 1
                    continue
                record = self._decode(decoder, text, line_no)
            self.record_count += 1
            yield record

    @staticmethod
    def _decode(decoder, text, line_no):
        # type: (json.JSONDecoder, str, int) -> Record
        try:
            obj = decoder.decode(text)
        except ValueError as exc:
            message = getattr(exc, "msg", None) or "ошибка разбора"
            position = getattr(exc, "pos", None)
            return error_record(line_no, line_no, "invalid_json",
                                "Некорректный JSON: %s (позиция %s)" % (message, position))
        except RecursionError:
            return error_record(line_no, line_no, "invalid_json",
                                "Некорректный JSON: слишком глубокая вложенность")
        return json_record(obj, line_no, line_no)
