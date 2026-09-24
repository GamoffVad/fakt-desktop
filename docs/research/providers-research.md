# Исследование HTTP API LLM-провайдеров для адаптеров (C#, .NET Framework 4.8, HttpClient, Newtonsoft.Json)

Состояние документации: **2026-09-24**. Все факты взяты из официальных источников провайдеров (сайты документации, API reference, официальные OpenAPI-спецификации). У каждого блока есть список источников `[XX-n]`, и каждое утверждение помечено ссылкой на источник.

**Методика.** Страницы загружались напрямую (`curl`/WebFetch), по возможности в сыром виде (`.md`, `.md.txt`, `openapi.yaml`, `?accept=text/markdown`), затем по ним шёл поиск нужных полей.

- `platform.openai.com/docs/*` сейчас отвечает `301` и перенаправляет на `developers.openai.com/api/docs/*`. Страницы reference на `platform.openai.com` вернули автоматическому клиенту `403`, поэтому для OpenAI использовались `developers.openai.com/...md`.
- Пометка **«не удалось проверить»** значит одно из двух: официальная документация об этом молчит, или страницу не удалось получить. Такие пункты лучше не хардкодить, а обрабатывать защитно.

---

## 0. Сводная таблица

| # | Провайдер | Base URL (по умолчанию) | Auth header | Список моделей | Параметр max output | JSON Schema mode | JSON object mode | «Обрезано по лимиту» |
|---|---|---|---|---|---|---|---|---|
| 1 | OpenAI | `https://api.openai.com/v1` | `Authorization: Bearer` | `GET /models`, без пагинации | Chat: `max_completion_tokens`; Responses: `max_output_tokens` | да (`strict`) | да | Chat: `finish_reason:"length"`; Responses: `status:"incomplete"` + `incomplete_details.reason:"max_output_tokens"` |
| 2 | Azure OpenAI | `https://{res}.openai.azure.com/openai/v1/` (v1) или `.../openai/deployments/{dep}/...?api-version=2024-10-21` | `api-key` или `Authorization: Bearer` | `GET /openai/v1/models` (модели, **не** деплойменты) | как у OpenAI | да (более жёсткие лимиты) | да | как у OpenAI |
| 3 | Anthropic | `https://api.anthropic.com` | `x-api-key` (legacy) или `Authorization: Bearer` + `anthropic-version: 2023-06-01` | `GET /v1/models`, курсор `after_id`/`before_id` | `max_tokens` (обязателен) | `output_config.format` (GA) | нет | `stop_reason:"max_tokens"` (+ `model_context_window_exceeded`) |
| 4 | Google Gemini | `https://generativelanguage.googleapis.com/v1beta` | `x-goog-api-key` | `GET /models`, `pageSize`/`pageToken` | `generationConfig.maxOutputTokens` | `responseFormat.text.schema` (новое) / `responseJsonSchema` / `responseSchema` (deprecated) | `responseMimeType:"application/json"` | `finishReason:"MAX_TOKENS"` |
| 5 | Mistral | `https://api.mistral.ai` (+ EU/US) | `Authorization: Bearer` | `GET /v1/models`, без пагинации | `max_tokens` | да | да | `finish_reason:"length"` (есть ещё `model_length`) |
| 6 | DeepSeek | `https://api.deepseek.com` | `Authorization: Bearer` | `GET /models`, без пагинации | `max_tokens` (≤ 393216) | **нет** | да | `finish_reason:"length"` |
| 7 | Qwen (Model Studio) | `https://{WorkspaceId}.{region}.maas.aliyuncs.com/compatible-mode/v1` | `Authorization: Bearer` | не удалось проверить | `max_completion_tokens` (`max_tokens` «to be deprecated») | да (не все модели/регионы) | да | `finish_reason:"length"` |
| 8 | xAI | `https://api.x.ai/v1` | `Authorization: Bearer` | `GET /v1/models`, `GET /v1/language-models` | `max_completion_tokens` | да | да | `finish_reason:"length"` |
| 9 | Groq | `https://api.groq.com/openai/v1` | `Authorization: Bearer` | `GET /models` | `max_completion_tokens` | да (`strict` только на части моделей) | да | `finish_reason:"length"` (стандарт OpenAI) |
| 10 | OpenRouter | `https://openrouter.ai/api/v1` | `Authorization: Bearer` | `GET /models` (публичный), `GET /models/user` (с ключом), `offset`/`limit` | `max_completion_tokens` / `max_tokens` | да (зависит от провайдера) | да | `finish_reason:"length"` (нормализован) |
| 11 | Together AI | `https://api.together.ai/v1` | `Authorization: Bearer` | `GET /models` → **голый массив** | `max_tokens` | да | да | `finish_reason:"length"` (есть ещё `eos`) |
| 12 | Fireworks AI | `https://api.fireworks.ai/inference/v1` | `Authorization: Bearer` | `GET https://api.fireworks.ai/v1/accounts/{account}/models`, `pageSize`/`pageToken` | `max_tokens` (`max_completion_tokens` = alias) | да | да (+ `grammar`) | `finish_reason:"length"` |
| 13 | Ollama | `http://localhost:11434` (native `/api`, OpenAI `/v1`) | локально не нужен; cloud: `Authorization: Bearer` | `GET /api/tags`, `GET /v1/models` | `options.num_predict` (native) / `max_tokens` (`/v1`) | native `format: {schema}` | `format:"json"` | `done_reason` (в доке задокументировано только `"stop"`) |
| 14 | LM Studio | `http://localhost:1234` (`/v1`, `/api/v1`, `/api/v0`) | опционально `Authorization: Bearer <LM_API_TOKEN>` | `GET /v1/models`, `GET /api/v1/models`, `GET /api/v0/models` | `max_tokens` | да | не удалось проверить | OpenAI-подобно (не удалось проверить) |
| 15 | OpenAI Compatible | задаёт пользователь | `Authorization: Bearer` | `GET {base}/models` (может отсутствовать) | `max_tokens` / `max_completion_tokens` | зависит от сервера | зависит от сервера | `finish_reason:"length"` |

---

## 1. OpenAI (Chat Completions и Responses API)

**Base URL / Auth**
- Base URL: `https://api.openai.com/v1` [OA-7]
- `Authorization: Bearer <OPENAI_API_KEY>`. Опционально: `OpenAI-Organization: <org_id>`, `OpenAI-Project: <project_id>` [OA-7]

**Список моделей**
- `GET /v1/models` [OA-3]
- Ответ: `{"object":"list","data":[{"id","object":"model","created","owned_by","shutdown_date"}]}`. Поле `shutdown_date` — строка или `null` [OA-3]
- Display name отсутствует, есть только `id` [OA-3]
- Пагинации нет: у метода нет query-параметров [OA-3]

**Генерация — Chat Completions:** `POST /v1/chat/completions` [OA-1]
- `messages`: роли `developer`, `system`, `user`, `assistant`, `tool`. Для o1 и новее: «`developer` messages replace the previous `system` messages» [OA-1]
- Лимит вывода:
  - `max_completion_tokens` — «upper bound … including visible output tokens and reasoning tokens» [OA-1]
  - `max_tokens` — «deprecated in favor of `max_completion_tokens`, and is not compatible with o-series models» [OA-1]
- `reasoning_effort`: `none|minimal|low|medium|high|xhigh|max`. Не все модели поддерживают все значения [OA-1]
- `temperature`: 0..2 [OA-1]
- Текущие модели в enum: `gpt-6-astra`, `gpt-6-sol`, `gpt-6-luna` и ещё около 85 [OA-1]

**Генерация — Responses API:** `POST /v1/responses` [OA-2]
- `input`: строка или массив items [OA-2]
- System prompt передаётся в `instructions`. При `previous_response_id` instructions не переносятся [OA-2]
- `max_output_tokens`: «including visible output tokens and reasoning tokens» [OA-2]
- Прочие параметры: `reasoning: {effort}`, `text: {format, verbosity}`, `temperature` (0..2) [OA-2]

**Structured output**

JSON Schema, Chat [OA-1]:
```json
"response_format": {"type":"json_schema","json_schema":{"name":"x","description":"...","schema":{...},"strict":true}}
```
- `name`: a-z, A-Z, 0-9, `_`, `-`, не длиннее 64 символов.

JSON Schema, Responses [OA-2]:
```json
"text": {"format": {"type":"json_schema","name":"x","schema":{...},"strict":true}}
```

JSON mode:
- Chat: `{"type":"json_object"}`. Responses: `text.format = {"type":"json_object"}` («Not recommended for gpt-4o and newer models») [OA-1][OA-2]
- Модели нужно явно велеть выдавать JSON: «the API will throw an error if the string "JSON" does not appear somewhere in the context» [OA-4]

Ограничения `strict: true` [OA-4]:
- Корень схемы — `object`, и это не `anyOf`.
- Все поля в `required`. Опциональность задаётся union-типом с `null`.
- `additionalProperties: false` в каждом объекте.
- До 5000 свойств и до 10 уровней вложенности.
- Суммарная длина имён свойств, definitions, enum и const — не больше 120 000 символов.
- Не больше 1000 значений enum. Для одного строкового enum с числом значений больше 250 — не больше 15 000 символов.
- Порядок ключей в ответе совпадает с порядком в схеме.

Поддерживается [OA-4]:
- Типы: String, Number, Boolean, Integer, Object, Array, Enum, anyOf.
- Для строк: `pattern`, `format` (`date-time`, `time`, `date`, `duration`, `email`, `hostname`, `ipv4`, `ipv6`, `uuid`).
- Для чисел: `multipleOf`, `maximum`, `exclusiveMaximum`, `minimum`, `exclusiveMinimum`.
- Для массивов: `minItems`, `maxItems`.
- `$defs`/definitions и рекурсия (`#`).

Не поддерживается [OA-4]:
- `allOf`, `not`, `dependentRequired`, `dependentSchemas`, `if`/`then`/`else`.
- Для fine-tuned моделей дополнительно: `minLength`, `maxLength`, `pattern`, `format`, `minimum`, `maximum`, `multipleOf`, `patternProperties`, `minItems`, `maxItems`.
- Неподдерживаемая схема при `strict: true` возвращает ошибку.

Модели: «starting with GPT-4o» (`gpt-4o-mini`, `gpt-4o-2024-08-06` и новее). Более старые модели — только JSON mode [OA-4].

**Разбор ответа**

Chat [OA-1]:
- Текст: `choices[i].message.content` (может быть `null`). Отказ модели: `choices[i].message.refusal`.
- `finish_reason`: `stop`, **`length`** (достигнут лимит токенов), `tool_calls`, `content_filter`, `function_call` (deprecated).
- `usage`: `prompt_tokens`, `completion_tokens`, `total_tokens`, `completion_tokens_details.reasoning_tokens`, `prompt_tokens_details.cached_tokens`.

Responses [OA-2]:
- Текст: элементы `output[]` с `type:"message"` → `content[]` с `type:"output_text"` → `text`. Отказ приходит как `content[]` с `type:"refusal"`.
- **`output_text` — «SDK-only convenience property»**, в сыром JSON на него полагаться нельзя, текст надо собирать самому [OA-2].
- `status`: `completed|failed|in_progress|cancelled|queued|incomplete`.
- Обрезка: `status:"incomplete"` + `incomplete_details.reason:"max_output_tokens"`. Возможны также `max_messages`, `content_filter`, `steered` [OA-2].
- Лимит может закончиться ещё до видимого вывода (весь бюджет уходит на reasoning). OpenAI рекомендует закладывать не меньше 25 000 токенов [OA-8].
- `usage`: `input_tokens`, `output_tokens`, `total_tokens`, `input_tokens_details.cached_tokens` / `cache_write_tokens`, `output_tokens_details.reasoning_tokens`.

