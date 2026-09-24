# -*- coding: utf-8 -*-
"""Top-level JSON array read with ``JSONDecoder.raw_decode`` over a growing
text buffer (64 KiB pieces). Elements must be objects; other values become
``not_json_object`` error records. An element that cannot be completed
within 16 MiB, or malformed JSON, stops reading with a protocol error:
there is no reliable way to resynchronize inside a JSON array."""
from typing import Any, Dict, Iterator

from ..protocol import (DECODE_ERROR, INVALID_STRUCTURE, MAX_RECORD_CHARS,
                        RECORD_TOO_LARGE, WorkerError)
from ..records import Record, error_record
from . import BaseParser
from .jsonl import json_record, make_decoder

READ_CHARS = 65536
_WHITESPACE = " \t\r\n﻿"
_TRIM_AT = 1 << 20


class _Truncated(Exception):
    """The validate fragment ended inside the array."""


class JsonArrayParser(BaseParser):
    dynamic_columns = True

    def __init__(self, stream, eff, chunk_size=5000, fragment=False, collect=False):
        # type: (Any, Dict[str, Any], int, bool, bool) -> None
        BaseParser.__init__(self, stream, eff, chunk_size, fragment, collect)
        self.truncated_tail = False
        self._buf = ""
        self._pos = 0
        self._eof = False

    def _fill(self, chars=READ_CHARS):
        # type: (int) -> bool
        if self._eof:
            return False
        text = self.stream.read(chars)
        if not text:
            self._eof = True
            return False
        if self._pos > _TRIM_AT:
            self._buf = self._buf[self._pos:]
            self._pos = 0
        self._buf += text
        return True

    def _skip_ws(self):
        # type: () -> str
        """Skip whitespace; return the next character or "" at the end."""
        while True:
            buf = self._buf
            pos = self._pos
            length = len(buf)
            while pos < length and buf[pos] in _WHITESPACE:
                pos += 1
            self._pos = pos
            if pos < length:
                return buf[pos]
            if not self._fill():
                return ""

    def _end_of_input(self, what):
        # type: (str) -> WorkerError
        if self.fragment:
            raise _Truncated()
        return WorkerError(DECODE_ERROR, "JSON-массив оборван: %s" % what, {})

    def _decode_element(self, decoder):
        # type: (Any) -> Any
        start = self._pos
        while True:
            buf = self._buf
            try:
                obj, end = decoder.raw_decode(buf, start)
            except ValueError as exc:
                position = getattr(exc, "pos", 0) or 0
                message = getattr(exc, "msg", "") or ""
                incomplete = position >= len(buf) - 16 or message.startswith("Unterminated")
                if self._eof or not incomplete:
                    if self._eof and incomplete:
                        raise self._end_of_input("неожиданный конец файла")
                    raise WorkerError(DECODE_ERROR, "Некорректный JSON в массиве: %s" % message,
                                      {"element": self.record_count + 1})
                self._grow(start)
                start = self._pos
                continue
            except RecursionError:
                raise WorkerError(DECODE_ERROR, "Слишком глубокая вложенность JSON",
                                  {"element": self.record_count + 1})
            if end >= len(buf) and not self._eof:
                # a number or literal may continue in the next piece
                self._grow(start)
                start = self._pos
                continue
            self._pos = end
            return obj, end - start

    def _grow(self, start):
        # type: (int) -> None
        """Read more data for an incomplete element starting at ``start``."""
        pending = len(self._buf) - start
        if pending > MAX_RECORD_CHARS:
            raise WorkerError(RECORD_TOO_LARGE, "Элемент JSON-массива больше 16 MiB: чтение "
                              "остановлено", {"element": self.record_count + 1})
        self._pos = start
        # double the pending part, but never buffer more than the element limit
        self._fill(min(max(READ_CHARS, pending), MAX_RECORD_CHARS + 1 - pending))

    def records(self):
        # type: () -> Iterator[Record]
        try:
            for record in self._records():
                yield record
        except _Truncated:
            self.truncated_tail = True

    def _records(self):
        # type: () -> Iterator[Record]
        decoder = make_decoder()
        first = self._skip_ws()
        if first != "[":
            if first == "" and self.fragment:
                raise _Truncated()
            found = "конец файла" if first == "" else "«%s»" % first
            raise WorkerError(INVALID_STRUCTURE, "Файл не является JSON-массивом: ожидался "
                              "символ «[», найдено: %s" % found, {})
        self._pos += 1
        expect_value = True
        count = 0
        while True:
            char = self._skip_ws()
            if char == "":
                raise self._end_of_input("нет закрывающей скобки «]»")
            if char == "]" and (count == 0 or not expect_value):
                return
            if not expect_value:
                if char != ",":
                    raise WorkerError(DECODE_ERROR, "Некорректный JSON в массиве: ожидалась "
                                      "запятая", {"element": count + 1})
                self._pos += 1
                expect_value = True
                continue
            obj, size = self._decode_element(decoder)
            expect_value = False
            count += 1
            if size > MAX_RECORD_CHARS:
                record = error_record(None, None, "record_too_large",
                                      "Элемент JSON-массива больше 16 MiB символов")
            else:
                record = json_record(obj, None, None)
            self.record_count += 1
            yield record
