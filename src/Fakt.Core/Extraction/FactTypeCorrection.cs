using System;
using System.Text.RegularExpressions;

namespace Fakt.Core.Extraction;

/// <summary>
/// Проверка типа факта по формату значения (после ответа модели). Исправляются только однозначные случаи —
/// например, 12-значный номер, отмеченный как СНИЛС (в СНИЛС 11 цифр), или госномер, отмеченный как сведения
/// об автомобиле. Значение не изменяется; каждое исправление сопровождается предупреждением в наблюдении.
/// Особенно важно для файлов без заголовка, где модель определяет смысл колонок только по значениям.
/// </summary>
public static class FactTypeCorrection
{
    private const string PlateLetters = "АВЕКМНОРСТУХABEKMHOPCTYX";

    private static readonly Regex SnilsPattern = new(@"^\d{3}-\d{3}-\d{3}[ -]\d{2}$", RegexOptions.CultureInvariant);

    private static readonly Regex PlatePattern = new(
        "^[" + PlateLetters + "]\\s?\\d{3}\\s?[" + PlateLetters + "]{2}\\s?\\d{2,3}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex VinPattern = new(@"^(?=.*\d)(?=.*[A-HJ-NPR-Z])[A-HJ-NPR-Z0-9]{17}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Уточнённый тип или исходный, если формат не противоречит ему однозначно.</summary>
    public static string Correct(string type, string value, string sourceColumn)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return type;
        }

        var trimmed = value.Trim();
        var digits = FactNormalizer.DigitsOnly(trimmed);
        var column = sourceColumn ?? string.Empty;

        if (type == FactTypes.Snils && !SnilsPattern.IsMatch(trimmed) && digits.Length == trimmed.Length && (digits.Length == 10 || digits.Length == 12) &&
            !Mentions(column, "снилс", "snils"))
        {
            return FactTypes.Inn; // в СНИЛС 11 цифр; 10 или 12 цифр — ИНН организации или физического лица
        }

        if (type == FactTypes.Inn && SnilsPattern.IsMatch(trimmed) && !Mentions(column, "инн", "inn"))
        {
            return FactTypes.Snils;
        }

        if ((type == FactTypes.Vehicle || type == FactTypes.Other || type == FactTypes.Document) && PlatePattern.IsMatch(trimmed))
        {
            return FactTypes.LicensePlate;
        }

        if ((type == FactTypes.Vehicle || type == FactTypes.Other || type == FactTypes.LicensePlate || type == FactTypes.Document) && VinPattern.IsMatch(trimmed))
        {
            return FactTypes.Vin;
        }

        return type;
    }

    private static bool Mentions(string column, params string[] words)
    {
        foreach (var word in words)
        {
            if (column.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