**Ошибки**
- Тело: `{"error":{"message","type","param","code"}}` [OA-5]
- **401**: Invalid Authentication, Incorrect API key, нет членства в организации, IP not authorized [OA-5]
- **403**: неподдерживаемая страна или регион [OA-5]
- **429** [OA-5]:
  - rate limit;
  - `type: rate_limit_error`, `code: slow_down`;
  - `code: credit_balance_exhausted`, `organization_spend_limit_exceeded`, `project_spend_limit_exceeded`, `organization_usage_limit_exceeded`;
  - у billing-ошибок `error.type` может быть `insufficient_quota`, такие ошибки ретраить бессмысленно.
- **500**; **503**: `type: service_unavailable_error`, `code: server_is_overloaded` [OA-5]
- **404**: в таблице Python-библиотеки это `NotFoundError`. Строка `code` для несуществующей модели — не удалось проверить [OA-5]
- **Переполнение контекста**: `context_length_exceeded` в текущей официальной документации **не найден** (поиск по llms-full.txt [OA-10]) → не удалось проверить.
- `Retry-After` (секунды) может приходить на 429 и 503 [OA-6]
- Заголовки лимитов [OA-6][OA-7]:
  - `x-ratelimit-limit-requests`, `x-ratelimit-limit-tokens`, `x-ratelimit-remaining-requests`, `x-ratelimit-remaining-tokens`;
  - `x-ratelimit-reset-requests` (формат `1s`), `x-ratelimit-reset-tokens` (формат `6m0s`);
  - `x-ratelimit-{limit,remaining,reset}-project-tokens`.
- Request ID: **`x-request-id`**. Также приходят `openai-processing-ms`, `openai-version` [OA-7]

**Temperature**
- Для GPT-6: «When reasoning effort is not `none`, remove `temperature`, `top_p`, and `top_logprobs`. For Chat Completions, also remove `logprobs`» [OA-9]
- GPT-6 Astra не поддерживает effort `none` [OA-9]. Практически это значит: не отправлять `temperature` для Astra.
- В гайде по evals: «`temperature` changes not supported for reasoning models» [OA-10]
- Точная форма ошибки (строки `code`/`type`) — не удалось проверить. Ожидается стандартный объект `error`.

**Источники**
- [OA-1] https://developers.openai.com/api/reference/resources/chat.md (раздел «Create chat completion»)
- [OA-2] https://developers.openai.com/api/reference/resources/responses/methods/create.md
- [OA-3] https://developers.openai.com/api/reference/resources/models/methods/list.md
- [OA-4] https://developers.openai.com/api/docs/guides/structured-outputs.md
- [OA-5] https://developers.openai.com/api/docs/guides/error-codes.md
- [OA-6] https://developers.openai.com/api/docs/guides/rate-limits.md
- [OA-7] https://developers.openai.com/api/reference/overview
- [OA-8] https://developers.openai.com/api/docs/guides/reasoning.md
- [OA-9] https://developers.openai.com/api/docs/guides/latest-model/gpt-6-astra.md
- [OA-10] https://developers.openai.com/api/llms-full.txt (полнотекстовый поиск кодов ошибок)

---

## 2. Azure OpenAI (Azure OpenAI in Microsoft Foundry Models)

**Base URL и версии API**
- **v1 API** (GA с августа 2025) [AZ-1][AZ-3]:
  - `https://{resource}.openai.azure.com/openai/v1/` или `https://{resource}.services.ai.azure.com/openai/v1/`.
  - «`api-version` is no longer a required parameter». Опционально `?api-version=v1|preview`, по умолчанию `v1`.
  - В поле `model` передаётся **имя деплоймента**.
  - Работает стандартный OpenAI-клиент со сменой `base_url`.
- **Классический путь** [AZ-2][AZ-11]:
  - `POST https://{resource}.openai.azure.com/openai/deployments/{deployment-id}/chat/completions?api-version=2024-10-21`.
  - `api-version` обязателен (формат YYYY-MM-DD).
- **Последние версии data plane** [AZ-2]:
  - Microsoft указывает для data plane: latest GA = **`v1`**, latest preview = **`v1 preview`**.
  - Последняя датированная GA-версия inference — **`2024-10-21`**, последняя датированная preview — **`2025-04-01-preview`**. Это подтверждено списком `inference/stable` и `inference/preview` в официальном репозитории спецификаций [AZ-11].
  - Control plane: GA `2025-06-01`, preview `2025-07-01-preview`.

**Auth** [AZ-2][AZ-3]
- `api-key: <key>`.
- Или `Authorization: Bearer <Microsoft Entra token>`.
- В v1 ключ также принимается в заголовке `authorization` (схема `ApiKeyAuth_`).
- OAuth scope: в REST reference — `https://cognitiveservices.azure.com/.default`; в примерах v1 — `https://ai.azure.com/.default`.

**Модели и деплойменты**
- **v1:** `GET {endpoint}/openai/v1/models`. Ответ: `{"object":"list","data":[{"id","object":"model","created","owned_by"}]}`. Пагинации нет. Request ID в заголовке `apim-request-id` [AZ-3]
- **Классический:** `GET {endpoint}/openai/models?api-version=2024-10-21` [AZ-4]
  - Возвращает «all models that are accessible by the Azure OpenAI resource … base models as well as all successfully completed fine-tuned models».
  - Поля: `id`, `status`, `capabilities{chat_completion, completion, embeddings, fine_tune, inference}`, `lifecycle_status`, `deprecation{inference, fine_tune}`, `created_at`.
  - **Это список моделей, а не деплойментов.**
- **Эндпоинта data plane для списка деплойментов сейчас нет:**
  - `/deployments` есть в authoring-спеке `2022-12-01`, но отсутствует в `2023-05-15`, `2024-10-21` и в v1-спеке `azure-v1-v1-generated.json` [AZ-11].
  - Модератор Microsoft Q&A (авг. 2025) подтверждает: data-plane эндпоинта нет, нужно использовать control plane [AZ-12, полуофициальный источник].
- **Control plane (ARM)** [AZ-5]:
  - `GET https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/deployments?api-version=2024-10-01`.
  - Нужен Entra-токен ARM, `api-key` не подходит.
  - Ответ: `{"value":[{"name","properties":{"model":{"name","version","format"},"provisioningState"},"sku":{"name","capacity"}}],"nextLink"}`. Пагинация через `nextLink`.

**Генерация**
- `POST {endpoint}/openai/v1/chat/completions` и `POST {endpoint}/openai/v1/responses` [AZ-1][AZ-6]
- Классический путь — см. выше [AZ-2]
- Параметры повторяют OpenAI: `messages` (есть `developer`), `max_completion_tokens`, `max_tokens` (deprecated, «not compatible with o1 series models»), `reasoning_effort`, `response_format` [AZ-6]
- Для reasoning-моделей в Chat работает только `max_completion_tokens`, в Responses — `max_output_tokens` [AZ-8]

**Structured output** [AZ-7]
- Формат как у OpenAI: Chat — `response_format`, Responses — `text.format`.
- Версии: «API version `2024-08-01-preview` is the first version that supports structured outputs». Поддерживается и в v1. В спеке GA `2024-10-21` есть `json_schema` и `max_completion_tokens` [AZ-11].
- Ограничения Azure (жёстче, чем у OpenAI):
  - до **100** свойств всего и до **5** уровней вложенности;
  - все поля `required`, `additionalProperties:false`, корень не `anyOf`.
- Неподдерживаемые ключевые слова:
  - String: `minLength`, `maxLength`, `pattern`, `format`;
  - Number: `minimum`, `maximum`, `multipleOf`;
  - Object: `patternProperties`, `unevaluatedProperties`, `propertyNames`, `minProperties`, `maxProperties`;
  - Array: `unevaluatedItems`, `contains`, `minContains`, `maxContains`, `minItems`, `maxItems`, `uniqueItems`.
- «Structured outputs are not supported with parallel function calls» — ставить `parallel_tool_calls:false`.
- Не поддерживается в сценариях Bring your own data, Assistants/Agents и для `gpt-4o-audio-preview`.
- Модели: в списке, среди прочих, `gpt-5.x`, `gpt-4.1*`, `gpt-4o` (2024-08-06, 2024-11-20), `o1`, `o3*`, `o4-mini`. Для GPT-6 в таблице reasoning structured outputs отмечены ✅ [AZ-8].

**Разбор ответа**
- Как у OpenAI [AZ-6].
- Дополнительно: `finish_reason:"content_filter"`, если completion отфильтрован. Если отфильтрован промпт, приходит HTTP 400 [AZ-10].

**Ошибки**
- v1: тело `{"error":{"code","message","param","type","inner_error"}}`, Request ID в `apim-request-id` [AZ-3][AZ-6]
- Классический authoring: Microsoft REST-формат `{"error":{"code","message","target","details","innererror"}}` [AZ-4]
- Фильтр промпта [AZ-10]:
  ```json
  {"error":{"message":"The response was filtered","type":null,"param":"prompt","code":"content_filter","status":400}}
  ```
- Лимиты [AZ-9]:
  - 429 с заголовком **`retry-after-ms`** (миллисекунды); SDK учитывает и `retry-after`.
  - Заголовки: `x-ratelimit-limit-requests`, `x-ratelimit-limit-tokens`, `x-ratelimit-remaining-requests`, `x-ratelimit-remaining-tokens`, `x-ratelimit-reset-requests`, `x-ratelimit-reset-tokens`.
- 404 для несуществующего деплоймента (`DeploymentNotFound`) описан только в Microsoft Q&A [AZ-12], не в reference → не удалось проверить по официальной спецификации.
- Код переполнения контекста — не удалось проверить.

**Temperature** [AZ-8]
- «Reasoning models other than GPT-6 Astra don't support the following parameters: `temperature`, `top_p`, `presence_penalty`, `frequency_penalty`, `logprobs`, `top_logprobs`, `logit_bias`, `max_tokens`».
- При этом в таблице GPT-6 для astra, sol и luna у `temperature` стоит ✅. Документация здесь частично противоречит сама себе.
- Форма ошибки — не удалось проверить.

**Источники**
- [AZ-1] https://learn.microsoft.com/en-us/azure/foundry/openai/api-version-lifecycle
- [AZ-2] https://learn.microsoft.com/en-us/azure/foundry/openai/reference
- [AZ-3] https://learn.microsoft.com/en-us/rest/api/microsoft-foundry/azureopenai/models
- [AZ-4] https://learn.microsoft.com/en-us/rest/api/azureopenai/models/list?view=rest-azureopenai-2024-10-21
- [AZ-5] https://learn.microsoft.com/en-us/rest/api/aiservices/accountmanagement/deployments/list
- [AZ-6] https://learn.microsoft.com/en-us/azure/foundry/openai/latest (canonical: https://learn.microsoft.com/en-us/rest/api/microsoft-foundry/azureopenai/chat)
- [AZ-7] https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/structured-outputs
- [AZ-8] https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning
- [AZ-9] https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/quota
- [AZ-10] https://learn.microsoft.com/en-us/azure/ai-foundry/openai/concepts/content-filter (canonical: .../azure/foundry-classic/foundry-models/concepts/content-filter)
- [AZ-11] Официальные спецификации: https://github.com/Azure/azure-rest-api-specs/tree/main/specification/cognitiveservices/data-plane/AzureOpenAI (inference/stable, authoring/stable/2022-12-01 vs 2023-05-15, 2024-10-21) и https://github.com/Azure/azure-rest-api-specs/blob/main/specification/ai/data-plane/OpenAI.v1/azure-v1-v1-generated.json
- [AZ-12] https://learn.microsoft.com/en-us/answers/questions/5521066/azure-ai-foundry-azure-openai-api-endpoint-for-lis (Microsoft Q&A, ответ модератора, полуофициальный)

