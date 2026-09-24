# -*- coding: utf-8 -*-
"""End-to-end test of the worker process over stdin/stdout."""
import json
import os
import subprocess
import sys
import threading
import unittest

from .synthetic import HEADER, TempDirTestCase, csv_text, delimited_structure, people

MAIN = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                    "fakt_worker_main.py")


class WorkerProcess(object):

    def __init__(self):
        self.proc = subprocess.Popen([sys.executable, "-I", "-X", "utf8", "-u", MAIN],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE)
        self.stderr = []
        self._drain = threading.Thread(target=self._read_stderr)
        self._drain.daemon = True
        self._drain.start()
        self.lines = []
        self._next_id = 0

    def _read_stderr(self):
        for line in iter(self.proc.stderr.readline, b""):
            self.stderr.append(line)

    def send_raw(self, data):
        self.proc.stdin.write(data)
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        self.lines.append(line)
        return json.loads(line.decode("utf-8"))

    def call(self, cmd, **args):
        self._next_id += 1
        request = {"v": 1, "id": str(self._next_id), "cmd": cmd, "args": args}
        response = self.send_raw(json.dumps(request, ensure_ascii=False).encode("utf-8") + b"\n")
        assert response["id"] == str(self._next_id), response
        return response

    def finish(self, timeout=30):
        try:
            self.proc.stdin.close()
        except OSError:
            pass
        code = self.proc.wait(timeout)
        rest = self.proc.stdout.read()
        self.proc.stdout.close()
        self._drain.join(timeout)
        self.proc.stderr.close()
        return code, rest


class ProcessTests(TempDirTestCase):

    def setUp(self):
        TempDirTestCase.setUp(self)
        self.worker = WorkerProcess()

    def tearDown(self):
        if self.worker.proc.poll() is None:
            self.worker.proc.kill()
            self.worker.finish()
        TempDirTestCase.tearDown(self)

    def test_full_session(self):
        worker = self.worker
        rows = people(50)
        rows[5][0] = "Тестов\r\nТест"  # a multiline field
        path = self.write_text("данные.csv", csv_text(rows, header=HEADER))

        hello = worker.call("hello")
        self.assertTrue(hello["ok"])
        self.assertEqual(hello["result"]["protocol_version"], 1)
        self.assertEqual(hello["result"]["worker_version"], "1.0.0")
        if sys.prefix == sys.base_prefix:
            self.assertEqual(hello["result"]["pid"], worker.proc.pid)
        else:  # a venv python.exe on Windows is a redirector that starts the real interpreter
            self.assertGreater(hello["result"]["pid"], 0)

        sample = worker.call("sample", path=path)["result"]
        self.assertEqual(sample["lines"][0], ";".join(HEADER))
        self.assertEqual(sample["encoding"], "utf-8")

        validated = worker.call("validate", path=path,
                                structure=delimited_structure(list(HEADER)))["result"]
        self.assertTrue(validated["ok"], validated["errors"])
        st = os.stat(path)
        opened = worker.call("open_reader", path=path, structure=validated["effective_structure"],
                             chunk_size=7, expected_size=st.st_size,
                             expected_mtime_ns=st.st_mtime_ns)
        reader_id = opened["result"]["reader_id"]
        records = []
        for _ in range(100):
            chunk = worker.call("read_chunk", reader_id=reader_id)["result"]
            records += chunk["records"]
            if chunk["eof"]:
                break
        self.assertEqual([r["ordinal"] for r in records], list(range(1, 51)))
        self.assertEqual([r["values"] for r in records], rows)
        self.assertEqual((records[5]["line"], records[5]["end_line"]), (7, 8))
        self.assertEqual(records[6]["line"], 9)
        self.assertEqual(worker.call("close_reader", reader_id=reader_id)["result"], {})
        missing = worker.call("read_chunk", reader_id=reader_id)
        self.assertEqual(missing["error"]["code"], "reader_not_found")

        self.assertEqual(worker.call("shutdown"), {"v": 1, "id": str(worker._next_id), "ok": True,
                                                   "result": {}})
        code, rest = worker.finish()
        self.assertEqual(code, 0)
        self.assertEqual(rest, b"")
        for line in worker.lines:  # stdout carries protocol lines only
            self.assertTrue(line.endswith(b"\n"))
            self.assertEqual(json.loads(line.decode("utf-8"))["v"], 1)
        self.assertNotIn("Тестов".encode("utf-8"), b"".join(worker.stderr))

    def test_malformed_requests(self):
        worker = self.worker
        bad_json = worker.send_raw(b"{not json\n")
        self.assertEqual((bad_json["id"], bad_json["ok"], bad_json["error"]["code"]),
                         (None, False, "bad_request"))
        self.assertEqual(worker.send_raw(b"[1, 2]\n")["error"]["code"], "bad_request")
        self.assertEqual(worker.send_raw(b'{"v": 1, "id": 5, "cmd": "hello"}\n')["id"], None)
        self.assertEqual(worker.send_raw(b"\xff\xfe\n")["error"]["code"], "bad_request")
        unknown = worker.call("no_such_command")
        self.assertEqual(unknown["error"]["code"], "unknown_command")
        self.assertEqual(unknown["id"], "1")
        version = worker.send_raw(b'{"v": 2, "id": "v2", "cmd": "hello", "args": {}}\n')
        self.assertEqual((version["id"], version["error"]["code"]),
                         ("v2", "unsupported_protocol_version"))
        args = worker.send_raw(b'{"v": 1, "id": "a", "cmd": "sample", "args": []}\n')
        self.assertEqual(args["error"]["code"], "bad_request")
        missing = worker.call("sample", path=self.path("нет.csv"))
        self.assertEqual(missing["error"]["code"], "file_not_found")
        too_long = worker.send_raw(b'{"v": 1, "id": "x", "cmd": "hello", "args": {"pad": "'
                                   + b"x" * (1024 * 1024) + b'"}}\n')
        self.assertEqual((too_long["id"], too_long["error"]["code"]), (None, "bad_request"))
        self.assertTrue(worker.call("hello")["ok"])  # still alive
        code, _ = worker.finish()  # closing stdin ends the worker
        self.assertEqual(code, 0)


if __name__ == "__main__":
    unittest.main()
