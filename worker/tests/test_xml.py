# -*- coding: utf-8 -*-
import hashlib
import json
import tracemalloc
import unittest

from fakt_worker import readers, validation
from fakt_worker.protocol import WorkerError

from .synthetic import TempDirTestCase, read_all, write_people_xml, xml_structure

DOC = """<?xml version="1.0" encoding="UTF-8"?>
<root>
  <meta><created>2000-01-01</created></meta>
  <items>
    <item id="1" type="a">
      <name>Тестов Тест</name>
      <phone kind="mobile">+7 000 000-00-00</phone>
      <phone>+7 000 000-00-01</phone>
      <address><city>Тестовск</city><street>Примерная</street></address>
      <address><city>Образцовск</city></address>
      <empty/>
      <note>Текст <b>жирный</b> хвост</note>
    </item>
    <item id="2"><name>Примеров</name><code>007</code></item>
  </items>
</root>
"""

FIRST = {"@id": "1", "@type": "a", "name": "Тестов Тест", "phone": "+7 000 000-00-00",
         "phone/@kind": "mobile", "phone[2]": "+7 000 000-00-01", "address/city": "Тестовск",
         "address/street": "Примерная", "address[2]/city": "Образцовск", "empty": "",
         "note/#text": "Текст хвост", "note/b": "жирный"}
SECOND = {"@id": "2", "name": "Примеров", "code": "007"}

NS_DOC = """<?xml version="1.0"?>
<r:root xmlns:r="urn:root" xmlns="urn:default" xmlns:x="urn:extra">
  <item x:attr="1"><name>A</name><x:code>007</x:code></item>
  <item><name>B</name></item>
</r:root>
"""