---

## 3. Anthropic (Messages API)

**Base URL / Auth** [AN-1]
- Base URL: `https://api.anthropic.com`.
- Авторизация:
  - `Authorization: Bearer <API key | WIF token>` — обязателен, если не задан `x-api-key`;
  - `x-api-key: <key>` — «Legacy fallback for `Authorization`, still supported».
- Обязательные заголовки: **`anthropic-version: 2023-06-01`** и `content-type: application/json`.
- Опционально `anthropic-workspace-id` (нужен для multi-workspace ключей).
- Версия `2023-06-01` — последняя в истории версий [AN-10].

**Список моделей** [AN-3][AN-1]
- `GET /v1/models`.
- Пагинация курсорная: `after_id`, `before_id`, `limit` (по умолчанию 20, диапазон 1..1000).
- В ответе: `has_more`, `first_id`, `last_id`. Для следующей страницы передать `after_id = last_id`.
- Поля элемента `data[]`: `type:"model"`, `id`, **`display_name`**, `created_at`, `max_input_tokens`, `max_tokens`, `capabilities` (в том числе `structured_outputs.supported`, `effort`, `thinking`).
- Порядок: новые модели идут первыми.
- Заголовок `anthropic-beta` на этом методе помечен как deprecated [AN-3].

**Генерация:** `POST /v1/messages` [AN-2]
- `model`.
- `max_tokens` (**обязателен**; `0` — только прогрев кэша).
- `messages` (роли `user`/`assistant`).
- **system — отдельное верхнеуровневое поле `system`**: строка или массив `{type:"text", text}`.
- `output_config: {effort, format}`, `stop_sequences`, `tool_choice`, `tools`.
- Prefill последнего assistant-сообщения не поддерживается на моделях Claude 4.6 и новее, а также на Mythos Preview: возвращается 400 [AN-6].
- Максимумы вывода: у Fable 5.1, Opus 5.5 и Sonnet 5 — 128K [AN-12].

**Structured output (JSON Schema)** [AN-4]
- Формат запроса:
  ```json
  "output_config": {"format": {"type":"json_schema","schema":{...}}}
  ```
- Статус **GA**: «beta headers are no longer required».
- Старый параметр `output_format` deprecated. Он работает только с beta-заголовком `structured-outputs-2025-11-13`, без него возвращается 400.
- Модели (featureMetadata): `claude-fable-5-1`, `claude-mythos-5-1`, `claude-fable-5`, `claude-mythos-5`, `claude-mythos-preview`, `claude-opus-5-5`, `claude-opus-5`, `claude-opus-4-8`, `claude-opus-4-7`, `claude-opus-4-6`, `claude-sonnet-5`, `claude-sonnet-4-6`, `claude-sonnet-4-5-20250929`, `claude-opus-4-5-20251101`, `claude-haiku-4-5-20251001`. Программно проверяется через `capabilities.structured_outputs` [AN-3].
- Поддерживается:
  - типы object, array, string, integer, number, boolean, null;
  - `enum` (только примитивы), `const`;
  - `anyOf` и `allOf` (`allOf` без `$ref`);
  - `$ref`/`$def`/`definitions` (только внутренние);
  - `default`, `required`;
  - **`additionalProperties` должен быть `false`**;
  - форматы `date-time`, `time`, `date`, `duration`, `email`, `hostname`, `uri`, `ipv4`, `ipv6`, `uuid`;
  - `minItems` только со значениями 0 или 1.
- Не поддерживается: рекурсивные схемы, сложные типы в enum, внешние `$ref`, `minimum`/`maximum`/`multipleOf`, `minLength`/`maxLength`, прочие ограничения массивов. Всё это даёт 400.
- Regex: без backreferences, lookaround и `\b`.
- **В отличие от OpenAI, делать все поля required не обязательно.** Но в ответе required-свойства идут первыми.
- Лимиты сложности на запрос:
  - не больше 20 strict-tools;
  - не больше 24 опциональных параметров суммарно;
  - не больше 16 параметров с union-типами.
- Несовместимо с Citations (400) и с message prefilling.
- Регистр значений enum/const не гарантирован.
- `stop_reason:"refusal"` или `"max_tokens"` означает, что ответ может не соответствовать схеме.

**Альтернатива — принудительный tool use** [AN-5][AN-6]
- `tool_choice: {"type":"tool","name":"..."}` или `{"type":"any"}`, плюс `strict: true` в определении инструмента.
- **Не работает:**
  - с ручным extended thinking (`thinking:{type:"enabled"}`);
  - на Claude Opus 5.5, Claude Fable 5.1 и Claude Mythos 5.1. Там возвращается 400 `invalid_request_error`: «tool_choice: type "tool" and "any" are not supported for this model.»
- Для этих моделей Anthropic рекомендует `auto` + strict tool use или structured outputs.

**Разбор ответа** [AN-2][AN-8][AN-9]
- Текст: `content[]` → блоки с `type:"text"` → `text`. Бывают также `thinking`, `tool_use` и другие блоки.
- `stop_reason`:
  - `end_turn`;
  - **`max_tokens`** — обрезка по лимиту;
  - `stop_sequence`, `tool_use`, `pause_turn`;
  - `refusal` (подробности в `stop_details`);
  - `model_context_window_exceeded` — ответ заполнил контекстное окно, считать его обрезанным.
- `usage`: `input_tokens`, `output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `output_tokens_details`, `service_tier`. Всего входных токенов = input + cache_creation + cache_read.

**Ошибки** [AN-6][AN-7][AN-9]
- Тело:
  ```json
  {"type":"error","error":{"type":"not_found_error","message":"..."},"request_id":"req_..."}
  ```
- Коды:
  - 400 `invalid_request_error` (в том числе при достижении spend limit);
  - **401 `authentication_error`**;
  - 402 `billing_error`;
  - 403 `permission_error`;
  - **404 `not_found_error`**;
  - 409 `conflict_error`;
  - 413 `request_too_large` (Messages API — 32 MB);
  - **429 `rate_limit_error`**;
  - 500 `api_error`;
  - 504 `timeout_error`;
  - **529 `overloaded_error`**.
- Контекст:
  - если вход сам по себе больше окна — 400 `invalid_request_error` («prompt is too long») на любой модели;
  - на Claude 4.5+ `input + max_tokens > окна` принимается и заканчивается `stop_reason:"model_context_window_exceeded"`.
- Заголовки лимитов:
  - `retry-after` (секунды; не приходит при 429 из-за spend cap);
  - `anthropic-ratelimit-{requests,tokens,input-tokens,output-tokens}-{limit,remaining,reset}`, где reset в формате RFC 3339.
- Request ID: заголовок **`request-id`** и поле `request_id` в теле ошибки.

**Temperature** [AN-2]
- «Models released after Claude Opus 4.6 do not support setting temperature. A value of 1.0 will be accepted for backwards compatibility, all other values will be rejected with a 400 error.»
- `top_p`: принимается только ≥ 0.99, иначе 400.
- `top_k`: на этих моделях любое значение даёт 400.

**OpenAI-совместимый слой** [AN-11]
- Base URL `https://api.anthropic.com/v1/`.
- `response_format` там **игнорируется**, `strict` у tools тоже.
- `temperature` больше 1 обрезается до 1.

**Источники**
- [AN-1] https://platform.claude.com/docs/en/api/overview
- [AN-2] https://platform.claude.com/docs/en/api/messages/create
- [AN-3] https://platform.claude.com/docs/en/api/models/list
- [AN-4] https://platform.claude.com/docs/en/build-with-claude/structured-outputs
- [AN-5] https://platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools
- [AN-6] https://platform.claude.com/docs/en/api/errors
- [AN-7] https://platform.claude.com/docs/en/api/rate-limits
- [AN-8] https://platform.claude.com/docs/en/build-with-claude/handling-stop-reasons
- [AN-9] https://platform.claude.com/docs/en/build-with-claude/context-windows
- [AN-10] https://platform.claude.com/docs/en/api/versioning
- [AN-11] https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk
- [AN-12] https://platform.claude.com/docs/en/models/overview

---

## 4. Google Gemini API (generativelanguage.googleapis.com)

> В индексе документации generateContent теперь описан как «legacy Gemini Generate Content API». Рекомендуемый путь — Interactions API (`POST /v1beta/interactions`) с полем `response_format` в snake_case [G-9][G-10]. Ниже описан generateContent, как было запрошено.

**Base URL / Auth** [G-1][G-2]
- Base URL: `https://generativelanguage.googleapis.com/v1beta`.
- Заголовок **`x-goog-api-key: <KEY>`**. В shell-примере models.list ключ передаётся ещё и как `?key=`.

**Список моделей** [G-2]
- `GET /v1beta/models?pageSize=&pageToken=`.
- `pageSize` по умолчанию 50, максимум 1000. При пагинации остальные параметры должны совпадать с исходным запросом.
- Ответ: `{"models":[...],"nextPageToken"}`. Если `nextPageToken` нет — страниц больше нет.
- Поля Model: `name` (формат `models/{model}`), `baseModelId`, `version`, **`displayName`**, `description`, `inputTokenLimit`, `outputTokenLimit`, `supportedGenerationMethods[]` (фильтровать по `generateContent`), `thinking`, `temperature`, `maxTemperature`, `topP`, `topK`.

**Генерация** [G-3]
- `POST /v1beta/{model=models/*}:generateContent`, например `/v1beta/models/gemini-3.5-flash:generateContent`.
- Тело:
  - `contents:[{role:"user"|"model", parts:[{text}]}]`;
  - system — отдельное поле `systemInstruction: {parts:[{text}]}` («Currently, text only»);
  - `generationConfig: {maxOutputTokens, temperature, topP, topK, stopSequences (≤5), candidateCount, responseMimeType, responseSchema, responseJsonSchema, responseFormat, thinkingConfig, seed, ...}`.
- `thinkingConfig` на модели без thinking даёт ошибку [G-3]. Одновременные `thinking_level` и `thinking_budget` дают 400 [G-7].

**Structured output**
- **Новый способ** (REST-пример в гайде) [G-4][G-3]:
  ```json
  "generationConfig": {"responseFormat": {"text": {"mimeType": "application/json", "schema": {<JSON Schema>}}}}
  ```
  - В reference у `TextResponseFormat.mimeType` enum `APPLICATION_JSON` / `TEXT_PLAIN`, а в примере гайда — строка `"application/json"`. Какая форма правильная, не удалось проверить; нужен тест.
- **Альтернативный способ** (есть в Go-примере того же гайда) [G-4][G-3]:
  - `responseMimeType:"application/json"` + JSON Schema в `responseJsonSchema`.
  - В reference поле `_responseJsonSchema` помечено deprecated, а `responseJsonSchema` — «An internal detail». Документация противоречива.
  - Для JSON-Schema-варианта перечислены поддерживаемые ключевые слова: `$id`, `$defs`, `$ref`, `$anchor`, `type`, `format`, `title`, `description`, `enum` (строки и числа), `items`, `prefixItems`, `minItems`, `maxItems`, `minimum`, `maximum`, `anyOf`, `oneOf` (трактуется как anyOf), `properties`, `additionalProperties`, `required`; нестандартное `propertyOrdering`.
  - Циклические `$ref` допускаются только в не-required свойствах.
