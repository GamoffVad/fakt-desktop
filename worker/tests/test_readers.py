# -*- coding: utf-8 -*-
import hashlib
import json
import os
import unittest

from fakt_worker import readers
from fakt_worker.protocol import WorkerError

from .synthetic import (HEADER, TempDirTestCase, csv_text, delimited_structure, json_structure,
                        people, read_all, response_size, write_people_xml, xml_structure)


def mixed_csv(count):
    """CSV with multiline fields, blank lines and bad records."""
    rows = people(count)
    for i in range(0, count, 7):
        rows[i][0] = rows[i][0] + "\r\nвторая строка"
    for i in range(3, count, 11):
        rows[i] = rows[i][:2]
    for i in range(5, count, 13):
        rows[i] = rows[i] + ["лишнее"]
    return csv_text(rows[:10], header=HEADER) + "\r\n" + csv_text(rows[10:])  # + a blank line


def key(record):
    return (record["ordinal"], record["line"], record["end_line"], record["hash"],
            record["values"], record["error"])


class ReaderTests(TempDirTestCase):

    def open(self, path, structure, **args):
        args.update(path=path, structure=structure)
        return readers.open_reader(args)["reader_id"]

    def test_resume_gives_identical_records(self):
        path = self.write_text("r.csv", mixed_csv(200))
        structure = delimited_structure(list(HEADER))
        full, _ = read_all(path, structure, chunk_size=13)
        self.assertEqual([r["ordinal"] for r in full], list(range(1, 201)))
        for start in (0, 1, 37, 150, 199, 200, 250):
            resumed, chunks = read_all(path, structure, chunk_size=10, start_after_ordinal=start)
            self.assertEqual([key(r) for r in resumed], [key(r) for r in full[start:]], start)
            self.assertEqual(chunks[-1]["stats"]["records_skipped"], min(start, 200))
        xml_path = self.path("r.xml")
        write_people_xml(xml_path, 300)
        full, _ = read_all(xml_path, xml_structure("person"), chunk_size=64)
        resumed, _ = read_all(xml_path, xml_structure("person"), start_after_ordinal=123)
        self.assertEqual([key(r) for r in resumed], [key(r) for r in full[123:]])
        jsonl = "\n".join(json.dumps({"i": i, "v": "x" * (i % 5)}) if i % 9 else "" for i in range(99))
        jsonl_path = self.write_text("r.jsonl", jsonl)
        full, _ = read_all(jsonl_path, json_structure("jsonl"))
        resumed, _ = read_all(jsonl_path, json_structure("jsonl"), start_after_ordinal=40)
        self.assertEqual([key(r) for r in resumed], [key(r) for r in full[40:]])

    def test_chunks_respect_max_chunk_bytes(self):
        rows = [[p[0], p[1], p[2], "текст " * 30] for p in people(300)]
        path = self.write_text("b.csv", csv_text(rows, header=HEADER))
        reader_id = self.open(path, delimited_structure(list(HEADER)), max_chunk_bytes=8192)
        ordinals = []
        chunks = 0
        while True:
            chunk = readers.read_chunk({"reader_id": reader_id})
            self.assertLessEqual(response_size(chunk), 8192)
            if chunk["records"]:
                self.assertGreater(len(chunk["records"]), 1)
            ordinals += [r["ordinal"] for r in chunk["records"]]
            chunks += 1
            if chunk["eof"]:
                break
        self.assertEqual(ordinals, list(range(1, 301)))
        self.assertGreater(chunks, 10)

    def test_dynamic_columns_respect_max_chunk_bytes(self):
        items = "\n".join(json.dumps({"k%d" % i: "значение %d" % i, "id": i}) for i in range(400))
        path = self.write_text("d.jsonl", items)
        reader_id = self.open(path, json_structure("jsonl"), max_chunk_bytes=6000)
        total = 0
        while True:
            chunk = readers.read_chunk({"reader_id": reader_id})
            self.assertLessEqual(response_size(chunk), 6000)
            total += len(chunk["records"])
            if chunk["eof"]:
                break
        self.assertEqual(total, 400)

    def test_chunk_size_and_eof_repeat(self):
        path = self.write_text("c.csv", csv_text(people(20), header=HEADER))
        reader_id = self.open(path, delimited_structure(list(HEADER)), chunk_size=7)
        sizes = []
        while True:
            chunk = readers.read_chunk({"reader_id": reader_id})
            sizes.append(len(chunk["records"]))
            if chunk["eof"]:
                break
        self.assertEqual(sum(sizes), 20)
        self.assertTrue(all(size <= 7 for size in sizes))
        again = readers.read_chunk({"reader_id": reader_id})
        self.assertEqual((again["records"], again["eof"]), ([], True))
        self.assertEqual(again["stats"]["records_emitted"], 20)
        self.assertEqual(again["columns"], HEADER)

    def test_record_larger_than_response_limit(self):
        rows = people(3)
        rows[1][3] = "я" * 20000
        path = self.write_text("l.csv", csv_text(rows, header=HEADER))
        records, _ = read_all(path, delimited_structure(list(HEADER)), max_chunk_bytes=8192)
        self.assertEqual(records[1]["error"]["code"], "record_too_large")
        self.assertEqual(records[2]["values"], rows[2])

    def test_hash_is_canonical(self):
        path = self.write_text("h.csv", csv_text(people(2), header=HEADER))
        records, _ = read_all(path, delimited_structure(list(HEADER)))
        for record in records:
            canon = json.dumps([[c, v] for c, v in zip(HEADER, record["values"])],
                               ensure_ascii=False, separators=(",", ":"))
            self.assertEqual(record["hash"], hashlib.sha256(canon.encode("utf-8")).hexdigest())

    def test_fingerprint(self):
        path = self.write_text("f.csv", csv_text(people(50), header=HEADER))
        st = os.stat(path)
        structure = delimited_structure(list(HEADER))
        reader_id = self.open(path, structure, expected_size=st.st_size,
                              expected_mtime_ns=st.st_mtime_ns)
        readers.close_reader({"reader_id": reader_id})
        for args in ({"expected_size": st.st_size + 1},
                     {"expected_mtime_ns": st.st_mtime_ns - 1000}):
            with self.assertRaises(WorkerError) as ctx:
                self.open(path, structure, **args)
            self.assertEqual(ctx.exception.code, "file_changed")

    def test_file_modified_during_reading(self):
        path = self.write_text("g.csv", csv_text(people(50), header=HEADER))
        reader_id = self.open(path, delimited_structure(list(HEADER)), chunk_size=10)
        readers.read_chunk({"reader_id": reader_id})
        with open(path, "ab") as fh:
            fh.write(b"new;row;;\r\n")
        with self.assertRaises(WorkerError) as ctx:
            readers.read_chunk({"reader_id": reader_id})
        self.assertEqual(ctx.exception.code, "file_changed")

    def test_reader_lifecycle_errors(self):
        path = self.write_text("x.csv", "A\n1\n")
        with self.assertRaises(WorkerError) as ctx:
            readers.read_chunk({"reader_id": "r999"})
        self.assertEqual(ctx.exception.code, "reader_not_found")
        reader_id = self.open(path, delimited_structure(["A"]))
        self.assertEqual(readers.close_reader({"reader_id": reader_id}), {})
        for call in (readers.read_chunk, readers.close_reader):
            with self.assertRaises(WorkerError) as ctx:
                call({"reader_id": reader_id})
            self.assertEqual(ctx.exception.code, "reader_not_found")
        with self.assertRaises(WorkerError) as ctx:
            self.open(self.path("missing.csv"), delimited_structure(["A"]))
        self.assertEqual(ctx.exception.code, "file_not_found")
        with self.assertRaises(WorkerError) as ctx:
            self.open(path, delimited_structure(["A"]), chunk_size=0)
        self.assertEqual(ctx.exception.code, "bad_request")
        with self.assertRaises(WorkerError) as ctx:
            self.open(path, {"format": "excel"})
        self.assertEqual(ctx.exception.code, "unsupported_format")
        self.assertEqual(readers.open_count(), 0)


if __name__ == "__main__":
    unittest.main()
