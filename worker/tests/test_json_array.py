# -*- coding: utf-8 -*-
import json
import unittest

from fakt_worker import readers, validation
from fakt_worker.protocol import WorkerError

from .synthetic import TempDirTestCase, json_structure, person, phone, read_all


class JsonArrayTests(TempDirTestCase):

    def validate(self, path, structure, **args):
        args.update(path=path, structure=structure)
        return validation.validate(args)

    def test_basic_array(self):
        text = ('﻿ [ {"ФИО": "Тестов Тест", "Код": "007", "n": 12.50e1},\n'
                ' 42, {"Вложенный": {"a": [1, {"b": null}]}, "t": true} ,[1],\n{} ]  \n')
        path = self.write_text("a.json", text)
        records, chunks = read_all(path, json_structure("json_array"))
        self.assertEqual(len(records), 5)
        self.assertEqual(records[0]["_row"], {"ФИО": "Тестов Тест", "Код": "007", "n": "12.50e1"})
        self.assertEqual(records[1]["error"]["code"], "not_json_object")
        self.assertEqual(records[2]["_row"], {"Вложенный.a": '[1,{"b":null}]', "t": "true"})
        self.assertEqual(records[3]["error"]["code"], "not_json_object")
        self.assertEqual(records[4]["values"], [None] * len(chunks[0]["columns"]))
        self.assertEqual([r["line"] for r in records], [None] * 5)

    def test_elements_across_read_pieces(self):
        items = [{"id": i, "ФИО": person(i), "Телефон": phone(i), "Текст": "ж" * (i * 997 % 70000)}
                 for i in range(60)]
        path = self.write_text("big.json", json.dumps(items, ensure_ascii=False, indent=1))
        records, _ = read_all(path, json_structure("json_array"), chunk_size=7)
        self.assertEqual(len(records), 60)
        for i, record in enumerate(records):
            self.assertEqual(record["_row"]["id"], str(i))
            self.assertEqual(len(record["_row"]["Текст"]), i * 997 % 70000)

    def test_top_level_not_array(self):
        path = self.write_text("o.json", '{"items": [{"a": 1}]}')
        result = self.validate(path, json_structure("json_array"))
        self.assertEqual([e["code"] for e in result["errors"]], ["decode_failed"])
        reader_id = readers.open_reader({"path": path, "structure": json_structure("json_array")})
        with self.assertRaises(WorkerError) as ctx:
            readers.read_chunk(reader_id)
        self.assertEqual(ctx.exception.code, "invalid_structure")

    def test_malformed_after_good_elements(self):
        path = self.write_text("m.json", '[{"a": 1}, {"a": 2}, {"a": 3 "b": 4}, {"a": 5}]')
        reader_id = readers.open_reader({"path": path, "structure": json_structure("json_array")})
        chunk = readers.read_chunk(reader_id)
        self.assertEqual([r["ordinal"] for r in chunk["records"]], [1, 2])
        with self.assertRaises(WorkerError) as ctx:
            readers.read_chunk(reader_id)
        self.assertEqual(ctx.exception.code, "decode_error")

    def test_unterminated_array(self):
        path = self.write_text("u.json", '[{"a": 1}, {"a": 2')
        reader_id = readers.open_reader({"path": path, "structure": json_structure("json_array")})
        chunk = readers.read_chunk(reader_id)
        self.assertEqual(len(chunk["records"]), 1)
        with self.assertRaises(WorkerError) as ctx:
            readers.read_chunk(reader_id)
        self.assertEqual(ctx.exception.code, "decode_error")

    def test_validate_fragment_and_empty(self):
        items = [{"id": i, "ФИО": person(i)} for i in range(500)]
        path = self.write_text("v.json", json.dumps(items, ensure_ascii=False))
        result = self.validate(path, json_structure("json_array"), max_bytes=2048, max_records=1000)
        self.assertTrue(result["ok"], result["errors"])
        self.assertGreater(result["records_checked"], 5)
        self.assertFalse(result["eof_reached"])
        result = self.validate(self.write_text("e.json", " [ ] "), json_structure("json_array"))
        self.assertEqual([e["code"] for e in result["errors"]], ["no_records_found"])

    def test_element_too_large(self):
        huge = '[{"a": 1}, {"a": "' + "x" * (16 * 1024 * 1024 + 100) + '"}, {"a": 3}]'
        path = self.write_text("h.json", huge)
        reader_id = readers.open_reader({"path": path, "structure": json_structure("json_array")})
        chunk = readers.read_chunk(reader_id)
        self.assertEqual(len(chunk["records"]), 1)
        with self.assertRaises(WorkerError) as ctx:
            readers.read_chunk(reader_id)
        self.assertEqual(ctx.exception.code, "record_too_large")


if __name__ == "__main__":
    unittest.main()
