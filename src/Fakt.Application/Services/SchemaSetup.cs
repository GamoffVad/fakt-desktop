using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Core.Storage;

namespace Fakt.Application.Services;

/// <summary>
/// Что делается при подключении к существующей (в том числе рабочей) базе. Создаётся всё, чего не хватает FAKT:
/// основные таблицы, если их нет, служебные таблицы, поисковая проекция, полнотекстовый индекс (если установлен
/// Full-Text Search). В уже существующие основные таблицы добавляется только то, без чего не работают обработка и
/// поиск (недостающие технические столбцы с NULL и индексы). Необязательные изменения существующих таблиц
/// (ограничение ISJSON, индексы фильтров) остаются на усмотрение администратора. Все миграции только добавляют
/// объекты: таблицы и данные не удаляются и не пересоздаются.
/// </summary>
public static class SchemaSetupPlan
{
    public static IReadOnlyList<MigrationInfo> AutoApply(SchemaReport report)
    {
        if (report == null || !report.Connected)
        {
            return Array.Empty<MigrationInfo>();
        }

        var mainTablesExist = report.SourceFiles?.Exists == true || report.PersonFacts?.Exists == true;
        return report.Migrations
            .Where(m => !m.Applied)
            .Where(m => !m.AltersUserTables || !mainTablesExist || m.RequiredForProcessing || m.RequiredForSearch)
            .Where(m => !(m.RequiresNoTransaction && report.FullText != null && !report.FullText.Installed && IsFullText(m)))
            .ToList();
    }

    /// <summary>Необязательные изменения существующих основных таблиц, которые не применяются автоматически.</summary>
    public static IReadOnlyList<MigrationInfo> LeftForAdministrator(SchemaReport report)
    {
        if (report == null || !report.Connected)
        {
            return Array.Empty<MigrationInfo>();
        }

        var auto = new HashSet<string>(AutoApply(report).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        return report.Migrations.Where(m => !m.Applied && !auto.Contains(m.Id)).ToList();
    }

    /// <summary>Текст подтверждения: что будет сделано в базе.</summary>
    public static string Describe(SchemaReport report, IReadOnlyList<MigrationInfo> plan)
    {
        var lines = new List<string>();
        var mainTablesExist = report.SourceFiles?.Exists == true || report.PersonFacts?.Exists == true;
        lines.Add(mainTablesExist
            ? "Основные таблицы уже есть — они сохраняются вместе с данными; в них будут добавлены только недостающие служебные столбцы (допускают NULL) и индексы."
            : "Будут созданы основные таблицы SourceFiles и PersonFacts.");
        lines.Add("Будет выполнено:");
        // Создание основных таблиц (V001) при уже существующих таблицах ничего не делает — в списке не показывается.
        lines.AddRange(plan.Where(m => !(mainTablesExist && string.Equals(m.Id, "V001", StringComparison.OrdinalIgnoreCase))).Select(m => "• " + m.Title));
        lines.Add("Таблицы и данные не удаляются и не пересоздаются.");
        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsFullText(MigrationInfo migration) =>
        (migration.Script ?? string.Empty).IndexOf("FULLTEXT", StringComparison.OrdinalIgnoreCase) >= 0;
}

public sealed class SchemaSetupResult
{
    public List<string> Applied { get; } = new();

    public bool Success { get; set; } = true;

    public string Message { get; set; }
}
