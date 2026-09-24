# -*- coding: utf-8 -*-
"""Encoding detection and text decoding (PROTOCOL.md §4).

Order of detection: BOM -> UTF-16/32 without BOM (distribution of zero
bytes + plausibility of the decoded text) -> strict UTF-8 (an incomplete
sequence at the sample boundary is not an error) -> scoring of single-byte
encodings: windows-1251, cp866, koi8-r (Russian letter statistics) and
windows-1252 (Latin letters inside Latin words).
"""
import codecs
import io
import math
import re
from collections import Counter
from typing import Dict, List, Optional, Tuple

from .protocol import UNSUPPORTED_ENCODING, WorkerError

CANONICAL = (
    "utf-8", "utf-8-sig", "utf-16-le", "utf-16-be", "utf-32-le", "utf-32-be",
    "windows-1251", "cp866", "koi8-r", "windows-1252", "iso-8859-1", "ascii",
)

# canonical name -> Python codec
_CODECS = {
    "utf-8": "utf-8",
    "utf-8-sig": "utf-8",  # the BOM is skipped explicitly
    "utf-16-le": "utf-16-le",
    "utf-16-be": "utf-16-be",
    "utf-32-le": "utf-32-le",
    "utf-32-be": "utf-32-be",
    "windows-1251": "cp1251",
    "cp866": "cp866",
    "koi8-r": "koi8_r",
    "windows-1252": "cp1252",
    "iso-8859-1": "latin-1",
    "ascii": "ascii",
}

# accepted spellings -> canonical name ("utf-16" is resolved by the BOM)
_SYNONYMS = {
    "utf8": "utf-8", "utf-8": "utf-8", "utf-8-sig": "utf-8-sig", "utf8-sig": "utf-8-sig",
    "utf-16": "utf-16", "utf16": "utf-16",
    "utf-16-le": "utf-16-le", "utf-16le": "utf-16-le",
    "utf-16-be": "utf-16-be", "utf-16be": "utf-16-be",
    "utf-32-le": "utf-32-le", "utf-32le": "utf-32-le",
    "utf-32-be": "utf-32-be", "utf-32be": "utf-32-be",
    "windows-1251": "windows-1251", "cp1251": "windows-1251", "1251": "windows-1251",
    "win-1251": "windows-1251",
    "cp866": "cp866", "ibm866": "cp866", "866": "cp866",
    "koi8-r": "koi8-r", "koi8r": "koi8-r",
    "windows-1252": "windows-1252", "cp1252": "windows-1252", "1252": "windows-1252",
    "iso-8859-1": "iso-8859-1", "latin-1": "iso-8859-1", "latin1": "iso-8859-1",
    "ascii": "ascii", "us-ascii": "ascii",
}

_BOMS = (  # UTF-32 first: its LE BOM starts with the UTF-16 LE BOM
    (codecs.BOM_UTF32_LE, "utf-32-le"),
    (codecs.BOM_UTF32_BE, "utf-32-be"),
    (codecs.BOM_UTF8, "utf-8"),
    (codecs.BOM_UTF16_LE, "utf-16-le"),
    (codecs.BOM_UTF16_BE, "utf-16-be"),
)
# BOM name -> canonical encoding reported for a file with that BOM
_BOM_ENCODING = {"utf-8": "utf-8-sig", "utf-16-le": "utf-16-le", "utf-16-be": "utf-16-be",
                 "utf-32-le": "utf-32-le", "utf-32-be": "utf-32-be"}
# canonical encoding -> BOM name that belongs to it (skipped when decoding)
_OWN_BOM = {"utf-8": "utf-8", "utf-8-sig": "utf-8", "utf-16-le": "utf-16-le",
            "utf-16-be": "utf-16-be", "utf-32-le": "utf-32-le", "utf-32-be": "utf-32-be"}

WIDE_ENCODINGS = frozenset(("utf-16-le", "utf-16-be", "utf-32-le", "utf-32-be", "utf-16"))
UTF8_FAMILY = frozenset(("utf-8", "utf-8-sig"))
# encodings in which bytes 0x00-0x7F are plain ASCII
ASCII_COMPATIBLE = frozenset(("utf-8", "utf-8-sig", "windows-1251", "cp866", "koi8-r",
                              "windows-1252", "iso-8859-1", "ascii"))


