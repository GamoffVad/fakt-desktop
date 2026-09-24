# -*- coding: utf-8 -*-
"""Fixed-width text through ``pandas.read_fwf`` with explicit ``colspecs``.

One record is one physical line. pandas yields one row per line fed to it
(blank lines become rows of empty strings), so the line number of a row is
the first data line plus the row index. Values are stripped of spaces, tabs
and line breaks on both sides (pandas does it; the same set is used for
the header)."""
from typing import Any, Dict, Iterator, List, Tuple

from ..records import Record
from . import BaseParser, LineSource

_STRIP = "\r\n\t "


def colspecs(widths):
    # type: (List[int]) -> List[Tuple[int, int]]
    specs = []
    start = 0
    for width in widths:
        specs.append((start, start + width))
        start += width
    return specs


class FixedWidthParser(BaseParser):

    def __init__(self, stream, eff, chunk_size=5000, fragment=False, collect=False):
        # type: (Any, Dict[str, Any], int, bool, bool) -> None
        BaseParser.__init__(self, stream, eff, chunk_size, fragment, collect)
        self.widths = list(eff["fixed_widths"])
        self.total_width = sum(self.widths)
        self.colspecs = colspecs(self.widths)
        self.source = None  # type: Any

    def split(self, line):
        # type: (str) -> List[str]
        return [line[start:end].strip(_STRIP) for start, end in self.colspecs]

    def records(self):
        # type: () -> Iterator[Record]
        import pandas
        source = LineSource(self.stream, oversized="truncate", replace_nul=False,
                            keep=1100 if self.collect else 0)
        self.source = source
        skip_rows = self.eff["skip_rows"]
        if source.skip(skip_rows) < skip_rows:
            return
        source.keeping = self.collect
        if self.eff["has_header"]:
            line = source.readline()
            self.header = self.split(line) if line else []
        line_no = source.consumed + 1
        count = len(self.widths)
        reader = pandas.read_fwf(
            source, colspecs=self.colspecs, header=None, names=list(range(count)),
            index_col=None, dtype=str, keep_default_na=False, na_filter=False,
            skip_blank_lines=False, chunksize=self.chunk_size)
        try:
            for frame in reader:
                for row in frame.values.tolist():
                    number = line_no
                    line_no += 1
                    if not any(row):
                        self.blank_lines += 1
                        continue
                    values = [value if type(value) is str else "" for value in row]
                    self.record_count += 1
                    yield Record(number, number, None, values)
        finally:
            reader.close()
