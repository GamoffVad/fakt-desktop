using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Extraction;
using Fakt.Core.Logging;
using Fakt.Core.Settings;
using Fakt.Core.Storage;

namespace Fakt.Infrastructure.Sql;

/// <summary>
/// Проверка соединения, прав, ожидаемых столбцов, вспомогательных объектов и Full-Text Search;
/// применение явных миграций. Существующие таблицы не удаляются и не пересоздаются.
/// </summary>
public sealed class DatabaseAdmin : IDatabaseAdmin
{
    public static readonly string[] AuxiliaryTables =
    {
        "FaktJobs", "FaktJobFiles", "FaktFileProgress", "FaktRowOutcomes", "FaktRowErrors", "FaktCommitLog", "FaktSearchDocs", "FaktFactValues",
    };

    private const string HistoryTable = "FaktSchemaMigrations";
    private readonly IAppLogger _logger;

    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb", "tempdb", "distribution" };

    public DatabaseAdmin(IAppLogger logger)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task<DatabaseProvisionResult> EnsureDatabaseAsync(DatabaseSettings settings, string sqlPassword, string appliedBy, CancellationToken cancellationToken)
    {
        var result = new DatabaseProvisionResult();
        var name = settings.Database?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > SqlNames.MaxIdentifierLength || name.Any(char.IsControl))
        {
            return Failed(result, ErrorCategory.Configuration, "Недопустимое имя базы данных: от 1 до 128 символов, без управляющих символов.");
        }

        if (SystemDatabases.Contains(name))
        {
            result.Existed = true;
            return result;
        }

        // Наличие проверяется из master с теми же сервером, учётными данными и параметрами шифрования.
        var master = Newtonsoft.Json.JsonConvert.DeserializeObject<DatabaseSettings>(Newtonsoft.Json.JsonConvert.SerializeObject(settings));
        master.Database = "master";
        SqlConnectionFactory factory;
        try
        {
            factory = new SqlConnectionFactory(master, sqlPassword);
        }
        catch (ArgumentException ex)
        {
            return Failed(result, ErrorCategory.Configuration, ex.Message);
        }