- **`responseSchema`** — подмножество OpenAPI 3.0; **deprecated** («Use `responseFormat` instead»); требует `responseMimeType` [G-3].
- **JSON mode без схемы:** `responseMimeType:"application/json"`, или `responseFormat.text.mimeType` без `schema` [G-3].
- Подмножество схемы по гайду [G-4]:
  - `type`: string, number, integer, boolean, object, array, null (null — через массив типов);
  - `title`, `description`;
  - object: `properties`, `required`, `additionalProperties`;
  - string: `enum`, `format` (`date-time`, `date`, `time`);
  - number: `enum`, `minimum`, `maximum`;
  - array: `items`, `prefixItems`, `minItems`, `maxItems`.
- Неподдерживаемые свойства **игнорируются**. Очень большие или глубокие схемы могут быть отклонены. Порядок ключей в выводе — как в схеме [G-4].
- Модели: Gemini 3.1 Flash-Lite, 3.1 Pro Preview, 3.5 Flash, 3.1 Flash-Lite Preview, 2.5 Pro/Flash/Flash-Lite, 2.0 Flash/Flash-Lite (для 2.0 нужен `propertyOrdering`) [G-4].

**Разбор ответа** [G-3]
- Текст: `candidates[0].content.parts[].text`. Части с `thought:true` — мысли модели, их не нужно включать в ответ.
- Если промпт заблокирован, `candidates` отсутствуют, а в `promptFeedback.blockReason` — `SAFETY`, `OTHER`, `BLOCKLIST`, `PROHIBITED_CONTENT` или `IMAGE_SAFETY`.
- `finishReason`: `STOP`, **`MAX_TOKENS`** (обрезка), `SAFETY`, `RECITATION`, `LANGUAGE`, `OTHER`, `BLOCKLIST`, `PROHIBITED_CONTENT`, `SPII`, `MALFORMED_FUNCTION_CALL`, `IMAGE_*`, `NO_IMAGE`, `UNEXPECTED_TOOL_CALL`, `TOO_MANY_TOOL_CALLS`, `MISSING_THOUGHT_SIGNATURE`, `MALFORMED_RESPONSE`, `ESCALATION`, `PUP_LIMITED_DISABLED`. Пояснение приходит в `finishMessage`.
- `usageMetadata`: `promptTokenCount`, `cachedContentTokenCount`, `candidatesTokenCount`, `toolUsePromptTokenCount`, `thoughtsTokenCount`, `totalTokenCount` (= prompt + thoughts + candidates), плюс `*TokensDetails`.
- Также приходят `modelVersion` и `responseId`.

**Ошибки** [G-5][G-6]
- Тело (gRPC status):
  ```json
  {"error":{"code":400,"message":"...","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"API_KEY_INVALID",...}]}}
  ```
- **Неверный API key → 400 `INVALID_ARGUMENT`** с `reason:"API_KEY_INVALID"`, а **не 401**.
- Коды:
  - 400 `FAILED_PRECONDITION` (free tier недоступен в регионе или не включён billing);
  - 402 `RESOURCE_EXHAUSTED` (закончились Prepay-кредиты; не ретраить);
  - 403 `PERMISSION_DENIED`;
  - 404 `NOT_FOUND`;
  - **429 `RESOURCE_EXHAUSTED`** (RPM/TPM/RPD/spend);
  - 499 `CANCELLED`;
  - 500 `INTERNAL` (пример причины — «Your input context is too long»);
  - 503 `UNAVAILABLE`;
  - 504 `DEADLINE_EXCEEDED`.
- Ретраить с экспоненциальной задержкой 429, 408 и 5xx; не ретраить 400, 402, 403 [G-6].
- Не удалось проверить: отдельный код переполнения контекста, заголовки rate-limit / `Retry-After`, заголовок с request ID. В найденных страницах они не описаны.

**Temperature**
- Диапазон [0.0, 2.0]; значение по умолчанию зависит от модели (`Model.temperature`) [G-3].
- Для Gemini 3 рекомендуется оставлять `1.0`: «Changing the temperature (setting it below 1.0) may lead to unexpected behavior, such as looping» [G-7]. Отказа (ошибки) при этом нет.

**OpenAI-совместимый слой (beta)** [G-8]
- Base URL `https://generativelanguage.googleapis.com/v1beta/openai/`.
- Есть `/chat/completions`, structured output, list/retrieve models.
- `reasoning_effort` маппится на `thinking_level` / `thinking_budget`.

**Источники**
- [G-1] https://ai.google.dev/api (md: https://ai.google.dev/api.md.txt)
- [G-2] https://ai.google.dev/api/models
- [G-3] https://ai.google.dev/api/generate-content
- [G-4] https://ai.google.dev/gemini-api/docs/generate-content/structured-output
- [G-5] https://ai.google.dev/gemini-api/docs/generate-content/api-errors
- [G-6] https://ai.google.dev/gemini-api/docs/troubleshooting
- [G-7] https://ai.google.dev/gemini-api/docs/generate-content/gemini-3
- [G-8] https://ai.google.dev/gemini-api/docs/openai
- [G-9] https://ai.google.dev/gemini-api/docs/llms.txt
- [G-10] https://ai.google.dev/gemini-api/docs/structured-output

---

## 5. Mistral (La Plateforme)

**Base URL / Auth**
- Base URL: `https://api.mistral.ai`, пути начинаются с `/v1/...` [M-1]
- Региональные endpoint'ы: `https://api.eu.mistral.ai` (EU), `https://api.us.mistral.ai` (US) [M-5]
  - Цена ×1.1.
  - Доступны только модели, размещённые в этом регионе.
- `Authorization: Bearer <key>` [M-1][M-4]

**Список моделей** [M-1]
- `GET /v1/models`, необязательные query `provider` и `model`.
- Схема `ModelList`: `{"object":"list","data":[BaseModelCard|FTModelCard]}` с дискриминатором `type` (`base` / `fine-tuned`).
- Но пример в той же спеке показывает **голый массив** → парсить нужно оба варианта.
- Поля: `id`, `object`, `created`, `owned_by`, `capabilities{completion_chat, completion_fim, function_calling, fine_tuning, vision, classification}`, **`name`**, `description`, `max_context_length`, `aliases`, `deprecation`, `deprecation_replacement_model`, `default_model_temperature`, `type`.
- Пагинации нет.

**Генерация** [M-1][M-6]
- `POST /v1/chat/completions`.
- `messages` (system — сообщение с ролью `system`).
- **`max_tokens`**: сумма промпта и `max_tokens` не должна превышать контекст модели.
- `temperature`: схема допускает 0..1.5; рекомендуется 0.0–0.7; значение по умолчанию зависит от модели.
- Прочее: `top_p`, `random_seed`, `response_format`, `reasoning_effort`, `prompt_mode`, `safe_prompt`.
- `reasoning_effort` (`high` / `none`) поддерживают, например, `mistral-small-latest` и `mistral-medium-3-5` [M-6].

**Structured output** [M-1][M-2][M-3]
- JSON Schema:
  ```json
  {"type":"json_schema","json_schema":{"name":"book","description":"...","schema":{...},"strict":true}}
  ```
  - `name` и `schema` обязательны; `strict` по умолчанию `false`.
- JSON mode: `{"type":"json_object"}`. Модели обязательно нужно сказать, чтобы отвечала в JSON («you MUST also instruct the model»).
- В режиме Custom Structured Outputs к system prompt всегда добавляется текст «Your output should be an instance of a JSON object following this schema: {{ json_schema }}» [M-2].
- Поддерживают все текущие модели, кроме `codestral-mamba` [M-2].
- Ограничения strict (обязательность required, неподдерживаемые ключевые слова) — не удалось проверить: в документации не описаны. В примерах используется `additionalProperties:false` и все поля в required.

**Разбор ответа** [M-1][M-6]
- `choices[0].message.content` — **строка или массив чанков**. При `reasoning_effort:"high"` приходят `{"type":"thinking","thinking":[{"type":"text","text":...}]}` и `{"type":"text","text":...}`.
- `finish_reason`: `stop`, **`length`**, **`model_length`**, `error`, `tool_calls`. Значение `model_length` в документации не пояснено; вероятно, это достижение лимита контекста модели.
- `usage`: `prompt_tokens`, `completion_tokens`, `total_tokens`, `prompt_audio_seconds`.

**Ошибки** [M-4][M-1]
- **Плоское тело** (без обёртки `error`):
  ```json
  {"object":"error","message":"...","type":"invalid_request_error","param":"model","code":"unknown_model"}
  ```
  - `type`: `invalid_request_error`, `authentication_error`, `rate_limit_error`, `server_error`.
- Коды:
  - 400;
  - **401** (нет или неверный `Authorization: Bearer`);
  - 403;
  - **404** (неверный model ID или путь);
  - 422 (валидация, `HTTPValidationError`);
  - **429** — «Check the `Retry-After` response header»;
  - 500, 502, 503, 504.
- Не удалось проверить: код переполнения контекста, заголовок request ID, заголовки `x-ratelimit-*`.

**Temperature**
- Моделей, которые отвергают `temperature`, в документации нет.
- Error glossary приводит пример 400 из-за «`temperature` outside 0 to 1 range», хотя схема допускает до 1.5. Это расхождение в самой документации [M-4][M-1].

**Источники**
- [M-1] https://docs.mistral.ai/openapi.yaml
- [M-2] https://docs.mistral.ai/studio/conversations/structured-output/custom
- [M-3] https://docs.mistral.ai/studio/conversations/structured-output/json_mode
- [M-4] https://docs.mistral.ai/resources/error-glossary
- [M-5] https://docs.mistral.ai/inference/regional-inference
- [M-6] https://docs.mistral.ai/studio/conversations/reasoning

---

## 6. DeepSeek

**Base URL / Auth** [DS-1][DS-6]
- Base URL в формате OpenAI: **`https://api.deepseek.com`**. В формате Anthropic: `https://api.deepseek.com/anthropic`.
- `Authorization: Bearer <key>`.
- Текущие модели:
  - **`deepseek-flash`** (DeepSeek-V4.1-Flash);
  - **`deepseek-v4-pro`** (DeepSeek-V4-Pro-0813).
- Устаревшие имена `deepseek-v4-flash` и `deepseek-v4-flash-vision-exp` ещё принимаются, но обслуживаются V4.1-Flash. Имена `deepseek-chat` и `deepseek-reasoner` в текущей документации не упоминаются.

**Список моделей** [DS-3]
- `GET /models`.
- Ответ:
  ```json
  {"object":"list","data":[{"id","object":"model","owned_by","name","context_window","max_output_tokens","input_modalities","output_modalities","effort":{"supported_levels","default_level"},"api_capabilities"}]}
  ```
  - `name` — отображаемое имя.
- Пагинации нет.

**Генерация** [DS-2][DS-6][DS-7]
- `POST /chat/completions`.
- Режим thinking:
  - **`thinking: {"type":"enabled"|"disabled"}`, по умолчанию `enabled`**;
  - `reasoning_effort`: `none`, `low`, `high`, `max`. Для совместимости `minimal` → `low`, `medium` и `xhigh` → `high`.
- **`max_tokens`**: 1..393216 (384K). По умолчанию 8K в non-thinking, 64K в thinking, 128K при `reasoning_effort=max`.
- Контекст 1M.

**Structured output** [DS-2][DS-4]
- Поддерживается **только** `response_format: {"type":"json_object"}` (допустимые значения — `text` и `json_object`). **`json_schema` не поддерживается.**
- Требования:
  - слово «json» в system- или user-промпте плюс пример нужного формата;
  - разумный `max_tokens`, чтобы не обрезать JSON;
  - API «may occasionally return empty content».

