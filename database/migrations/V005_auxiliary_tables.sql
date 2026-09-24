-- fakt:id=V005
-- fakt:title=Вспомогательные таблицы заданий, checkpoint и ошибок
-- fakt:description=Создаёт вспомогательные таблицы FAKT: задания (снимок конфигурации), файлы заданий, границы подтверждённой обработки, реестр результатов записей (обработана с результатом / без результата / с ошибкой), журнал ошибок записей и журнал фиксаций для идемпотентного повтора. Основные таблицы не изменяются.
-- fakt:alters-user-tables=false
-- fakt:required=processing
-- fakt:transaction=true

IF SCHEMA_ID({{A_LIT}}) IS NULL
    EXEC(N'CREATE SCHEMA ' + {{A_QUOTED_LIT}});
GO

IF OBJECT_ID({{AUX_LIT:FaktJobs}}, N'U') IS NULL
    CREATE TABLE {{AUX:FaktJobs}}
    (
        [JobId] BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_FaktJobs] PRIMARY KEY CLUSTERED,
        [JobGuid] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [UQ_FaktJobs_JobGuid] UNIQUE,
        [Status] VARCHAR(32) NOT NULL,
        [CreatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_FaktJobs_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        [FinishedAtUtc] DATETIME2(3) NULL,
        [CreatedBy] NVARCHAR(256) NULL,
        [SnapshotJson] NVARCHAR(MAX) NOT NULL CONSTRAINT [CK_FaktJobs_Snapshot_IsJson] CHECK (ISJSON([SnapshotJson]) = 1),
        [Summary] NVARCHAR(2000) NULL
    );
GO

IF OBJECT_ID({{AUX_LIT:FaktJobFiles}}, N'U') IS NULL
    CREATE TABLE {{AUX:FaktJobFiles}}
    (
        [JobFileId] BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_FaktJobFiles] PRIMARY KEY CLUSTERED,
        [JobId] BIGINT NOT NULL CONSTRAINT [FK_FaktJobFiles_FaktJobs] FOREIGN KEY REFERENCES {{AUX:FaktJobs}} ([JobId]),
        [SourceFileId] BIGINT NOT NULL,
        [FileCode] VARCHAR(32) NULL,
        [FileName] NVARCHAR(1024) NULL,
        [SourcePath] NVARCHAR(MAX) NULL,
        [Status] VARCHAR(32) NOT NULL,
        [StructureJson] NVARCHAR(MAX) NULL,
        [ExtractionVersion] VARCHAR(64) NOT NULL,
        [ExpectedSize] BIGINT NOT NULL,
        [ExpectedLastWriteUtc] DATETIME2(7) NOT NULL,
        [ContentHash] BINARY(32) NULL,
        [RecordsRead] BIGINT NOT NULL CONSTRAINT [DF_FaktJobFiles_RecordsRead] DEFAULT (0),
        [RecordsExtracted] BIGINT NOT NULL CONSTRAINT [DF_FaktJobFiles_RecordsExtracted] DEFAULT (0),
        [RecordsNoFacts] BIGINT NOT NULL CONSTRAINT [DF_FaktJobFiles_RecordsNoFacts] DEFAULT (0),
        [RecordsError] BIGINT NOT NULL CONSTRAINT [DF_FaktJobFiles_RecordsError] DEFAULT (0),
        [ObservationsSaved] BIGINT NOT NULL CONSTRAINT [DF_FaktJobFiles_ObservationsSaved] DEFAULT (0),
        [Requests] BIGINT NOT NULL CONSTRAINT [DF_FaktJobFiles_Requests] DEFAULT (0),
        [InputTokens] BIGINT NOT NULL CONSTRAINT [DF_FaktJobFiles_InputTokens] DEFAULT (0),
        [OutputTokens] BIGINT NOT NULL CONSTRAINT [DF_FaktJobFiles_OutputTokens] DEFAULT (0),
        [LastError] NVARCHAR(2000) NULL,
        [UpdatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_FaktJobFiles_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({{AUX_LIT:FaktJobFiles}}) AND name = N'IX_FaktJobFiles_JobId')
    CREATE NONCLUSTERED INDEX [IX_FaktJobFiles_JobId] ON {{AUX:FaktJobFiles}} ([JobId]);
GO

