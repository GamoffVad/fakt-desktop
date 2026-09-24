# -*- coding: utf-8 -*-
"""The ``validate`` command (PROTOCOL.md §3.3).

The first ``max_bytes`` of the file are decoded in memory (a partial last
line of a truncated fragment is dropped) and parsed by the same parser as
``read_chunk``, up to ``max_records`` records. Structure problems are
returned in ``result`` with ``ok=false``; protocol errors are raised only
for an inaccessible file (or an internal failure)."""
import csv
import io
from collections import Counter
from typing import Any, Dict, List, Optional, Tuple

from . import binary_detect, encoding_detect, readers, sampling
from .parsers import NEWLINE, create_parser
from .parsers.xml_stream import declared_encoding
from .protocol import WorkerError, arg_int, arg_obj, arg_path, stat_file
from .records import Record
from .structure import (auto_column, dedupe_columns, issue, normalize, normalize_header_name)

VALIDATE_MAX_BYTES = 64 * 1024 * 1024
BAD_RATIO_LIMIT = 0.2
REPLACEMENT_RATIO_LIMIT = 0.05
LINE_FORMATS = ("delimited", "fixed_width", "jsonl")
_DELIMITER_CANDIDATES = (";", ",", "\t", "|")


def char_name(char):
    # type: (str) -> str
    return {"\t": "табуляция", " ": "пробел"}.get(char, "«%s»" % char)


def _norm_names(names):
    # type: (List[str]) -> List[str]
    return [normalize_header_name(name) for name in names]


def guess_delimiter(lines):
    # type: (List[str]) -> Optional[str]
    best = None  # type: Optional[Tuple[Tuple[float, int], str]]
    lines = [line for line in lines if line.strip()]
    for candidate in _DELIMITER_CANDIDATES:
        counts = [line.count(candidate) for line in lines]
        if not counts or sum(1 for c in counts if c) < 0.8 * len(counts):
            continue
        mode, frequency = Counter(counts).most_common(1)[0]
        score = (float(frequency) / len(counts), mode)
        if best is None or score > best[0]:
            best = (score, candidate)
    return best[1] if best else None


