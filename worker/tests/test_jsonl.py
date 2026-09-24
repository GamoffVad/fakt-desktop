# -*- coding: utf-8 -*-
import json
import unittest

from fakt_worker import validation

from .synthetic import TempDirTestCase, json_structure, read_all


class JsonlTests(TempDirTestCase):

    def validate(self, path, structure, **args):
        args.update(path=path, structure=structure)
        return validation.validate(args)

    def test_blank_invalid_and_non_object_lines(self):
        lines = [
            '{"ФИО": "Тестов Тест", "Код": "007", "Сумма": 1.50, "Кол": 12}',
            "",
            "   ",
            '{"ФИО": "Примеров Пример", broken',
            "[1, 2, 3]",
            '"строка"',
            '{"ФИО": "Образцов", "Флаг": true, "Нет": false, "Пусто": null}',
        ]
        path = self.write_text("a.jsonl", "\r\n".join(lines) + "\r\n")
        records, chunks = read_all(path, json_structure("jsonl"))
        self.assertEqual([(r["ordinal"], r["line"], r["end_line"]) for r in records],
                         [(1, 1, 1), (2, 4, 4), (3, 5, 5), (4, 6, 6), (5, 7, 7)])
        self.assertEqual(records[0]["_row"], {"ФИО": "Тестов Тест", "Код": "007", "Сумма": "1.50",
                                              "Кол": "12"})
        self.assertEqual(records[1]["error"]["code"], "invalid_json")
        self.assertEqual(records[2]["error"]["code"], "not_json_object")
        self.assertEqual(records[3]["error"]["code"], "not_json_object")
        self.assertEqual(records[4]["_row"], {"ФИО": "Образцов", "Флаг": "true", "Нет": "false"})
        columns = chunks[0]["columns"]
        self.assertIsNone(records[4]["values"][columns.index("Пусто")])  # JSON null
        self.assertIsNone(records[4]["values"][columns.index("Код")])  # absent key
        stats = chunks[-1]["stats"]
        self.assertEqual((stats["blank_lines"], stats["bad_records"]), (2, 3))

    def test_flattening_rules(self):
        obj = {"a": {"b": {"c": 1}, "d": "x"}, "arr": [1, 2.50, "т", None, True, {"k": 1e3}],
               "empty": {}, "big": 12345678901234567890, "neg": -0.0}
        text = ('{"a": {"b": {"c": 1}, "d": "x"}, "arr": [1, 2.50, "т", null, true, {"k": 1e3}], '
                '"empty": {}, "big": 12345678901234567890, "neg": -0.0}')
        self.assertEqual(json.loads(text)["a"], obj["a"])
        path = self.write_text("f.jsonl", text + "\n")
        records, _ = read_all(path, json_structure("jsonl"))
        self.assertEqual(records[0]["_row"], {
            "a.b.c": "1", "a.d": "x", "arr": '[1,2.50,"т",null,true,{"k":1e3}]',
            "empty": "{}", "big": "12345678901234567890", "neg": "-0.0"})

    def test_lone_surrogate_is_replaced(self):
        path = self.write_text("s.jsonl", '{"a": "x\\ud800y", "\\udfff": "1"}\n')
        records, chunks = read_all(path, json_structure("jsonl"))
        self.assertEqual(records[0]["_row"], {"a": "x�y", "�": "1"})
        json.dumps(chunks, ensure_ascii=False).encode("utf-8")

    def test_skip_rows_and_bom(self):
        path = self.write_text("b.jsonl", 'комментарий\n{"a": 1}\n', bom=True)
        records, _ = read_all(path, json_structure("jsonl", skip_rows=1))
        self.assertEqual([(r["line"], r["_row"]) for r in records], [(2, {"a": "1"})])

    def test_very_long_line(self):
        huge = '{"a": "' + "x" * (16 * 1024 * 1024 + 10) + '"}'
        path = self.write_text("h.jsonl", '{"a": "1"}\n' + huge + '\n{"a": "3"}\n')
        records, _ = read_all(path, json_structure("jsonl"))
        self.assertEqual([r["line"] for r in records], [1, 2, 3])
        self.assertEqual(records[1]["error"]["code"], "record_too_large")
        self.assertEqual(records[2]["_row"], {"a": "3"})

    def test_validate(self):
        good = "\n".join(json.dumps({"id": i, "ФИО": "Тестов"}, ensure_ascii=False)
                         for i in range(10)) + "\n"
        result = self.validate(self.write_text("g.jsonl", good), json_structure("jsonl"))
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual(result["columns"], ["id", "ФИО"])
        self.assertEqual(result["preview"]["rows"][3]["values"], ["3", "Тестов"])
        bad = "[1]\n[2]\n{\"a\": 1}\n"
        result = self.validate(self.write_text("b.jsonl", bad), json_structure("jsonl"))
        self.assertIn("not_json_object", [e["code"] for e in result["errors"]])
        result = self.validate(self.write_text("p.jsonl", good),
                               json_structure("jsonl", json_record_path="$.x"))
        self.assertEqual([e["code"] for e in result["errors"]], ["invalid_parameter"])


if __name__ == "__main__":
    unittest.main()
