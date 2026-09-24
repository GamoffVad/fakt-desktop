-- fakt:id=V001
-- fakt:title=Основные таблицы SourceFiles и PersonFacts
-- fakt:description=Создаёт две основные таблицы, если их нет. Существующие таблицы не изменяются, не удаляются и не пересоздаются.
-- fakt:alters-user-tables=false
-- fakt:required=processing,search
-- fakt:transaction=true

IF SCHEMA_ID({{S_LIT}}) IS NULL
    EXEC(N'CREATE SCHEMA ' + {{S_QUOTED_LIT}});
GO

IF OBJECT_ID({{SF_LIT}}, N'U') IS NULL
BEGIN
    CREATE TABLE {{SF}}
    (
        {{c:SourceFilesId}} BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_{{SF_NAME}}] PRIMARY KEY CLUSTERED,
        {{c:FileCode}} VARCHAR(32) NOT NULL CONSTRAINT [UQ_{{SF_NAME}}_FileCode] UNIQUE,
        {{c:FileName}} NVARCHAR(1024) NOT NULL
    );
END
GO

IF OBJECT_ID({{PF_LIT}}, N'U') IS NULL
BEGIN
    CREATE TABLE {{PF}}
    (
        {{c:PersonFactsId}} BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_{{PF_NAME}}] PRIMARY KEY CLUSTERED,
        {{c:Surname}} NVARCHAR(200) NULL,
        {{c:Name}} NVARCHAR(200) NULL,
        {{c:Patronymic}} NVARCHAR(200) NULL,
        {{c:BirthDate}} DATE NULL,
        {{c:BirthPlace}} NVARCHAR(1000) NULL,
        {{c:All}} NVARCHAR(MAX) NOT NULL
            CONSTRAINT [DF_{{PF_NAME}}_ALL] DEFAULT (N'{}')
            CONSTRAINT [CK_{{PF_NAME}}_ALL_IsJson] CHECK (ISJSON({{c:All}}) = 1),
        {{c:FileId}} BIGINT NOT NULL
            CONSTRAINT [FK_{{PF_NAME}}_{{SF_NAME}}] FOREIGN KEY REFERENCES {{SF}} ({{c:SourceFilesId}})
    );

    CREATE NONCLUSTERED INDEX [IX_{{PF_NAME}}_FileId] ON {{PF}} ({{c:FileId}});
END
GO