def normalize_name(name):
    # type: (str) -> Optional[str]
    """Canonical name for an accepted spelling, None if unknown."""
    key = name.strip().lower().replace("_", "-")
    return _SYNONYMS.get(key)


def canonical_encoding(name):
    # type: (str) -> str
    canon = normalize_name(name)
    if canon is None:
        raise WorkerError(UNSUPPORTED_ENCODING, "Неподдерживаемая кодировка",
                          {"encoding": name[:64], "supported": list(CANONICAL)})
    return canon


def detect_bom(data):
    # type: (bytes) -> Tuple[Optional[str], int]
    for bom, name in _BOMS:
        if data.startswith(bom):
            return name, len(bom)
    return None, 0


def resolve_for_file(canonical, head):
    # type: (str, bytes) -> Tuple[str, str, int]
    """(canonical, python codec, number of BOM bytes to skip) for a file that
    starts with ``head``. Resolves "utf-16" by the BOM (LE without BOM)."""
    bom, bom_len = detect_bom(head)
    if canonical == "utf-16":
        canonical = "utf-16-be" if bom == "utf-16-be" else "utf-16-le"
    own = _OWN_BOM.get(canonical)
    skip = 0
    if own is not None and bom is not None:
        if bom == own:
            skip = bom_len
        elif own == "utf-16-le" and bom == "utf-32-le":
            skip = 2  # FF FE 00 00 read as UTF-16 LE: BOM followed by U+0000
    return canonical, _CODECS[canonical], skip


class Detection(object):
    __slots__ = ("encoding", "source", "confidence", "bom", "bom_length", "is_ascii")

    def __init__(self, encoding, source, confidence, bom=None, bom_length=0, is_ascii=False):
        self.encoding = encoding  # type: Optional[str]
        self.source = source  # type: Optional[str]
        self.confidence = confidence  # type: float
        self.bom = bom  # type: Optional[str]
        self.bom_length = bom_length  # type: int
        self.is_ascii = is_ascii  # type: bool


# --- UTF-16/32 without BOM ---------------------------------------------------

_IMPLAUSIBLE = re.compile(
    "[^\t\n\r\x20-\x7e -ɏͰ-ԯ -⁯₠-⃏℀-⇿]")


def _plausible_ratio(text):
    # type: (str) -> float
    if not text:
        return 0.0
    return 1.0 - float(len(_IMPLAUSIBLE.findall(text))) / len(text)


def _detect_wide(data):
    # type: (bytes) -> Optional[Tuple[str, float]]
    if len(data) < 4 or b"\x00" not in data:
        return None
    best = None  # type: Optional[Tuple[str, float]]
    n4 = len(data) - len(data) % 4
    quads = n4 // 4
    if quads:
        for enc, always_zero, mostly_zero in (
                ("utf-32-le", data[3:n4:4], data[2:n4:4]),
                ("utf-32-be", data[0:n4:4], data[1:n4:4])):
            if always_zero.count(0) == quads and mostly_zero.count(0) >= quads * 0.9:
                ratio = _plausible_ratio(data[:n4].decode(enc, "replace"))
                if ratio >= 0.95:
                    return enc, round(min(0.99, ratio), 3)
    n2 = len(data) - len(data) % 2
    pairs = n2 // 2
    for enc, high, low in (("utf-16-le", data[1:n2:2], data[0:n2:2]),
                           ("utf-16-be", data[0:n2:2], data[1:n2:2])):
        # ASCII-range characters (spaces, digits, delimiters, line breaks)
        # have a zero high byte; low bytes are zero only for U+0000.
        if high.count(0) < pairs * 0.05 or low.count(0) > pairs * 0.02:
            continue
        text = codecs.getincrementaldecoder(enc)("replace").decode(data[:n2], False)
        ratio = _plausible_ratio(text)
        if ratio >= 0.95 and (best is None or ratio > best[1]):
            best = (enc, round(min(0.99, ratio), 3))
    return best


# --- strict UTF-8 -------------------------------------------------------------