**Разбор ответа** [DS-2][DS-7]
- Текст: `choices[0].message.content`. Рассуждения — в `message.reasoning_content`.
- `finish_reason`: `stop`, **`length`**, `content_filter`, `tool_calls`, `insufficient_system_resource`, `aborted`.
- `usage`: `prompt_tokens` (= hit + miss), `completion_tokens`, `total_tokens`, `prompt_cache_hit_tokens`, `prompt_cache_miss_tokens`, `prompt_tokens_details.cached_tokens`, `completion_tokens_details.reasoning_tokens`.
- В запросах с `tools` значение `reasoning_content` нужно возвращать в историю полностью, иначе API вернёт ошибку [DS-7].

**Ошибки** [DS-5][DS-8]
- Коды:
  - 400 invalid format;
  - **401** authentication fails;
  - **402** insufficient balance;
  - 422 invalid parameters;
  - **429** rate limit (лимит конкурентности на аккаунт: `deepseek-flash` 2500, `deepseek-v4-pro` 500);
  - 500;
  - 503 overloaded.
- Не удалось проверить: форма JSON-тела ошибки, код «model not found», код переполнения контекста, заголовок request ID, rate-limit заголовки.

**Temperature** [DS-2][DS-7]
- Диапазон 0..2, по умолчанию 1.
- «Has no effect in thinking mode». В thinking mode `temperature`, `presence_penalty` и `frequency_penalty` не поддерживаются, но **ошибки не вызывают**.
- `top_p` действует только в thinking (эффективно 0.95–1.0).

**Источники**
- [DS-1] https://api-docs.deepseek.com/
- [DS-2] https://api-docs.deepseek.com/api/create-chat-completion
- [DS-3] https://api-docs.deepseek.com/api/list-models
- [DS-4] https://api-docs.deepseek.com/guides/json_mode
- [DS-5] https://api-docs.deepseek.com/quick_start/error_codes
- [DS-6] https://api-docs.deepseek.com/quick_start/pricing
- [DS-7] https://api-docs.deepseek.com/guides/thinking_mode
- [DS-8] https://api-docs.deepseek.com/quick_start/rate_limit

---

## 7. Qwen / Alibaba Cloud Model Studio (DashScope, OpenAI-compatible mode)

**Base URL по регионам** [Q-2][Q-1]

Model Studio ввёл **workspace-специфичные домены** и просит мигрировать на них.

| Регион | base_url (OpenAI-compatible) |
|---|---|
| Singapore (международный) | `https://{WorkspaceId}.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1` (старый: `https://dashscope-intl.aliyuncs.com/compatible-mode/v1`) |
| US (Virginia) | в API reference: `https://{WorkspaceId}.us-east-1.maas.aliyuncs.com/compatible-mode/v1`; на странице совместимости: `https://dashscope-us.aliyuncs.com/compatible-mode/v1` |
| China (Beijing) | `https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1` (старый: `https://dashscope.aliyuncs.com/compatible-mode/v1`) |
| Hong Kong (China) | `https://{WorkspaceId}.cn-hongkong.maas.aliyuncs.com/compatible-mode/v1` (старый: `https://cn-hongkong.dashscope.aliyuncs.com`) |
| Germany (Frankfurt) | `https://{WorkspaceId}.eu-central-1.maas.aliyuncs.com/compatible-mode/v1` |
| Japan (Tokyo) | `https://{WorkspaceId}.ap-northeast-1.maas.aliyuncs.com/compatible-mode/v1` |

- В списке на [Q-1] для Beijing по ошибке указан домен `ap-southeast-1`, но в тексте о миграции там же — `cn-beijing`.
- **API key привязан к региону.** Ключ из чужого региона даёт `401 Incorrect API key provided` / `invalid_api_key` [Q-1].

**Auth:** `Authorization: Bearer <DASHSCOPE_API_KEY>` (ключ начинается с `sk-`) [Q-1][Q-4]

**Список моделей:** **не удалось проверить.** Эндпоинт `GET .../compatible-mode/v1/models` в официальной документации (compat, API reference, error codes, structured output) не описан. Список моделей нужно вести вручную или сделать опциональным.

**Генерация** [Q-2]
- `POST {base}/chat/completions`.
- `messages`: «Only `messages[0]` supports the system role» [Q-1].
- **`max_completion_tokens`** включает chain-of-thought и ответ. Для thinking-моделей рекомендуется именно он.
- `max_tokens` помечен «to be deprecated»: для большинства моделей он ограничивает только ответ, без reasoning.
- **Обязательные и нестандартные параметры:**
  - `enable_thinking` (boolean). При прямом HTTP-вызове кладётся **в корень тела**, не в `extra_body`. У многих моделей он по умолчанию включён: Qwen3.5–3.8, deepseek-v4, glm-5.
  - Часть моделей работает только в стриминге. Non-streaming вызов с включённым thinking даёт 400 `parameter.enable_thinking must be set to false for non-streaming calls` или «This model only support stream mode» [Q-4][Q-5].
  - `glm-5.3` — только `enable_thinking=true`. Для MiniMax используется параметр `thinking` [Q-2].

**Structured output** [Q-3][Q-2][Q-4]
- JSON mode: `{"type":"json_object"}`. В messages обязательно слово «JSON», иначе ошибка: `'messages' must contain the word 'json' in some form, to use 'response_format' of type 'json_object'`.
- JSON Schema:
  ```json
  {"type":"json_schema","json_schema":{"name":"...","strict":true,"schema":{...,"additionalProperties":false}}}
  ```
  - Слово «JSON» не требуется.
  - Поддерживают только Qwen3.7-Plus, 3.7-Flash, 3.7-Max, 3.8-Max, 3.8-Flash.
  - **«Singapore region models are not supported yet»**.
  - Мультимодальный ввод тихо откатывается к `json_object`.
- Structured output вместе с включённым thinking даёт ошибку. Решение — `enable_thinking:false` [Q-4].
- Не рекомендуется задавать `max_tokens` вместе со structured output, чтобы JSON не обрезался [Q-3].
- Неподдерживаемые ключевые слова JSON Schema — не удалось проверить.

**Разбор ответа** [Q-2]
- Текст: `choices[0].message.content`. Рассуждения — в `reasoning_content`.
- `finish_reason`: `stop`, **`length`**, `tool_calls`.
- `usage`: `prompt_tokens`, `completion_tokens`, `total_tokens`, `completion_tokens_details` (есть не у всех моделей). В нём `text_tokens` включает `reasoning_tokens`.

**Ошибки** [Q-4]
- Коды в документации записываются в паре «DashScope-код / OpenAI-код»:
  - 400 `InvalidParameter`, `Arrearage` (задолженность);
  - **401 `InvalidApiKey`/`invalid_api_key`**;
  - 403 `AccessDenied`/`access_denied`, `Model.AccessDenied`;
  - **404 `ModelNotFound`/`model_not_found`**, `model_not_supported`;
  - **429 `Throttling`**, `Throttling.RateQuota`/`limit_requests`, `Throttling.AllocationQuota`/`insufficient_quota`, `Throttling.ServiceOverloaded`;
  - 500 `InternalError`/`internal_error`.
- Переполнение контекста: 400 `Range of input length should be [1, xxx]` (`InternalError.Algo.InvalidParameter`); неверный `max_tokens`: `Range of max_tokens should be [1, xxx]`.
- Не удалось проверить: точная форма JSON-тела ошибки в compat-режиме, заголовки rate-limit и request ID.

**Temperature** [Q-1]
- Диапазон [0, 2); значение 0 не рекомендуется. Отказов по моделям в документации нет.

**Источники**
- [Q-1] https://www.alibabacloud.com/help/en/model-studio/compatibility-of-openai-with-dashscope
- [Q-2] https://www.alibabacloud.com/help/en/model-studio/qwen-api-via-openai-chat-completions
- [Q-3] https://www.alibabacloud.com/help/en/model-studio/qwen-structured-output
- [Q-4] https://www.alibabacloud.com/help/en/model-studio/error-code
- [Q-5] https://www.alibabacloud.com/help/en/model-studio/deep-thinking

---

## 8. xAI

**Base URL / Auth** [X-1]
- Base URL: `https://api.x.ai` (пути `/v1/...`).
- `Authorization: Bearer <xAI API key>`.
- Management API живёт на `https://management-api.x.ai` с отдельным management-ключом.
- xAI называет свой REST API «compatible with the OpenAI REST API». Chat Completions — «OpenAI-compatible predecessor of the Responses API», для новых интеграций рекомендуется Responses [X-2].

**Список моделей** [X-3]
- `GET /v1/models`: `{"object":"list","data":[{"id","aliases","created","object":"model","owned_by","context_length","capabilities":{"reasoning_effort":[...],"default_reasoning_effort"},"prompt_text_token_price",...}]}`.
- `GET /v1/language-models`: `{"models":[{"id","aliases","version","fingerprint","input_modalities","output_modalities",...цены}]}`.
- Display name отсутствует. Пагинации нет.

**Генерация** [X-2]
- `POST /v1/chat/completions`.
- **`max_completion_tokens`** — «only applies to visible output tokens (i.e. does not apply to tokens used for reasoning…)», по умолчанию 128 000.
- `max_tokens` — DEPRECATED.
- Прочее: `reasoning_effort`, `temperature` (0..2), `response_format`, `search_parameters`.

**Structured output** [X-4]
- `response_format.type = "json_schema"` со схемой в `response_format.json_schema`. Также есть `"json_object"` и `"text"`.
- Tool calling всегда строгий: «`strict` flag is implicitly always `true`».
- Поддерживаемые типы и конструкции: string, number, integer, boolean, null, enum, const, array, object, anyOf, oneOf, allOf (одна подсхема), `$ref`/`$defs` (без циклов).
- **`additionalProperties` по умолчанию `false`**. Поля вне `required` считаются опциональными.
- Строгие форматы: `date`, `time`, `date-time`, `email`, `uuid`, `ipv4`, `ipv6`, `uri`.
- Гарантируемые пределы ограничений:
  - `minLength` / `maxLength` — до 2048;
  - `minItems` / `maxItems` — до 256;
  - `minProperties` / `maxProperties` — до 64.
- Best-effort (принимаются, но не гарантируются): `not`, `if`/`then`/`else`, `allOf` с несколькими подсхемами, прочие `format`.
- **Отклоняются с 400:** пустые `enum`/`anyOf`, свойства со схемой `true`/`false`, `maxContains`/`minContains`, `items` в виде массива.
- Regex: без backreferences, `\p{}`, `\b`, lookaround; `^` и `$` подразумеваются неявно.

**Разбор ответа** [X-2]
- Текст: `choices[0].message.content`. Также приходят `reasoning_content` и `refusal`.
- `finish_reason`: `stop`, **`length`**, `end_turn`.
- `usage`: `prompt_tokens`, `completion_tokens`, `total_tokens`, `completion_tokens_details.reasoning_tokens`, `prompt_tokens_details.cached_tokens`, `cost_in_usd_ticks`.

**Ошибки** [X-5][X-6]
- Коды:
  - 400;
  - **401** (нет заголовка или неверный токен);
  - 403;
  - **404**;
  - 405, 415;
  - 422 (неверный формат поля);
  - **429** — превышен лимит, повторять с экспоненциальной задержкой.
- 202 — deferred completion ещё не готов.
- Лимиты по тарифным уровням (tier): RPS и TPM на модель.
- Не удалось проверить: форма тела ошибки, заголовки rate-limit / `Retry-After`, заголовок request ID, код переполнения контекста.

**Temperature** [X-7][X-2]
- Reasoning-модели: «`presencePenalty`, `frequencyPenalty`, and `stop` cannot be used with reasoning models. Requests that include them return an error.»
- `temperature` в этом списке нет.

