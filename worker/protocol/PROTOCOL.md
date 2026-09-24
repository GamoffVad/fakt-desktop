# Протокол FAKT worker, версия 1

Worker — отдельный процесс Python, который по запросу клиента (WPF) читает исходные файлы: определяет кодировку, выдаёт образец первых строк, проверяет параметры структуры на ограниченном фрагменте и потоково читает логические записи пакетами через Pandas. Worker **не** обращается к LLM, базе данных, сети и секретам и не изменяет исходные файлы.

## 1. Транспорт

- Клиент запускает процесс: `python.exe -I -X utf8 -u <каталог worker>\fakt_worker_main.py`.
  - `-I` — изолированный режим: игнорируются `PYTHON*`-переменные окружения и пользовательский site-packages. В этом режиме каталог скрипта не попадает в `sys.path`, поэтому `fakt_worker_main.py` добавляет свой каталог явно.
  - Аргументы командной строки не содержат путей к данным, ключей, паролей и иных секретов.
- **stdin** — запросы клиента, **stdout** — только ответы протокола, **stderr** — диагностика в свободной форме (не разбирается клиентом как протокол; не содержит значений записей).
- Кодировка каналов — UTF-8 без BOM. Кадрирование — один JSON-объект на строку, строка завершается `\n`. JSON сериализуется без литеральных переводов строк (стандартное экранирование `json.dumps`), `ensure_ascii=False`.
- Запросы обрабатываются последовательно, по одному. Клиент не отправляет следующий запрос до получения ответа на предыдущий.
- Максимальная длина строки запроса — 1 MiB. Длина ответа `read_chunk` ограничивается аргументом `max_chunk_bytes`.
- Worker ничего не пишет в stdout до первого запроса.

## 2. Конверт сообщений

Запрос:

```json
{"v": 1, "id": "42", "cmd": "sample", "args": {"path": "C:\\data\\a.csv"}}
```

Успешный ответ:

```json
{"v": 1, "id": "42", "ok": true, "result": {}}
```

Ошибка:

```json
{"v": 1, "id": "42", "ok": false, "error": {"code": "file_not_found", "message": "Файл не найден", "details": {}}}
```

- `v` — версия протокола. При `v != 1` ответ `unsupported_protocol_version`.
- `id` — строка, возвращается без изменений. Если запрос не удалось разобрать, `id` = `null`.
- `message` — краткое описание на русском, без содержимого записей.
- Внутренние исключения не прерывают процесс: возвращается `internal_error` с типом исключения в `details.exception_type`; traceback пишется только в stderr.

Коды ошибок: `bad_request`, `unsupported_protocol_version`, `unknown_command`, `file_not_found`, `access_denied`, `file_locked`, `io_error`, `file_changed`, `invalid_structure`, `unsupported_format`, `unsupported_encoding`, `decode_error`, `reader_not_found`, `record_too_large`, `internal_error`.

## 3. Команды

### 3.1. `hello`

`args`: `{}`.

```json
{"protocol_version": 1, "worker_version": "1.0.0", "python_version": "3.8.10",
 "pandas_version": "2.0.3", "numpy_version": "1.24.4", "defusedxml_version": "0.7.1",
 "platform": "Windows-7-6.1.7601-SP1", "pid": 1234}
```

### 3.2. `sample` — первые физические строки

Читает не более `max_bytes` байт с начала файла, не загружая файл целиком.

`args`:

