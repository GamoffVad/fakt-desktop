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


def map_os_error(exc, reading=False):
    # type: (OSError, bool) -> WorkerError
    """Map an OSError to a protocol error (never includes file contents).

    ``reading=True`` means the file was already opened successfully: a
    permission error while reading is then a byte-range lock held by another
    process (ERROR_LOCK_VIOLATION, reported by the C runtime as EACCES)."""
    winerror = getattr(exc, "winerror", None)
    err_no = getattr(exc, "errno", None)
    details = {"errno": err_no, "winerror": winerror}
    if winerror in _WIN_LOCKED or (reading and (isinstance(exc, PermissionError)
                                                or err_no == errno.EACCES)):
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


def format_traceback(exc):
    # type: (BaseException) -> str
    """Exception type and stack frames WITHOUT the exception message: the
    message of a library exception might quote file contents, and stderr
    must not contain record values."""
    import traceback
    frames = traceback.format_list(traceback.extract_tb(exc.__traceback__))
    return "%s (message omitted)\n%s" % (type(exc).__name__, "".join(frames).rstrip())


try:  # Windows: open through CreateFileW to keep the Windows error code
    import ctypes  # type: ignore
    import msvcrt  # type: ignore
    from ctypes import wintypes  # type: ignore

    _kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    _CreateFileW = _kernel32.CreateFileW
    _CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                             wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    _CreateFileW.restype = wintypes.HANDLE
    _CloseHandle = _kernel32.CloseHandle
    _CloseHandle.argtypes = [wintypes.HANDLE]
    _CloseHandle.restype = wintypes.BOOL
except (ImportError, AttributeError, OSError):  # pragma: no cover - not Windows
    ctypes = None
    msvcrt = None

_GENERIC_READ = 0x80000000
_FILE_SHARE_READ_WRITE = 0x00000001 | 0x00000002  # like the C runtime's open()
_OPEN_EXISTING = 3
_FILE_FLAGS = 0x00000080 | 0x08000000  # FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN
_INVALID_HANDLE_VALUE = (1 << (8 * (ctypes.sizeof(ctypes.c_void_p) if ctypes else 8))) - 1


def _open_windows(path):
    """``open(path, "rb")`` via CreateFileW: the C runtime turns a sharing
    violation into a plain EACCES, CreateFileW keeps winerror 32.

    ``_winapi.CreateFile`` is not used: in Python 3.8 it calls the ANSI
    ``CreateFileA`` with UTF-8 bytes, so any non-ASCII (e.g. Cyrillic) file name
    is not found. ``ctypes`` calls the wide-character ``CreateFileW`` directly."""
    handle = _CreateFileW(path, _GENERIC_READ, _FILE_SHARE_READ_WRITE, None,
                          _OPEN_EXISTING, _FILE_FLAGS, None)
    if handle is None or handle == _INVALID_HANDLE_VALUE or handle == -1:
        error = ctypes.get_last_error()
        raise OSError(None, ctypes.FormatError(error).strip(), path, error)
    try:
        fd = msvcrt.open_osfhandle(handle, os.O_RDONLY | getattr(os, "O_BINARY", 0))
    except BaseException:
        _CloseHandle(handle)
        raise
    return os.fdopen(fd, "rb")


def open_binary(path):
    """Open a file for binary reading with protocol error mapping."""
    try:
        if ctypes is not None and msvcrt is not None:
            return _open_windows(path)
        return open(path, "rb")
    except OSError as exc:
        raise map_os_error(exc)
    except (ValueError, TypeError):
        raise _bad("path", "некорректный путь")
