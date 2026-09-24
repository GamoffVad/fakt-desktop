# -*- coding: utf-8 -*-
import random
import unittest
import zlib

from fakt_worker import binary_detect, sampling

from .synthetic import TempDirTestCase

PNG = b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x02\x00\x00\x00"
PDF = b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"
ZIP = b"PK\x03\x04\x14\x00\x00\x00\x08\x00" + b"\x00" * 20 + b"word/document.xml"
BMP = b"BM" + (70).to_bytes(4, "little") + b"\x00\x00\x00\x00" + (54).to_bytes(4, "little") \
    + (40).to_bytes(4, "little") + b"\x01\x00\x00\x00" * 4
EXE = b"MZ\x90\x00\x03\x00\x00\x00\x04\x00\x00\x00\xff\xff\x00\x00" + b"\x00" * 48


def random_bytes(size, seed=1234):
    rng = random.Random(seed)
    return bytes(rng.getrandbits(8) for _ in range(size))


class SignatureTests(unittest.TestCase):

    def test_signatures(self):
        cases = {
            "png": PNG, "pdf": PDF, "zip": ZIP, "bmp": BMP, "exe": EXE,
            "jpeg": b"\xff\xd8\xff\xe0\x00\x10JFIF\x00", "gif": b"GIF89a\x01\x00\x01\x00",
            "tiff": b"II*\x00\x08\x00\x00\x00", "ole": b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1" + b"\x00" * 8,
            "gzip": b"\x1f\x8b\x08\x00" + zlib.compress(b"x"), "rar": b"Rar!\x1a\x07\x01\x00",
            "7z": b"7z\xbc\xaf\x27\x1c\x00\x04", "sqlite": b"SQLite format 3\x00\x10\x00",
            "riff": b"RIFF\x24\x00\x00\x00WAVEfmt ", "mp4": b"\x00\x00\x00\x18ftypmp42\x00\x00",
            "mp3": b"ID3\x03\x00\x00\x00\x00\x00\x21",
        }
        for kind, data in cases.items():
            self.assertEqual(binary_detect.signature_kind(data), kind, kind)

    def test_text_starting_like_signatures_is_not_binary(self):
        for text in (b"BM;BMW;Model\r\n1;2;3\r\n", b"MZ-1234;x\r\n", b"ID3;name\r\n",
                     b"RIFF;x\r\n", "Тестов Тест".encode("cp1251")):
            self.assertIsNone(binary_detect.detect_binary(text), text)

    def test_random_bytes_are_binary(self):
        found = binary_detect.detect_binary(random_bytes(4096))
        self.assertIsNotNone(found)
        self.assertEqual(found[0], "unknown_binary")

    def test_utf16_text_is_not_binary_without_control_check(self):
        data = "id;name\r\n1;Тест\r\n".encode("utf-16-le")
        self.assertIsNone(binary_detect.detect_binary(data, check_controls=False))


class SampleBinaryTests(TempDirTestCase):

    def test_sample_reports_binary(self):
        for name, data, kind in (("a.png", PNG, "png"), ("a.pdf", PDF, "pdf"),
                                 ("a.zip", ZIP, "zip"), ("r.bin", random_bytes(8192), "unknown_binary")):
            path = self.write_bytes(name, data)
            result = sampling.sample({"path": path})
            self.assertTrue(result["is_binary"], name)
            self.assertEqual(result["binary_kind"], kind)
            self.assertTrue(result["binary_reason"])
            self.assertEqual(result["lines"], [])
            self.assertIsNone(result["encoding"])
            self.assertFalse(result["is_empty"])

    def test_utf16_file_is_text(self):
        path = self.write_bytes("u16.csv", "id;ФИО\r\n1;Тестов Тест\r\n".encode("utf-16-le"))
        result = sampling.sample({"path": path})
        self.assertFalse(result["is_binary"])
        self.assertEqual(result["encoding"], "utf-16-le")
        self.assertEqual(result["lines"], ["id;ФИО", "1;Тестов Тест"])


if __name__ == "__main__":
    unittest.main()
