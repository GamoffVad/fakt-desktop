# -*- coding: utf-8 -*-
import unittest

from fakt_worker import validation

from .synthetic import (HEADER, TempDirTestCase, csv_text, delimited_structure, people, person,
                        read_all)


def codes(items):
    return [item["code"] for item in items]


class DelimitedTests(TempDirTestCase):

    def validate(self, path, structure, **args):
        args.update(path=path, structure=structure)
        return validation.validate(args)

    def test_delimiters_and_quoted_delimiters(self):
        for delimiter in (",", ";", "\t", "|"):
            rows = people(30)
            rows[3][0] = "Тестов%s Тест" % delimiter  # delimiter inside a quoted field
            rows[4][0] = 'ООО "Пример%s Тест"' % delimiter  # doubled quotes
            path = self.write_text("d.csv", csv_text(rows, delimiter=delimiter, header=HEADER))
            structure = delimited_structure(list(HEADER), delimiter=delimiter)
            result = self.validate(path, structure)
            self.assertTrue(result["ok"], (delimiter, result["errors"]))
            self.assertEqual(result["records_checked"], 30)
            self.assertEqual(result["columns"], HEADER)
            self.assertEqual(result["preview"]["rows"][3]["values"], rows[3])
            records, _ = read_all(path, result["effective_structure"])
            self.assertEqual([r["values"] for r in records], rows, delimiter)
            self.assertEqual([r["line"] for r in records], list(range(2, 32)))
            self.assertEqual([r["ordinal"] for r in records], list(range(1, 31)))

    def test_multiline_fields_line_numbers(self):
        text = ("ФИО;Примечание;Телефон;Код\r\n"
                'Тестов Тест;"строка1\r\nстрока2";+7 000 000-00-00;001\r\n'
                "Примеров Пример;одна;x;002\r\n"
                "\r\n"
                'Образцов Образец;"a\nb\nc";y;003\r\n'
                'Проба;"\r\n";z;004\r\n'
                'Демо;"x\ry";w;005\r\n'
                "Последний;;;006")
        path = self.write_text("m.csv", text)
        structure = delimited_structure(["ФИО", "Примечание", "Телефон", "Код"])
        records, chunks = read_all(path, structure, chunk_size=2)
        spans = [(r["line"], r["end_line"]) for r in records]
        self.assertEqual(spans, [(2, 3), (4, 4), (6, 8), (9, 10), (11, 12), (13, 13)])
        self.assertEqual(records[0]["values"][1], "строка1\r\nстрока2")
        self.assertEqual(records[2]["values"][1], "a\nb\nc")
        self.assertEqual(records[3]["values"][1], "\r\n")
        self.assertEqual(records[5]["values"], ["Последний", "", "", "006"])
        self.assertEqual(chunks[-1]["stats"]["blank_lines"], 1)
        result = self.validate(path, structure)
        self.assertEqual([row["line"] for row in result["preview"]["rows"]], [2, 4, 6, 9, 11, 13])
        self.assertIn("blank_lines_present", codes(result["warnings"]))

    def test_no_header(self):
        rows = people(10)
        path = self.write_text("n.csv", csv_text(rows))
        structure = delimited_structure(list(HEADER), has_header=False)
        records, _ = read_all(path, structure)
        self.assertEqual([r["values"] for r in records], rows)
        self.assertEqual(records[0]["line"], 1)
        result = self.validate(path, delimited_structure(None, has_header=False))
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual(result["columns"], ["Колонка 1", "Колонка 2", "Колонка 3", "Колонка 4"])
        self.assertEqual(result["effective_structure"]["columns"], result["columns"])

    def test_header_with_duplicate_names(self):
        path = self.write_text("dup.csv", "ФИО;ФИО;Телефон\r\nа;б;в\r\n")
        result = self.validate(path, delimited_structure(None))
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual(result["columns"], ["ФИО", "ФИО (2)", "Телефон"])
        self.assertIn("duplicate_columns_renamed", codes(result["warnings"]))
        records, _ = read_all(path, result["effective_structure"])
        self.assertEqual(records[0]["values"], ["а", "б", "в"])

    def test_preamble_skip_rows(self):
        text = csv_text(people(5), header=HEADER,
                        preamble=["Выгрузка тестовых данных", "Дата: 01.01.2000", ""])
        path = self.write_text("p.csv", text)
        structure = delimited_structure(list(HEADER), skip_rows=3)
        result = self.validate(path, structure)
        self.assertTrue(result["ok"], result["errors"])
        records, _ = read_all(path, result["effective_structure"])
        self.assertEqual([r["line"] for r in records], [5, 6, 7, 8, 9])
        self.assertEqual(records[0]["values"], people(1)[0])

    def test_header_differs_same_count(self):
        path = self.write_text("h.csv", csv_text(people(3), header=HEADER))
        result = self.validate(path, delimited_structure(["A", "B", "C", "D"]))
        self.assertTrue(result["ok"], result["errors"])
        self.assertIn("header_differs", codes(result["warnings"]))
        self.assertEqual(result["columns"], HEADER)
        self.assertEqual(result["effective_structure"]["columns"], HEADER)
        same = self.validate(path, delimited_structure([" фио ", "ДАТА РОЖДЕНИЯ", "Телефон", "код"]))
        self.assertNotIn("header_differs", codes(same["warnings"]))

    def test_header_count_mismatch(self):
        path = self.write_text("h.csv", csv_text(people(3), header=HEADER))
        result = self.validate(path, delimited_structure(["ФИО", "Дата рождения", "Телефон"]))
        self.assertFalse(result["ok"])
        self.assertIn("column_count_mismatch", codes(result["errors"]))
        self.assertEqual(result["suggestions"]["columns"], HEADER)

    def test_data_has_more_fields_than_header(self):
        rows = [[person(i), "15.02.1990", "+7 000 000-00-00", "лишнее"] for i in range(10)]
        path = self.write_text("x.csv", csv_text(rows, header=["ФИО", "Дата рождения", "Телефон"]))
        result = self.validate(path, delimited_structure(["ФИО", "Дата рождения", "Телефон"]))
        self.assertFalse(result["ok"])
        self.assertIn("column_count_mismatch", codes(result["errors"]))
        self.assertIn("too_many_bad_records", codes(result["errors"]))
        self.assertEqual(result["suggestions"]["columns"],
                         ["ФИО", "Дата рождения", "Телефон", "Колонка 4"])
        self.assertIsNone(result["preview"]["rows"][0]["values"])
        self.assertEqual(result["preview"]["rows"][0]["error"]["code"], "field_count_mismatch")

    def test_bad_records_keep_positions(self):
        text = ("A;B;C;D\r\n1;2;3;4\r\n5;6\r\n7;8;9;10;11\r\n12;13;14;15\r\n")
        path = self.write_text("b.csv", text)
        records, chunks = read_all(path, delimited_structure(["A", "B", "C", "D"]))
        self.assertEqual([r["ordinal"] for r in records], [1, 2, 3, 4])
        self.assertEqual([r["line"] for r in records], [2, 3, 4, 5])
        self.assertEqual(records[1]["error"], {"code": "short_record",
                                               "message": "Ожидалось 4 поля, получено 2"})
        self.assertEqual(records[2]["error"], {"code": "field_count_mismatch",
                                               "message": "Ожидалось 4 поля, получено 5"})
        self.assertIsNone(records[1]["values"])
        self.assertIsNone(records[1]["hash"])
        self.assertEqual(records[3]["values"], ["12", "13", "14", "15"])
        self.assertEqual(chunks[-1]["stats"]["bad_records"], 2)

    def test_first_data_row_longer_than_columns(self):
        path = self.write_text("f.csv", "1;2;3;4;5\r\n6;7;8;9\r\n")
        records, _ = read_all(path, delimited_structure(["A", "B", "C", "D"], has_header=False))
        self.assertEqual(records[0]["error"]["code"], "field_count_mismatch")
        self.assertEqual(records[1]["values"], ["6", "7", "8", "9"])

    def test_blank_lines_are_not_records(self):
        text = "A;B\r\n1;2\r\n\r\n\r\n3;4\r\n   \r\n5;6\r\n\r\n"
        path = self.write_text("bl.csv", text)
        records, chunks = read_all(path, delimited_structure(["A", "B"]))
        self.assertEqual([(r["ordinal"], r["line"]) for r in records], [(1, 2), (2, 5), (3, 7)])
        self.assertEqual(chunks[-1]["stats"]["blank_lines"], 4)

    def test_leading_zeros_and_numeric_text(self):
        rows = [["007", "0012345", "1e5", "-0", "00", "1,50"]]
        path = self.write_text("z.csv", csv_text(rows, header=list("ABCDEF")))
        records, _ = read_all(path, delimited_structure(list("ABCDEF")))
        self.assertEqual(records[0]["values"], rows[0])

    def test_delimiter_not_found(self):
        path = self.write_text("c.csv", csv_text(people(10), delimiter=",", header=HEADER))
        result = self.validate(path, delimited_structure(list(HEADER), delimiter=";"))
        self.assertFalse(result["ok"])
        self.assertIn("delimiter_not_found", codes(result["errors"]))
        self.assertEqual(result["suggestions"]["delimiter"], ",")

    def test_too_many_bad_records(self):
        rows = people(10)
        for i in range(0, 10, 2):
            rows[i] = rows[i] + ["лишнее"]
        path = self.write_text("t.csv", csv_text(rows, header=HEADER))
        result = self.validate(path, delimited_structure(list(HEADER)))
        self.assertIn("too_many_bad_records", codes(result["errors"]))
        self.assertEqual(result["bad_records"], 5)
        few = people(20)
        few[5] = few[5][:2]
        path = self.write_text("t2.csv", csv_text(few, header=HEADER))
        result = self.validate(path, delimited_structure(list(HEADER)))
        self.assertTrue(result["ok"], result["errors"])
        warning = [w for w in result["warnings"] if w["code"] == "bad_records_present"][0]
        self.assertEqual(warning["ordinal"], 6)

    def test_header_repeated_mid_file(self):
        text = csv_text(people(5), header=HEADER) + csv_text(people(5, 5), header=HEADER)
        path = self.write_text("r.csv", text)
        records, chunks = read_all(path, delimited_structure(list(HEADER)))
        self.assertEqual(len(records), 11)
        self.assertEqual(records[5]["error"]["code"], "structure_changed")
        self.assertEqual(records[5]["line"], 7)
        warnings = [w for c in chunks for w in c["warnings"]]
        self.assertEqual(codes(warnings), ["structure_change_suspected"])
        self.assertEqual(warnings[0]["ordinal"], 6)

    def test_bad_streak_warning(self):
        rows = people(5) + [r + ["x"] for r in people(25, 5)]
        path = self.write_text("s.csv", csv_text(rows, header=HEADER))
        records, chunks = read_all(path, delimited_structure(list(HEADER)))
        warnings = [w for c in chunks for w in c["warnings"]]
        self.assertEqual(codes(warnings), ["structure_change_suspected"])
        self.assertEqual(warnings[0]["ordinal"], 6)

    def test_quote_errors(self):
        text = 'A;B\r\n"bad"x;1\r\n2;3\r\n"unterminated;4\r\n5;6\r\n'
        path = self.write_text("q.csv", text)
        records, _ = read_all(path, delimited_structure(["A", "B"]))
        self.assertEqual([(r["line"], r["end_line"]) for r in records], [(2, 2), (3, 3), (4, 5)])
        self.assertEqual(records[0]["error"]["code"], "decode_error")
        self.assertEqual(records[1]["values"], ["2", "3"])
        self.assertEqual(records[2]["error"]["code"], "decode_error")

    def test_windows1251_and_utf16(self):
        rows = people(8)
        text = csv_text(rows, header=HEADER)
        for name, data in (("w.csv", text.encode("cp1251")),
                           ("u.csv", b"\xff\xfe" + text.encode("utf-16-le")),
                           ("n.csv", text.encode("utf-16-le"))):
            path = self.write_bytes(name, data)
            result = self.validate(path, delimited_structure(list(HEADER)))
            self.assertTrue(result["ok"], (name, result["errors"]))
            records, _ = read_all(path, result["effective_structure"])
            self.assertEqual([r["values"] for r in records], rows, name)

    def test_encoding_differs_warning(self):
        path = self.write_bytes("w.csv", csv_text(people(20), header=HEADER).encode("cp1251"))
        result = self.validate(path, delimited_structure(list(HEADER), encoding="utf-8"))
        self.assertIn("encoding_differs", codes(result["warnings"]))
        self.assertFalse(result["ok"])  # nearly every Cyrillic byte is replaced
        self.assertIn("decode_failed", codes(result["errors"]))

    def test_quote_none_and_escape(self):
        path = self.write_text("qn.csv", 'A;B\r\n"x";"y z"\r\n')
        records, _ = read_all(path, delimited_structure(["A", "B"], quote_char=None))
        self.assertEqual(records[0]["values"], ['"x"', '"y z"'])
        path = self.write_text("e.csv", "A;B\r\na\\;b;c\r\n")
        records, _ = read_all(path, delimited_structure(["A", "B"], quote_char=None,
                                                        escape_char="\\"))
        self.assertEqual(records[0]["values"], ["a;b", "c"])

    def test_validate_fragment_cut_inside_multiline_field(self):
        rows = [[person(i), "первая строка\r\nвторая строка\r\nтретья", "x", "%03d" % i]
                for i in range(200)]
        path = self.write_text("big.csv", csv_text(rows, header=HEADER))
        for budget in (2048, 3000, 4097, 5555):
            result = self.validate(path, delimited_structure(list(HEADER)), max_bytes=budget)
            self.assertTrue(result["ok"], (budget, result["errors"]))
            self.assertEqual(result["bad_records"], 0, budget)
            self.assertFalse(result["eof_reached"])

    def test_validate_limits(self):
        path = self.write_text("l.csv", csv_text(people(100), header=HEADER))
        result = self.validate(path, delimited_structure(list(HEADER)), max_records=10)
        self.assertEqual(result["records_checked"], 10)
        self.assertFalse(result["eof_reached"])
        result = self.validate(path, delimited_structure(list(HEADER)), max_records=1000)
        self.assertEqual(result["records_checked"], 100)
        self.assertTrue(result["eof_reached"])

    def test_nul_and_invalid_bytes(self):
        data = "A;B\r\n".encode("utf-8") + b"x\x00y;\xff\xfez\r\n"
        path = self.write_bytes("nul.csv", data)
        records, chunks = read_all(path, delimited_structure(["A", "B"], encoding="utf-8"))
        self.assertEqual(records[0]["values"], ["x�y", "��z"])
        self.assertEqual(chunks[-1]["stats"]["replacement_count"], 3)

    def test_single_column(self):
        path = self.write_text("one.txt", "Значение\r\nодин\r\n\r\nдва\r\n")
        records, _ = read_all(path, delimited_structure(["Значение"], delimiter=";"))
        self.assertEqual([(r["line"], r["values"]) for r in records],
                         [(2, ["один"]), (4, ["два"])])

    def test_empty_and_header_only(self):
        path = self.write_text("h.csv", "A;B\r\n")
        result = self.validate(path, delimited_structure(["A", "B"]))
        self.assertEqual(codes(result["errors"]), ["no_records_found"])
        path = self.write_bytes("e.csv", b"")
        result = self.validate(path, delimited_structure(["A", "B"]))
        self.assertEqual(codes(result["errors"]), ["no_records_found"])
        records, chunks = read_all(path, delimited_structure(["A", "B"]))
        self.assertEqual(records, [])
        self.assertTrue(chunks[0]["eof"])


if __name__ == "__main__":
    unittest.main()
