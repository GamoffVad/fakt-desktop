# -*- coding: utf-8 -*-
import unittest

from fakt_worker import validation

from .synthetic import TempDirTestCase, read_all

WIDTHS = [25, 12, 18, 8]
COLUMNS = ["ФИО", "Дата", "Телефон", "Код"]


def fixed_line(values, widths=WIDTHS):
    return "".join(value.ljust(width)[:width] for value, width in zip(values, widths))


def fw_structure(columns=None, widths=None, has_header=False, skip_rows=0):
    return {"format": "fixed_width", "encoding": None, "has_header": has_header,
            "header_row": skip_rows if has_header else None, "skip_rows": skip_rows,
            "delimiter": None, "quote_char": None, "escape_char": None,
            "columns": list(COLUMNS) if columns is None else columns,
            "fixed_widths": list(WIDTHS) if widths is None else widths,
            "xml_record_path": None, "xml_namespaces": {}, "json_record_path": None}


ROWS = [["Тестов Тест Тестович", "15.02.1990", "+7 000 000-00-00", "0000007"],
        ["Примеров Пример", "01.01.1985", "+7 000 000-00-01", "0012345"],
        ["Образцова Проба", "", "+7 000 000-00-02", "00"]]


class FixedWidthTests(TempDirTestCase):

    def validate(self, path, structure, **args):
        args.update(path=path, structure=structure)
        return validation.validate(args)

    def test_read_with_preamble_header_blank_and_short_lines(self):
        lines = ["Отчёт о тестовых данных", fixed_line(COLUMNS)]
        lines += [fixed_line(ROWS[0]), "", fixed_line(ROWS[1]), "   ", fixed_line(ROWS[2]).rstrip(),
                  "Короткая"]
        path = self.write_text("fw.txt", "\r\n".join(lines) + "\r\n", encoding="utf-8")
        structure = fw_structure(has_header=True, skip_rows=1)
        result = self.validate(path, structure)
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual(result["columns"], COLUMNS)
        records, chunks = read_all(path, result["effective_structure"], chunk_size=2)
        self.assertEqual([r["values"] for r in records],
                         ROWS + [["Короткая", "", "", ""]])
        self.assertEqual([r["line"] for r in records], [3, 5, 7, 8])
        self.assertEqual([r["end_line"] for r in records], [3, 5, 7, 8])
        self.assertEqual(chunks[-1]["stats"]["blank_lines"], 2)

    def test_header_differs(self):
        lines = [fixed_line(["Имя", "Рождение", "Тел", "Номер"]), fixed_line(ROWS[0])]
        path = self.write_text("h.txt", "\n".join(lines) + "\n")
        result = self.validate(path, fw_structure(has_header=True))
        self.assertTrue(result["ok"], result["errors"])
        self.assertIn("header_differs", [w["code"] for w in result["warnings"]])
        self.assertEqual(result["columns"], ["Имя", "Рождение", "Тел", "Номер"])

    def test_data_beyond_last_column(self):
        lines = [fixed_line(row) + "  ХВОСТ" for row in ROWS]
        path = self.write_text("b.txt", "\n".join(lines) + "\n")
        result = self.validate(path, fw_structure())
        self.assertTrue(result["ok"], result["errors"])
        self.assertIn("data_beyond_last_column", [w["code"] for w in result["warnings"]])
        # Nothing is dropped silently: the tail stays in the last column.
        records, _ = read_all(path, result["effective_structure"])
        self.assertEqual([r["values"][-1] for r in records],
                         [row[-1].ljust(WIDTHS[-1]) + "  ХВОСТ" for row in ROWS])
        clean = self.write_text("c.txt", "\n".join(fixed_line(r) + "   " for r in ROWS) + "\n")
        result = self.validate(clean, fw_structure())
        self.assertNotIn("data_beyond_last_column", [w["code"] for w in result["warnings"]])

    def test_widths_out_of_range(self):
        path = self.write_text("w.txt", fixed_line(ROWS[0]) + "\n")
        for widths in ([25, 12, 18], [25, 0, 18, 8]):
            result = self.validate(path, fw_structure(widths=widths))
            self.assertFalse(result["ok"])
            self.assertEqual([e["code"] for e in result["errors"]], ["widths_out_of_range"])

    def test_windows1251(self):
        text = "\r\n".join(fixed_line(r) for r in ROWS) + "\r\n"
        path = self.write_bytes("w.txt", text.encode("cp1251"))
        result = self.validate(path, fw_structure())
        self.assertEqual(result["effective_structure"]["encoding"], "windows-1251")
        records, _ = read_all(path, result["effective_structure"])
        self.assertEqual([r["values"] for r in records], ROWS)


if __name__ == "__main__":
    unittest.main()
