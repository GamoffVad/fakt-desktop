# -*- coding: utf-8 -*-
"""Delimited text (CSV/TSV/TXT) through ``pandas.read_csv(engine="python")``.

pandas' python engine reads rows with ``csv.reader(f, dialect, strict=True)``
taken from the module global ``csv`` of ``pandas.io.parsers.python_parser``.
That global is replaced by a thin shim whose ``reader`` wraps the real csv
reader in ``_RowTracker`` when ``f`` is our ``LineSource``. The tracker:

* records the physical line span of every row it hands to pandas, so line
  numbers are exact for multiline quoted fields;
* consumes blank lines itself (counted in ``blank_lines``, not records);
* replaces rows with more fields than columns by a sentinel row of exactly
  ``ncols`` fields, so pandas keeps the row in place (and never infers an
  implicit index from a long first row);
* turns ``csv.Error`` (bad quotes, unterminated quote, huge field) into a
  sentinel row instead of letting pandas drop rows of the current chunk.

``on_bad_lines`` is also given a callable that returns a sentinel row, as a
second line of defence. Rows with fewer fields are padded by pandas with
None/NaN: with ``na_filter=False`` a present empty field is "", so a
non-string value means a missing field (``short_record``).
"""
import collections
import csv
import types
from typing import Any, Callable, Dict, Iterator, List, Optional, Tuple

from ..protocol import INTERNAL_ERROR, INVALID_STRUCTURE, MAX_RECORD_CHARS, WorkerError
from ..records import Record, error_record
from ..structure import dedupe_columns, normalize_header_name
from . import OVERSIZED_MARK, BaseParser, LineSource, fields_message

SENTINEL = ""
_SEP = "\x1f"
STRUCTURE_STREAK = 20
MAX_STRUCTURE_WARNINGS = 20
_UNUSED_QUOTE = ""

_pandas = None  # type: Any


class _CsvShim(types.ModuleType):
    """Stands in for the ``csv`` module inside pandas' python parser."""

    def __init__(self):
        types.ModuleType.__init__(self, "csv")

    def __getattr__(self, name):
        return getattr(csv, name)

    def reader(self, source, *args, **kwargs):
        real = csv.reader(source, *args, **kwargs)
        factory = getattr(source, "fakt_tracker_factory", None)
        return factory(real) if factory is not None else real


def _version_tuple(text):
    # type: (str) -> Tuple[int, int]
    parts = []
    for piece in text.split(".")[:2]:
        digits = ""
        for ch in piece:
            if not ch.isdigit():
                break
            digits += ch
        parts.append(int(digits or 0))
    while len(parts) < 2:
        parts.append(0)
    return parts[0], parts[1]


def ensure_supported():
    """Import pandas, check the version and install the csv shim."""
    global _pandas
    if _pandas is not None:
        return _pandas
    import pandas
    if _version_tuple(pandas.__version__) < (1, 4):
        raise WorkerError(INTERNAL_ERROR, "Для чтения CSV требуется pandas 1.4 или новее "
                          "(on_bad_lines с функцией)", {"pandas_version": pandas.__version__})
    try:
        from pandas.io.parsers import python_parser
    except ImportError:
        python_parser = None
    current = getattr(python_parser, "csv", None)
    if not isinstance(current, _CsvShim):
        if current is not csv:
            raise WorkerError(INTERNAL_ERROR, "Неподдерживаемая версия pandas: не найден модуль "
                              "разбора CSV движка python", {"pandas_version": pandas.__version__})
        python_parser.csv = _CsvShim()
    csv.field_size_limit(MAX_RECORD_CHARS)
    _pandas = pandas
    return pandas


