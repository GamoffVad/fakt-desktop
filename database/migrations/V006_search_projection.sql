-- fakt:id=V006
-- fakt:title=Поисковая проекция и индекс идентификаторов
-- fakt:description=Создаёт восстанавливаемую поисковую проекцию FaktSearchDocs (одна строка на каждое наблюдение PersonFacts: текст основных полей, значения фактов из JSON и данные файла) и таблицу FaktFactValues для точного и префиксного поиска телефонов, счетов и других идентификаторов по нормализованному значению. Строки проекции удаляются вместе с наблюдением (ON DELETE CASCADE).
-- fakt:alters-user-tables=false
-- fakt:required=processing,search
-- fakt:transaction=true

IF SCHEMA_ID({{A_LIT}}) IS NULL
    EXEC(N'CREATE SCHEMA ' + {{A_QUOTED_LIT}});
GO

IF OBJECT_ID({{AUX_LIT:FaktSearchDocs}}, N'U') IS NULL
    CREATE TABLE {{AUX:FaktSearchDocs}}
    (
        [PersonFactId] BIGINT NOT NULL CONSTRAINT [PK_FaktSearchDocs] PRIMARY KEY CLUSTERED
            CONSTRAINT [FK_FaktSearchDocs_{{PF_NAME}}] FOREIGN KEY REFERENCES {{PF}} ({{c:PersonFactsId}}) ON DELETE CASCADE,
        [SourceFileId] BIGINT NOT NULL,
        [AllText] NVARCHAR(MAX) NOT NULL,
        [NameText] NVARCHAR(1000) NULL,
        [BirthPlaceText] NVARCHAR(2000) NULL,
        [FactsText] NVARCHAR(MAX) NULL,
        [FileText] NVARCHAR(2000) NULL,
        [UpdatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_FaktSearchDocs_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({{AUX_LIT:FaktSearchDocs}}) AND name = N'IX_FaktSearchDocs_SourceFileId')
    CREATE NONCLUSTERED INDEX [IX_FaktSearchDocs_SourceFileId] ON {{AUX:FaktSearchDocs}} ([SourceFileId]);
GO

IF OBJECT_ID({{AUX_LIT:FaktFactValues}}, N'U') IS NULL
    CREATE TABLE {{AUX:FaktFactValues}}
    (
        [FactType] VARCHAR(32) NOT NULL,
        [NormalizedValue] NVARCHAR(400) NOT NULL,
        [PersonFactId] BIGINT NOT NULL
            CONSTRAINT [FK_FaktFactValues_{{PF_NAME}}] FOREIGN KEY REFERENCES {{PF}} ({{c:PersonFactsId}}) ON DELETE CASCADE,
        CONSTRAINT [PK_FaktFactValues] PRIMARY KEY CLUSTERED ([FactType], [NormalizedValue], [PersonFactId])
    );
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({{AUX_LIT:FaktFactValues}}) AND name = N'IX_FaktFactValues_Value')
    CREATE NONCLUSTERED INDEX [IX_FaktFactValues_Value] ON {{AUX:FaktFactValues}} ([NormalizedValue]) INCLUDE ([FactType], [PersonFactId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({{AUX_LIT:FaktFactValues}}) AND name = N'IX_FaktFactValues_PersonFactId')
    CREATE NONCLUSTERED INDEX [IX_FaktFactValues_PersonFactId] ON {{AUX:FaktFactValues}} ([PersonFactId]);
GO
