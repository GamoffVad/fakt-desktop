using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fakt.Core.Settings;

namespace Fakt.Infrastructure.Sql;

/// <summary>
/// Имена объектов базы из настроек. Идентификаторы экранируются только квадратными скобками
/// (с удвоением «]»), в строковых литералах — с удвоением апострофа. Пользовательские имена
/// никогда не подставляются в SQL без экранирования.
/// </summary>
public sealed class SqlNames
{
    public const int MaxIdentifierLength = 128;

    public SqlNames(DatabaseSettings settings)
    {
        Schema = Require(settings.Schema, "Схема");
        AuxSchema = Require(settings.EffectiveAuxiliarySchema, "Схема вспомогательных таблиц");
        SourceFilesTable = Require(settings.SourceFilesTable, "Таблица источников");
        PersonFactsTable = Require(settings.PersonFactsTable, "Таблица наблюдений");
        Columns = settings.Columns ?? new ColumnMap();
        foreach (var column in Columns.SourceFilesColumns().Concat(Columns.PersonFactsColumns()))
        {
            Require(column.Value, "Столбец " + column.Key);
        }
    }

    public string Schema { get; }
    public string AuxSchema { get; }
    public string SourceFilesTable { get; }
    public string PersonFactsTable { get; }
    public ColumnMap Columns { get; }

    public string SF => Quote(Schema) + "." + Quote(SourceFilesTable);
    public string PF => Quote(Schema) + "." + Quote(PersonFactsTable);

    public string Aux(string table) => Quote(AuxSchema) + "." + Quote(table);

    public string Col(string physicalName) => Quote(physicalName);

    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public static string Literal(string text) => "N'" + (text ?? string.Empty).Replace("'", "''") + "'";

    private static string Require(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{what}: имя не задано.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxIdentifierLength || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException($"{what}: недопустимое имя «{trimmed}».");
        }

        return trimmed;
    }

    public string ColumnOf(string logicalKey)
    {
        var map = Columns.SourceFilesColumns().Concat(Columns.PersonFactsColumns()).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (!map.TryGetValue(logicalKey, out var name))
        {
            throw new ArgumentException("Неизвестное логическое поле " + logicalKey);
        }

        return name;
    }

    private static readonly Regex TokenPattern = new(@"\{\{([A-Za-z_]+)(?::([A-Za-z_]+))?\}\}", RegexOptions.CultureInvariant);

    /// <summary>Подстановка имён в шаблон миграции.</summary>
    public string Render(string template)
    {
        return TokenPattern.Replace(template, match =>
        {
            var token = match.Groups[1].Value;
            var argument = match.Groups[2].Success ? match.Groups[2].Value : null;
            switch (token)
            {
                case "S": return Quote(Schema);
                case "A": return Quote(AuxSchema);
                case "S_LIT": return Literal(Schema);
                case "A_LIT": return Literal(AuxSchema);
                case "S_QUOTED_LIT": return Literal(Quote(Schema));
                case "A_QUOTED_LIT": return Literal(Quote(AuxSchema));
                case "SF": return SF;
                case "PF": return PF;
                case "SF_LIT": return Literal(SF);
                case "PF_LIT": return Literal(PF);
                case "SF_NAME": return SourceFilesTable.Replace("]", "]]");
                case "PF_NAME": return PersonFactsTable.Replace("]", "]]");
                case "SF_NAME_LIT": return SourceFilesTable.Replace("'", "''");
                case "PF_NAME_LIT": return PersonFactsTable.Replace("'", "''");
                case "c": return Quote(ColumnOf(Require(argument, "Токен c")));
                case "c_lit": return Literal(ColumnOf(Require(argument, "Токен c_lit")));
                case "AUX": return Aux(Require(argument, "Токен AUX"));
                case "AUX_LIT": return Literal(Aux(Require(argument, "Токен AUX_LIT")));
                default: throw new InvalidOperationException("Неизвестный токен шаблона: " + match.Value);
            }
        });
    }

    /// <summary>Разбиение скрипта на пакеты по строкам «GO».</summary>
    public static IReadOnlyList<string> SplitBatches(string script)
    {
        var batches = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var line in script.Replace("\r\n", "\n").Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*GO\s*(--.*)?$", RegexOptions.IgnoreCase))
            {
                if (current.ToString().Trim().Length > 0)
                {
                    batches.Add(current.ToString());
                }

                current.Clear();
                continue;
            }

            current.Append(line).Append('\n');
        }

        if (current.ToString().Trim().Length > 0)
        {
            batches.Add(current.ToString());
        }

        return batches;
    }
}