**Источники**
- [X-1] https://docs.x.ai/developers/rest-api-reference/inference
- [X-2] https://docs.x.ai/developers/rest-api-reference/inference/chat-completions
- [X-3] https://docs.x.ai/developers/rest-api-reference/inference/models
- [X-4] https://docs.x.ai/developers/model-capabilities/text/structured-outputs
- [X-5] https://docs.x.ai/developers/debugging
- [X-6] https://docs.x.ai/developers/rate-limits
- [X-7] https://docs.x.ai/developers/model-capabilities/text/reasoning

---

## 9. Groq

**Base URL / Auth** [GQ-1][GQ-4]
- Base URL: `https://api.groq.com/openai/v1`.
- `Authorization: Bearer <GROQ_API_KEY>`.

**Список моделей** [GQ-1]
- `GET /openai/v1/models`.
- Ответ: `{"object":"list","data":[{"id","object":"model","created","owned_by","active","context_window","public_apps"}]}`.
- Пагинации нет. Display name нет.

**Генерация** [GQ-1]
- `POST /openai/v1/chat/completions`.
- **`max_completion_tokens`**; `max_tokens` — deprecated.
- `n` только 1, иначе 400.
- `reasoning_effort`: `none`, `default`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`. Набор зависит от модели, неподдерживаемое значение даёт 400.
- `reasoning_format`: `hidden`, `raw`, `parsed`. Взаимоисключающе с ним работает `include_reasoning`.

**Structured output** [GQ-2]
- Формат:
  ```json
  {"type":"json_schema","json_schema":{"name":"...","strict":true,"schema":{...}}}
  ```
- **`strict:true`** (constrained decoding) работает только на `openai/gpt-oss-20b`, `openai/gpt-oss-120b` и `qwen/qwen3.8-27b`.
  - Все поля должны быть `required`; во всех объектах `additionalProperties:false`.
- **`strict:false`** (best-effort, по умолчанию) работает на `openai/gpt-oss-20b`, `openai/gpt-oss-120b`, `openai/gpt-oss-safeguard-20b` и `qwen/qwen3.8-27b`.
  - Возможна ошибка 400 «Generated JSON does not match the expected schema. Please adjust your prompt.»
- Остальные модели — только **JSON Object Mode** `{"type":"json_object"}`. Нужна явная инструкция выдавать JSON. Режим «Can error: Occasionally».
- **Streaming и tool use вместе со Structured Outputs не поддерживаются.**
- Поддерживаемые типы: string, number, boolean, integer, object, array, enum, anyOf.
- С JSON mode нельзя `reasoning_format:"raw"` — это 400 [GQ-6]. При `raw` рассуждения приходят в `content` внутри тегов `<think>`.

**Разбор ответа** [GQ-1][GQ-6]
- Текст: `choices[0].message.content`. При `reasoning_format:"parsed"` рассуждения приходят отдельно в поле `reasoning`.
- `finish_reason` — стандарт OpenAI (в примере `"stop"`). Отдельного перечня нет → `length` как признак обрезки не удалось проверить по документации Groq.
- `usage` — в формате OpenAI.

**Ошибки** [GQ-3][GQ-5][GQ-4]
- Тело: `{"error":{"message","type"}}`, например `type:"invalid_request_error"`.
- Коды:
  - 400;
  - **401**;
  - 403;
  - **404**;
  - 413;
  - 422;
  - 424;
  - **429**;
  - 498 (flex tier переполнен);
  - 499 (запрос отменён);
  - 500, 502, 503.
- Заголовки:
  - `retry-after` (секунды; приходит только на 429);
  - `x-ratelimit-limit-requests` (RPD), `x-ratelimit-limit-tokens` (TPM);
  - `x-ratelimit-remaining-requests`, `x-ratelimit-remaining-tokens`;
  - `x-ratelimit-reset-requests` (формат `2m59.56s`), `x-ratelimit-reset-tokens`.
- С 400 отклоняются `logprobs`, `logit_bias`, `top_logprobs`, `messages[].name` и `n` ≠ 1 [GQ-4].
- Не удалось проверить: код переполнения контекста, заголовок request ID.

**Temperature** [GQ-4][GQ-1]
- Диапазон 0..2, по умолчанию 1.
- `temperature: 0` превращается в `1e-8`. Отказов по моделям нет.

**Источники**
- [GQ-1] https://console.groq.com/docs/api-reference
- [GQ-2] https://console.groq.com/docs/structured-outputs
- [GQ-3] https://console.groq.com/docs/errors
- [GQ-4] https://console.groq.com/docs/openai
- [GQ-5] https://console.groq.com/docs/rate-limits
- [GQ-6] https://console.groq.com/docs/reasoning

---

## 10. OpenRouter

**Base URL / Auth / необязательные заголовки** [OR-1][OR-7][OR-6]
- Base URL: `https://openrouter.ai/api/v1`.
- `Authorization: Bearer <OPENROUTER_API_KEY>`.
- Необязательные заголовки атрибуции приложения:
  - **`HTTP-Referer`** — URL приложения; обязателен, чтобы попасть в рейтинги.
  - **`X-OpenRouter-Title`** — отображаемое имя. `X-Title` поддерживается для обратной совместимости. Без `HTTP-Referer` страница приложения не создаётся.
  - `X-OpenRouter-Categories`, `X-OpenRouter-App-Visibility`.

**Список моделей** [OR-1][OR-2][OR-3]
- **`GET /api/v1/models`**:
  - В гайде вызывается без авторизации («freely available»), в OpenAPI указана глобальная `apiKey` security. Ключ лучше передавать, если он есть.
  - Пагинация **opt-in**: если не передать ни `offset`, ни `limit`, возвращается полный список и `links.next = null`. `limit` по умолчанию 500, максимум 1000.
  - Ответ: `{"data":[...],"total_count":N,"links":{"next":"/api/v1/models?offset=500&limit=500"}}`.
  - Поля модели: `id`, `canonical_slug`, **`name`**, `description`, `context_length`, `architecture{input_modalities, output_modalities, modality, tokenizer}`, `pricing{prompt, completion, request, image}`, `top_provider{context_length, max_completion_tokens, is_moderated}`, `supported_parameters[]`, `per_request_limits`, `default_parameters`, `expiration_date`, `knowledge_cutoff`.
  - Фильтры: `supported_parameters` (например, `structured_outputs`), `output_modalities`, `category`, `sort`, `q` и другие.
- **`GET /api/v1/models/user`** — «filtered by user provider preferences, privacy settings, and guardrails». **Требует Bearer**, те же `offset`/`limit`.

**Генерация** [OR-11][OR-8]
- `POST /api/v1/chat/completions`.
- `max_completion_tokens`. `max_tokens` помечен deprecated: «some providers enforce a minimum of 16».
- `temperature` 0..2. Если параметр не передан, OpenRouter не подставляет значение, а оставляет дефолт провайдера.

**Structured output** [OR-4][OR-8]
- JSON Schema в формате OpenAI: `response_format: {"type":"json_schema","json_schema":{"name","strict":true,"schema"}}`.
- Поддержка определяется **для конкретного endpoint'а провайдера**, а не для модели в целом.
- Чтобы маршрутизировать только к подходящим провайдерам: `provider.require_parameters: true`.
- `strict` соблюдается по-разному: одни провайдеры гарантируют схему, другие считают её подсказкой.
- JSON mode: `{"type":"json_object"}` плюс инструкция в промпте.

**Разбор ответа** [OR-9][OR-5]
- Текст: `choices[0].message.content`.
- `finish_reason` нормализован к `tool_calls`, `stop`, **`length`**, `content_filter`, `error`. Исходное значение провайдера — в `native_finish_reason`.
- Reasoning-модель может потратить весь `max_tokens` на рассуждения: ответ 200, `finish_reason:"length"`, пустой `content`.
- `usage` — как в OpenAI, включая `completion_tokens_details.reasoning_tokens`.

**Ошибки** [OR-5][OR-10]
- Тело:
  ```json
  {"error":{"code":<number>,"message":"...","metadata":{...}}}
  ```
  - **`code` — число** (HTTP-статус), а не строка.
- Если ошибка возникла уже во время генерации, возможен **HTTP 200** с ошибкой в теле или в SSE: `finish_reason:"error"` и объект `error` внутри choice.
- Коды:
  - 400;
  - **401**;
  - 402 (нет кредитов);
  - 403 (guardrail или модерация);
  - 408;
  - **429**;
  - 502 (модель недоступна);
  - 503 (нет провайдера, подходящего под требования).
- Типизированные `error.metadata.error_type`:
  - `context_length_exceeded` (400), `max_tokens_exceeded`, `token_limit_exceeded`, `string_too_long`;
  - `authentication`, `permission_denied`, `payment_required`;
  - `rate_limit_exceeded`, `provider_overloaded`, `provider_unavailable`;
  - `invalid_request`, `invalid_prompt`, **`not_found`** (404);
  - `content_policy_violation`, `refusal`;
  - `unmapped`.
- Заголовки:
  - `Retry-After` на 429 и 503;
  - `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset` — только в ответах на 429 из-за лимитов самой платформы.
- Заголовок request ID — не удалось проверить.

**Temperature**
- OpenRouter передаёт параметр провайдеру как есть. Какие параметры поддерживает модель, видно в `supported_parameters` [OR-8][OR-1].
- Какие модели отвергают `temperature` — определяется провайдером, у OpenRouter это не описано.

**Источники**
- [OR-1] https://openrouter.ai/docs/api/api-reference/models/list-all-models-and-their-properties
- [OR-2] https://openrouter.ai/docs/api/api-reference/models/list-models-filtered-by-user-provider-preferences-privacy-settings-and-guardrails
- [OR-3] https://openrouter.ai/docs/guides/overview/models
- [OR-4] https://openrouter.ai/docs/guides/features/structured-outputs
- [OR-5] https://openrouter.ai/docs/api_reference/errors-and-debugging
- [OR-6] https://openrouter.ai/docs/app-attribution
- [OR-7] https://openrouter.ai/docs/api_reference/authentication
- [OR-8] https://openrouter.ai/docs/api_reference/parameters
- [OR-9] https://openrouter.ai/docs/api_reference/overview
- [OR-10] https://openrouter.ai/docs/api_reference/limits
- [OR-11] https://openrouter.ai/docs/api/api-reference/chat/create-a-chat-completion

---

## 11. Together AI

**Base URL / Auth** [T-1]
- Base URL: `https://api.together.ai/v1` («Default environment»). Есть также `https://api-inference.together.ai/v2` («Optimized environment for inference»).
- `Authorization: Bearer <TOGETHER_API_KEY>`.

**Список моделей** [T-1]
- `GET /v1/models`, необязательный query `dedicated` (bool).
- **Ответ — голый JSON-массив** `ModelInfo[]`, **без обёртки `{data}`**.
- Поля: `id`, `object:"model"`, `created`, `type` (`chat`, `language`, `code`, `image`, `embedding`, `moderation`, `rerank`), **`display_name`**, `organization`, `link`, `license`, `context_length`, `pricing{base, finetune, hourly, input, output, cached_input}`.
- Пагинации нет.

**Генерация** [T-2]
- `POST /v1/chat/completions`.
- `max_tokens`.
- `temperature` («A decimal number from 0-1»).
- Прочее: `top_p`, `top_k`, `repetition_penalty`, `reasoning: {"enabled": false}`, `reasoning_effort`.
- **`context_length_exceeded_behavior`**: `truncate` или `error` (по умолчанию `error`, возвращает 400).