        try
        {
            using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (await DatabaseExistsAsync(factory, connection, name, cancellationToken).ConfigureAwait(false))
            {
                result.Existed = true;
                return result;
            }

            // Имя базы нельзя передать параметром в CREATE DATABASE — только экранированный идентификатор.
            using (var create = factory.Command(connection, $"CREATE DATABASE {SqlNames.Quote(name)};", timeoutSeconds: Math.Max(factory.CommandTimeout, 300)))
            {
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await WaitOnlineAsync(factory, connection, name, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == 1801)
        {
            // База появилась одновременно (другой экземпляр приложения) или не видна по правам метаданных.
            result.Existed = true;
            return result;
        }
        catch (Exception ex) when (ex is SqlException || ex is InvalidOperationException)
        {
            var (category, message) = SqlConnectionFactory.Describe(ex);
            if (ex is SqlException sql && (sql.Number == 262 || sql.Number == 5133 || sql.Number == 1802))
            {
                category = ErrorCategory.Access;
                message = $"База данных «{name}» не найдена на сервере, и создать её не удалось: у учётной записи нет права CREATE DATABASE " +
                          "или сервер не может создать файлы базы. Создайте базу вручную или выдайте роль dbcreator. " + sql.Message;
            }

            _logger.Error("db.create", $"Не удалось проверить или создать базу данных {name}", category, ex);
            return Failed(result, category, message);
        }

        result.Created = true;
        _logger.Info("db.created", $"База данных {name} не найдена и создана автоматически", e => e.User = appliedBy);

        // Новая пустая база: в ней нет существующих таблиц и данных, поэтому схема FAKT создаётся сразу всеми миграциями.
        foreach (var migration in MigrationCatalog.All)
        {
            var applied = await ApplyMigrationAsync(settings, sqlPassword, migration.Id, appliedBy, cancellationToken).ConfigureAwait(false);
            if (!applied.Success)
            {
                return Failed(result, ErrorCategory.Configuration,
                    $"База данных «{name}» создана автоматически, но схема создана не полностью: {applied.Message} Остальные миграции можно применить явно в разделе «Миграции схемы».");
            }

            result.AppliedMigrations.Add(migration.Id);
        }

        result.Message = $"База данных «{name}» не найдена на сервере и создана автоматически; схема FAKT создана (миграции {result.AppliedMigrations.First()}–{result.AppliedMigrations.Last()}).";
        return result;
    }

    private static DatabaseProvisionResult Failed(DatabaseProvisionResult result, ErrorCategory category, string message)
    {
        result.Success = false;
        result.ErrorCategory = category;
        result.Message = message;
        return result;
    }

    private static async Task<bool> DatabaseExistsAsync(SqlConnectionFactory factory, SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        using var command = factory.Command(connection, "SELECT DB_ID(@name);");
        command.Parameters.Add("@name", SqlDbType.NVarChar, SqlNames.MaxIdentifierLength).Value = name;
        var id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return id != null && id != DBNull.Value;
    }

    private static async Task WaitOnlineAsync(SqlConnectionFactory factory, SqlConnection connection, string name, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using (var command = factory.Command(connection, "SELECT state_desc FROM sys.databases WHERE name = @name;"))
            {
                command.Parameters.Add("@name", SqlDbType.NVarChar, SqlNames.MaxIdentifierLength).Value = name;
                if (string.Equals(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string, "ONLINE", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"База данных «{name}» создана, но не перешла в состояние ONLINE за 30 секунд.");
    }

    public async Task<SchemaReport> InspectAsync(DatabaseSettings settings, string sqlPassword, CancellationToken cancellationToken)
    {
        var report = new SchemaReport { DatabaseName = settings.Database };
        SqlConnectionFactory factory;
        try
        {
            factory = new SqlConnectionFactory(settings, sqlPassword);
        }
        catch (ArgumentException ex)
        {
            report.ConnectionError = ex.Message;
            report.ErrorCategory = ErrorCategory.Configuration;
            report.ProcessingBlockers.Add(ex.Message);
            report.SearchBlockers.Add(ex.Message);
            return report;
        }

        SqlConnection connection;
        try
        {
            connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException || ex is InvalidOperationException)
        {
            var (category, message) = SqlConnectionFactory.Describe(ex);
            report.ConnectionError = message;
            report.ErrorCategory = category;
            report.ProcessingBlockers.Add("Нет соединения с базой данных.");
            report.SearchBlockers.Add("Нет соединения с базой данных.");
            _logger.Error("db.connect", "Не удалось подключиться к SQL Server", category, ex);
            return report;
        }

        using (connection)
        {
            report.Connected = true;
            var names = factory.Names;
            await ReadServerInfoAsync(factory, connection, report, cancellationToken).ConfigureAwait(false);
            report.SourceFiles = await ReadTableAsync(factory, connection, names.Schema, names.SourceFilesTable, cancellationToken).ConfigureAwait(false);
            report.PersonFacts = await ReadTableAsync(factory, connection, names.Schema, names.PersonFactsTable, cancellationToken).ConfigureAwait(false);
            var auxPresent = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var table in AuxiliaryTables)
            {
                auxPresent[table] = await ObjectExistsAsync(factory, connection, names.Aux(table), cancellationToken).ConfigureAwait(false);
            }

            await ReadPermissionsAsync(factory, connection, report, names, cancellationToken).ConfigureAwait(false);
            report.FullText = await ReadFullTextAsync(factory, connection, names, auxPresent["FaktSearchDocs"], cancellationToken).ConfigureAwait(false);
            await ReadMigrationsAsync(factory, connection, report, names, cancellationToken).ConfigureAwait(false);
            var constraints = await CheckConstraintsAsync(factory, connection, report, names, cancellationToken).ConfigureAwait(false);
            Evaluate(report, settings, names, auxPresent, constraints);
        }

        return report;
    }

    private static async Task ReadServerInfoAsync(SqlConnectionFactory factory, SqlConnection connection, SchemaReport report, CancellationToken cancellationToken)
    {
        using (var command = factory.Command(connection,
                   "SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)), CAST(SERVERPROPERTY('Edition') AS NVARCHAR(128)), @@VERSION, DB_NAME(), SUSER_SNAME(), CAST(CONNECTIONPROPERTY('auth_scheme') AS NVARCHAR(40))"))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                report.ProductVersion = reader.IsDBNull(0) ? null : reader.GetString(0);
                report.Edition = reader.IsDBNull(1) ? null : reader.GetString(1);
                var version = reader.IsDBNull(2) ? null : reader.GetString(2);
                report.ServerVersion = version?.Split('\n').FirstOrDefault()?.Trim();
                report.DatabaseName = reader.IsDBNull(3) ? report.DatabaseName : reader.GetString(3);
                report.LoginName = reader.IsDBNull(4) ? null : reader.GetString(4);
                report.AuthScheme = reader.IsDBNull(5) ? null : reader.GetString(5);
            }
        }

        try
        {
            using var encrypt = factory.Command(connection, "SELECT encrypt_option FROM sys.dm_exec_connections WHERE session_id = @@SPID");
            var value = await encrypt.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is string text)
            {
                report.EncryptedConnection = string.Equals(text, "TRUE", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (SqlException)
        {
            // Без VIEW SERVER STATE состояние шифрования канала неизвестно; это не ошибка.
        }
    }

    private static async Task<TableInfo> ReadTableAsync(SqlConnectionFactory factory, SqlConnection connection, string schema, string table, CancellationToken cancellationToken)
    {
        var info = new TableInfo { Schema = schema, Name = table };
        const string sql = @"
SELECT c.name, t.name, c.max_length, c.is_nullable, c.is_identity, c.is_computed, dc.definition,
       (SELECT SUM(p.rows) FROM sys.partitions AS p WHERE p.object_id = c.object_id AND p.index_id IN (0, 1))
FROM sys.columns AS c
INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints AS dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE c.object_id = OBJECT_ID(@obj, N'U')
ORDER BY c.column_id;";
        using var command = factory.Command(connection, sql);
        command.Parameters.Add("@obj", SqlDbType.NVarChar, 600).Value = SqlNames.Quote(schema) + "." + SqlNames.Quote(table);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            info.Exists = true;
            var type = reader.GetString(1);
            int? length = reader.GetInt16(2);
            if (length == -1)
            {
                length = -1;
            }
            else if (type == "nvarchar" || type == "nchar")
            {
                length /= 2;
            }
            else if (!(type == "varchar" || type == "char" || type == "varbinary" || type == "binary"))
            {
                length = null;
            }

            info.Columns.Add(new ColumnInfo
            {
                Name = reader.GetString(0),
                DataType = type,
                MaxLength = length,
                IsNullable = reader.GetBoolean(3),
                IsIdentity = reader.GetBoolean(4),
                IsComputed = reader.GetBoolean(5),
                DefaultDefinition = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
            info.ApproximateRows ??= reader.IsDBNull(7) ? (long?)null : Convert.ToInt64(reader.GetValue(7));
        }

        return info;
    }

    private static async Task<bool> ObjectExistsAsync(SqlConnectionFactory factory, SqlConnection connection, string quotedName, CancellationToken cancellationToken)
    {
        using var command = factory.Command(connection, "SELECT CASE WHEN OBJECT_ID(@obj, N'U') IS NULL THEN 0 ELSE 1 END");
        command.Parameters.Add("@obj", SqlDbType.NVarChar, 600).Value = quotedName;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    private static async Task ReadPermissionsAsync(SqlConnectionFactory factory, SqlConnection connection, SchemaReport report, SqlNames names, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT ISNULL(HAS_PERMS_BY_NAME(@sf, N'OBJECT', N'SELECT'), 0) & ISNULL(HAS_PERMS_BY_NAME(@pf, N'OBJECT', N'SELECT'), 0),
       ISNULL(HAS_PERMS_BY_NAME(@sf, N'OBJECT', N'INSERT'), 0) & ISNULL(HAS_PERMS_BY_NAME(@pf, N'OBJECT', N'INSERT'), 0),
       ISNULL(HAS_PERMS_BY_NAME(@pf, N'OBJECT', N'UPDATE'), 0),
       ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 0),
       ISNULL(HAS_PERMS_BY_NAME(@schema, N'SCHEMA', N'ALTER'), 0),
       ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE FULLTEXT CATALOG'), 0);";
        using var command = factory.Command(connection, sql);
        command.Parameters.Add("@sf", SqlDbType.NVarChar, 600).Value = names.SF;
        command.Parameters.Add("@pf", SqlDbType.NVarChar, 600).Value = names.PF;
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 256).Value = names.Schema;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            report.Permissions = new PermissionInfo
            {
                CanSelect = Convert.ToInt32(reader.GetValue(0)) == 1,
                CanInsert = Convert.ToInt32(reader.GetValue(1)) == 1,
                CanUpdate = Convert.ToInt32(reader.GetValue(2)) == 1,
                CanCreateTable = Convert.ToInt32(reader.GetValue(3)) == 1,
                CanAlter = Convert.ToInt32(reader.GetValue(4)) == 1,
                CanCreateFullText = Convert.ToInt32(reader.GetValue(5)) == 1,
            };
        }
    }

    public static async Task<FullTextInfo> ReadFullTextAsync(SqlConnectionFactory factory, SqlConnection connection, SqlNames names, bool projectionExists, CancellationToken cancellationToken)
    {
        var info = new FullTextInfo();
        using (var command = factory.Command(connection,
                   "SELECT CAST(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') AS INT), (SELECT COUNT(*) FROM sys.fulltext_catalogs WHERE name = N'FaktCatalog')"))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                info.Installed = !reader.IsDBNull(0) && reader.GetInt32(0) == 1;
                info.CatalogExists = reader.GetInt32(1) > 0;
            }
        }

