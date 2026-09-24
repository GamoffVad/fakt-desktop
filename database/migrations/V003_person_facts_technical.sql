-- fakt:id=V003
-- fakt:title=Технические поля PersonFacts для идемпотентной записи
-- fakt:description=Добавляет в PersonFacts необязательные столбцы SourceRecordKey, PersonIndex, ExtractionVersion, JobId, CreatedAtUtc и уникальный индекс (ID_FileName, SourceRecordKey, PersonIndex, ExtractionVersion): повтор записи того же результата не создаёт дубликат, а две одинаковые строки в разных позициях файла не схлопываются. Создаёт индекс по внешнему ключу, если его нет. Существующие строки не изменяются.
-- fakt:alters-user-tables=true
-- fakt:required=processing
-- fakt:transaction=true

IF COL_LENGTH({{PF_LIT}}, N'SourceRecordKey') IS NULL
    ALTER TABLE {{PF}} ADD [SourceRecordKey] VARCHAR(64) NULL;
GO
IF COL_LENGTH({{PF_LIT}}, N'PersonIndex') IS NULL
    ALTER TABLE {{PF}} ADD [PersonIndex] INT NULL;
GO
IF COL_LENGTH({{PF_LIT}}, N'ExtractionVersion') IS NULL
    ALTER TABLE {{PF}} ADD [ExtractionVersion] VARCHAR(64) NULL;
GO
IF COL_LENGTH({{PF_LIT}}, N'JobId') IS NULL
    ALTER TABLE {{PF}} ADD [JobId] BIGINT NULL;
GO
IF COL_LENGTH({{PF_LIT}}, N'CreatedAtUtc') IS NULL
    ALTER TABLE {{PF}} ADD [CreatedAtUtc] DATETIME2(3) NULL
        CONSTRAINT [DF_{{PF_NAME}}_CreatedAtUtc] DEFAULT (SYSUTCDATETIME());
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({{PF_LIT}}) AND name = N'UX_{{PF_NAME_LIT}}_Observation')
    CREATE UNIQUE NONCLUSTERED INDEX [UX_{{PF_NAME}}_Observation]
        ON {{PF}} ({{c:FileId}}, [SourceRecordKey], [PersonIndex], [ExtractionVersion])
        WHERE [SourceRecordKey] IS NOT NULL;
GO
IF NOT EXISTS (
    SELECT 1
    FROM sys.index_columns AS ic
    INNER JOIN sys.indexes AS i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
    WHERE ic.object_id = OBJECT_ID({{PF_LIT}})
      AND ic.key_ordinal = 1
      AND COL_NAME(ic.object_id, ic.column_id) = {{c_lit:FileId}})
    CREATE NONCLUSTERED INDEX [IX_{{PF_NAME}}_FileId] ON {{PF}} ({{c:FileId}});
GO