**Structured output** [T-2][T-3]
- JSON Schema:
  ```json
  {"type":"json_schema","json_schema":{"name":"...","schema":{...},"strict":false,"description":"..."}}
  ```
  - `name` обязателен, `strict` по умолчанию `false`.
- JSON mode: `{"type":"json_object"}`, модели нужна инструкция.
- Дополнительно есть **regex**: `{"type":"regex","pattern":"..."}`.
- Документация советует дублировать схему текстом в промпте.
- Список моделей со structured outputs — в каталоге моделей. Ограничения strict и неподдерживаемые ключевые слова — не удалось проверить.

**Разбор ответа** [T-2]
- Текст: `choices[0].message.content`. Также могут приходить `reasoning` / `reasoning_content`.
- `finish_reason`: `stop`, `eos`, **`length`**, `tool_calls`, `function_call`.
- `usage`: `prompt_tokens`, `completion_tokens`, `total_tokens`.

**Ошибки** [T-1][T-4][T-5]
- Тело: `{"error":{"message","type","param","code"}}`.
- Коды:
  - 400;
  - **401**;
  - 402 (spending limit);
  - **403** — в таблице ошибок: «Input token count + `max_tokens` parameter must be less than the context length». В reference при `context_length_exceeded_behavior:"error"` — 400. Документация расходится.
  - **404** (неверный URL или имя модели);
  - **429** — с заголовком **`x-ratelimit-reset`**;
  - 500, 503, 504, 524, 529.
- Заголовок request ID — не удалось проверить.

**Temperature:** отказов по моделям в документации нет.

**Источники**
- [T-1] https://docs.together.ai/reference/models
- [T-2] https://docs.together.ai/reference/chat-completions
- [T-3] https://docs.together.ai/docs/inference/chat/structured-outputs
- [T-4] https://docs.together.ai/docs/error-codes
- [T-5] https://docs.together.ai/docs/serverless/rate-limits

---

## 12. Fireworks AI

**Base URL / Auth** [F-5][F-2][F-1]
- Inference: **`https://api.fireworks.ai/inference/v1`**, например `POST /inference/v1/chat/completions`.
- Management / Gateway API: `https://api.fireworks.ai/v1/...`.
- `Authorization: Bearer <FIREWORKS_API_KEY>`.
- ID моделей имеют вид `accounts/fireworks/models/<name>`.

**Список моделей** [F-1][F-7]
- `GET https://api.fireworks.ai/v1/accounts/{account_id}/models` — модели в разрезе аккаунта.
- Параметры:
  - `pageSize` (по умолчанию 50, максимум 200);
  - `pageToken`;
  - `filter` (синтаксис AIP-160);
  - `orderBy`, `readMask`.
- Ответ: `{"models":[...],"nextPageToken","totalSize"}`. Если `nextPageToken` нет — это последняя страница.
- Публичный serverless-каталог:
  ```
  GET https://api.fireworks.ai/v1/accounts/fireworks/models?filter=supports_serverless%3Dtrue&pageSize=50
  ```
- Поля модели: `name` (ID), **`displayName`**, `description`, `contextLength`, `supportsTools`, `supportsImageInput`, `supportsServerless`, `state`, `kind`, `deprecationDate` и другие.

**Генерация** [F-2][F-5]
- **`max_tokens`**. `max_completion_tokens` — «Alias for max_tokens. Cannot be specified together with max_tokens».
- По умолчанию `max_tokens` уменьшается, чтобы влезть в контекст (`context_length_exceeded_behavior`: по умолчанию `truncate`, есть вариант `error`). У OpenAI в такой ситуации была бы ошибка.
- `usage` приходит и в стриме, в последнем чанке.

**Structured output** [F-2][F-3]
- `response_format.type`: `json_object`, `json_schema`, `grammar`, `text`. Поля `schema`, `json_schema`, `grammar`.
- JSON Schema:
  ```json
  {"type":"json_schema","json_schema":{"name":"...","schema":{...}}}
  ```
- JSON mode: `{"type":"json_object"}`, модели нужна инструкция.
- Поддерживается большая часть JSON Schema 2020-12 (включая `minLength`, `maxLength`, `pattern` в режиме best-effort, `anyOf`/`allOf`/`oneOf`, `$defs`, рекурсию).
- Не поддерживаются внешние `$ref`.
- Схемы с `properties` обрабатываются как с `unevaluatedProperties:false`.
- **`json_schema` отключает вывод reasoning.**
- При `finish_reason="length"` JSON может оказаться невалидным.

**Разбор ответа** [F-2]
- Текст: `choices[0].message.content`.
- `finish_reason`: `stop` или **`length`**.
- `usage`: `prompt_tokens`, `completion_tokens`, `total_tokens`, `prompt_tokens_details`, `completion_tokens_details`.

**Ошибки** [F-4][F-6]
- Коды:
  - 400;
  - **401** (неверный ключ);
  - 402;
  - 403;
  - **404** (модель не существует, не задеплоена или нет доступа);
  - 405, 408;
  - 412 (аккаунт приостановлен или LoRA не загрузилась);
  - 413;
  - **429**;
  - 500, 502, 503, 504, 520.
- Заголовки лимитов: `X-Ratelimit-Limit-Tokens-Prompt`, `X-Ratelimit-Limit-Tokens-Cache-Adjusted-Prompt`, `X-Ratelimit-Limit-Tokens-Generated`.
- Не удалось проверить: форма тела ошибки, заголовок request ID, код переполнения контекста.

**Temperature:** отказов по моделям в документации нет.

**Источники**
- [F-1] https://docs.fireworks.ai/api-reference/list-models
- [F-2] https://docs.fireworks.ai/api-reference/post-chatcompletions
- [F-3] https://docs.fireworks.ai/structured-responses/structured-response-formatting
- [F-4] https://docs.fireworks.ai/guides/inference-error-codes
- [F-5] https://docs.fireworks.ai/tools-sdks/openai-compatibility
- [F-6] https://docs.fireworks.ai/serverless/rate-limits
- [F-7] https://docs.fireworks.ai/faq-new/models-inference/how-to-check-if-a-model-is-available-on-serverless

---

## 13. Ollama

**Base URL / Auth** [O-1][O-9]
- Локально: native API `http://localhost:11434/api`, OpenAI-совместимый `http://localhost:11434/v1`.
- Облако: `https://ollama.com/api` и `https://ollama.com/v1` с `Authorization: Bearer <OLLAMA_API_KEY>`.
- Локальному API авторизация не нужна.

**Список моделей** [O-3][O-6]
- `GET /api/tags` → `{"models":[{"name","model","modified_at","size","digest","details":{"format","family","families","parameter_size","quantization_level"}}]}`. Пагинации нет.
- `GET /v1/models` — в формате OpenAI. `created` — время последнего изменения, `owned_by` по умолчанию `"library"`.

**Генерация (native)** [O-2]
- `POST /api/chat`: `{model, messages[{role, content}], format, options, stream, think, keep_alive}`.
- **`stream` по умолчанию `true`**. Для одного JSON-ответа передавать `"stream": false`.
- System prompt — сообщение с ролью `system`.
- `options`:
  - `num_predict` — максимум токенов; в Modelfile по умолчанию `-1` (бесконечно) [O-7];
  - `temperature` — в Modelfile по умолчанию 0.8 [O-7];
  - `num_ctx`, `top_k`, `top_p`, `min_p`, `seed`, `stop`.
- `num_ctx` по умолчанию — **документация противоречива**:
  - Modelfile: «Default: 2048»;
  - FAQ: 4096;
  - страница Context length: зависит от VRAM — меньше 24 GiB → 4k, 24–48 GiB → 32k, от 48 GiB → 256k [O-7][O-8].
- `think`: `true`, `false`, `null` или строка уровня. Доступные значения смотреть в `/api/show`.

**Structured output (native)** [O-2][O-4]
- `"format": "json"` — JSON mode.
- `"format": {<JSON Schema>}` — structured output.
- Схему рекомендуется также дублировать текстом в промпте.
- **«Ollama's Cloud currently does not support structured outputs.»**

**OpenAI-совместимый `/v1`** [O-6]
- `POST /v1/chat/completions` поддерживает `response_format` («JSON mode» ✓), `max_tokens`, `temperature`, `reasoning_effort`, `stream_options.include_usage`.
- Не поддерживаются `tool_choice`, `logit_bias`, `user`, `n`, logprobs.
- Поддерживается ли именно `response_format.type:"json_schema"` — **не удалось проверить**: указан только «JSON mode».
- Контекст через OpenAI API не задать; нужен Modelfile с `PARAMETER num_ctx`.

**Разбор ответа (native)** [O-2]
- Текст: `message.content`; рассуждения — `message.thinking`.
- `done: true`.
- `done_reason` описан только как «Reason the response finished». В примерах встречается лишь `"stop"`. Значение для обрезки по `num_predict` (`"length"`) **не удалось проверить** в документации.
- Токены: `prompt_eval_count`, `prompt_eval_cached_count`, `eval_count`.
- Тайминги: `total_duration`, `load_duration`, `prompt_eval_duration`, `eval_duration` (в наносекундах).

**Ошибки** [O-5]
- Тело: `{"error":"<message>"}`.
- Коды: 400, **404** (модель не существует), **429**, 500, 502 (облачная модель недоступна).
- Ошибка в середине стрима приходит строкой NDJSON `{"error":...}`, HTTP-статус при этом остаётся прежним.
- Заголовки rate-limit и request ID — не описаны.

**Temperature:** отказов по моделям нет.

**Источники**
- [O-1] https://docs.ollama.com/api/introduction
- [O-2] https://docs.ollama.com/api/chat и https://docs.ollama.com/openapi.yaml
- [O-3] https://docs.ollama.com/api/tags
- [O-4] https://docs.ollama.com/capabilities/structured-outputs
- [O-5] https://docs.ollama.com/api/errors
- [O-6] https://docs.ollama.com/api/openai-compatibility
- [O-7] https://docs.ollama.com/modelfile
- [O-8] https://docs.ollama.com/context-length и https://docs.ollama.com/faq
- [O-9] https://docs.ollama.com/api/authentication

---

## 14. LM Studio

**Base URL / Auth** [L-4][L-9]
- `http://localhost:1234`, порт по умолчанию 1234 (так указано в примерах).
- OpenAI-совместимые пути: `/v1/...`.
- Native REST v1 (с LM Studio 0.4.0): `/api/v1/...`. Native REST v0 (legacy, с 0.3.6): `/api/v0/...`.
- Авторизация по умолчанию выключена. Если её включить в Server Settings, нужен `Authorization: Bearer $LM_API_TOKEN` (LM Studio 0.4.0+).

**Список моделей**
- **OpenAI-совместимый** `GET /v1/models`: «The list may include all downloaded models when Just-In-Time loading is enabled» [L-5].
  - JIT включён — все скачанные модели.
  - JIT выключен — только загруженные в память [L-8].
- **Native v1** `GET /api/v1/models` [L-2]:
  - Параметров нет. Ответ: `{"models":[...]}`.
  - Поля: `type` (`llm` / `embedding`), `publisher`, **`key`** (ID), **`display_name`**, `architecture`, `quantization{name, bits_per_weight}`, `size_bytes`, `params_string`, `loaded_instances[{id, config{context_length,...}}]`, `max_context_length`, `format` (`gguf` / `mlx`), `capabilities{vision, trained_for_tool_use, reasoning{allowed_options, default}}`, `description`, `variants`, `selected_variant`.
  - Модель загружена, если `loaded_instances` не пуст.
- **Native v0** `GET /api/v0/models` (legacy) [L-3]:
  - «List all loaded and downloaded models».
  - Ответ: `{"object":"list","data":[{"id","object":"model","type":"llm|vlm|embeddings","publisher","arch","compatibility_type","quantization","state":"loaded|not-loaded","max_context_length"}]}`.
