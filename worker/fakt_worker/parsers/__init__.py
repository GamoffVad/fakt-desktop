# -*- coding: utf-8 -*-
"""Parsers of the supported formats (PROTOCOL.md §5, §7).

Every parser reads a *text* stream (``io.TextIOWrapper`` over the file for
``read_chunk`` or ``io.StringIO`` over a decoded fragment for ``validate``)
and yields ``records.Record`` objects in file order. Blank lines are not
records; they are counted in ``blank_lines``. Ordinals are assigned by the
caller: the n-th yielded record has ordinal n."""
from typing import Any, Dict, Iterator, List, Optional

from ..protocol import MAX_RECORD_CHARS
from ..records import Record

# newline argument for the text stream of each format
NEWLINE = {"delimited": "", "fixed_width": "", "xml": "", "jsonl": "\n", "json_array": ""}

# Lines longer than this are not kept in memory as a whole.
LINE_LIMIT = MAX_RECORD_CHARS
# Placeholder for an oversized physical line (private-use characters only,
# never a delimiter, quote or escape character: see structure.py).
OVERSIZED_MARK = ""


def plural(number, one, few, many):
    # type: (int, str, str, str) -> str
    """Russian plural form: 1 поле, 2 поля, 5 полей."""
    n = abs(number) % 100
    if 11 <= n <= 19:
        return many
    n %= 10
    if n == 1:
        return one
    if 2 <= n <= 4:
        return few
    return many


def fields_message(expected, actual):
    # type: (int, int) -> str
    return "Ожидалось %d %s, получено %d" % (expected, plural(expected, "поле", "поля", "полей"),
                                             actual)


class LineSource(object):
    """Iterator over physical lines of a text stream (terminators kept).

    Counts consumed lines, bounds the length of a line (an oversized line is
    replaced by OVERSIZED_MARK or truncated), optionally replaces NUL (the
    csv module of Python < 3.11 rejects it) and keeps the first lines for
    diagnostics. Also looks like a file object for pandas."""

    def __init__(self, stream, oversized="mark", replace_nul=False, keep=0):
        # type: (Any, str, bool, int) -> None
        self._stream = stream
        self._oversized = oversized
        self._replace_nul = replace_nul
        self._keep = keep
        self.kept = []  # type: List[str]
        self.consumed = 0
        self.keeping = False
        self.oversized_lines = 0
        self.fakt_tracker_factory = None  # see parsers.delimited

    def __iter__(self):
        return self

    def __next__(self):
        # type: () -> str
        line = self._stream.readline(LINE_LIMIT)
        if not line:
            raise StopIteration
        self.consumed += 1
        if len(line) >= LINE_LIMIT and line[-1] not in "\r\n":
            line = self._drop_rest(line)
        if self._replace_nul and "\x00" in line:
            line = line.replace("\x00", "�")
        if self.keeping and len(self.kept) < self._keep:
            self.kept.append(line)
        return line

    next = __next__

    def _drop_rest(self, line):
        # type: (str) -> str
        self.oversized_lines += 1
        terminator = "\n"
        while True:
            more = self._stream.readline(LINE_LIMIT)
            if not more:
                break
            if more[-1] in "\r\n":
                terminator = "\r\n" if more.endswith("\r\n") else more[-1]
                break
        if self._oversized == "truncate":
            return line + terminator
        return OVERSIZED_MARK + terminator

    def readline(self, size=-1):
        # type: (int) -> str
        try:
            return self.__next__()
        except StopIteration:
            return ""

    def read(self, size=-1):
        # type: (int) -> str
        return self.readline()

    def skip(self, count):
        # type: (int) -> int
        """Consume ``count`` physical lines; returns how many were available."""
        done = 0
        while done < count:
            if not self.readline():
                break
            done += 1
        return done


class BaseParser(object):
    """Common state of parsers. ``fragment`` means the stream is a truncated
    prefix of the file (validate): errors caused by the cut are ignored.
    ``collect`` enables diagnostics used by validate."""

    dynamic_columns = False

    def __init__(self, stream, eff, chunk_size=5000, fragment=False, collect=False):
        # type: (Any, Dict[str, Any], int, bool, bool) -> None
        self.stream = stream
        self.eff = eff
        self.chunk_size = chunk_size
        self.fragment = fragment
        self.collect = collect
        self.columns = list(eff.get("columns") or [])  # type: List[str]
        self.blank_lines = 0
        self.record_count = 0
        self.warnings = []  # type: List[Dict[str, Any]]
        self.header = None  # type: Optional[List[str]]

    def records(self):
        # type: () -> Iterator[Record]
        raise NotImplementedError

    def warn(self, code, message, ordinal=None):
        # type: (str, str, Optional[int]) -> None
        self.warnings.append({"code": code, "message": message, "ordinal": ordinal})

    def drain_warnings(self):
        # type: () -> List[Dict[str, Any]]
        warnings, self.warnings = self.warnings, []
        return warnings


def create_parser(stream, eff, chunk_size=5000, fragment=False, collect=False):
    # type: (Any, Dict[str, Any], int, bool, bool) -> BaseParser
    fmt = eff["format"]
    if fmt == "delimited":
        from .delimited import DelimitedParser as cls  # type: Any
    elif fmt == "fixed_width":
        from .fixed_width import FixedWidthParser as cls
    elif fmt == "xml":
        from .xml_stream import XmlParser as cls
    elif fmt == "jsonl":
        from .jsonl import JsonlParser as cls
    elif fmt == "json_array":
        from .json_array import JsonArrayParser as cls
    else:  # pragma: no cover - rejected by structure.normalize
        raise ValueError(fmt)
    return cls(stream, eff, chunk_size=chunk_size, fragment=fragment, collect=collect)
