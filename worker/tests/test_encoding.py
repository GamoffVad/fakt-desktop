# -*- coding: utf-8 -*-
import codecs
import unittest

from fakt_worker import encoding_detect as ed
from fakt_worker import sampling
from fakt_worker.protocol import WorkerError

from .synthetic import RUSSIAN_TEXT, TempDirTestCase, csv_text, people


class BomTests(unittest.TestCase):

    def test_boms(self):
        cases = [
            (codecs.BOM_UTF8 + "Тест".encode("utf-8"), "utf-8-sig", "utf-8"),
            (codecs.BOM_UTF16_LE + "Тест".encode("utf-16-le"), "utf-16-le", "utf-16-le"),
            (codecs.BOM_UTF16_BE + "Тест".encode("utf-16-be"), "utf-16-be", "utf-16-be"),
            (codecs.BOM_UTF32_LE + "Тест".encode("utf-32-le"), "utf-32-le", "utf-32-le"),
            (codecs.BOM_UTF32_BE + "Тест".encode("utf-32-be"), "utf-32-be", "utf-32-be"),
        ]
        for data, encoding, bom in cases:
            detection = ed.detect_encoding(data, True)
            self.assertEqual((detection.encoding, detection.source, detection.bom),
                             (encoding, "bom", bom))
            self.assertEqual(detection.confidence, 1.0)

    def test_resolve_skips_own_bom_only(self):
        head = codecs.BOM_UTF16_BE + b"\x00a"
        self.assertEqual(ed.resolve_for_file("utf-16", head), ("utf-16-be", "utf-16-be", 2))
        self.assertEqual(ed.resolve_for_file("utf-16", b"a\x00"), ("utf-16-le", "utf-16-le", 0))
        self.assertEqual(ed.resolve_for_file("utf-8", codecs.BOM_UTF8 + b"a")[2], 3)
        self.assertEqual(ed.resolve_for_file("windows-1251", codecs.BOM_UTF8 + b"a")[2], 0)


class DetectionTests(unittest.TestCase):

    def assertDetected(self, data, expected, eof=True):
        detection = ed.detect_encoding(data, eof)
        self.assertEqual(detection.encoding, expected)
        self.assertEqual(detection.source, "detected")
        return detection

    def test_utf16_without_bom(self):
        ascii_text = "id;name;code\r\n1;abc;007\r\n"
        cyr = csv_text(people(5), header=["ФИО", "Дата", "Телефон", "Код"])
        self.assertDetected(ascii_text.encode("utf-16-le"), "utf-16-le")
        self.assertDetected(cyr.encode("utf-16-le"), "utf-16-le")
        self.assertDetected(ascii_text.encode("utf-16-be"), "utf-16-be")
        self.assertDetected(cyr.encode("utf-16-be"), "utf-16-be")
        self.assertDetected(cyr.encode("utf-32-le"), "utf-32-le")

    def test_utf8_and_ascii(self):
        detection = self.assertDetected(b"plain;ascii\r\n1;2\r\n", "utf-8")
        self.assertTrue(detection.is_ascii)
        self.assertDetected(RUSSIAN_TEXT.encode("utf-8"), "utf-8")

    def test_utf8_cut_at_sample_boundary(self):
        data = RUSSIAN_TEXT.encode("utf-8")
        cut = data[:31]  # 31 is odd: inside a two-byte Cyrillic character
        with self.assertRaises(UnicodeDecodeError):
            cut.decode("utf-8")
        self.assertDetected(cut, "utf-8", eof=False)

    def test_single_byte_russian(self):
        text = RUSSIAN_TEXT + csv_text(people(20))
        for codec, expected in (("cp1251", "windows-1251"), ("cp866", "cp866"),
                                ("koi8_r", "koi8-r")):
            self.assertDetected(text.encode(codec), expected)
            for line in RUSSIAN_TEXT.splitlines():  # a single short line is enough too
                self.assertDetected(line.encode(codec), expected)

    def test_windows_1252_latin(self):
        text = "Müller;café;Straße;crème brûlée;Ålesund\r\nGarçon;naïve;façade;Öl;Übung\r\n"
        self.assertDetected(text.encode("cp1252"), "windows-1252")

    def test_names_and_synonyms(self):
        self.assertEqual(ed.canonical_encoding("UTF8"), "utf-8")
        self.assertEqual(ed.canonical_encoding("cp1251"), "windows-1251")
        self.assertEqual(ed.canonical_encoding("1251"), "windows-1251")
        self.assertEqual(ed.canonical_encoding("win-1251"), "windows-1251")
        self.assertEqual(ed.canonical_encoding("latin-1"), "iso-8859-1")
        self.assertEqual(ed.canonical_encoding("cp1252"), "windows-1252")
        self.assertEqual(ed.canonical_encoding("ibm866"), "cp866")
        self.assertEqual(ed.canonical_encoding("utf-16"), "utf-16")
        for name in ed.CANONICAL:
            self.assertEqual(ed.canonical_encoding(name), name)
        with self.assertRaises(WorkerError) as ctx:
            ed.canonical_encoding("ebcdic")
        self.assertEqual(ctx.exception.code, "unsupported_encoding")


class SampleEncodingTests(TempDirTestCase):

    def test_sample_decodes_each_encoding(self):
        text = csv_text(people(6), header=["ФИО", "Дата", "Телефон", "Код"])
        expected_lines = text.split("\r\n")[:5]
        cases = [("utf-8", False, "utf-8"), ("utf-8", True, "utf-8-sig"),
                 ("utf-16-le", True, "utf-16-le"), ("utf-16-le", False, "utf-16-le"),
                 ("utf-16-be", True, "utf-16-be"), ("cp1251", False, "windows-1251"),
                 ("cp866", False, "cp866"), ("koi8_r", False, "koi8-r")]
        for codec, bom, expected in cases:
            if bom:
                path = self.write_text("f.csv", text, encoding=codec, bom=True)
            else:
                path = self.write_bytes("f.csv", text.encode(codec))
            result = sampling.sample({"path": path})
            self.assertEqual(result["encoding"], expected, codec)
            self.assertEqual(result["lines"], expected_lines, codec)
            self.assertEqual(result["replacement_count"], 0)
            self.assertEqual(result["line_terminator"], "crlf")

    def test_override_and_replacements(self):
        path = self.write_bytes("bad.txt", "Тест".encode("cp1251") + b"\r\n")
        result = sampling.sample({"path": path, "encoding": "utf8"})
        self.assertEqual((result["encoding"], result["encoding_source"]), ("utf-8", "override"))
        self.assertEqual(result["encoding_confidence"], 1.0)
        self.assertEqual(result["replacement_count"], 4)
        with self.assertRaises(WorkerError) as ctx:
            sampling.sample({"path": path, "encoding": "klingon"})
        self.assertEqual(ctx.exception.code, "unsupported_encoding")

    def test_ascii_file_reports_utf8(self):
        path = self.write_bytes("a.csv", b"a,b\n1,2\n")
        result = sampling.sample({"path": path})
        self.assertEqual((result["encoding"], result["encoding_source"]), ("utf-8", "detected"))


if __name__ == "__main__":
    unittest.main()