| Поле | Тип | По умолчанию | Смысл |
|---|---|---|---|
| `path` | string | — | Абсолютный путь; допускается префикс `\\?\` |
| `max_lines` | int 1..1000 | 5 | Сколько физических строк вернуть |
| `max_bytes` | int 1024..16777216 | 65536 | Предел прочитанных байт |
| `encoding` | string \| null | null | Ручная кодировка; `null` — определить автоматически |

`result`:

| Поле | Тип | Смысл |
|---|---|---|
| `file_size` | int | Размер файла в байтах |
| `is_empty` | bool | Размер 0 байт или только BOM |
| `is_binary` | bool | Двоичный файл: сигнатура или доля управляющих байтов |
| `binary_kind` | string \| null | `pdf`, `zip`, `png`, `jpeg`, `gif`, `bmp`, `tiff`, `ole`, `exe`, `gzip`, `rar`, `7z`, `sqlite`, `riff`, `mp4`, `mp3`, `unknown_binary` |
| `binary_reason` | string \| null | Пояснение на русском |
| `encoding` | string \| null | Каноническое имя (см. §4) |
| `encoding_source` | string \| null | `bom`, `detected`, `override` |
| `encoding_confidence` | number 0..1 | Уверенность эвристики (для BOM и override = 1.0) |
| `bom` | string \| null | `utf-8`, `utf-16-le`, `utf-16-be`, `utf-32-le`, `utf-32-be` |
| `lines` | string[] | Строки без символов конца строки, не более `max_lines` |
| `line_count` | int | Длина `lines` |
| `bytes_read` | int | Сколько байт прочитано |
| `eof_reached` | bool | Весь файл поместился в прочитанный объём |
| `truncated` | bool | Лимит `max_bytes` исчерпан раньше, чем набрано `max_lines` полных строк |
| `truncated_line_index` | int \| null | Индекс (с 0) строки, обрезанной лимитом |
| `fewer_lines` | bool | Файл закончился раньше, чем набралось `max_lines` строк |
| `line_terminator` | string \| null | `crlf`, `lf`, `cr`, `mixed`; `null`, если в образце нет перевода строки |
| `replacement_count` | int | Сколько символов U+FFFD появилось при декодировании образца |

Для двоичного или пустого файла `lines` = `[]`, `encoding` = `null`.

### 3.3. `validate` — проверка структуры и предпросмотр

Разбирает ограниченный фрагмент тем же кодом, что и `read_chunk`, и возвращает предпросмотр. Нарушения структуры возвращаются в `result` при `ok=false`. Ошибка протокола возвращается только при недоступном файле или внутреннем сбое.

`args`: `path`, `structure` (§5), `max_records` (int 1..1000, по умолчанию 100), `max_bytes` (по умолчанию 1048576 — бюджет разбора в байтах файла).

`result`:

```json
{
  "ok": false,
  "errors": [{"code": "column_count_mismatch", "message": "…", "ordinal": null}],
  "warnings": [{"code": "header_differs", "message": "…", "ordinal": null}],
  "columns": ["ФИО", "Дата рождения", "Телефон"],
  "effective_structure": {},
  "suggestions": {"columns": ["ФИО", "Дата рождения", "Телефон", "Колонка 4"]},
  "records_checked": 100,
  "bad_records": 2,
  "blank_lines": 0,
  "eof_reached": false,
  "preview": {
    "columns": ["ФИО", "Дата рождения", "Телефон"],
    "rows": [{"ordinal": 1, "line": 2, "values": ["Тестов Тест", "15.02.1990", null]}]
  }
}
```

- `effective_structure` — нормализованная структура со значениями по умолчанию и уникальными именами колонок. Именно её клиент передаёт в `open_reader`.
- `suggestions` — необязательное предложение исправления: реальный заголовок файла или число полей по данным. Клиент показывает его пользователю, но **не применяет молча**.
- Блокирующие коды `errors`: `invalid_parameter`, `column_count_mismatch`, `delimiter_not_found`, `no_records_found`, `record_path_not_found`, `not_json_object`, `widths_out_of_range`, `decode_failed`, `too_many_bad_records`.
- Предупреждения `warnings`: `header_differs`, `duplicate_columns_renamed`, `bad_records_present`, `blank_lines_present`, `data_beyond_last_column`, `dtd_ignored`, `replacement_characters`, `encoding_differs`, `structure_change_suspected`.

### 3.4. `open_reader` — открыть потоковое чтение

`args`:

| Поле | Тип | По умолчанию | Смысл |
|---|---|---|---|
| `path` | string | — | Файл |
| `structure` | object | — | `effective_structure` из `validate` |
| `chunk_size` | int 1..100000 | 5000 | Записей на один chunk Pandas |
| `start_after_ordinal` | int ≥ 0 | 0 | Возобновление: записи с `ordinal ≤ start_after_ordinal` разбираются и пропускаются |
| `max_chunk_bytes` | int | 16777216 | Предел размера ответа `read_chunk` |
| `expected_size` | int \| null | null | Отпечаток файла: размер |
| `expected_mtime_ns` | int \| null | null | Отпечаток файла: время изменения, нс Unix |

Если отпечаток не совпадает, возвращается ошибка `file_changed`. Результат: `{"reader_id": "r1"}`.

Возобновление — **безопасное перечитывание**: файл разбирается с начала тем же парсером, записи до границы пропускаются без выдачи. Произвольный seek по байтам не используется, поэтому способ одинаково корректен для CSV с многострочными полями и для XML.

### 3.5. `read_chunk`

`args`: `{"reader_id": "r1"}`.

```json
{
  "columns": ["ФИО", "Дата рождения", "Телефон"],
  "records": [
    {"ordinal": 128, "line": 130, "end_line": 131,
     "values": ["Тестов Тест", "15.02.1990", "+7 000 000-00-00"],
     "hash": "9f2c…64 hex", "error": null},
    {"ordinal": 129, "line": 132, "end_line": 132, "values": null, "hash": null,
     "error": {"code": "field_count_mismatch", "message": "Ожидалось 3 поля, получено 5"}}
  ],
  "eof": false,
  "stats": {"records_emitted": 129, "records_skipped": 0, "bad_records": 1,
            "blank_lines": 0, "replacement_count": 0},
  "warnings": []
}
```

- Не более `chunk_size` записей и не более примерно `max_chunk_bytes` байт JSON. Остаток выдаётся следующим вызовом.
- `records` может быть пустым только при `eof=true`.
- После `eof=true` повторный `read_chunk` возвращает пустой `records` и `eof=true`.
- `columns` — колонки текущего ответа. Для XML и JSON набор может меняться между ответами, поэтому клиент сопоставляет значения по именам `columns` этого ответа.
- `values[i]` — строка или `null`. `null` означает отсутствие значения в источнике: нет элемента XML, нет ключа JSON, недостающее поле. Пустая строка `""` остаётся пустой строкой.
- Коды ошибок записей: `field_count_mismatch`, `short_record`, `invalid_json`, `not_json_object`, `record_too_large`, `decode_error`, `structure_changed`.

### 3.6. `close_reader`

`args`: `{"reader_id": "r1"}`, результат `{}`. Неизвестный `reader_id` возвращает ошибку `reader_not_found`.

### 3.7. `shutdown`

Результат `{}`, после ответа процесс завершается с кодом 0. Закрытие stdin также завершает worker.

## 4. Кодировки

Канонические имена: `utf-8`, `utf-8-sig`, `utf-16-le`, `utf-16-be`, `utf-32-le`, `utf-32-be`, `windows-1251`, `cp866`, `koi8-r`, `windows-1252`, `iso-8859-1`, `ascii`. Принимаемые синонимы: `utf8`, `cp1251`, `1251`, `win-1251`, `utf-16` (с BOM), `latin-1`, `cp1252`, `ibm866`.

Порядок определения:

1. BOM.
2. Признаки UTF-16/32 без BOM по распределению нулевых байтов.
3. Строгий UTF-8. Незавершённая последовательность на границе образца не считается ошибкой.
4. Эвристика однобайтовых кириллических кодировок: `windows-1251`, `cp866`, `koi8-r`, а также `windows-1252` для латиницы.

Результат эвристики — вероятность, а не доказательство. Пользователь может задать кодировку вручную.

При чтении данных ошибки декодирования заменяются на U+FFFD и подсчитываются в `replacement_count`. Предупреждение `replacement_characters` не скрывает их.

## 5. Структура (`structure`)

Контракт совпадает с ответом модели на запрос определения структуры:

```json
{
  "classification": "structured",
  "format": "delimited",
  "encoding": "utf-8",
  "has_header": true,
  "header_row": 0,
  "skip_rows": 0,
  "delimiter": ";",
  "quote_char": "\"",
  "escape_char": null,
  "columns": ["ФИО", "Дата рождения", "Телефон"],
  "fixed_widths": null,
  "xml_record_path": null,
  "xml_namespaces": {},
  "json_record_path": null,
  "confidence": 0.93,
  "reason": "Повторяется набор из трёх полей"
}
```

Семантика полей для чтения:

- `format`: `delimited` | `fixed_width` | `xml` | `jsonl` | `json_array`.
- `skip_rows` — число физических строк преамбулы перед заголовком. Если заголовка нет, это число строк перед первой записью.
- `header_row` — абсолютный номер физической строки заголовка, считая с 0. При `has_header=true` должно выполняться `header_row == skip_rows`; при `has_header=false` значение равно `null`.
- `delimited`: `delimiter` — ровно один символ, не `"\r"` и не `"\n"`, не совпадает с `quote_char`. `quote_char = null` означает отсутствие кавычек (`QUOTE_NONE`). Удвоение кавычки внутри поля поддерживается.
- `fixed_width`: `fixed_widths` — положительные ширины колонок в символах, их число равно числу `columns`. Значения обрезаются по пробелам с обеих сторон.
- `xml`: `xml_record_path` — путь к повторяющемуся элементу записи. Абсолютный путь: `/root/items/item`. Путь без ведущей `/` сопоставляется с концом пути элемента: `item` или `items/item`. Префиксы разрешаются через `xml_namespaces` (`{"ns": "urn:…"}`). DTD с объявлениями сущностей и внешние ссылки запрещены (defusedxml: `forbid_entities`, `forbid_external`). Внешние сущности не загружаются.
- `jsonl`: каждая непустая строка — JSON-объект. `json_record_path` = `null`; другие значения в версии 1 возвращают `unsupported_format`.
- `json_array`: файл — JSON-массив объектов верхнего уровня, читается потоково. `json_record_path` = `null`.
- `columns` для `xml`, `jsonl` и `json_array` — справочные. Фактические колонки формируются из данных.

### 5.1. Плоское представление XML и JSON

- XML: атрибут → `@имя`; дочерний элемент с текстом → `имя`; вложенность → `a/b`; повтор имени на одном уровне → `имя`, `имя[2]`, `имя[3]`; текст элемента со вложенными детьми → `#text`. Префиксы пространств имён берутся из `xml_namespaces`, для неизвестных используется локальное имя.
- JSON: вложенные объекты → `a.b`; массивы и значения внутри массивов → компактный JSON-текст; числа сохраняются в исходной текстовой записи (`parse_int` и `parse_float` возвращают исходную строку), `true`/`false` → `"true"`/`"false"`, `null` → `null`.

