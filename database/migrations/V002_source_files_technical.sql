-- fakt:id=V002
-- fakt:title=Технические поля SourceFiles
-- fakt:description=Добавляет в SourceFiles необязательные столбцы SourcePath, SourcePathHash, FileSize, LastWriteTimeUtc, ContentHash, CreatedAtUtc и уникальный индекс версии источника (путь + хеш содержимого). Повтор обработки той же версии файла не создаёт новый источник; изменённое содержимое получает новую запись и новый FileCode. Существующие строки не изменяются: новые столбцы допускают NULL.
-- fakt:alters-user-tables=true
-- fakt:required=processing
-- fakt:transaction=true

IF COL_LENGTH({{SF_LIT}}, N'SourcePath') IS NULL
    ALTER TABLE {{SF}} ADD [SourcePath] NVARCHAR(MAX) NULL;
GO
IF COL_LENGTH({{SF_LIT}}, N'SourcePathHash') IS NULL
    ALTER TABLE {{SF}} ADD [SourcePathHash] BINARY(32) NULL;
GO
IF COL_LENGTH({{SF_LIT}}, N'FileSize') IS NULL
    ALTER TABLE {{SF}} ADD [FileSize] BIGINT NULL;
GO
IF COL_LENGTH({{SF_LIT}}, N'LastWriteTimeUtc') IS NULL
    ALTER TABLE {{SF}} ADD [LastWriteTimeUtc] DATETIME2(7) NULL;
GO
IF COL_LENGTH({{SF_LIT}}, N'ContentHash') IS NULL
    ALTER TABLE {{SF}} ADD [ContentHash] BINARY(32) NULL;
GO
IF COL_LENGTH({{SF_LIT}}, N'CreatedAtUtc') IS NULL
    ALTER TABLE {{SF}} ADD [CreatedAtUtc] DATETIME2(3) NULL
        CONSTRAINT [DF_{{SF_NAME}}_CreatedAtUtc] DEFAULT (SYSUTCDATETIME());
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({{SF_LIT}}) AND name = N'UX_{{SF_NAME_LIT}}_Version')
    CREATE UNIQUE NONCLUSTERED INDEX [UX_{{SF_NAME}}_Version]
        ON {{SF}} ([SourcePathHash], [ContentHash])
        WHERE [SourcePathHash] IS NOT NULL AND [ContentHash] IS NOT NULL;
GO