class Validation(object):

    def __init__(self, norm, data, eof, max_records, explicit_encoding):
        # type: (Any, bytes, bool, int, bool) -> None
        self.eff = dict(norm.effective) if norm.effective else None  # type: Optional[Dict[str, Any]]
        self.errors = list(norm.errors)  # type: List[Dict[str, Any]]
        self.warnings = list(norm.warnings)  # type: List[Dict[str, Any]]
        self.suggestions = {}  # type: Dict[str, Any]
        self.data = data
        self.eof = eof
        self.max_records = max_records
        self.explicit_encoding = explicit_encoding
        self.records = []  # type: List[Tuple[int, Record]]
        self.columns = list(self.eff["columns"]) if self.eff else []  # type: List[str]
        self.parser = None  # type: Any
        self.eof_reached = False

    def error(self, code, message, ordinal=None):
        # type: (str, str, Optional[int]) -> None
        self.errors.append(issue(code, message, ordinal))

    def warning(self, code, message, ordinal=None):
        # type: (str, str, Optional[int]) -> None
        self.warnings.append(issue(code, message, ordinal))

    def has_error(self, *codes):
        # type: (str) -> bool
        return any(item["code"] in codes for item in self.errors)

    # --- main flow --------------------------------------------------------------

    def run(self):
        # type: () -> Dict[str, Any]
        eff = self.eff
        if eff is None:
            return self.result()
        data = self.data
        bom, bom_len = encoding_detect.detect_bom(data)
        if not data or (self.eof and bom is not None and len(data) == bom_len):
            self.error("no_records_found", "Файл пуст")
            return self.result()
        head = data[:readers.HEAD_BYTES]
        head_eof = self.eof and len(data) <= readers.HEAD_BYTES
        canonical, codec, skip = readers.resolve_encoding(eff, head, head_eof)
        found = binary_detect.detect_binary(
            data, check_controls=canonical not in encoding_detect.WIDE_ENCODINGS)
        if found is not None:
            self.error("decode_failed", "Файл двоичный: %s" % found[1])
            return self.result()
        self.check_encoding(canonical, head, head_eof)
        eff["encoding"] = canonical

        text = encoding_detect.decode_prefix(data[skip:], codec, self.eof)
        if not self.eof and eff["format"] in LINE_FORMATS:
            text = encoding_detect.cut_at_last_line_break(text)
        replaced = text.count("�")
        if text and replaced > len(text) * REPLACEMENT_RATIO_LIMIT:
            self.error("decode_failed", "Много нераспознанных символов (%d): кодировка %s не "
                       "подходит для файла" % (replaced, canonical))
        elif replaced:
            self.warning("replacement_characters",
                         "При декодировании заменено нераспознанных символов: %d" % replaced)
        if eff["format"] == "delimited" and not eff["columns"] and not eff["has_header"]:
            eff["columns"] = self.columns_from_mode(text)
            self.columns = list(eff["columns"])
        self.parse(text)
        check = {"delimited": self.check_delimited, "fixed_width": self.check_fixed_width,
                 "xml": self.check_xml, "jsonl": self.check_json,
                 "json_array": self.check_json}[eff["format"]]
        check()
        self.common_checks()
        return self.result()

    def parse(self, text):
        # type: (str) -> None
        eff = self.eff
        assert eff is not None
        stream = io.StringIO(text, newline=NEWLINE[eff["format"]])
        try:
            parser = create_parser(stream, eff, chunk_size=min(self.max_records, 1000),
                                   fragment=not self.eof, collect=True)
        except WorkerError as exc:
            self.error("decode_failed", exc.message)
            return
        self.parser = parser
        iterator = parser.records()
        try:
            exhausted = True
            for record in iterator:
                self.records.append((len(self.records) + 1, record))
                if len(self.records) >= self.max_records:
                    exhausted = False
                    break
            self.eof_reached = exhausted and self.eof
        except WorkerError as exc:
            self.error("decode_failed", exc.message)
        finally:
            iterator.close()

    def columns_from_mode(self, text):
        # type: (str) -> List[str]
        """Column count of a delimited file without header and columns."""
        from .parsers.delimited import dialect_kwargs
        eff = self.eff
        assert eff is not None
        source = io.StringIO(text, newline="")
        for _ in range(eff["skip_rows"]):
            if not source.readline():
                break
        counts = Counter()  # type: Counter
        try:
            for row in csv.reader(source, strict=False, **dialect_kwargs(eff)):
                if row and not (len(row) == 1 and not row[0].strip()):
                    counts[len(row)] += 1
                if sum(counts.values()) >= self.max_records:
                    break
        except csv.Error:
            pass
        mode = counts.most_common(1)[0][0] if counts else 1
        return [auto_column(i) for i in range(mode)]

    # --- format checks ------------------------------------------------------------

    def use_file_header(self, header):
        # type: (List[str]) -> None
        real, renamed = dedupe_columns(header)
        self.columns = real
        if renamed:
            self.warning("duplicate_columns_renamed", "Повторяющиеся имена колонок в заголовке "
                         "файла переименованы")

    def check_delimited(self):
        eff = self.eff
        parser = self.parser
        if eff is None or parser is None:
            return
        if eff["has_header"]:
            header = parser.header or []
            if parser.header_error:
                self.error("decode_failed", "Не удалось разобрать строку заголовка")
            elif not eff["columns"]:
                if header:
                    self.use_file_header(header)
            elif not header:
                if self.records:
                    self.error("column_count_mismatch", "Строка заголовка пуста")
            else:
                real = dedupe_columns(header)[0]
                if _norm_names(real) != _norm_names(self.columns):
                    if len(real) == len(self.columns):
                        self.warning("header_differs", "Заголовок файла отличается от колонок "
                                     "структуры: используются имена из файла")
                        self.use_file_header(header)
                    else:
                        self.error("column_count_mismatch", "В заголовке файла %d колонок, в "
                                   "структуре %d" % (len(real), len(self.columns)))
                        self.suggestions["columns"] = real
        ncols = len(parser.columns)
        lines = parser.source.kept if parser.source is not None else []
        delimiter = eff["delimiter"]
        if ncols > 1 and lines and not any(delimiter in line for line in lines):
            self.error("delimiter_not_found", "Разделитель %s не встречается в первых строках "
                       "файла" % char_name(delimiter))
            guess = guess_delimiter(lines)
            if guess is not None:
                self.suggestions["delimiter"] = guess
        elif parser.field_counts and not self.has_error("column_count_mismatch"):
            counts = Counter(parser.field_counts)
            top = counts.most_common()
            mode = top[0][0]
            if len(top) > 1 and top[1][1] == top[0][1] and ncols in (top[0][0], top[1][0]):
                mode = ncols
            if mode != ncols:
                self.error("column_count_mismatch", "Большинство записей содержит %d полей, а "
                           "колонок в структуре %d" % (mode, ncols))
                names = self.columns + [auto_column(i) for i in range(len(self.columns), mode)]
                self.suggestions["columns"] = names[:mode]

    def check_fixed_width(self):
        eff = self.eff
        parser = self.parser
        if eff is None or parser is None:
            return
        if eff["has_header"] and parser.header and any(parser.header):
            real = dedupe_columns(parser.header)[0]
            if _norm_names(real) != _norm_names(self.columns):
                self.warning("header_differs", "Заголовок файла отличается от колонок структуры: "
                             "используются имена из файла")
                self.use_file_header(parser.header)
        total = parser.total_width
        lines = parser.source.kept if parser.source is not None else []
        for line in lines[1 if eff["has_header"] else 0:]:
            body = line.rstrip("\r\n")
            if len(body) > total and body[total:].strip():
                self.warning("data_beyond_last_column", "Есть данные правее последней колонки "
                             "(после позиции %d)" % total)
                break

    def check_xml(self):
        if not self.records and not self.has_error("decode_failed"):
            eff = self.eff
            assert eff is not None
            self.error("record_path_not_found", "Элементы записи по пути «%s» не найдены в "
                       "проверенном фрагменте" % eff["xml_record_path"])
            if self.parser is not None:
                suggestion = self.parser.suggest_record_path()
                if suggestion:
                    self.suggestions["xml_record_path"] = suggestion
        self.dynamic_columns()

    def check_json(self):
        not_objects = sum(1 for _, record in self.records
                          if record.error is not None and record.error[0] == "not_json_object")
        if self.records and not_objects * 2 > len(self.records):
            self.error("not_json_object", "Большинство проверенных записей не являются "
                       "JSON-объектами")
        self.dynamic_columns()

    def dynamic_columns(self):
        seen = {}  # type: Dict[str, int]
        for _, record in self.records:
            for key in record.keys or ():
                if key not in seen:
                    seen[key] = len(seen)
        self.columns = list(seen)

    def check_encoding(self, canonical, head, head_eof):
        # type: (str, bytes, bool) -> None
        eff = self.eff
        assert eff is not None
        if eff["format"] == "xml":
            bom, _ = encoding_detect.detect_bom(head)
            declared = declared_encoding(head) if bom is None else None
            if bom is not None:
                reference = encoding_detect.detect_encoding(head, head_eof).encoding
            elif declared:
                reference = encoding_detect.normalize_name(declared)
                if reference is None:
                    self.warning("encoding_differs", "Кодировка из объявления XML (%s) не "
                                 "поддерживается, использована %s" % (declared[:40], canonical))
                    return
            else:
                return
            if not encoding_detect.encodings_equivalent(canonical, reference, False):
                self.warning("encoding_differs", "Кодировка структуры (%s) отличается от "
                             "указанной в файле (%s)" % (canonical, reference))
            return
        if not self.explicit_encoding:
            return
        detection = encoding_detect.detect_encoding(head, head_eof)
        if encoding_detect.encodings_equivalent(canonical, detection.encoding, detection.is_ascii):
            return
        if detection.source == "bom" or detection.confidence >= 0.8:
            self.warning("encoding_differs", "Кодировка структуры (%s) отличается от "
                         "определённой по файлу (%s)" % (canonical, detection.encoding))

    def common_checks(self):
        parser = self.parser
        if parser is not None:
            self.warnings.extend(parser.drain_warnings())
        checked = len(self.records)
        bad = [ordinal for ordinal, record in self.records if record.error is not None]
        if not checked and not self.has_error("decode_failed", "record_path_not_found"):
            self.error("no_records_found", "Записи не найдены")
        if bad:
            ratio = float(len(bad)) / checked
            if ratio > BAD_RATIO_LIMIT:
                self.error("too_many_bad_records", "Ошибки в %d из %d проверенных записей (%.0f%%)"
                           % (len(bad), checked, ratio * 100), bad[0])
            else:
                self.warning("bad_records_present", "Ошибки в %d из %d проверенных записей"
                             % (len(bad), checked), bad[0])
        if parser is not None and parser.blank_lines:
            self.warning("blank_lines_present", "Пустых строк: %d" % parser.blank_lines)

    # --- result -----------------------------------------------------------------

    def preview_rows(self):
        # type: () -> List[Dict[str, Any]]
        index = dict((name, i) for i, name in enumerate(self.columns))
        rows = []
        for ordinal, record in self.records:
            error = None  # type: Optional[Dict[str, str]]
            if record.error is not None:
                values = None  # type: Optional[List[Optional[str]]]
                error = {"code": record.error[0], "message": record.error[1]}
            elif record.keys is None:
                values = record.values
            else:
                values = [None] * len(self.columns)
                assert record.values is not None
                for key, value in zip(record.keys, record.values):
                    values[index[key]] = value
            rows.append({"ordinal": ordinal, "line": record.line, "values": values,
                         "error": error})
        return rows

    def result(self):
        # type: () -> Dict[str, Any]
        eff = self.eff
        if eff is not None:
            eff["columns"] = list(self.columns)
        warnings = []  # type: List[Dict[str, Any]]
        for item in self.warnings:
            if item not in warnings:
                warnings.append(item)
        return {
            "ok": not self.errors,
            "errors": self.errors,
            "warnings": warnings,
            "columns": list(self.columns),
            "effective_structure": eff,
            "suggestions": self.suggestions or None,
            "records_checked": len(self.records),
            "bad_records": sum(1 for _, record in self.records if record.error is not None),
            "blank_lines": self.parser.blank_lines if self.parser is not None else 0,
            "eof_reached": self.eof_reached,
            "preview": {"columns": list(self.columns), "rows": self.preview_rows()},
        }


def validate(args):
    # type: (Dict[str, Any]) -> Dict[str, Any]
    path = arg_path(args)
    structure = arg_obj(args, "structure")
    max_records = arg_int(args, "max_records", 100, 1, 1000)
    max_bytes = arg_int(args, "max_bytes", 1048576, 1024, VALIDATE_MAX_BYTES)
    stat_file(path)
    norm = normalize(structure)
    if norm.errors:
        return Validation(norm, b"", True, max_records, False).result()
    data, eof = sampling.read_prefix(path, max_bytes)
    explicit = bool(structure.get("encoding"))
    return Validation(norm, data, eof, max_records, explicit).run()