def is_valid_utf8(data, final):
    # type: (bytes, bool) -> bool
    """Strict UTF-8 check; with final=False an incomplete sequence at the
    end of ``data`` (sample boundary) is not an error."""
    try:
        codecs.getincrementaldecoder("utf-8")("strict").decode(data, final)
        return True
    except UnicodeDecodeError:
        return False


# --- single-byte scoring ------------------------------------------------------

_RU_FREQ = {
    "о": 10.97, "е": 8.45, "а": 8.01, "и": 7.35, "н": 6.70, "т": 6.26, "с": 5.47,
    "р": 4.73, "в": 4.54, "л": 4.40, "к": 3.49, "м": 3.21, "д": 2.98, "п": 2.81,
    "у": 2.62, "я": 2.01, "ы": 1.90, "ь": 1.74, "г": 1.70, "з": 1.65, "б": 1.59,
    "ч": 1.44, "й": 1.21, "х": 0.97, "ж": 0.94, "ш": 0.73, "ю": 0.64, "ц": 0.48,
    "щ": 0.36, "э": 0.32, "ф": 0.26, "ъ": 0.04, "ё": 0.04,
}
_RU_WEIGHT = dict((ch, math.log(freq)) for ch, freq in _RU_FREQ.items())
# punctuation that is normal in Russian and Western texts: neutral
_NEUTRAL = set(" «»–—…“”„‘’•·°№©®±§­€™")
_OTHER_CYRILLIC_WEIGHT = -2.0
_BAD_WEIGHT = -4.0
_CASE_BREAK_PENALTY = 3.0   # Russian lowercase letter followed by uppercase
_MIXED_SCRIPT_PENALTY = 2.0  # ASCII letter adjacent to a Russian letter
_LATIN_BASE = -0.5          # windows-1252: accented letter ...
_LATIN_NEIGHBOUR = 1.0      # ... plus this per adjacent ASCII letter
_LATIN_RUN_PENALTY = 1.0    # ... minus this per pair of adjacent accented letters

_SINGLE_BYTE = ("windows-1251", "cp866", "koi8-r", "windows-1252")
_HIGH_BYTES = bytes(range(0x80, 0x100))


def _build_tables():
    # type: () -> Dict[str, Tuple[List[float], bytes]]
    """Per encoding: weight of every byte 0x80..0xFF and a translation table
    mapping bytes to classes: l/u = Russian lower/upper letter, h = other
    high-range letter, a = ASCII letter, '.' = anything else."""
    ascii_cls = bytearray(b"." * 256)
    for b in range(256):
        if 0x41 <= b <= 0x5A or 0x61 <= b <= 0x7A:
            ascii_cls[b] = ord("a")
    tables = {}
    for canon in _SINGLE_BYTE:
        codec = _CODECS[canon]
        weights = [0.0] * 256
        cls = bytearray(ascii_cls)
        for b in range(0x80, 0x100):
            ch = bytes([b]).decode(codec, "replace")
            low = ch.lower()
            if canon == "windows-1252":
                if ch.isalpha():
                    weights[b] = _LATIN_BASE
                    cls[b] = ord("h")
                elif ch in _NEUTRAL:
                    weights[b] = 0.0
                elif ch == "�":
                    weights[b] = _BAD_WEIGHT
                else:
                    weights[b] = -2.0
                continue
            if low in _RU_WEIGHT:
                weights[b] = _RU_WEIGHT[low]
                cls[b] = ord("l") if ch == low else ord("u")
            elif ch in _NEUTRAL:
                weights[b] = 0.0
            elif "Ѐ" <= ch <= "ӿ":
                weights[b] = _OTHER_CYRILLIC_WEIGHT
                cls[b] = ord("h")
            else:
                weights[b] = _BAD_WEIGHT
        tables[canon] = (weights, bytes(cls))
    return tables


_TABLES = _build_tables()