- Пагинации нет нигде.

**JIT и TTL** [L-8]
- JIT по умолчанию включён: первый запрос к модели загружает её в память.
- Idle TTL по умолчанию 60 минут; можно задать поле `ttl` (секунды) в теле запроса.
- Auto-Evict по умолчанию включён: одновременно держится не больше одной JIT-загруженной модели.
- Для адаптера это значит: первый запрос может быть долгим (идёт загрузка), и модель из `/v1/models` может оказаться не загруженной.

**Генерация** [L-7][L-1]
- `POST /v1/chat/completions` (также есть `/v1/responses` и `/v1/messages`).
- Поддерживаемые поля: `model`, `top_p`, `top_k`, `messages`, `temperature`, **`max_tokens`**, `stream`, `stop`, `presence_penalty`, `frequency_penalty`, `logit_bias`, `repeat_penalty`, `seed`.
- Native `POST /api/v1/chat` позволяет задать `context_length` прямо в запросе. Через `/v1/chat/completions` это сделать нельзя.

**Structured output** [L-6]
- `response_format: {"type":"json_schema","json_schema":{"name","strict","schema"}}` — «same format as OpenAI's Structured Output API».
- В официальном примере `"strict": "true"` передан **строкой**.
- Результат — строка JSON в `choices[0].message.content`.
- `json_object` — не удалось проверить.

**Разбор ответа, ошибки, temperature**
- Ответ OpenAI-подобный: `choices[0].message.content` [L-6].
- Не удалось проверить: перечень `finish_reason`, формат ошибок, коды и заголовки — в документации не описаны.
- Отказов по `temperature` нет.

**Источники**
- [L-1] https://lmstudio.ai/docs/developer/rest
- [L-2] https://lmstudio.ai/docs/developer/rest/list
- [L-3] https://lmstudio.ai/docs/developer/rest/endpoints
- [L-4] https://lmstudio.ai/docs/developer/openai-compat
- [L-5] https://lmstudio.ai/docs/developer/openai-compat/models
- [L-6] https://lmstudio.ai/docs/developer/openai-compat/structured-output
- [L-7] https://lmstudio.ai/docs/developer/openai-compat/chat-completions
- [L-8] https://lmstudio.ai/docs/developer/core/ttl-and-auto-evict
- [L-9] https://lmstudio.ai/docs/developer/core/authentication

---

## 15. OpenAI Compatible (generic)

Своих источников у этого варианта нет: формы запросов и ответов берутся из блока OpenAI [OA-1][OA-2][OA-3]. Для генерического адаптера по наблюдениям из блоков выше:
- **Base URL задаёт пользователь.** Встречаются варианты с `/v1` и без него: DeepSeek — `https://api.deepseek.com` [DS-1]; Fireworks — `.../inference/v1` [F-5]; Groq — `.../openai/v1` [GQ-4].
- **`GET {base}/models` может отсутствовать** (для Qwen он не описан [Q-1]) или иметь нестандартную форму: голый массив у Together [T-1] и Mistral-примера [M-1]; `models[]` у xAI `/language-models` [X-3], LM Studio `/api/v1/models` [L-2] и Ollama `/api/tags` [O-3]. Разбор должен принимать обе формы, а сбой списка моделей не должен блокировать генерацию.
- **Лимит вывода.** Одни серверы понимают только `max_tokens` (DeepSeek, Mistral, Together, LM Studio), другие объявили его deprecated в пользу `max_completion_tokens` (OpenAI, xAI, Groq, OpenRouter, Qwen). Нужна настройка «какой параметр отправлять».
- **`response_format`.** `json_schema` поддерживается не везде: DeepSeek понимает только `json_object` [DS-2]; Anthropic-compat игнорирует `response_format` [AN-11]. Нужен откат: json_schema → json_object → схема в промпте плюс валидация на клиенте.
- **Форма ошибки:** OpenAI-подобная `{"error":{...}}` у OpenAI, Groq, Together, Azure v1; плоская у Mistral; `code` числом у OpenRouter; `{"error":"string"}` у Ollama.

---

## Отличия от OpenAI-совместимости

| Провайдер | Совместимость | Чем отличается |
|---|---|---|
| **Azure OpenAI** | Почти полная (v1) | В классическом пути модель задаётся деплойментом в URL и нужен `api-version`. Заголовок `api-key` вместо Bearer. Список деплойментов есть только в control plane (ARM). Более жёсткие ограничения JSON Schema (100 свойств, 5 уровней, нет `pattern`/`format`/`minimum`...). Заголовок `retry-after-ms`. Request ID в `apim-request-id`. Фильтр контента: 400 `content_filter` [AZ-*] |
| **Anthropic** | **Не совместим** (native) | Другой endpoint (`/v1/messages`). Заголовки `x-api-key`/`anthropic-version`. `system` отдельным полем, `max_tokens` обязателен. `content[]` вместо `choices`. `stop_reason` вместо `finish_reason`. Structured output через `output_config.format`. Ошибки `{type:"error", error:{type,message}}`, есть 529. Курсорная пагинация моделей. На новых моделях `temperature ≠ 1` даёт 400. В OpenAI-compat слое `response_format` игнорируется [AN-*] |
| **Google Gemini** | **Не совместим** (native generateContent) | Ключ в `x-goog-api-key`. `contents/parts`, роль `model`, `systemInstruction`. Параметры camelCase в `generationConfig`. `finishReason` в UPPER_CASE. Токены в `usageMetadata`. **Неверный ключ даёт 400 INVALID_ARGUMENT, а не 401.** Ошибки в формате gRPC status. Пагинация через `pageToken`. Три параллельных способа задать схему. Есть отдельный OpenAI-compat (beta) [G-*] |
| **Mistral** | Частичная | Плоское тело ошибки без `error`. `content` может быть массивом чанков (thinking/text). Есть `finish_reason: model_length/error`. Параметры `random_seed` и `safe_prompt`. Список моделей: в схеме `{data}`, в примере массив [M-*] |
| **DeepSeek** | Частичная | Нет `json_schema`, только `json_object`. **Thinking включён по умолчанию** (`reasoning_content`, при thinking `temperature` игнорируется). Дополнительные значения `finish_reason`: `insufficient_system_resource`, `aborted`. Своя структура usage с cache hit/miss [DS-*] |
| **Qwen (Model Studio)** | Частичная | Workspace-домены и ключи, привязанные к региону. Нестандартный `enable_thinking` в корне тела. Для части моделей обязателен стриминг. `json_schema` только у части моделей и не в Singapore. Для `json_object` нужно слово «JSON» в промпте. Structured output несовместим с thinking. `/models` не задокументирован. Коды ошибок в стиле DashScope [Q-*] |
| **xAI** | Высокая | `max_completion_tokens` не учитывает reasoning. `finish_reason:"end_turn"`. Reasoning-модели отвергают `stop`, `presence_penalty`, `frequency_penalty`. Есть отдельный `/v1/language-models` [X-*] |
| **Groq** | Высокая | С 400 отклоняются `logprobs`, `logit_bias`, `top_logprobs`, `messages[].name`, `n`≠1. `strict` только на 3 моделях. Structured outputs несовместимы со streaming и tools. `reasoning_format` (при `raw` появляются теги `<think>` в `content`). Статусы 498/499 [GQ-*] |
| **OpenRouter** | Высокая (агрегатор) | `error.code` — число. Возможен HTTP 200 с ошибкой в теле. Поддержка параметров зависит от провайдера (`require_parameters`). Нормализованный `finish_reason` + `native_finish_reason`. Своя схема `/models` (`total_count`, `links.next`) [OR-*] |
| **Together AI** | Высокая | `/models` отдаёт голый массив. Дополнительный `finish_reason:"eos"`. `context_length_exceeded_behavior`. `response_format.type:"regex"`. Переполнение контекста задокументировано как 403 [T-*] |
| **Fireworks AI** | Высокая (inference) | Список моделей не в OpenAI-формате (`/v1/accounts/{acct}/models`, `pageToken`, ID `accounts/.../models/...`). По умолчанию `max_tokens` урезается под контекст, а не даёт ошибку. `response_format.type:"grammar"`. `json_schema` отключает reasoning [F-*] |
| **Ollama** | Native — нет, `/v1` — частично | Native: `/api/chat`, `format`, `options.num_predict`, `done_reason`, NDJSON-стрим по умолчанию, ошибка `{"error":"..."}`. `/v1`: нет `tool_choice`/`n`/logprobs, `json_schema` не подтверждён, контекст задаётся только через Modelfile [O-*] |
| **LM Studio** | Высокая (`/v1`) | Состав `/v1/models` зависит от JIT. Native `/api/v1/models` с полями `key`/`display_name`/`loaded_instances`. Формат ошибок не задокументирован. `strict` в примере передан строкой [L-*] |
| **OpenAI Compatible** | По определению | Всё зависит от сервера, см. раздел 15 |

---

## Сквозные выводы для реализации адаптеров (по документации выше)

1. **Модель ошибки должна быть терпимой к формату.**
   - `error.code` бывает строкой (OpenAI [OA-5]), числом (OpenRouter [OR-5], Gemini [G-5]) или отсутствует. Разбирать через `JToken`, не через строго типизированный DTO.
   - Mistral присылает плоское тело [M-4], Ollama — `{"error":"string"}` [O-5].
2. **Признак обрезки по лимиту у всех разный:**
   - `length` (OpenAI Chat и совместимые);
   - `status:"incomplete"` + `max_output_tokens` (Responses);
   - `max_tokens` / `model_context_window_exceeded` (Anthropic);
   - `MAX_TOKENS` (Gemini);
   - `model_length` (Mistral);
   - `done_reason` (Ollama; значение не подтверждено).
3. **Параметр лимита вывода** нужно выбирать под провайдера: `max_completion_tokens` / `max_output_tokens` / `max_tokens` / `maxOutputTokens` / `num_predict`. У reasoning-моделей лимит часто включает рассуждения (OpenAI [OA-1], OpenRouter [OR-8], Qwen `max_completion_tokens` [Q-2]); у xAI — нет [X-2].
4. **Temperature нужно уметь не отправлять:**
   - OpenAI GPT-6 при effort ≠ `none` [OA-9];
   - Azure reasoning-модели [AZ-8];
   - Anthropic: модели новее Opus 4.6 принимают только 1.0 [AN-2];
   - Gemini 3: рекомендуется 1.0 [G-7];
   - DeepSeek thinking игнорирует [DS-2].
5. **Retry:**
   - `Retry-After` в секундах: OpenAI [OA-6], Anthropic [AN-7], Groq [GQ-5], Mistral [M-4], OpenRouter [OR-5];
   - `retry-after-ms`: Azure [AZ-9];
   - `x-ratelimit-reset`: Together [T-5];
   - у Anthropic есть 529, у Groq — 498;
   - не ретраить billing- и quota-ошибки: OpenAI 429 `insufficient_quota` [OA-5], Gemini 402 [G-5], DeepSeek 402 [DS-5], OpenRouter 402 [OR-5].
6. **Structured output — лестница отката:** native JSON Schema (strict) → JSON Schema без strict → JSON object mode (часто нужно слово «JSON» в промпте: OpenAI [OA-4], Qwen [Q-3], DeepSeek [DS-4]) → схема в промпте плюс локальная валидация. Отдельно учитывать несовместимости: Anthropic + citations/prefill [AN-4]; Groq + stream/tools [GQ-2]; Qwen + thinking [Q-4]; Fireworks: json_schema отключает reasoning [F-3].