class XmlTests(TempDirTestCase):

    def validate(self, path, structure, **args):
        args.update(path=path, structure=structure)
        return validation.validate(args)

    def rows(self, path, structure, **kwargs):
        records, chunks = read_all(path, structure, **kwargs)
        return [r.get("_row") for r in records], records, chunks

    def test_flattening(self):
        path = self.write_text("d.xml", DOC)
        for record_path in ("/root/items/item", "item", "items/item"):
            rows, records, chunks = self.rows(path, xml_structure(record_path))
            self.assertEqual(rows, [FIRST, SECOND], record_path)
            self.assertEqual([(r["line"], r["end_line"]) for r in records], [(None, None)] * 2)
        self.assertEqual(chunks[0]["columns"][:3], ["@id", "@type", "name"])
        self.assertIn("code", chunks[0]["columns"])
        second = records[1]
        index = chunks[0]["columns"].index("phone")
        self.assertIsNone(second["values"][index])  # absent element -> null
        result = self.validate(path, xml_structure("item"))
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual(result["records_checked"], 2)
        self.assertEqual(result["columns"], chunks[0]["columns"])

    def test_namespaces(self):
        path = self.write_text("ns.xml", NS_DOC)
        rows, _, _ = self.rows(path, xml_structure("item", {"x": "urn:extra"}))
        self.assertEqual(rows, [{"@x:attr": "1", "name": "A", "x:code": "007"}, {"name": "B"}])
        namespaces = {"r": "urn:root", "d": "urn:default", "x": "urn:extra"}
        rows, _, _ = self.rows(path, xml_structure("/r:root/d:item", namespaces))
        self.assertEqual(rows, [{"@x:attr": "1", "d:name": "A", "x:code": "007"}, {"d:name": "B"}])
        rows, _, _ = self.rows(path, xml_structure("/root/item"))
        self.assertEqual(len(rows), 2)
        rows, _, _ = self.rows(path, xml_structure("/r:root/x:item", namespaces))
        self.assertEqual(rows, [])

    def test_leaf_records(self):
        path = self.write_text("leaf.xml", "<root><v>1</v><v a='x'>2</v><v/></root>")
        rows, _, _ = self.rows(path, xml_structure("v"))
        self.assertEqual(rows, [{"#text": "1"}, {"@a": "x", "#text": "2"}, {}])

    def test_record_path_not_found(self):
        path = self.write_text("d.xml", DOC)
        result = self.validate(path, xml_structure("/root/missing"))
        self.assertFalse(result["ok"])
        self.assertEqual([e["code"] for e in result["errors"]], ["record_path_not_found"])
        self.assertEqual(result["suggestions"], {"xml_record_path": "/root/items/item"})

    def test_doctype_ignored(self):
        for doctype in ('<!DOCTYPE root SYSTEM "http://example.invalid/root.dtd">',
                        "<!DOCTYPE root [<!ELEMENT root ANY>]>"):
            path = self.write_text("dt.xml", '<?xml version="1.0"?>\n%s\n<root><item><a>1</a>'
                                   "</item></root>" % doctype)
            result = self.validate(path, xml_structure("item"))
            self.assertTrue(result["ok"], result["errors"])
            self.assertIn("dtd_ignored", [w["code"] for w in result["warnings"]])
            rows, _, chunks = self.rows(path, xml_structure("item"))
            self.assertEqual(rows, [{"a": "1"}])
            self.assertIn("dtd_ignored", [w["code"] for w in chunks[0]["warnings"]])

    def test_entities_forbidden(self):
        path = self.write_text("e.xml", '<?xml version="1.0"?>\n<!DOCTYPE root [<!ENTITY e "x">]>'
                               "\n<root><item>&e;</item></root>")
        result = self.validate(path, xml_structure("item"))
        self.assertFalse(result["ok"])
        self.assertEqual([e["code"] for e in result["errors"]], ["decode_failed"])
        self.assertIn("ENTITY", result["errors"][0]["message"])
        reader_id = readers.open_reader({"path": path, "structure": xml_structure("item")})["reader_id"]
        with self.assertRaises(WorkerError) as ctx:
            readers.read_chunk({"reader_id": reader_id})
        self.assertEqual(ctx.exception.code, "decode_error")

    def test_single_byte_and_utf16_xml(self):
        text = ('<?xml version="1.0" encoding="windows-1251"?>\n<root><item><name>Тестов Тест'
                "</name></item></root>\n")
        path = self.write_bytes("w.xml", text.encode("cp1251"))
        result = self.validate(path, xml_structure("item"))
        self.assertTrue(result["ok"], result["errors"])
        self.assertEqual(result["effective_structure"]["encoding"], "windows-1251")
        self.assertEqual(result["preview"]["rows"][0]["values"], ["Тестов Тест"])
        rows, _, _ = self.rows(path, xml_structure("item"))
        self.assertEqual(rows, [{"name": "Тестов Тест"}])
        u16 = text.replace("windows-1251", "UTF-16")
        path = self.write_bytes("u.xml", b"\xff\xfe" + u16.encode("utf-16-le"))
        rows, _, _ = self.rows(path, xml_structure("item"))
        self.assertEqual(rows, [{"name": "Тестов Тест"}])

    def test_malformed_after_records_is_deferred(self):
        path = self.write_text("m.xml", "<root><item><a>1</a></item><item><a>2</a></item>"
                               "<item><a>3</b></item></root>")
        reader_id = readers.open_reader({"path": path, "structure": xml_structure("item")})["reader_id"]
        chunk = readers.read_chunk({"reader_id": reader_id})
        self.assertEqual([r["ordinal"] for r in chunk["records"]], [1, 2])
        self.assertFalse(chunk["eof"])
        for _ in range(2):
            with self.assertRaises(WorkerError) as ctx:
                readers.read_chunk({"reader_id": reader_id})
            self.assertEqual(ctx.exception.code, "decode_error")
            self.assertIn("line", ctx.exception.details)

    def test_validate_fragment_of_large_xml(self):
        path = self.path("big.xml")
        write_people_xml(path, 2000)
        result = self.validate(path, xml_structure("/people/person"), max_bytes=4096, max_records=1000)
        self.assertTrue(result["ok"], result["errors"])
        self.assertGreater(result["records_checked"], 10)
        self.assertFalse(result["eof_reached"])
        self.assertEqual(result["columns"], ["@id", "name", "phone"])

    def test_hashes_do_not_depend_on_chunking(self):
        path = self.write_text("d.xml", DOC)
        _, small, _ = self.rows(path, xml_structure("item"), chunk_size=1)
        _, large, _ = self.rows(path, xml_structure("item"), chunk_size=1000)
        self.assertEqual([(r["ordinal"], r["hash"]) for r in small],
                         [(r["ordinal"], r["hash"]) for r in large])
        canon = json.dumps(list(SECOND.items()), ensure_ascii=False, separators=(",", ":"))
        self.assertEqual(large[1]["hash"], hashlib.sha256(canon.encode("utf-8")).hexdigest())

    def test_large_xml_memory_is_bounded(self):
        path = self.path("huge.xml")
        count = 200000
        write_people_xml(path, count)
        reader_id = readers.open_reader({"path": path, "structure": xml_structure("person"),
                                         "chunk_size": 5000})["reader_id"]
        total = 0
        last = None
        tracemalloc.start()
        try:
            while True:
                chunk = readers.read_chunk({"reader_id": reader_id})
                total += len(chunk["records"])
                if chunk["records"]:
                    last = chunk["records"][-1]
                if chunk["eof"]:
                    break
                del chunk
            _, peak = tracemalloc.get_traced_memory()
        finally:
            tracemalloc.stop()
        self.assertEqual(total, count)
        self.assertEqual(last["ordinal"], count)
        self.assertLess(peak, 50 * 1024 * 1024, "peak %.1f MB" % (peak / 1048576.0))


if __name__ == "__main__":
    unittest.main()