## 6. Логические записи и идентификаторы

- `ordinal` — порядковый номер логической записи с 1 в порядке файла после преамбулы и заголовка. Он учитывает и записи с ошибками. Для одного и того же содержимого файла и структуры номер детерминирован и не зависит от параллельности клиента.
- Пустые строки CSV, TXT и JSONL не являются записями. Они подсчитываются в `blank_lines`.
- `line` и `end_line` — физические строки начала и конца записи, считая с 1. Для многострочного CSV они различаются. Для XML и JSON-массива равны `null`.
- `hash` — SHA-256 (hex) канонического представления записи: компактный JSON-массив пар `[колонка, значение]` в порядке колонок, UTF-8, `ensure_ascii=False`, `separators=(",", ":")`.
- Устойчивый `source_row_id` клиента строится из `ordinal`, а не из хеша: две одинаковые строки в разных позициях остаются разными записями.

## 7. Ограничения памяти

- CSV и TXT: `pandas.read_csv` с `chunksize` (движок `python`, `dtype=str`, `keep_default_na=False`, `na_filter=False`, `skip_blank_lines=False`). Лишние поля перехватываются `on_bad_lines` без потери позиции записи.
- Фиксированная ширина: `pandas.read_fwf` с `colspecs` и `chunksize`.
- XML: `defusedxml.ElementTree.iterparse` с освобождением обработанных узлов. Дерево всего файла не строится.
- JSONL: построчно. JSON-массив: `json.JSONDecoder.raw_decode` по буферу. Предел одной записи — 16 MiB, иначе ошибка `record_too_large`.
- Объём памяти ограничен размером chunk и размером одной записи и не растёт с размером файла.
