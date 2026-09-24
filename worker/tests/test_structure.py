# -*- coding: utf-8 -*-
import unittest

from fakt_worker import structure, validation
from fakt_worker.protocol import WorkerError

from .synthetic import TempDirTestCase, delimited_structure


def codes(items):
    return [item["code"] for item in items]


class NormalizeTests(unittest.TestCase):

    def test_defaults_and_passthrough(self):
        norm = structure.normalize({"format": "delimited", "delimiter": ";",
                                    "columns": ["A", "B"], "confidence": 0.5, "reason": "x"})
        self.assertEqual(norm.errors, [])
        eff = norm.effective
        self.assertEqual(eff["quote_char"], '"')
        self.assertEqual((eff["has_header"], eff["header_row"], eff["skip_rows"]), (False, None, 0))
        self.assertEqual((eff["confidence"], eff["reason"]), (0.5, "x"))
        self.assertEqual(list(eff), list(structure._KEYS))

    def test_dedupe_columns(self):
        names, renamed = structure.dedupe_columns(["X", "X", " x ", None, "", "X (2)"])
        self.assertEqual(names, ["X", "X (2)", "x (3)", "Колонка 4", "Колонка 5", "X (2) (2)"])
        self.assertTrue(renamed)
        norm = structure.normalize({"format": "delimited", "delimiter": ",", "columns": ["a", "A"]})
        self.assertEqual(norm.effective["columns"], ["a", "A (2)"])
        self.assertEqual(codes(norm.warnings), ["duplicate_columns_renamed"])

    def test_invalid_delimited_parameters(self):
        bad = [
            {"delimiter": ";;"}, {"delimiter": "\n"}, {"delimiter": None},
            {"delimiter": '"'}, {"delimiter": ";", "quote_char": "ab"},
            {"delimiter": ";", "escape_char": ";"}, {"delimiter": ";", "skip_rows": -1},
            {"delimiter": ";", "has_header": "yes"},
            {"delimiter": ";", "has_header": True, "skip_rows": 2, "header_row": 0},
            {"delimiter": ";", "columns": "A,B"},
        ]
        for extra in bad:
            spec = {"format": "delimited"}
            spec.update(extra)
            norm = structure.normalize(spec)
            self.assertEqual(codes(norm.errors)[:1], ["invalid_parameter"], extra)
            self.assertIsNone(norm.effective)

    def test_tab_and_quote_rules(self):
        norm = structure.normalize({"format": "delimited", "delimiter": "\\t", "quote_char": None,
                                    "escape_char": '"', "columns": ["A"]})
        self.assertEqual(norm.effective["delimiter"], "\t")
        self.assertIsNone(norm.effective["quote_char"])
        norm = structure.normalize({"format": "delimited", "delimiter": ",", "escape_char": '"',
                                    "columns": ["A"]})
        self.assertIsNone(norm.effective["escape_char"])  # doubling is always supported

    def test_header_row(self):
        norm = structure.normalize({"format": "delimited", "delimiter": ";", "has_header": True,
                                    "skip_rows": 3, "header_row": None, "columns": ["A"]})
        self.assertEqual(norm.effective["header_row"], 3)
        norm = structure.normalize({"format": "delimited", "delimiter": ";", "has_header": False,
                                    "header_row": 0, "columns": ["A"]})
        self.assertIsNone(norm.effective["header_row"])

    def test_fixed_widths(self):
        ok = structure.normalize({"format": "fixed_width", "fixed_widths": [3, 5]})
        self.assertEqual(ok.effective["columns"], ["Колонка 1", "Колонка 2"])
        for widths, columns in (([3, 0], None), ([3, -1], None), ("3,4", None), ([3, 4], ["A"]),
                                ([10 ** 6, 10], None), (None, None)):
            norm = structure.normalize({"format": "fixed_width", "fixed_widths": widths,
                                        "columns": columns})
            self.assertEqual(codes(norm.errors), ["widths_out_of_range"], widths)

    def test_xml_path(self):
        good = structure.normalize({"format": "xml", "xml_record_path": "/r/ns:item",
                                    "xml_namespaces": {"ns": "urn:x"}})
        self.assertEqual(good.errors, [])
        absolute, segments = structure.parse_xml_path("/r/ns:item", {"ns": "urn:x"})
        self.assertTrue(absolute)
        self.assertEqual(segments, [(None, "r"), ("urn:x", "item")])
        for path in ("", "a//b", "p:item", "a/*", None):
            norm = structure.normalize({"format": "xml", "xml_record_path": path})
            self.assertEqual(codes(norm.errors), ["invalid_parameter"], path)

    def test_protocol_errors_for_reader(self):
        cases = [({"format": "excel"}, "unsupported_format"),
                 ({"format": "jsonl", "json_record_path": "$.items"}, "unsupported_format"),
                 ({"format": "jsonl", "encoding": "ebcdic"}, "unsupported_encoding"),
                 ({"format": "delimited", "delimiter": ";;"}, "invalid_structure"),
                 ({"format": "delimited", "delimiter": ";"}, "invalid_structure")]
        for spec, code in cases:
            with self.assertRaises(WorkerError) as ctx:
                structure.for_reader(spec)
            self.assertEqual(ctx.exception.code, code, spec)

    def test_non_text_formats_ignore_header(self):
        norm = structure.normalize({"format": "jsonl", "has_header": True, "skip_rows": 2})
        self.assertEqual((norm.effective["has_header"], norm.effective["skip_rows"]), (False, 2))
        norm = structure.normalize({"format": "json_array", "skip_rows": 2})
        self.assertEqual(norm.effective["skip_rows"], 0)


class ValidateParameterTests(TempDirTestCase):

    def test_invalid_structure_is_result_not_protocol_error(self):
        path = self.write_text("a.csv", "A;B\n1;2\n")
        result = validation.validate({"path": path, "structure": {"format": "excel"}})
        self.assertFalse(result["ok"])
        self.assertEqual(codes(result["errors"]), ["invalid_parameter"])
        self.assertIsNone(result["effective_structure"])
        self.assertEqual(result["records_checked"], 0)

    def test_missing_file_is_protocol_error(self):
        with self.assertRaises(WorkerError) as ctx:
            validation.validate({"path": self.path("none.csv"),
                                 "structure": delimited_structure(["A"])})
        self.assertEqual(ctx.exception.code, "file_not_found")


if __name__ == "__main__":
    unittest.main()
