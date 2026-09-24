# -*- coding: utf-8 -*-
"""The ``sample`` command: first physical lines of a file (PROTOCOL.md §3.2)."""
import re
from typing import Any, Dict, List, Optional

from . import binary_detect, encoding_detect
from .protocol import (arg_int, arg_path, arg_str, map_os_error, open_binary, stat_file)

_LINE_BREAK = re.compile("\r\n|\r|\n")


def read_prefix(path, max_bytes):
    """(bytes, eof): at most ``max_bytes`` from the start of the file; ``eof``
    tells whether the whole file fit."""
    fh = open_binary(path)
    try:
        data = fh.read(max_bytes)
        eof = len(data) < max_bytes or not fh.read(1)
    except OSError as exc:
        raise map_os_error(exc, reading=True)
    finally:
        fh.close()
    return data, eof


def split_lines(text, max_lines, eof):
    """Split decoded text on \\r\\n, \\n, \\r.

    Returns (lines, truncated, truncated_line_index)."""
    lines = []  # type: List[str]
    pos = 0
    for match in _LINE_BREAK.finditer(text):
        if len(lines) >= max_lines:
            break
        lines.append(text[pos:match.start()])
        pos = match.end()
    truncated = False
    truncated_index = None  # type: Optional[int]
    if len(lines) < max_lines:
        rest = text[pos:]
        if rest:
            lines.append(rest)
            if not eof:
                truncated = True
                truncated_index = len(lines) - 1
        elif not eof:
            truncated = True  # the byte limit ended exactly on a line break
    return lines, truncated, truncated_index


def line_terminator(text, eof):
    # type: (str, bool) -> Optional[str]
    crlf = text.count("\r\n")
    cr = text.count("\r") - crlf
    lf = text.count("\n") - crlf
    if not eof and text.endswith("\r"):
        cr -= 1  # may be the first half of \r\n cut by the byte limit
    kinds = [name for name, count in (("crlf", crlf), ("lf", lf), ("cr", cr)) if count > 0]
    if not kinds:
        return None
    return kinds[0] if len(kinds) == 1 else "mixed"


def sample(args):
    # type: (Dict[str, Any]) -> Dict[str, Any]
    path = arg_path(args)
    max_lines = arg_int(args, "max_lines", 5, 1, 1000)
    max_bytes = arg_int(args, "max_bytes", 65536, 1024, 16777216)
    enc_arg = arg_str(args, "encoding", None, allow_none=True)
    override = encoding_detect.canonical_encoding(enc_arg) if enc_arg else None

    st = stat_file(path)
    data, eof = read_prefix(path, max_bytes)
    bom, bom_len = encoding_detect.detect_bom(data)
    result = {
        "file_size": st.st_size,
        "is_empty": False,
        "is_binary": False,
        "binary_kind": None,
        "binary_reason": None,
        "encoding": None,
        "encoding_source": None,
        "encoding_confidence": 0.0,
        "bom": bom,
        "lines": [],
        "line_count": 0,
        "bytes_read": len(data),
        "eof_reached": eof,
        "truncated": False,
        "truncated_line_index": None,
        "fewer_lines": False,
        "line_terminator": None,
        "replacement_count": 0,
    }  # type: Dict[str, Any]

    if not data or (eof and bom is not None and len(data) == bom_len):
        result["is_empty"] = True
        result["fewer_lines"] = eof
        return result

    signature = binary_detect.signature_kind(data)
    if signature is not None:
        found = binary_detect.detect_binary(data, check_controls=False)
        assert found is not None
        result.update(is_binary=True, binary_kind=found[0], binary_reason=found[1])
        return result

    if override is not None:
        encoding, codec, skip = encoding_detect.resolve_for_file(override, data[:4])
        source, confidence = "override", 1.0
    else:
        detection = encoding_detect.detect_encoding(data, eof)
        encoding, codec, skip = encoding_detect.resolve_for_file(detection.encoding, data[:4])
        source, confidence = detection.source, detection.confidence

    if encoding not in encoding_detect.WIDE_ENCODINGS:
        found = binary_detect.detect_binary(data, check_controls=True)
        if found is not None:
            result.update(is_binary=True, binary_kind=found[0], binary_reason=found[1])
            return result

    text = encoding_detect.decode_prefix(data[skip:], codec, eof)
    lines, truncated, truncated_index = split_lines(text, max_lines, eof)
    result.update(
        encoding=encoding,
        encoding_source=source,
        encoding_confidence=confidence,
        lines=lines,
        line_count=len(lines),
        truncated=truncated,
        truncated_line_index=truncated_index,
        fewer_lines=eof and len(lines) < max_lines,
        line_terminator=line_terminator(text, eof),
        replacement_count=text.count("�"),
    )
    return result
