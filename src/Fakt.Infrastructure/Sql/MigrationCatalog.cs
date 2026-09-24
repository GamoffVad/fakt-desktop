using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Fakt.Infrastructure.Sql;

public sealed class MigrationScript
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public bool AltersUserTables { get; set; }
    public bool RequiredForProcessing { get; set; }
    public bool RequiredForSearch { get; set; }
    public bool UseTransaction { get; set; } = true;
    public string Template { get; set; }
}

/// <summary>Миграции из каталога database/migrations, встроенные в сборку. Метаданные — в заголовке «-- fakt:ключ=значение».</summary>
public static class MigrationCatalog
{
    private static readonly Lazy<IReadOnlyList<MigrationScript>> Scripts = new(Load);

    public static IReadOnlyList<MigrationScript> All => Scripts.Value;

    public static MigrationScript Find(string id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) ??
        throw new ArgumentException("Неизвестная миграция: " + id);

    private static IReadOnlyList<MigrationScript> Load()
    {
        var assembly = typeof(MigrationCatalog).Assembly;
        var result = new List<MigrationScript>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Fakt.Migrations.", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            result.Add(Parse(reader.ReadToEnd()));
        }

        return result;
    }

    public static MigrationScript Parse(string text)
    {
        var script = new MigrationScript { Template = text };
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.StartsWith("-- fakt:", StringComparison.Ordinal))
            {
                continue;
            }

            var body = line.Substring("-- fakt:".Length);
            var eq = body.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = body.Substring(0, eq).Trim();
            var value = body.Substring(eq + 1).Trim();
            switch (key)
            {
                case "id": script.Id = value; break;
                case "title": script.Title = value; break;
                case "description": script.Description = value; break;
                case "alters-user-tables": script.AltersUserTables = value == "true"; break;
                case "transaction": script.UseTransaction = value != "false"; break;
                case "required":
                    var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
                    script.RequiredForProcessing = parts.Contains("processing");
                    script.RequiredForSearch = parts.Contains("search");
                    break;
            }
        }

        if (string.IsNullOrEmpty(script.Id))
        {
            throw new InvalidDataException("Миграция без идентификатора fakt:id.");
        }

        return script;
    }
}