def dialect_kwargs(eff):
    # type: (Dict[str, Any]) -> Dict[str, Any]
    delimiter = eff["delimiter"]
    quote = eff["quote_char"]
    escape = eff["escape_char"]
    if quote:
        return {"delimiter": delimiter, "quotechar": quote, "escapechar": escape,
                "doublequote": True, "quoting": csv.QUOTE_MINIMAL}
    unused = '"' if '"' not in (delimiter, escape) else _UNUSED_QUOTE
    return {"delimiter": delimiter, "quotechar": unused, "escapechar": escape,
            "doublequote": True, "quoting": csv.QUOTE_NONE}


def _classify(exc):
    # type: (Exception) -> str
    text = str(exc)
    if "unexpected end of data" in text:
        return "eof_in_quotes"
    if "field larger than field limit" in text:
        return "record_too_large"
    if "expected after" in text:
        return "bad_quotes"
    return "csv_error"


class _RowTracker(object):
    """Wraps the csv reader used by pandas (see module docstring)."""

    def __init__(self, reader, source, ncols, parser):
        # type: (Any, LineSource, int, DelimitedParser) -> None
        self.reader = reader
        self.source = source
        self.ncols = ncols
        self.parser = parser
        self.spans = collections.deque()  # type: Any

    def __iter__(self):
        return self

    def sentinel(self, code, info):
        # type: (str, str) -> List[str]
        return [SENTINEL + code + _SEP + info] + [""] * (self.ncols - 1)

    def __next__(self):
        # type: () -> List[str]
        source = self.source
        reader = self.reader
        while True:
            start = source.consumed + 1
            try:
                row = next(reader)
            except csv.Error as exc:
                self.spans.append((start, source.consumed))
                return self.sentinel(_classify(exc), "")
            count = len(row)
            if count == 0 or (count == 1 and source.consumed == start and not row[0].strip()):
                self.parser.blank_lines += 1
                continue
            self.spans.append((start, source.consumed))
            if count > self.ncols:
                return self.sentinel("field_count_mismatch", str(count))
            if count == 1 and row[0] == OVERSIZED_MARK:
                return self.sentinel("record_too_large", "")
            return row

    next = __next__


