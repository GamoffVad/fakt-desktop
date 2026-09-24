# -*- coding: utf-8 -*-
"""Deterministic synthetic test data and helpers.

Only obviously fake data is generated: names like "Тестов Тест Тестович",
phones like "+7 000 000-00-00". Never real people."""
import codecs
import json
import os
import shutil
import tempfile
import unittest
from typing import Any, Dict, List, Optional, Tuple

from fakt_worker import readers

SURNAMES = ("Тестов", "Примеров", "Образцов", "Проверкин", "Демонстров")
NAMES = ("Тест", "Пример", "Образец", "Проба", "Демо")
PATRONYMICS = ("Тестович", "Примерович", "Образцович", "Пробович", "Демович")

RUSSIAN_TEXT = (
    "Тестовая запись для проверки определения кодировки файла.\n"
    "Каждая строка этого текста придумана только для автоматических тестов.\n"
    "Проверочная организация \"Пример\" зарегистрирована в городе Тестовске.\n"
    "Сотрудник Тестов Тест Тестович получил номер телефона +7 000 000-00-00.\n"
    "Съешь же ещё этих мягких французских булок, да выпей чаю.\n"
    "Образцова Проба Тестовна работает в отделе проверки данных.\n"
)


def person(i):
    # type: (int) -> str
    return "%s %s %s" % (SURNAMES[i % 5], NAMES[(i * 3) % 5], PATRONYMICS[(i * 7) % 5])


def phone(i):
    # type: (int) -> str
    return "+7 000 000-%02d-%02d" % ((i // 100) % 100, i % 100)


def birth_date(i):
    # type: (int) -> str
    return "%02d.%02d.%d" % (i % 28 + 1, i % 12 + 1, 1950 + i % 50)


def code(i):
    # type: (int) -> str
    return "%07d" % i  # leading zeros must survive


def people(count, start=0):
    # type: (int, int) -> List[List[str]]
    return [[person(i), birth_date(i), phone(i), code(i)] for i in range(start, start + count)]


HEADER = ["ФИО", "Дата рождения", "Телефон", "Код"]


def quote_field(value, delimiter, quote='"'):
    # type: (str, str, str) -> str
    if any(ch in value for ch in (delimiter, quote, "\r", "\n")):
        return quote + value.replace(quote, quote * 2) + quote
    return value


def csv_text(rows, delimiter=";", header=None, line_end="\r\n", preamble=()):
    # type: (List[List[str]], str, Optional[List[str]], str, Any) -> str
    lines = list(preamble)
    if header is not None:
        lines.append(delimiter.join(quote_field(v, delimiter) for v in header))
    for row in rows:
        lines.append(delimiter.join(quote_field(v, delimiter) for v in row))
    return line_end.join(lines) + line_end


def delimited_structure(columns, delimiter=";", has_header=True, skip_rows=0, **extra):
    # type: (Optional[List[str]], str, bool, int, Any) -> Dict[str, Any]
    structure = {
        "classification": "structured", "format": "delimited", "encoding": None,
        "has_header": has_header, "header_row": skip_rows if has_header else None,
        "skip_rows": skip_rows, "delimiter": delimiter, "quote_char": '"',
        "escape_char": None, "columns": columns, "fixed_widths": None,
        "xml_record_path": None, "xml_namespaces": {}, "json_record_path": None,
        "confidence": 0.9, "reason": "синтетический тест",
    }
    structure.update(extra)
    return structure


def xml_structure(path, namespaces=None, **extra):
    # type: (str, Optional[Dict[str, str]], Any) -> Dict[str, Any]
    structure = {"format": "xml", "encoding": None, "has_header": False, "header_row": None,
                 "skip_rows": 0, "delimiter": None, "quote_char": None, "escape_char": None,
                 "columns": [], "fixed_widths": None, "xml_record_path": path,
                 "xml_namespaces": namespaces or {}, "json_record_path": None}
    structure.update(extra)
    return structure


def json_structure(fmt, **extra):
    # type: (str, Any) -> Dict[str, Any]
    structure = {"format": fmt, "encoding": None, "has_header": False, "header_row": None,
                 "skip_rows": 0, "columns": [], "json_record_path": None}
    structure.update(extra)
    return structure


def write_people_xml(path, count, encoding="utf-8"):
    # type: (str, int, str) -> None
    """Streamed synthetic XML: <people><person id=".."><name/>..</person>..."""
    with open(path, "w", encoding=encoding, newline="\n") as fh:
        fh.write('<?xml version="1.0" encoding="%s"?>\n<people>\n' % encoding)
        for i in range(count):
            fh.write('  <person id="%d"><name>%s</name><phone>%s</phone></person>\n'
                     % (i, person(i), phone(i)))
        fh.write("</people>\n")


class TempDirTestCase(unittest.TestCase):
    """Creates a temporary directory per test and closes leftover readers."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="fakt_test_")

    def tearDown(self):
        readers.close_all()
        shutil.rmtree(self.tmp, ignore_errors=True)

    def path(self, name):
        # type: (str) -> str
        return os.path.join(self.tmp, name)

    def write_bytes(self, name, data):
        # type: (str, bytes) -> str
        path = self.path(name)
        with open(path, "wb") as fh:
            fh.write(data)
        return path

    def write_text(self, name, text, encoding="utf-8", bom=False):
        # type: (str, str, str, bool) -> str
        data = text.encode(encoding)
        if bom:
            data = {"utf-8": codecs.BOM_UTF8, "utf-16-le": codecs.BOM_UTF16_LE,
                    "utf-16-be": codecs.BOM_UTF16_BE}[encoding] + data
        return self.write_bytes(name, data)


def read_all(path, structure, **kwargs):
    # type: (str, Dict[str, Any], Any) -> Tuple[List[Dict[str, Any]], List[Dict[str, Any]]]
    """All records through open_reader/read_chunk; returns (records, chunks).
    Records of XML/JSON get an extra "_row" dict {column: value}."""
    args = dict(kwargs)
    args.update(path=path, structure=structure)
    reader_id = readers.open_reader(args)["reader_id"]
    records = []  # type: List[Dict[str, Any]]
    chunks = []  # type: List[Dict[str, Any]]
    try:
        for _ in range(1000000):
            chunk = readers.read_chunk({"reader_id": reader_id})
            chunks.append(chunk)
            for record in chunk["records"]:
                if record["values"] is not None:
                    record["_row"] = dict((c, v) for c, v in zip(chunk["columns"], record["values"])
                                          if v is not None)
                records.append(record)
            if chunk["eof"]:
                break
    finally:
        readers.close_reader({"reader_id": reader_id})
    return records, chunks


def response_size(chunk):
    # type: (Dict[str, Any]) -> int
    """Size of a read_chunk response line as the server would send it."""
    envelope = {"v": 1, "id": "123456", "ok": True, "result": chunk}
    return len(json.dumps(envelope, ensure_ascii=False, separators=(",", ":")).encode("utf-8")) + 1
