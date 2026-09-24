# FAKT worker

Python worker process for the FAKT desktop client. The client starts it as

```
python.exe -I -X utf8 -u <worker dir>\fakt_worker_main.py
```

and talks JSON lines over stdin/stdout. The contract is `protocol/PROTOCOL.md`
(authoritative). The worker only reads source files: encoding detection, a
sample of the first lines, structure validation with preview and streaming
chunked reading. It never modifies files and never touches the network,
databases or secrets.

## Runtime

- Python 3.8.10 (embeddable) — the code is Python 3.8 compatible.
- pandas 2.0.3 (works with pandas >= 1.4 and < 2.3), numpy 1.24.4, defusedxml 0.7.1.
- No other third-party packages. Tests use the standard `unittest`.

## Layout

| Module | Purpose |
|---|---|
| `fakt_worker_main.py` | Entry point; adds its own directory to `sys.path` (needed with `-I`). |
| `fakt_worker/server.py` | stdin loop, envelope, dispatch, error mapping, protocol-only stdout. |
| `fakt_worker/protocol.py` | `WorkerError`, error codes, argument checks, OS error mapping, file opening. |
| `fakt_worker/encoding_detect.py` | BOM, UTF-16/32 without BOM, strict UTF-8, windows-1251 / cp866 / koi8-r / windows-1252 scoring, canonical names. |
| `fakt_worker/binary_detect.py` | Magic signatures and control-byte ratio. |
| `fakt_worker/sampling.py` | `sample` command. |
| `fakt_worker/structure.py` | Normalization of `structure` into `effective_structure`. |
| `fakt_worker/validation.py` | `validate` command (checks and preview on a bounded fragment). |
| `fakt_worker/records.py` | Record hash (§6), value conversion, chunk assembly within `max_chunk_bytes`. |
| `fakt_worker/readers.py` | `open_reader` / `read_chunk` / `close_reader`, fingerprint, resume. |
| `fakt_worker/parsers/` | `delimited`, `fixed_width`, `xml_stream`, `jsonl`, `json_array`. |

## Running the tests

From this directory (`worker/`):

```
python -m unittest discover -s tests -t .
```

All test data is synthetic and generated into temporary directories (fake
names like "Тестов Тест Тестович", phones like "+7 000 000-00-00"). The
largest test builds a 200 000-record XML file (~20 MB) in a temporary
directory and checks that memory stays bounded; the suite takes about 20 s.

## Implementation notes

- **CSV/TXT** is read by `pandas.read_csv(engine="python", dtype=str,
  keep_default_na=False, na_filter=False, skip_blank_lines=False, chunksize=...)`
  from a text stream opened with `newline=""` and `errors="replace"`. The csv
  reader that pandas creates is wrapped (a shim for the `csv` global of
  `pandas.io.parsers.python_parser`) to record the physical line span of every
  row, to consume blank lines, to keep rows with extra fields in place as error
  records (`on_bad_lines` is also set as a second line of defence) and to turn
  `csv.Error` (bad quotes, unterminated quote, huge field) into error records
  instead of losing rows. Line numbers are therefore exact for multiline fields.
- **Fixed width** uses `pandas.read_fwf` with explicit `colspecs`.
- **XML** uses `defusedxml.ElementTree.iterparse` (entities and external
  references forbidden); processed nodes are cleared and detached.
- **JSONL** is read line by line, **JSON arrays** with `JSONDecoder.raw_decode`
  over a growing buffer; numbers keep their source text.
- Resume (`start_after_ordinal`) re-parses the file from the start and skips
  records, so it is exact for multiline CSV and XML alike.
- stdout carries protocol lines only (descriptor 1 and `sys.stdout` are
  redirected to stderr). stderr gets diagnostics without record values
  (tracebacks are logged without exception messages).