class DelimitedParser(BaseParser):

    def __init__(self, stream, eff, chunk_size=5000, fragment=False, collect=False):
        # type: (Any, Dict[str, Any], int, bool, bool) -> None
        BaseParser.__init__(self, stream, eff, chunk_size, fragment, collect)
        ensure_supported()
        self.dialect = dialect_kwargs(eff)
        self.source = None  # type: Optional[LineSource]
        self.header_error = None  # type: Optional[str]
        self.field_counts = []  # type: List[int]
        self.truncated_tail = False
        self._header_norm = None  # type: Optional[List[str]]
        self._streak = 0
        self._structure_warnings = 0

    # --- header ---------------------------------------------------------------

    def _read_header(self, source):
        # type: (LineSource) -> None
        header = []  # type: List[str]
        try:
            header = next(csv.reader(source, strict=True, **self.dialect))
        except StopIteration:
            header = []
        except csv.Error as exc:
            self.header_error = str(exc)
        self.header = header
        if not self.columns and header:
            self.columns = dedupe_columns(header)[0]
        normalized = [normalize_header_name(name) for name in header]
        if any(normalized) and len(normalized) == len(self.columns):
            self._header_norm = normalized

    # --- records --------------------------------------------------------------

    def _structure_warning(self, message, ordinal):
        # type: (str, int) -> None
        if self._structure_warnings < MAX_STRUCTURE_WARNINGS:
            self._structure_warnings += 1
            self.warn("structure_change_suspected", message, ordinal)

    def _bad(self, record):
        # type: (Record) -> Record
        self._streak += 1
        if self._streak == STRUCTURE_STREAK:
            self._structure_warning(
                "Подряд %d записей с ошибками: структура файла могла измениться"
                % STRUCTURE_STREAK, self.record_count + 2 - STRUCTURE_STREAK)
        return record

    def _make_record(self, row, start, end, ncols):
        # type: (List[Any], int, int, int) -> Optional[Record]
        first = row[0]
        if type(first) is str and first.startswith(SENTINEL):
            code, _, info = first[len(SENTINEL):].partition(_SEP)
            if code == "field_count_mismatch":
                actual = int(info)
                if self.collect:
                    self.field_counts.append(actual)
                return self._bad(error_record(start, end, code, fields_message(ncols, actual)))
            if code == "eof_in_quotes":
                if self.fragment:
                    self.truncated_tail = True  # cut by the validate byte budget
                    return None
                return self._bad(error_record(
                    start, end, "decode_error",
                    "Незакрытая кавычка: запись не завершена до конца файла"))
            if code == "record_too_large":
                return self._bad(error_record(start, end, code,
                                              "Запись или поле длиннее 16 MiB символов"))
            if code == "bad_quotes":
                return self._bad(error_record(start, end, "decode_error",
                                              "Некорректное использование кавычек в записи"))
            return self._bad(error_record(start, end, "decode_error", "Ошибка разбора записи CSV"))
        if type(row[-1]) is not str:
            present = sum(1 for value in row if type(value) is str)
            if self.collect:
                self.field_counts.append(present)
            return self._bad(error_record(start, end, "short_record",
                                          fields_message(ncols, present)))
        if self.collect:
            self.field_counts.append(ncols)
        header = self._header_norm
        if header is not None and row[0].strip().casefold() == header[0] \
                and [value.strip().casefold() for value in row] == header:
            self._structure_warning("Строка заголовка повторяется внутри файла: возможно, "
                                    "файл склеен из нескольких", self.record_count + 1)
            return self._bad(error_record(start, end, "structure_changed",
                                          "Запись совпадает со строкой заголовка"))
        self._streak = 0
        return Record(start, end, None, row)

    def _bad_lines_handler(self, ncols):
        # type: (int) -> Callable[[List[str]], List[str]]
        def handler(fields):
            return [SENTINEL + "field_count_mismatch" + _SEP + str(len(fields))] \
                + [""] * (ncols - 1)
        return handler

    def records(self):
        # type: () -> Iterator[Record]
        pandas = ensure_supported()
        source = LineSource(self.stream, oversized="mark", replace_nul=True,
                            keep=60 if self.collect else 0)
        self.source = source
        skip_rows = self.eff["skip_rows"]
        if source.skip(skip_rows) < skip_rows:
            return
        source.keeping = self.collect
        if self.eff["has_header"]:
            self._read_header(source)
        ncols = len(self.columns)
        if ncols == 0:
            raise WorkerError(INVALID_STRUCTURE, "Не удалось определить колонки файла")

        trackers = []  # type: List[_RowTracker]

        def factory(real_reader):
            tracker = _RowTracker(real_reader, source, ncols, self)
            trackers.append(tracker)
            return tracker

        source.fakt_tracker_factory = factory
        reader = pandas.read_csv(
            source, sep=self.dialect["delimiter"], header=None, names=list(range(ncols)),
            index_col=None, dtype=str, keep_default_na=False, na_filter=False,
            skip_blank_lines=False, engine="python", chunksize=self.chunk_size,
            on_bad_lines=self._bad_lines_handler(ncols), quotechar=self.dialect["quotechar"],
            quoting=self.dialect["quoting"], escapechar=self.dialect["escapechar"],
            doublequote=True, skipinitialspace=False)
        try:
            if len(trackers) != 1:
                raise WorkerError(INTERNAL_ERROR, "Неподдерживаемая версия pandas: чтение CSV "
                                  "идёт в обход отслеживания строк", {})
            spans = trackers[0].spans
            for frame in reader:
                rows = frame.values.tolist()
                if len(spans) < len(rows):
                    raise WorkerError(INTERNAL_ERROR, "Нарушено соответствие строк файла и "
                                      "записей pandas", {})
                for row in rows:
                    start, end = spans.popleft()
                    record = self._make_record(row, start, end, ncols)
                    if record is None:
                        continue
                    self.record_count += 1
                    yield record
        finally:
            reader.close()
