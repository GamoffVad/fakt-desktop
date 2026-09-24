# -*- coding: utf-8 -*-
"""JSON-lines server over stdin/stdout (PROTOCOL.md §1, §2).

stdout carries protocol lines only: the real stdout descriptor is kept for
the protocol and descriptor 1 plus ``sys.stdout`` are redirected to stderr,
so a stray print or library output can never corrupt the protocol stream.
Diagnostics go to stderr through ``logging`` and never contain record
values (tracebacks are logged without exception messages)."""
import io
import json
import logging
import os
import platform
import sys
import time
from typing import Any, Callable, Dict, Optional

from . import PROTOCOL_VERSION, __version__, readers, sampling, validation
from .protocol import (BAD_REQUEST, INTERNAL_ERROR, UNKNOWN_COMMAND,
                       UNSUPPORTED_PROTOCOL_VERSION, WorkerError, format_traceback,
                       map_os_error)
from .records import clean_str

MAX_REQUEST_BYTES = 1024 * 1024
_TOO_LONG = object()

log = logging.getLogger("fakt_worker")


def _module_version(name):
    # type: (str) -> Optional[str]
    try:
        module = __import__(name)
        return getattr(module, "__version__", None)
    except Exception:  # noqa: B902 - reported as null
        log.error("cannot import %s", name)
        return None


def hello(args):
    # type: (Dict[str, Any]) -> Dict[str, Any]
    return {
        "protocol_version": PROTOCOL_VERSION,
        "worker_version": __version__,
        "python_version": platform.python_version(),
        "pandas_version": _module_version("pandas"),
        "numpy_version": _module_version("numpy"),
        "defusedxml_version": _module_version("defusedxml"),
        "platform": platform.platform(),
        "pid": os.getpid(),
    }


def _error(request_id, code, message, details=None):
    # type: (Optional[str], str, str, Optional[Dict[str, Any]]) -> Dict[str, Any]
    return {"v": PROTOCOL_VERSION, "id": request_id, "ok": False,
            "error": {"code": code, "message": message, "details": details or {}}}


class Server(object):

    def __init__(self):
        self.running = True
        self.handlers = {
            "hello": hello,
            "sample": sampling.sample,
            "validate": validation.validate,
            "open_reader": readers.open_reader,
            "read_chunk": readers.read_chunk,
            "close_reader": readers.close_reader,
            "shutdown": self.shutdown,
        }  # type: Dict[str, Callable[[Dict[str, Any]], Dict[str, Any]]]

    def shutdown(self, args):
        # type: (Dict[str, Any]) -> Dict[str, Any]
        self.running = False
        return {}

    def handle(self, line):
        # type: (Any) -> Optional[Dict[str, Any]]
        """Response for one request line (bytes); None for an empty line."""
        if line is _TOO_LONG:
            return _error(None, BAD_REQUEST, "Строка запроса длиннее 1 MiB")
        try:
            text = line.decode("utf-8")
        except UnicodeDecodeError:
            return _error(None, BAD_REQUEST, "Запрос не в кодировке UTF-8")
        text = text.lstrip("﻿").strip()
        if not text:
            return None
        try:
            request = json.loads(text)
        except (ValueError, RecursionError):
            return _error(None, BAD_REQUEST, "Запрос не является корректным JSON")
        if not isinstance(request, dict):
            return _error(None, BAD_REQUEST, "Запрос должен быть JSON-объектом")
        request_id = request.get("id")
        if not isinstance(request_id, str):
            return _error(None, BAD_REQUEST, "Поле id должно быть строкой")
        version = request.get("v")
        if version is None:
            return _error(request_id, BAD_REQUEST, "Не указана версия протокола (v)")
        if type(version) is not int or version != PROTOCOL_VERSION:
            return _error(request_id, UNSUPPORTED_PROTOCOL_VERSION,
                          "Неподдерживаемая версия протокола", {"supported": [PROTOCOL_VERSION]})
        command = request.get("cmd")
        if not isinstance(command, str):
            return _error(request_id, BAD_REQUEST, "Поле cmd должно быть строкой")
        args = request.get("args")
        if args is None:
            args = {}
        if not isinstance(args, dict):
            return _error(request_id, BAD_REQUEST, "Поле args должно быть объектом")
        handler = self.handlers.get(command)
        if handler is None:
            return _error(request_id, UNKNOWN_COMMAND, "Неизвестная команда",
                          {"command": command[:64]})
        started = time.time()
        code = "ok"
        try:
            result = handler(args)
            response = {"v": PROTOCOL_VERSION, "id": request_id, "ok": True,
                        "result": result}  # type: Dict[str, Any]
        except WorkerError as exc:
            code = exc.code
            response = _error(request_id, exc.code, exc.message, exc.details)
        except OSError as exc:
            mapped = map_os_error(exc)
            code = mapped.code
            response = _error(request_id, mapped.code, mapped.message, mapped.details)
        except MemoryError:
            code = INTERNAL_ERROR
            response = _error(request_id, INTERNAL_ERROR, "Недостаточно памяти",
                              {"exception_type": "MemoryError"})
        except Exception as exc:  # noqa: B902 - any failure becomes internal_error
            code = INTERNAL_ERROR
            log.error("command %s failed: %s", command, format_traceback(exc))
            response = _error(request_id, INTERNAL_ERROR, "Внутренняя ошибка worker",
                              {"exception_type": type(exc).__name__})
        log.info("%s -> %s (%.1f ms)", command, code, (time.time() - started) * 1000.0)
        return response


