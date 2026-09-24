using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fakt.Core.Storage;

/// <summary>
/// Код файла T_… — значение уникального кода источника (столбец FileCode), а не имя столбца или таблицы.
/// 18 случайных цифр из криптографического генератора; уникальность гарантирует ограничение UNIQUE,
/// при коллизии код генерируется повторно.
/// </summary>
public static class FileCodeGenerator
{
    public const int DigitCount = 18;
    public const string Prefix = "T_";

    private static readonly Regex Pattern = new(@"^T_\d{" + DigitCount + "}$", RegexOptions.CultureInvariant);

    public static string Next()
    {
        var builder = new StringBuilder(Prefix.Length + DigitCount);
        builder.Append(Prefix);
        var buffer = new byte[1];
        using var rng = RandomNumberGenerator.Create();
        while (builder.Length < Prefix.Length + DigitCount)
        {
            rng.GetBytes(buffer);
            // Отбрасываем 250..255, чтобы распределение цифр было равномерным.
            if (buffer[0] < 250)
            {
                builder.Append((char)('0' + buffer[0] % 10));
            }
        }

        return builder.ToString();
    }

    public static bool IsValid(string code) => code != null && Pattern.IsMatch(code);
}
