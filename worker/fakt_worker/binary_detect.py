# -*- coding: utf-8 -*-
"""Binary file detection: magic signatures (PROTOCOL.md §3.2 binary_kind)
and the share of control bytes. Short signatures (BM, MZ, ID3, MP3 frame
sync) are confirmed by further header bytes so text files that merely
start with the same letters are not reported as binary."""
from typing import Optional, Tuple

_NAMES = {
    "pdf": "PDF", "zip": "ZIP (в том числе DOCX/XLSX)", "png": "PNG", "jpeg": "JPEG",
    "gif": "GIF", "bmp": "BMP", "tiff": "TIFF", "ole": "OLE (DOC/XLS)", "exe": "EXE/DLL",
    "gzip": "GZIP", "rar": "RAR", "7z": "7-Zip", "sqlite": "SQLite", "riff": "RIFF (WAV/AVI)",
    "mp4": "MP4/MOV", "mp3": "MP3",
}

# (prefix, kind) checked with startswith
_SIMPLE = (
    (b"%PDF-", "pdf"),
    (b"PK\x03\x04", "zip"), (b"PK\x05\x06", "zip"), (b"PK\x07\x08", "zip"),
    (b"\x89PNG\r\n\x1a\n", "png"),
    (b"\xff\xd8\xff", "jpeg"),
    (b"GIF87a", "gif"), (b"GIF89a", "gif"),
    (b"II*\x00", "tiff"), (b"MM\x00*", "tiff"),
    (b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1", "ole"),
    (b"\x1f\x8b\x08", "gzip"),
    (b"Rar!\x1a\x07", "rar"),
    (b"7z\xbc\xaf\x27\x1c", "7z"),
    (b"SQLite format 3\x00", "sqlite"),
)

_BMP_DIB_SIZES = (12, 40, 52, 56, 64, 108, 124)

# Bytes that do not occur in text: C0 controls except TAB, LF, VT, FF, CR,
# SUB (0x1A, DOS end-of-file) and ESC, plus DEL.
_CONTROL = bytes([b for b in range(0x20) if b not in (0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x1A, 0x1B)]
                 + [0x7F])
CONTROL_RATIO_LIMIT = 0.05


def _le32(data, offset):
    # type: (bytes, int) -> int
    return int.from_bytes(data[offset:offset + 4], "little")


def signature_kind(data):
    # type: (bytes) -> Optional[str]
    """Kind of a known binary format by its signature, or None."""
    for prefix, kind in _SIMPLE:
        if data.startswith(prefix):
            return kind
    if data.startswith(b"BM") and len(data) >= 18 and data[6:10] == b"\x00\x00\x00\x00" \
            and _le32(data, 14) in _BMP_DIB_SIZES:
        return "bmp"
    if data.startswith(b"MZ") and b"\x00" in data[2:64]:
        return "exe"
    if data.startswith(b"RIFF") and len(data) >= 12 and data[8:12].isalnum():
        return "riff"
    if len(data) >= 12 and data[4:8] == b"ftyp":
        return "mp4"
    if data.startswith(b"ID3") and len(data) >= 10 and data[3] in (2, 3, 4) and data[4] == 0:
        return "mp3"
    if len(data) >= 4 and data[0] == 0xFF and data[1] in (0xFB, 0xF3, 0xF2) \
            and b"\x00" in data[:4096]:
        return "mp3"
    return None


def control_ratio(data):
    # type: (bytes) -> float
    if not data:
        return 0.0
    return float(len(data) - len(data.translate(None, _CONTROL))) / len(data)


def detect_binary(data, check_controls=True):
    # type: (bytes, bool) -> Optional[Tuple[str, str]]
    """(binary_kind, Russian reason) if ``data`` looks binary, else None.
    ``check_controls`` must be False for UTF-16/32 text (zero bytes are
    normal there)."""
    kind = signature_kind(data)
    if kind is not None:
        return kind, "Обнаружена сигнатура двоичного формата: %s" % _NAMES[kind]
    if check_controls:
        ratio = control_ratio(data)
        if ratio >= CONTROL_RATIO_LIMIT or (b"\x00\x00\x00\x00" in data and ratio >= 0.01):
            return "unknown_binary", (
                "Высокая доля управляющих байтов (%.1f%%): файл похож на двоичный" % (ratio * 100))
    return None