def encode_message(message):
    # type: (Dict[str, Any]) -> bytes
    text = json.dumps(message, ensure_ascii=False, separators=(",", ":"))
    try:
        data = text.encode("utf-8")
    except UnicodeEncodeError:  # lone surrogates that slipped through
        data = clean_str(text).encode("utf-8")
    return data + b"\n"


def _write_all(out, data):
    # type: (Any, bytes) -> None
    view = memoryview(data)
    while view:
        written = out.write(view)
        if written is None:
            written = 0
        view = view[written:]
    out.flush()


def _protocol_stdout():
    # type: () -> Any
    """Binary stream for protocol output; stdout itself goes to stderr."""
    out = None
    try:
        fd = os.dup(sys.__stdout__.fileno())
        out = io.FileIO(fd, "wb", closefd=True)
        try:
            os.dup2(sys.__stderr__.fileno(), sys.__stdout__.fileno())
        except (AttributeError, OSError, ValueError):
            pass
    except (AttributeError, OSError, ValueError):
        out = sys.stdout.buffer if sys.stdout is not None else None
    sys.stdout = sys.stderr
    return out


def _setup_logging():
    handler = logging.StreamHandler(sys.stderr) if sys.stderr is not None \
        else logging.NullHandler()  # type: logging.Handler
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s"))
    for name in ("fakt_worker", "py.warnings"):
        logger = logging.getLogger(name)
        logger.addHandler(handler)
        logger.setLevel(logging.INFO)
        logger.propagate = False
    logging.captureWarnings(True)


def _read_request(stdin):
    # type: (Any) -> Any
    line = stdin.readline(MAX_REQUEST_BYTES + 1)
    if not line:
        return None
    if len(line) > MAX_REQUEST_BYTES and not line.endswith(b"\n"):
        while True:
            rest = stdin.readline(65536)
            if not rest or rest.endswith(b"\n"):
                break
        return _TOO_LONG
    return line


def main(argv=None):
    # type: (Any) -> int
    _setup_logging()
    out = _protocol_stdout()
    if out is None or sys.stdin is None:
        log.error("stdin/stdout are not available")
        return 2
    stdin = sys.stdin.buffer
    server = Server()
    log.info("FAKT worker %s started (Python %s, pid %d)", __version__,
             platform.python_version(), os.getpid())
    try:
        while server.running:
            line = _read_request(stdin)
            if line is None:
                log.info("stdin closed")
                break
            response = server.handle(line)
            if response is not None:
                _write_all(out, encode_message(response))
    except (BrokenPipeError, ConnectionError, OSError) as exc:
        log.error("protocol channel failed: %s", type(exc).__name__)
        return 1
    except KeyboardInterrupt:
        return 1
    finally:
        readers.close_all()
    return 0
