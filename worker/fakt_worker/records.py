# -*- coding: utf-8 -*-
"""Logical records (PROTOCOL.md §6): canonical hash, value conversion and
assembly of ``read_chunk`` responses within ``max_chunk_bytes``."""
import hashlib
import json
import math
import re
from typing import Any, Dict, List, Optional, Tuple

_SURROGATES = re.compile("[\ud800-\udfff]")

# Fixed part of a read_chunk response (envelope, stats, warnings) and of one
# record object besides its values, in bytes of JSON.
RESPONSE_OVERHEAD = 1024
RECORD_OVERHEAD = 160
NULL_COST = 5  # "null," for an absent column of a dynamic-column record


class Record(object):
    """A record produced by a parser.

    ``keys`` is None for formats with fixed columns (delimited, fixed width);
    for XML/JSON it lists the record's own flattened keys in source order.
    ``error`` is None or (code, message); then ``values`` is None."""
    __slots__ = ("line", "end_line", "keys", "values", "error")

    def __init__(self, line, end_line, keys, values, error=None):
        self.line = line  # type: Optional[int]
        self.end_line = end_line  # type: Optional[int]
        self.keys = keys  # type: Optional[List[str]]
        self.values = values  # type: Optional[List[Optional[str]]]
        self.error = error  # type: Optional[Tuple[str, str]]


def error_record(line, end_line, code, message):
    # type: (Optional[int], Optional[int], str, str) -> Record
    return Record(line, end_line, None, None, (code, message))


def clean_str(value):
    # type: (str) -> str
    """Replace lone surrogates (possible after JSON \\uD800 escapes) with
    U+FFFD so the value can be encoded as UTF-8."""
    if _SURROGATES.search(value):
        return _SURROGATES.sub("�", value)
    return value


def to_value(value):
    # type: (Any) -> Optional[str]
    """None/NaN -> None; strings unchanged; anything else -> str."""
    if value is None:
        return None
    if isinstance(value, str):
        return value
    if isinstance(value, float) and math.isnan(value):
        return None
    return str(value)


def canonical_json(columns, values):
    # type: (List[str], List[Optional[str]]) -> bytes
    """Compact JSON array of [column, value] pairs, UTF-8 (§6)."""
    return json.dumps(list(zip(columns, values)), ensure_ascii=False,
                      separators=(",", ":")).encode("utf-8")


def record_hash(columns, values):
    # type: (List[str], List[Optional[str]]) -> str
    return hashlib.sha256(canonical_json(columns, values)).hexdigest()


def replacement_count(values):
    # type: (List[Optional[str]]) -> int
    total = 0
    for value in values:
        if value and "�" in value:
            total += value.count("�")
    return total


class Prepared(object):
    """A record ready for output with its estimated JSON size."""
    __slots__ = ("ordinal", "record", "hash", "size")

    def __init__(self, ordinal, record, digest, size):
        self.ordinal = ordinal  # type: int
        self.record = record  # type: Record
        self.hash = digest  # type: Optional[str]
        self.size = size  # type: int


def prepare(ordinal, record, fixed_columns, max_chunk_bytes):
    # type: (int, Record, Optional[List[str]], int) -> Prepared
    """Compute hash and size; a record that alone exceeds the response
    limit becomes a ``record_too_large`` error record."""
    if record.error is not None:
        return Prepared(ordinal, record, None, RECORD_OVERHEAD + 3 * len(record.error[1]))
    columns = fixed_columns if record.keys is None else record.keys
    values = record.values
    assert values is not None and columns is not None
    canon = canonical_json(columns, values)
    size = RECORD_OVERHEAD + len(canon)
    if size + RESPONSE_OVERHEAD > max_chunk_bytes:
        too_large = error_record(record.line, record.end_line, "record_too_large",
                                 "Запись больше предела ответа max_chunk_bytes (%d байт)"
                                 % max_chunk_bytes)
        return Prepared(ordinal, too_large, None, RECORD_OVERHEAD + 200)
    return Prepared(ordinal, record, hashlib.sha256(canon).hexdigest(), size)


class ChunkBuilder(object):
    """Collects prepared records while the estimated response stays within
    ``max_chunk_bytes``. With ``fixed_columns=None`` the columns are the
    union of the records' keys in order of first appearance."""

    def __init__(self, fixed_columns, max_chunk_bytes):
        # type: (Optional[List[str]], int) -> None
        self.fixed = fixed_columns
        self.limit = max_chunk_bytes
        self.items = []  # type: List[Prepared]
        self.keys = []  # type: List[str]
        self.key_index = {}  # type: Dict[str, int]
        self.values_size = 0
        self.columns_size = len(json.dumps(fixed_columns, ensure_ascii=False).encode("utf-8")) \
            if fixed_columns is not None else 2

    def _estimate(self, extra_records, extra_values, extra_keys, extra_keys_size):
        # type: (int, int, int, int) -> int
        records = len(self.items) + extra_records
        total = RESPONSE_OVERHEAD + self.columns_size + extra_keys_size + self.values_size \
            + extra_values
        if self.fixed is None:
            total += records * (len(self.keys) + extra_keys) * NULL_COST
        return total

    def try_add(self, item):
        # type: (Prepared) -> bool
        """Add unless it would exceed the limit (the first item always fits)."""
        new_keys = []  # type: List[str]
        new_keys_size = 0
        record = item.record
        if self.fixed is None and record.keys:
            for key in record.keys:
                if key not in self.key_index and key not in new_keys:
                    new_keys.append(key)
                    new_keys_size += len(key.encode("utf-8")) + 4
        if self.items and self._estimate(1, item.size, len(new_keys), new_keys_size) > self.limit:
            return False
        self.items.append(item)
        self.values_size += item.size
        for key in new_keys:
            self.key_index[key] = len(self.keys)
            self.keys.append(key)
        self.columns_size += new_keys_size
        return True

    def columns(self):
        # type: () -> List[str]
        return list(self.fixed) if self.fixed is not None else list(self.keys)

    def output(self):
        # type: () -> List[Dict[str, Any]]
        out = []
        width = len(self.keys)
        for item in self.items:
            record = item.record
            if record.error is not None:
                values = None  # type: Optional[List[Optional[str]]]
                error = {"code": record.error[0], "message": record.error[1]}  # type: Any
            elif self.fixed is not None or record.keys is None:
                values = record.values
                error = None
            else:
                values = [None] * width
                index = self.key_index
                assert record.values is not None
                for key, value in zip(record.keys, record.values):
                    values[index[key]] = value
                error = None
            out.append({"ordinal": item.ordinal, "line": record.line,
                        "end_line": record.end_line, "values": values,
                        "hash": item.hash, "error": error})
        return out
