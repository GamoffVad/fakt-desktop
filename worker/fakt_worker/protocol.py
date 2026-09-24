# -*- coding: utf-8 -*-
"""Protocol primitives: error type, error codes, argument validation and
mapping of OS errors to protocol error codes (PROTOCOL.md §2)."""
import errno
import os
import stat
from typing import Any, Dict, Optional

# --- protocol error codes (§2) ---------------------------------------------
BAD_REQUEST = "bad_request"
UNSUPPORTED_PROTOCOL_VERSION = "unsupported_protocol_version"
UNKNOWN_COMMAND = "unknown_command"
FILE_NOT_FOUND = "file_not_found"
ACCESS_DENIED = "access_denied"
FILE_LOCKED = "file_locked"
IO_ERROR = "io_error"
FILE_CHANGED = "file_changed"
INVALID_STRUCTURE = "invalid_structure"
UNSUPPORTED_FORMAT = "unsupported_format"
UNSUPPORTED_ENCODING = "unsupported_encoding"
DECODE_ERROR = "decode_error"
READER_NOT_FOUND = "reader_not_found"
RECORD_TOO_LARGE = "record_too_large"
INTERNAL_ERROR = "internal_error"

# Limit for one logical record (JSON element, CSV field, physical line).
MAX_RECORD_CHARS = 16 * 1024 * 1024

# Windows error codes (winerror attribute of OSError).
_WIN_NOT_FOUND = frozenset((2, 3, 53, 67, 123, 161))
_WIN_ACCESS_DENIED = frozenset((5,))
_WIN_LOCKED = frozenset((32, 33))

_MISSING = object()


class WorkerError(Exception):
    """Error reported to the client as ``{"ok": false, "error": {...}}``."""

    def __init__(self, code, message, details=None):
        # type: (str, str, Optional[Dict[str, Any]]) -> None
        Exception.__init__(self, code, message)
        self.code = code
        self.message = message
        self.details = dict(details) if details else {}

    def to_dict(self):
        # type: () -> Dict[str, Any]
        return {"code": self.code, "message": self.message, "details": self.details}


def _bad(name, text):
    # type: (str, str) -> WorkerError
    return WorkerError(BAD_REQUEST, "Некорректный аргумент %s: %s" % (name, text),
                       {"field": name})


def _is_int(value):
    # type: (Any) -> bool
    return isinstance(value, int) and not isinstance(value, bool)


def arg_int(args, name, default=_MISSING, min_value=None, max_value=None,
            allow_none=False):
    # type: (Dict[str, Any], str, Any, Optional[int], Optional[int], bool) -> Optional[int]
    """Integer argument with optional range check. Booleans are rejected.

    A missing value (or an explicit null for a non-nullable argument that has
    a default) yields the default."""
    value = args.get(name, _MISSING)
    if value is None:
        if allow_none:
            return None
        value = _MISSING
    if value is _MISSING:
        if default is _MISSING:
            raise _bad(name, "обязательное поле отсутствует")
        return default
    if isinstance(value, float) and value.is_integer():
        value = int(value)
    if not _is_int(value):
        raise _bad(name, "ожидалось целое число")
    if min_value is not None and value < min_value:
        raise _bad(name, "значение меньше %d" % min_value)
    if max_value is not None and value > max_value:
        raise _bad(name, "значение больше %d" % max_value)
    return value


def arg_str(args, name, default=_MISSING, allow_none=False):
    # type: (Dict[str, Any], str, Any, bool) -> Optional[str]
    value = args.get(name, _MISSING)
    if value is _MISSING:
        if default is _MISSING:
            raise _bad(name, "обязательное поле отсутствует")
        return default
    if value is None:
        if allow_none:
            return None
        raise _bad(name, "значение не может быть null")
    if not isinstance(value, str):
        raise _bad(name, "ожидалась строка")
    return value


def arg_obj(args, name):
    # type: (Dict[str, Any], str) -> Dict[str, Any]
    value = args.get(name, _MISSING)
    if value is _MISSING or value is None:
        raise _bad(name, "обязательное поле отсутствует")
    if not isinstance(value, dict):
        raise _bad(name, "ожидался объект")
    return value


def arg_path(args, name="path"):
    # type: (Dict[str, Any], str) -> str
    """Absolute file path; the ``\\\\?\\`` prefix is allowed."""
    path = arg_str(args, name)
    assert path is not None
    if not path.strip():
        raise _bad(name, "пустой путь")
    if "\x00" in path:
        raise _bad(name, "путь содержит символ NUL")
    if not os.path.isabs(path):
        raise _bad(name, "путь должен быть абсолютным")
    return path


def map_os_error(exc):
    # type: (OSError) -> WorkerError
    """Map an OSError to a protocol error (never includes file contents)."""
    winerror = getattr(exc, "winerror", None)
    err_no = getattr(exc, "errno", None)
    details = {"errno": err_no, "winerror": winerror}
    if winerror in _WIN_LOCKED:
        return WorkerError(FILE_LOCKED, "Файл заблокирован другим процессом", details)
    if isinstance(exc, FileNotFoundError) or err_no in (errno.ENOENT, errno.ENOTDIR) \
            or winerror in _WIN_NOT_FOUND:
        return WorkerError(FILE_NOT_FOUND, "Файл не найден", details)
    if isinstance(exc, IsADirectoryError) or err_no == errno.EISDIR:
        details["reason"] = "is_directory"
        return WorkerError(IO_ERROR, "Указанный путь является каталогом, а не файлом", details)
    if isinstance(exc, PermissionError) or err_no in (errno.EACCES, errno.EPERM) \
            or winerror in _WIN_ACCESS_DENIED:
        return WorkerError(ACCESS_DENIED, "Нет доступа к файлу", details)
    return WorkerError(IO_ERROR, "Ошибка ввода-вывода при чтении файла", details)


def stat_file(path):
    # type: (str) -> os.stat_result
    """``os.stat`` of a regular file with protocol error mapping."""
    try:
        st = os.stat(path)
    except OSError as exc:
        raise map_os_error(exc)
    except ValueError:
        raise _bad("path", "некорректный путь")
    if stat.S_ISDIR(st.st_mode):
        raise WorkerError(IO_ERROR, "Указанный путь является каталогом, а не файлом",
                          {"reason": "is_directory"})
    return st


def open_binary(path):
    """Open a file for binary reading with protocol error mapping."""
    try:
        return open(path, "rb")
    except OSError as exc:
        raise map_os_error(exc)
    except ValueError:
        raise _bad("path", "некорректный путь")
