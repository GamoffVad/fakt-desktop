using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Fakt.Core.Extraction;

/// <summary>
/// Нормализация значений фактов. Правила задания: не добавлять код страны к телефону, счета/телефоны/номера
/// документов — строки с ведущими нулями. Нормализованное значение модели не принимается на веру: приложение
/// вычисляет его само из исходного значения.
/// </summary>
public static class FactNormalizer
{
    public const int MaxSearchValueLength = 400;

    private static readonly Regex EmailPattern = new(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.CultureInvariant);

    // Буквы, допустимые в российских госномерах: кириллица и латинские двойники приводятся к латинице.
    private const string PlateCyrillic = "АВЕКМНОРСТУХ";
    private const string PlateLatin = "ABEKMHOPCTYX";

    /// <summary>Нормализованное значение для хранения в [ALL] (для телефона — с «+», если он был в источнике).</summary>
    public static string NormalizeForStorage(string type, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        switch (type)
        {
            case FactTypes.Phone:
            {
                var digits = DigitsOnly(trimmed);
                if (digits.Length < 5)
                {
                    return null;
                }

                return trimmed.StartsWith("+", StringComparison.Ordinal) ? "+" + digits : digits;
            }

            case FactTypes.Email:
            {
                var lower = trimmed.ToLowerInvariant();
                return EmailPattern.IsMatch(lower) ? lower : null;
            }

            case FactTypes.BankAccount:
            case FactTypes.BankCard:
            case FactTypes.Inn:
            case FactTypes.Snils:
            {
                var digits = DigitsOnly(trimmed);
                return digits.Length == 0 ? null : digits;
            }

            case FactTypes.Vin:
            case FactTypes.Document:
            {
                var alnum = AlnumUpper(trimmed);
                return alnum.Length == 0 ? null : alnum;
            }

            case FactTypes.LicensePlate:
            {
                var plate = NormalizePlate(trimmed);
                return plate.Length == 0 ? null : plate;
            }

            case FactTypes.Website:
            case FactTypes.SocialAccount:
                return trimmed.ToLowerInvariant();

            default:
                return null;
        }
    }

    /// <summary>
    /// Ключ индекса поиска (FactSearchValues.NormalizedValue). Для телефона — только цифры (без «+»),
    /// чтобы «+7 000 123-45-67» и «7(000)1234567» совпадали; «8…» остаётся другим номером.
    /// </summary>
    public static string SearchKey(string type, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string key;
        switch (type)
        {
            case FactTypes.Phone:
                key = DigitsOnly(value);
                if (key.Length < 5)
                {
                    return null;
                }

                break;
            case FactTypes.Email:
            case FactTypes.Website:
            case FactTypes.SocialAccount:
                key = value.Trim().ToLowerInvariant();
                break;
            case FactTypes.BankAccount:
            case FactTypes.BankCard:
            case FactTypes.Inn:
            case FactTypes.Snils:
                key = DigitsOnly(value);
                break;
            case FactTypes.Vin:
            case FactTypes.Document:
                key = AlnumUpper(value);
                break;
            case FactTypes.LicensePlate:
                key = NormalizePlate(value);
                break;
            default:
                key = CollapseWhitespace(value).ToLowerInvariant().Replace('ё', 'е');
                break;
        }

        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return key.Length > MaxSearchValueLength ? null : key;
    }

    /// <summary>
    /// Ключ для поиска по «иному идентификатору» без указания типа: цифры, если во вводе есть хотя бы 5 цифр
    /// и нет букв; иначе буквенно-цифровая форма в верхнем регистре.
    /// </summary>
    public static string SearchKeyForAnyIdentifier(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = DigitsOnly(input);
        var hasLetters = input.Any(char.IsLetter);
        if (!hasLetters && digits.Length >= 5)
        {
            return digits;
        }

        if (input.Contains("@"))
        {
            return input.Trim().ToLowerInvariant();
        }

        return AlnumUpper(input);
    }

    public static string DigitsOnly(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch >= '0' && ch <= '9')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    public static string AlnumUpper(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return builder.ToString();
    }

    public static string NormalizePlate(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var raw in value)
        {
            var ch = char.ToUpperInvariant(raw);
            if (!char.IsLetterOrDigit(ch))
            {
                continue;
            }

            var index = PlateCyrillic.IndexOf(ch);
            builder.Append(index >= 0 ? PlateLatin[index] : ch);
        }

        return builder.ToString();
    }

    public static string CollapseWhitespace(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }

    public static string ToInvariantString(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