IF OBJECT_ID({{AUX_LIT:FaktFileProgress}}, N'U') IS NULL
    CREATE TABLE {{AUX:FaktFileProgress}}
    (
        [SourceFileId] BIGINT NOT NULL,
        [ExtractionVersion] VARCHAR(64) NOT NULL,
        [ConfirmedOrdinal] BIGINT NOT NULL CONSTRAINT [DF_FaktFileProgress_Confirmed] DEFAULT (0),
        [Extracted] BIGINT NOT NULL CONSTRAINT [DF_FaktFileProgress_Extracted] DEFAULT (0),
        [NoFacts] BIGINT NOT NULL CONSTRAINT [DF_FaktFileProgress_NoFacts] DEFAULT (0),
        [Errors] BIGINT NOT NULL CONSTRAINT [DF_FaktFileProgress_Errors] DEFAULT (0),
        [Observations] BIGINT NOT NULL CONSTRAINT [DF_FaktFileProgress_Observations] DEFAULT (0),
        [Completed] BIT NOT NULL CONSTRAINT [DF_FaktFileProgress_Completed] DEFAULT (0),
        [UpdatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_FaktFileProgress_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_FaktFileProgress] PRIMARY KEY CLUSTERED ([SourceFileId], [ExtractionVersion])
    );
GO

IF OBJECT_ID({{AUX_LIT:FaktRowOutcomes}}, N'U') IS NULL
    CREATE TABLE {{AUX:FaktRowOutcomes}}
    (
        [SourceFileId] BIGINT NOT NULL,
        [ExtractionVersion] VARCHAR(64) NOT NULL,
        [RecordOrdinal] BIGINT NOT NULL,
        -- 1 — извлечены факты, 2 — обработана без результата, 3 — ошибка записи.
        [Outcome] TINYINT NOT NULL CONSTRAINT [CK_FaktRowOutcomes_Outcome] CHECK ([Outcome] IN (1, 2, 3)),
        [ObservationCount] INT NOT NULL,
        [SourceLine] BIGINT NULL,
        [RowHash] CHAR(64) NULL,
        [JobId] BIGINT NULL,
        [DetailsJson] NVARCHAR(MAX) NULL CONSTRAINT [CK_FaktRowOutcomes_Details_IsJson] CHECK ([DetailsJson] IS NULL OR ISJSON([DetailsJson]) = 1),
        [ProcessedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_FaktRowOutcomes_ProcessedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [PK_FaktRowOutcomes] PRIMARY KEY CLUSTERED ([SourceFileId], [ExtractionVersion], [RecordOrdinal])
    );
GO

IF OBJECT_ID({{AUX_LIT:FaktRowErrors}}, N'U') IS NULL
    CREATE TABLE {{AUX:FaktRowErrors}}
    (
        [ErrorId] BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT [PK_FaktRowErrors] PRIMARY KEY CLUSTERED,
        [JobFileId] BIGINT NOT NULL,
        [SourceFileId] BIGINT NOT NULL,
        [ExtractionVersion] VARCHAR(64) NOT NULL,
        [RecordOrdinal] BIGINT NOT NULL,
        [SourceLine] BIGINT NULL,
        [Stage] VARCHAR(32) NOT NULL,
        [ErrorCode] VARCHAR(64) NOT NULL,
        [Message] NVARCHAR(2000) NULL,
        [CreatedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_FaktRowErrors_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT [UQ_FaktRowErrors_Row] UNIQUE ([SourceFileId], [ExtractionVersion], [RecordOrdinal], [Stage])
    );
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID({{AUX_LIT:FaktRowErrors}}) AND name = N'IX_FaktRowErrors_JobFileId')
    CREATE NONCLUSTERED INDEX [IX_FaktRowErrors_JobFileId] ON {{AUX:FaktRowErrors}} ([JobFileId]);
GO

IF OBJECT_ID({{AUX_LIT:FaktCommitLog}}, N'U') IS NULL
    CREATE TABLE {{AUX:FaktCommitLog}}
    (
        [CommitId] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_FaktCommitLog] PRIMARY KEY NONCLUSTERED,
        [JobFileId] BIGINT NOT NULL,
        [ToOrdinal] BIGINT NOT NULL,
        [CommittedAtUtc] DATETIME2(3) NOT NULL CONSTRAINT [DF_FaktCommitLog_CommittedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
GO