        if (!projectionExists)
        {
            info.Notes = "Поисковая проекция не создана (миграция V006).";
            return info;
        }

        const string sql = @"
DECLARE @id INT = OBJECT_ID(@obj, N'U');
SELECT CAST(OBJECTPROPERTYEX(@id, 'TableHasActiveFulltextIndex') AS INT),
       CAST(OBJECTPROPERTYEX(@id, 'TableFulltextPopulateStatus') AS INT),
       CAST(OBJECTPROPERTYEX(@id, 'TableFulltextPendingChanges') AS BIGINT),
       CAST(OBJECTPROPERTYEX(@id, 'TableFulltextItemCount') AS BIGINT),
       (SELECT TOP (1) language_id FROM sys.fulltext_index_columns WHERE object_id = @id);";
        using (var command = factory.Command(connection, sql))
        {
            command.Parameters.Add("@obj", SqlDbType.NVarChar, 600).Value = names.Aux("FaktSearchDocs");
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                info.IndexExists = !reader.IsDBNull(0) && reader.GetInt32(0) == 1;
                info.PopulateStatus = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                info.PendingChanges = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                info.IndexedItems = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
                info.Language = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
            }
        }

        info.PopulateStatusText = info.PopulateStatus switch
        {
            0 => "простаивает (индекс актуален)",
            1 => "идёт полное заполнение",
            2 => "заполнение приостановлено",
            3 => "заполнение замедлено",
            4 => "восстановление",
            5 => "выключено",
            6 => "идёт добавочное заполнение",
            7 => "построение индекса",
            8 => "диск заполнен",
            9 => "применение изменений",
            _ => null,
        };
        if (info.PendingChanges > 0)
        {
            info.Notes = $"Изменений, ещё не попавших в полнотекстовый индекс: {info.PendingChanges}. Недавно сохранённые записи появятся в полнотекстовом поиске с задержкой.";
        }

        return info;
    }

    private static async Task ReadMigrationsAsync(SqlConnectionFactory factory, SqlConnection connection, SchemaReport report, SqlNames names, CancellationToken cancellationToken)
    {
        var applied = new Dictionary<string, (DateTime At, string By)>(StringComparer.OrdinalIgnoreCase);
        if (await ObjectExistsAsync(factory, connection, names.Aux(HistoryTable), cancellationToken).ConfigureAwait(false))
        {
            using var command = factory.Command(connection, $"SELECT [Id], [AppliedAtUtc], [AppliedBy] FROM {names.Aux(HistoryTable)}");
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                applied[reader.GetString(0)] = (reader.GetDateTime(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        foreach (var script in MigrationCatalog.All)
        {
            var isApplied = applied.TryGetValue(script.Id, out var record);
            report.Migrations.Add(new MigrationInfo
            {
                Id = script.Id,
                Title = script.Title,
                Description = script.Description,
                Applied = isApplied,
                AppliedAtUtc = isApplied ? record.At : (DateTime?)null,
                AppliedBy = isApplied ? record.By : null,
                AltersUserTables = script.AltersUserTables,
                RequiresNoTransaction = !script.UseTransaction,
                RequiredForProcessing = script.RequiredForProcessing,
                RequiredForSearch = script.RequiredForSearch,
                Script = names.Render(script.Template),
            });
        }
    }

    private sealed class ConstraintFacts
    {
        public bool ForeignKey { get; set; }
        public bool IsJsonCheck { get; set; }
        public bool FileCodeUnique { get; set; }
        public bool ObservationUnique { get; set; }
        public bool VersionUnique { get; set; }
    }

    private static async Task<ConstraintFacts> CheckConstraintsAsync(SqlConnectionFactory factory, SqlConnection connection, SchemaReport report, SqlNames names, CancellationToken cancellationToken)
    {
        var facts = new ConstraintFacts();
        if (report.SourceFiles?.Exists != true || report.PersonFacts?.Exists != true)
        {
            return null;
        }

        const string sql = @"
DECLARE @sfId INT = OBJECT_ID(@sf, N'U'), @pfId INT = OBJECT_ID(@pf, N'U');
SELECT
  (SELECT COUNT(*) FROM sys.foreign_keys AS fk
     INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
   WHERE fk.parent_object_id = @pfId AND fk.referenced_object_id = @sfId
     AND COL_NAME(fkc.parent_object_id, fkc.parent_column_id) = @fileIdCol
     AND COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) = @sfIdCol),
  (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = @pfId AND LOWER(definition) LIKE N'%isjson%'),
  (SELECT COUNT(*) FROM sys.indexes AS i
     INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
   WHERE i.object_id = @sfId AND i.is_unique = 1 AND COL_NAME(ic.object_id, ic.column_id) = @fileCodeCol
     AND (SELECT COUNT(*) FROM sys.index_columns AS x WHERE x.object_id = i.object_id AND x.index_id = i.index_id AND x.is_included_column = 0) = 1),
  (SELECT COUNT(*) FROM sys.indexes WHERE object_id = @pfId AND is_unique = 1 AND name = @obsIndex),
  (SELECT COUNT(*) FROM sys.indexes WHERE object_id = @sfId AND is_unique = 1 AND name = @verIndex);";
        using var command = factory.Command(connection, sql);
        command.Parameters.Add("@sf", SqlDbType.NVarChar, 600).Value = names.SF;
        command.Parameters.Add("@pf", SqlDbType.NVarChar, 600).Value = names.PF;
        command.Parameters.Add("@fileIdCol", SqlDbType.NVarChar, 128).Value = names.Columns.FileId;
        command.Parameters.Add("@sfIdCol", SqlDbType.NVarChar, 128).Value = names.Columns.SourceFilesId;
        command.Parameters.Add("@fileCodeCol", SqlDbType.NVarChar, 128).Value = names.Columns.FileCode;
        command.Parameters.Add("@obsIndex", SqlDbType.NVarChar, 256).Value = "UX_" + names.PersonFactsTable + "_Observation";
        command.Parameters.Add("@verIndex", SqlDbType.NVarChar, 256).Value = "UX_" + names.SourceFilesTable + "_Version";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            facts.ForeignKey = reader.GetInt32(0) > 0;
            facts.IsJsonCheck = reader.GetInt32(1) > 0;
            facts.FileCodeUnique = reader.GetInt32(2) > 0;
            facts.ObservationUnique = reader.GetInt32(3) > 0;
            facts.VersionUnique = reader.GetInt32(4) > 0;
        }

        return facts;
    }

    private static void Evaluate(SchemaReport report, DatabaseSettings settings, SqlNames names, Dictionary<string, bool> auxPresent, ConstraintFacts constraintFacts)
    {
        var issues = report.Issues;
        void Issue(IssueSeverity severity, string obj, string message, string expected = null, string actual = null, string suggestion = null,
            string migration = null, string logical = null, bool blocksProcessing = false, bool blocksSearch = false)
        {
            issues.Add(new SchemaIssue
            {
                Severity = severity, Object = obj, Message = message, Expected = expected, Actual = actual,
                Suggestion = suggestion, MigrationId = migration, LogicalField = logical,
            });
            if (blocksProcessing)
            {
                report.ProcessingBlockers.Add(message);
            }

            if (blocksSearch)
            {
                report.SearchBlockers.Add(message);
            }
        }

        var sf = report.SourceFiles;
        var pf = report.PersonFacts;
        if (!sf.Exists)
        {
            Issue(IssueSeverity.Blocking, names.SF, $"Таблица {names.SF} не найдена.", suggestion: "Укажите имя существующей таблицы или примените миграцию V001.", migration: "V001", blocksProcessing: true, blocksSearch: true);
        }

        if (!pf.Exists)
        {
            Issue(IssueSeverity.Blocking, names.PF, $"Таблица {names.PF} не найдена.", suggestion: "Укажите имя существующей таблицы или примените миграцию V001.", migration: "V001", blocksProcessing: true, blocksSearch: true);
        }

        ColumnInfo Find(TableInfo table, string name) => table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        void CheckColumn(TableInfo table, string tableName, string logical, string physical, string[] okTypes, string[] warnTypes, int? minLength,
            bool mustBeNullable, bool mustBeNotNull, bool identity, string expected)
        {
            var column = Find(table, physical);
            var obj = tableName + "." + SqlNames.Quote(physical);
            if (column == null)
            {
                Issue(IssueSeverity.Blocking, obj, $"Столбец {obj} не найден.", expected, "нет",
                    $"Сопоставьте логическое поле «{logical}» с существующим столбцом (вкладка «База данных» → «Сопоставление имён»).", logical: logical,
                    blocksProcessing: true, blocksSearch: true);
                return;
            }

            var type = column.DataType.ToLowerInvariant();
            if (!okTypes.Contains(type))
            {
                var severity = warnTypes.Contains(type) ? IssueSeverity.Warning : IssueSeverity.Blocking;
                Issue(severity, obj, $"Столбец {obj}: тип {column.TypeText()} вместо ожидаемого.", expected, column.TypeText(),
                    severity == IssueSeverity.Warning ? "Запись возможна, но с ограничениями (см. сообщение)." : "Тип несовместим; измените сопоставление или структуру таблицы вручную.",
                    logical: logical, blocksProcessing: severity == IssueSeverity.Blocking, blocksSearch: severity == IssueSeverity.Blocking);
            }

            if (minLength.HasValue && column.MaxLength.HasValue && column.MaxLength.Value >= 0 && column.MaxLength.Value < minLength.Value)
            {
                Issue(IssueSeverity.Warning, obj, $"Столбец {obj}: длина {column.MaxLength} меньше рекомендуемой {minLength}. Более длинные значения не будут усечены: они сохранятся в [ALL] как неразрешённые.", expected, column.TypeText(), logical: logical);
            }

            if (mustBeNullable && !column.IsNullable)
            {
                Issue(IssueSeverity.Blocking, obj, $"Столбец {obj} не допускает NULL, а отсутствующие значения записываются как NULL.", expected, column.TypeText(),
                    "Разрешите NULL в столбце или сопоставьте другой столбец.", logical: logical, blocksProcessing: true);
            }

            if (mustBeNotNull && column.IsNullable)
            {
                Issue(IssueSeverity.Warning, obj, $"Столбец {obj} допускает NULL; ожидается NOT NULL.", expected, column.TypeText(), logical: logical);
            }

            if (identity && !column.IsIdentity)
            {
                Issue(IssueSeverity.Blocking, obj, $"Столбец {obj} не является IDENTITY: приложение не назначает идентификаторы само.", expected, column.TypeText(), logical: logical, blocksProcessing: true);
            }
        }

        var map = names.Columns;
        if (sf.Exists)
        {
            CheckColumn(sf, names.SF, "ID (SourceFiles)", map.SourceFilesId, new[] { "bigint" }, new[] { "int" }, null, false, true, true, "BIGINT IDENTITY, PK");
            CheckColumn(sf, names.SF, "FileCode", map.FileCode, new[] { "varchar", "nvarchar", "char", "nchar" }, Array.Empty<string>(), 20, false, true, false, "VARCHAR(32) NOT NULL UNIQUE");
            CheckColumn(sf, names.SF, "FileName", map.FileName, new[] { "nvarchar" }, new[] { "varchar" }, 260, false, true, false, "NVARCHAR(1024) NOT NULL");
            foreach (var technical in new[] { "SourcePath", "SourcePathHash", "FileSize", "LastWriteTimeUtc", "ContentHash", "CreatedAtUtc" })
            {
                if (Find(sf, technical) == null)
                {
                    Issue(IssueSeverity.Blocking, names.SF + "." + SqlNames.Quote(technical), $"Нет технического столбца {technical}: версии файлов нельзя различить без дубликатов.",
                        "см. миграцию V002", "нет", "Примените миграцию V002 (добавляет только необязательные столбцы).", "V002", blocksProcessing: true);
                }
            }

            if (constraintFacts != null && !constraintFacts.FileCodeUnique && Find(sf, map.FileCode) != null)
            {
                Issue(IssueSeverity.Warning, names.SF + "." + SqlNames.Quote(map.FileCode), "Нет ограничения UNIQUE на FileCode: уникальность кода обеспечивается только приложением.", "UNIQUE", "нет");
            }

            if (constraintFacts != null && !constraintFacts.VersionUnique && Find(sf, "ContentHash") != null)
            {
                Issue(IssueSeverity.Blocking, names.SF, "Нет уникального индекса версии источника (путь + хеш).", "UX_…_Version", "нет", "Примените миграцию V002.", "V002", blocksProcessing: true);
            }
        }

        if (pf.Exists)
        {
            CheckColumn(pf, names.PF, "ID (PersonFacts)", map.PersonFactsId, new[] { "bigint" }, new[] { "int" }, null, false, true, true, "BIGINT IDENTITY, PK");
            CheckColumn(pf, names.PF, "Фамилия", map.Surname, new[] { "nvarchar" }, new[] { "varchar" }, 200, true, false, false, "NVARCHAR(200) NULL");
            CheckColumn(pf, names.PF, "Имя", map.Name, new[] { "nvarchar" }, new[] { "varchar" }, 200, true, false, false, "NVARCHAR(200) NULL");
            CheckColumn(pf, names.PF, "Отчество", map.Patronymic, new[] { "nvarchar" }, new[] { "varchar" }, 200, true, false, false, "NVARCHAR(200) NULL");
            CheckColumn(pf, names.PF, "Дата рождения", map.BirthDate, new[] { "date" }, new[] { "datetime", "datetime2", "smalldatetime" }, null, true, false, false, "DATE NULL");
            CheckColumn(pf, names.PF, "Место рождения", map.BirthPlace, new[] { "nvarchar" }, new[] { "varchar" }, 1000, true, false, false, "NVARCHAR(1000) NULL");
            CheckColumn(pf, names.PF, "ALL", map.All, new[] { "nvarchar" }, Array.Empty<string>(), null, false, false, false, "NVARCHAR(MAX) NOT NULL, ISJSON");
            CheckColumn(pf, names.PF, "ID_FileName", map.FileId, new[] { "bigint" }, new[] { "int" }, null, false, true, false, "BIGINT NOT NULL, FK → SourceFiles.ID");
            var all = Find(pf, map.All);
            if (all != null && all.MaxLength.HasValue && all.MaxLength.Value > 0)
            {
                Issue(IssueSeverity.Warning, names.PF + "." + SqlNames.Quote(map.All), $"[ALL] ограничен {all.MaxLength} символами; более длинный JSON вызовет ошибку записи этой строки (без усечения).", "NVARCHAR(MAX)", all.TypeText());
            }

            foreach (var technical in new[] { "SourceRecordKey", "PersonIndex", "ExtractionVersion", "JobId", "CreatedAtUtc" })
            {
                if (Find(pf, technical) == null)
                {
                    Issue(IssueSeverity.Blocking, names.PF + "." + SqlNames.Quote(technical), $"Нет технического столбца {technical}: идемпотентная запись невозможна.",
                        "см. миграцию V003", "нет", "Примените миграцию V003 (добавляет только необязательные столбцы).", "V003", blocksProcessing: true);
                }
            }

            if (constraintFacts != null)
            {
                if (!constraintFacts.ObservationUnique && Find(pf, "SourceRecordKey") != null)
                {
                    Issue(IssueSeverity.Blocking, names.PF, "Нет уникального индекса наблюдений (файл, запись, лицо, версия извлечения).", "UX_…_Observation", "нет", "Примените миграцию V003.", "V003", blocksProcessing: true);
                }

                if (!constraintFacts.ForeignKey)
                {
                    Issue(IssueSeverity.Warning, names.PF, "Нет внешнего ключа ID_FileName → SourceFiles.ID: связь не проверяется сервером.", "FOREIGN KEY", "нет");
                }

                if (!constraintFacts.IsJsonCheck)
                {
                    Issue(IssueSeverity.Warning, names.PF + "." + SqlNames.Quote(map.All), "Нет проверки ISJSON([ALL]) = 1.", "CHECK (ISJSON)", "нет", "Примените миграцию V004.", "V004");
                }
            }

            report.Limits = new FieldLimits
            {
                Surname = LimitOf(Find(pf, map.Surname), 200),
                Name = LimitOf(Find(pf, map.Name), 200),
                Patronymic = LimitOf(Find(pf, map.Patronymic), 200),
                BirthPlace = LimitOf(Find(pf, map.BirthPlace), 1000),
            };
        }

        foreach (var table in new[] { "FaktJobs", "FaktJobFiles", "FaktFileProgress", "FaktRowOutcomes", "FaktRowErrors", "FaktCommitLog" })
        {
            if (!auxPresent[table])
            {
                Issue(IssueSeverity.Blocking, names.Aux(table), $"Нет вспомогательной таблицы {names.Aux(table)}.", "таблица", "нет", "Примените миграцию V005.", "V005", blocksProcessing: true);
            }
        }

        foreach (var table in new[] { "FaktSearchDocs", "FaktFactValues" })
        {
            if (!auxPresent[table])
            {
                Issue(IssueSeverity.Blocking, names.Aux(table), $"Нет поисковой проекции {names.Aux(table)}.", "таблица", "нет", "Примените миграцию V006.", "V006", blocksProcessing: true, blocksSearch: true);
            }
        }

        var fts = report.FullText;
        if (!fts.Installed)
        {
            Issue(IssueSeverity.Warning, "Full-Text Search", "Компонент Full-Text Search не установлен: режимы «Все слова», «Любое слово», «Точная фраза» недоступны. Доступен только медленный режим «Подстрока».",
                "установлен", "нет", "Установите компонент Full-Text Search на SQL Server и примените миграцию V007.");
        }
        else if (!fts.IndexExists)
        {
            Issue(IssueSeverity.Warning, "Full-Text Search", "Полнотекстовый индекс поисковой проекции не создан: полнотекстовые режимы поиска недоступны.",
                "FULLTEXT INDEX", "нет", "Примените миграцию V007.", "V007");
        }

        if (!report.Permissions.CanSelect)
        {
            Issue(IssueSeverity.Blocking, "Права", "Нет права SELECT на основные таблицы.", "SELECT", "нет", blocksProcessing: true, blocksSearch: true);
        }

        if (!report.Permissions.CanInsert)
        {
            Issue(IssueSeverity.Blocking, "Права", "Нет права INSERT на основные таблицы.", "INSERT", "нет", blocksProcessing: true);
        }

        if (settings.Encrypt == SqlEncryptMode.Optional)
        {
            Issue(IssueSeverity.Warning, "Соединение", "Шифрование соединения не обязательно: используйте только в изолированной сети.", "Encrypt=Mandatory", "Optional");
        }

        if (settings.TrustServerCertificate)
        {
            Issue(IssueSeverity.Warning, "Соединение", "Сертификат сервера не проверяется (TrustServerCertificate): соединение уязвимо для подмены сервера.", "проверка сертификата", "отключена");
        }
    }

    private static int LimitOf(ColumnInfo column, int fallback)
    {
        if (column?.MaxLength == null)
        {
            return fallback;
        }

        return column.MaxLength.Value < 0 ? int.MaxValue : column.MaxLength.Value;
    }

    public async Task<MigrationResult> ApplyMigrationAsync(DatabaseSettings settings, string sqlPassword, string migrationId, string appliedBy, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var script = MigrationCatalog.Find(migrationId);
        var factory = new SqlConnectionFactory(settings, sqlPassword);
        var names = factory.Names;
        var rendered = names.Render(script.Template);
        var batches = SqlNames.SplitBatches(rendered);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureHistoryTableAsync(factory, connection, names, cancellationToken).ConfigureAwait(false);
        SqlTransaction transaction = null;
        try
        {
            if (script.UseTransaction)
            {
                transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            }

            foreach (var batch in batches)
            {
                using var command = factory.Command(connection, batch, transaction, timeoutSeconds: Math.Max(factory.CommandTimeout, 600));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var record = factory.Command(connection, $@"
IF NOT EXISTS (SELECT 1 FROM {names.Aux(HistoryTable)} WHERE [Id] = @id)
    INSERT INTO {names.Aux(HistoryTable)} ([Id], [Title], [AppliedAtUtc], [AppliedBy]) VALUES (@id, @title, SYSUTCDATETIME(), @by);", transaction))
            {
                record.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = script.Id;
                record.Parameters.Add("@title", SqlDbType.NVarChar, 400).Value = script.Title ?? script.Id;
                record.Parameters.Add("@by", SqlDbType.NVarChar, 256).Value = (object)appliedBy ?? DBNull.Value;
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction?.Commit();
            _logger.Info("db.migration", $"Применена миграция {script.Id}", e =>
            {
                e.User = appliedBy;
                e.DurationMs = stopwatch.ElapsedMilliseconds;
            });
            return new MigrationResult { Success = true, Message = $"Миграция {script.Id} применена.", Elapsed = stopwatch.Elapsed };
        }
        catch (SqlException ex)
        {
            try
            {
                transaction?.Rollback();
            }
            catch (InvalidOperationException)
            {
            }

            var (category, message) = SqlConnectionFactory.Describe(ex);
            _logger.Error("db.migration", $"Миграция {script.Id} не применена", category, ex);
            return new MigrationResult
            {
                Success = false,
                Message = $"Миграция {script.Id} не применена{(script.UseTransaction ? " (изменения отменены)" : string.Empty)}: {message}",
                Elapsed = stopwatch.Elapsed,
            };
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    /// <summary>Журнал применённых миграций — вспомогательная таблица, создаётся при первом явном применении миграции.</summary>
    private static async Task EnsureHistoryTableAsync(SqlConnectionFactory factory, SqlConnection connection, SqlNames names, CancellationToken cancellationToken)
    {
        var createSchema = $@"
IF SCHEMA_ID({SqlNames.Literal(names.AuxSchema)}) IS NULL
    EXEC(N'CREATE SCHEMA ' + {SqlNames.Literal(SqlNames.Quote(names.AuxSchema))});";
        var createTable = $@"
IF OBJECT_ID({SqlNames.Literal(names.Aux(HistoryTable))}, N'U') IS NULL
    CREATE TABLE {names.Aux(HistoryTable)}
    (
        [Id] NVARCHAR(100) NOT NULL CONSTRAINT [PK_FaktSchemaMigrations] PRIMARY KEY,
        [Title] NVARCHAR(400) NULL,
        [AppliedAtUtc] DATETIME2(3) NOT NULL,
        [AppliedBy] NVARCHAR(256) NULL
    );";
        foreach (var sql in new[] { createSchema, createTable })
        {
            using var command = factory.Command(connection, sql);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<long> RebuildSearchProjectionAsync(DatabaseSettings settings, string sqlPassword, IProgress<long> progress, CancellationToken cancellationToken)
    {
        var factory = new SqlConnectionFactory(settings, sqlPassword);
        var names = factory.Names;
        var c = names.Columns;
        long lastId = 0;
        long total = 0;
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = new List<(long Id, long FileId, SearchDocument Doc)>();
            var select = $@"
SELECT TOP (2000) p.{names.Col(c.PersonFactsId)}, p.{names.Col(c.FileId)}, p.{names.Col(c.Surname)}, p.{names.Col(c.Name)}, p.{names.Col(c.Patronymic)},
       p.{names.Col(c.BirthDate)}, p.{names.Col(c.BirthPlace)}, p.{names.Col(c.All)}, f.{names.Col(c.FileName)}, f.{names.Col(c.FileCode)}
FROM {names.PF} AS p
INNER JOIN {names.SF} AS f ON p.{names.Col(c.FileId)} = f.{names.Col(c.SourceFilesId)}
WHERE p.{names.Col(c.PersonFactsId)} > @last
ORDER BY p.{names.Col(c.PersonFactsId)};";
            using (var command = factory.Command(connection, select))
            {
                command.Parameters.Add("@last", SqlDbType.BigInt).Value = lastId;
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = Convert.ToInt64(reader.GetValue(0));
                    var fileId = Convert.ToInt64(reader.GetValue(1));
                    string Str(int i) => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i));
                    DateTime? birth = reader.IsDBNull(5) ? (DateTime?)null : Convert.ToDateTime(reader.GetValue(5));
                    var doc = SearchDocumentBuilder.FromStored(Str(2), Str(3), Str(4), birth, Str(6), Str(7), SearchDocumentBuilder.FileText(Str(8), Str(9)));
                    rows.Add((id, fileId, doc));
                }
            }

            if (rows.Count == 0)
            {
                break;
            }

            using (var transaction = connection.BeginTransaction())
            {
                using (var delete = factory.Command(connection, $@"
DELETE FROM {names.Aux("FaktSearchDocs")} WHERE [PersonFactId] BETWEEN @from AND @to;
DELETE FROM {names.Aux("FaktFactValues")} WHERE [PersonFactId] BETWEEN @from AND @to;", transaction))
                {
                    delete.Parameters.Add("@from", SqlDbType.BigInt).Value = rows[0].Id;
                    delete.Parameters.Add("@to", SqlDbType.BigInt).Value = rows[rows.Count - 1].Id;
                    await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var docs = SqlFactWriter.DocsTable();
                var values = SqlFactWriter.ValuesTable();
                foreach (var row in rows)
                {
                    docs.Rows.Add(row.Id, row.FileId, row.Doc.AllText, (object)row.Doc.NameText ?? DBNull.Value, (object)row.Doc.BirthPlaceText ?? DBNull.Value,
                        (object)row.Doc.FactsText ?? DBNull.Value, (object)row.Doc.FileText ?? DBNull.Value, DateTime.UtcNow);
                    foreach (var value in row.Doc.Values.Distinct())
                    {
                        values.Rows.Add(value.Key, value.Value, row.Id);
                    }
                }

                await SqlFactWriter.BulkCopyAsync(connection, transaction, names.Aux("FaktSearchDocs"), docs, cancellationToken).ConfigureAwait(false);
                await SqlFactWriter.BulkCopyAsync(connection, transaction, names.Aux("FaktFactValues"), values, cancellationToken).ConfigureAwait(false);
                transaction.Commit();
            }

            total += rows.Count;
            lastId = rows[rows.Count - 1].Id;
            progress?.Report(total);
        }

        _logger.Info("db.projection_rebuilt", "Поисковая проекция перестроена", e => e.Count = total);
        return total;
    }
}
