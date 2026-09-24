# -*- coding: utf-8 -*-
import codecs
import os
import unittest

from fakt_worker import sampling
from fakt_worker.protocol import WorkerError

from .synthetic import TempDirTestCase, csv_text, people


class SampleTests(TempDirTestCase):

    def sample(self, path, **args):
        args["path"] = path
        return sampling.sample(args)

    def test_empty_and_bom_only(self):
        result = self.sample(self.write_bytes("empty.csv", b""))
        self.assertTrue(result["is_empty"])
        self.assertEqual((result["lines"], result["encoding"], result["file_size"]), ([], None, 0))
        result = self.sample(self.write_bytes("bom.csv", codecs.BOM_UTF8))
        self.assertTrue(result["is_empty"])
        self.assertEqual(result["bom"], "utf-8")
        self.assertIsNone(result["encoding"])

    def test_fewer_than_max_lines(self):
        path = self.write_text("few.csv", "a;b\n1;2\n3;4\n")
        result = self.sample(path)
        self.assertEqual(result["lines"], ["a;b", "1;2", "3;4"])
        self.assertEqual(result["line_count"], 3)
        self.assertTrue(result["fewer_lines"])
        self.assertTrue(result["eof_reached"])
        self.assertFalse(result["truncated"])
        self.assertEqual(result["line_terminator"], "lf")

    def test_first_five_of_many(self):
        text = csv_text(people(100))
        path = self.write_text("many.csv", text)
        result = self.sample(path)
        self.assertEqual(result["lines"], text.split("\r\n")[:5])
        self.assertFalse(result["truncated"])
        self.assertFalse(result["fewer_lines"])
        self.assertTrue(result["eof_reached"])  # ~6 KB fits into the default 64 KiB
        result = self.sample(path, max_lines=1000)
        self.assertEqual(result["line_count"], 100)
        self.assertTrue(result["fewer_lines"])

    def test_single_huge_line_is_truncated(self):
        path = self.write_bytes("long.txt", b"x" * (1024 * 1024) + b"\n")
        result = self.sample(path)
        self.assertEqual(result["line_count"], 1)
        self.assertEqual(len(result["lines"][0]), 65536)
        self.assertTrue(result["truncated"])
        self.assertEqual(result["truncated_line_index"], 0)
        self.assertFalse(result["eof_reached"])
        self.assertEqual(result["bytes_read"], 65536)
        self.assertIsNone(result["line_terminator"])

    def test_truncated_after_complete_lines(self):
        text = "".join("строка номер %d с текстом\n" % i for i in range(200))
        path = self.write_text("t.txt", text)
        result = self.sample(path, max_lines=1000, max_bytes=1024)
        self.assertTrue(result["truncated"])
        index = result["truncated_line_index"]
        self.assertEqual(index, result["line_count"] - 1)
        full = text.split("\n")
        for i, line in enumerate(result["lines"][:-1]):
            self.assertEqual(line, full[i])
        self.assertTrue(full[index].startswith(result["lines"][-1]))
        self.assertEqual(result["replacement_count"], 0)  # cut inside a 2-byte char is dropped

    def test_line_terminators(self):
        for name, text, expected in (("crlf", "a\r\nb\r\n", "crlf"), ("lf", "a\nb\n", "lf"),
                                     ("cr", "a\rb\r", "cr"), ("mixed", "a\r\nb\nc\rd", "mixed"),
                                     ("none", "abc", None)):
            result = self.sample(self.write_text(name + ".txt", text))
            self.assertEqual(result["line_terminator"], expected, name)
        result = self.sample(self.write_text("m.txt", "a\r\nb\nc\rd"))
        self.assertEqual(result["lines"], ["a", "b", "c", "d"])
        result = self.sample(self.write_text("blank.txt", "a\n\nb\n"))
        self.assertEqual(result["lines"], ["a", "", "b"])

    def test_xml_declaration_first_line(self):
        text = '<?xml version="1.0" encoding="windows-1251"?>\r\n<root>\r\n<item>Тест</item>\r\n</root>\r\n'
        result = self.sample(self.write_bytes("d.xml", text.encode("cp1251")))
        self.assertEqual(result["lines"][0], '<?xml version="1.0" encoding="windows-1251"?>')
        self.assertEqual(result["lines"][2], "<item>Тест</item>")
        self.assertEqual(result["encoding"], "windows-1251")

    def test_argument_errors(self):
        path = self.write_text("a.txt", "a\n")
        for bad in ({"path": path, "max_lines": 0}, {"path": path, "max_bytes": 10},
                    {"path": "relative.txt"}, {"path": 5}, {"path": path, "max_lines": True},
                    {}):
            with self.assertRaises(WorkerError) as ctx:
                sampling.sample(bad)
            self.assertEqual(ctx.exception.code, "bad_request", bad)

    def test_file_errors(self):
        with self.assertRaises(WorkerError) as ctx:
            self.sample(os.path.join(self.tmp, "missing.csv"))
        self.assertEqual(ctx.exception.code, "file_not_found")
        with self.assertRaises(WorkerError) as ctx:
            self.sample(self.tmp)
        self.assertEqual(ctx.exception.code, "io_error")

    def test_long_path_prefix(self):
        path = self.write_text("p.txt", "a\n")
        if os.name == "nt":
            result = self.sample("\\\\?\\" + os.path.abspath(path))
            self.assertEqual(result["lines"], ["a"])


if __name__ == "__main__":
    unittest.main()