def score_single_byte(data):
    # type: (bytes) -> List[Tuple[str, float]]
    """Average plausibility per high byte for each candidate, best first."""
    counts = Counter(data)
    n_high = sum(c for b, c in counts.items() if b >= 0x80)
    if not n_high:
        return [("windows-1251", 0.0)]
    results = []
    for canon in _SINGLE_BYTE:
        weights, cls_table = _TABLES[canon]
        total = sum(weights[b] * c for b, c in counts.items() if b >= 0x80)
        cls = data.translate(cls_table)
        if canon == "windows-1252":
            total += _LATIN_NEIGHBOUR * (cls.count(b"ah") + cls.count(b"ha"))
            total -= _LATIN_RUN_PENALTY * cls.count(b"hh")
        else:
            total -= _CASE_BREAK_PENALTY * cls.count(b"lu")
            total -= _MIXED_SCRIPT_PENALTY * (cls.count(b"al") + cls.count(b"au")
                                              + cls.count(b"la") + cls.count(b"ua"))
        results.append((canon, total / n_high))
    # stable sort keeps the preference order of _SINGLE_BYTE on ties
    results.sort(key=lambda item: -item[1])
    return results


def _single_byte_detection(data):
    # type: (bytes) -> Tuple[str, float]
    scores = score_single_byte(data)
    best_name, best = scores[0]
    second = scores[1][1] if len(scores) > 1 else best - 1.0
    margin = best - second
    n_high = len(data) - len(data.translate(None, _HIGH_BYTES))
    confidence = 1.0 / (1.0 + math.exp(-3.0 * margin))
    confidence *= 0.7 + 0.3 * min(1.0, n_high / 100.0)
    if best < 0:
        confidence *= 0.6
    return best_name, round(max(0.05, min(0.99, confidence)), 3)


def detect_encoding(data, eof):
    # type: (bytes, bool) -> Detection
    """Detect the encoding of a file prefix. ``eof`` tells whether ``data``
    is the whole file (then an incomplete UTF-8 sequence is an error)."""
    bom, bom_len = detect_bom(data)
    if bom is not None:
        return Detection(_BOM_ENCODING[bom], "bom", 1.0, bom, bom_len)
    wide = _detect_wide(data)
    if wide is not None:
        return Detection(wide[0], "detected", wide[1])
    if _is_ascii(data):
        return Detection("utf-8", "detected", 0.9, is_ascii=True)
    if is_valid_utf8(data, eof):
        return Detection("utf-8", "detected", 0.99)
    name, confidence = _single_byte_detection(data)
    return Detection(name, "detected", confidence)


def _is_ascii(data):
    # type: (bytes) -> bool
    try:
        data.decode("ascii")
        return True
    except UnicodeDecodeError:
        return False


def encodings_equivalent(a, b, sample_is_ascii):
    # type: (Optional[str], Optional[str], bool) -> bool
    """Whether two canonical encodings decode the checked data identically
    enough not to warn (UTF-8 family; ASCII-only data in ASCII supersets)."""
    if a == b or a is None or b is None:
        return True
    if a in UTF8_FAMILY and b in UTF8_FAMILY:
        return True
    if sample_is_ascii and a in ASCII_COMPATIBLE and b in ASCII_COMPATIBLE:
        return True
    return False


# --- decoding helpers ---------------------------------------------------------

def decode_prefix(data, codec, eof):
    # type: (bytes, str, bool) -> str
    """Decode a file prefix (without BOM) replacing invalid bytes with U+FFFD.
    An incomplete multibyte sequence at the end is dropped unless ``eof``."""
    return codecs.getincrementaldecoder(codec)("replace").decode(data, eof)


def open_text(raw, codec, skip, newline):
    # type: (io.BufferedReader, str, int, Optional[str]) -> io.TextIOWrapper
    """Text stream over a binary file positioned after ``skip`` BOM bytes.
    Decoding errors are replaced by U+FFFD."""
    raw.seek(skip)
    text = io.TextIOWrapper(raw, encoding=codec, errors="replace", newline=newline)
    try:
        text._CHUNK_SIZE = 65536  # larger decode chunks, fewer Python calls
    except AttributeError:
        pass
    return text


def cut_at_last_line_break(text):
    # type: (str) -> str
    """Drop an incomplete last line of a truncated fragment."""
    pos = max(text.rfind("\n"), text.rfind("\r"))
    return text[:pos + 1] if pos >= 0 else ""
