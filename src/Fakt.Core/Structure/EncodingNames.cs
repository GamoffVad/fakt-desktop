using System;
using System.Collections.Generic;

namespace Fakt.Core.Structure;

/// <summary>Канонические имена кодировок, общие для клиента и worker (протокол worker, раздел 4).</summary>
public static class EncodingNames
{
    public static readonly IReadOnlyList<string> Supported = new[]
    {
        "utf-8", "utf-8-sig", "utf-16-le", "utf-16-be", "utf-32-le", "utf-32-be",
        "windows-1251", "cp866", "koi8-r", "windows-1252", "iso-8859-1", "ascii",
    };

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["utf-8"] = "utf-8", ["utf8"] = "utf-8", ["utf_8"] = "utf-8",
        ["utf-8-sig"] = "utf-8-sig", ["utf8-sig"] = "utf-8-sig", ["utf-8 bom"] = "utf-8-sig", ["utf-8-bom"] = "utf-8-sig",
        ["utf-16"] = "utf-16-le", ["utf16"] = "utf-16-le", ["utf-16-le"] = "utf-16-le", ["utf-16le"] = "utf-16-le", ["unicode"] = "utf-16-le",
        ["utf-16-be"] = "utf-16-be", ["utf-16be"] = "utf-16-be",
        ["utf-32"] = "utf-32-le", ["utf-32-le"] = "utf-32-le", ["utf-32le"] = "utf-32-le",
        ["utf-32-be"] = "utf-32-be", ["utf-32be"] = "utf-32-be",
        ["windows-1251"] = "windows-1251", ["cp1251"] = "windows-1251", ["1251"] = "windows-1251", ["win-1251"] = "windows-1251",
        ["win1251"] = "windows-1251", ["cp-1251"] = "windows-1251",
        ["cp866"] = "cp866", ["ibm866"] = "cp866", ["866"] = "cp866", ["dos-866"] = "cp866",
        ["koi8-r"] = "koi8-r", ["koi8r"] = "koi8-r", ["koi8_r"] = "koi8-r",
        ["windows-1252"] = "windows-1252", ["cp1252"] = "windows-1252", ["1252"] = "windows-1252",
        ["iso-8859-1"] = "iso-8859-1", ["latin-1"] = "iso-8859-1", ["latin1"] = "iso-8859-1", ["iso8859-1"] = "iso-8859-1",
        ["ascii"] = "ascii", ["us-ascii"] = "ascii",
    };

    /// <summary>Возвращает каноническое имя или null, если кодировка не поддерживается.</summary>
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return Synonyms.TryGetValue(name.Trim(), out var canonical) ? canonical : null;
    }

    public static string DisplayName(string canonical)
    {
        switch (canonical)
        {
            case "utf-8": return "UTF-8";
            case "utf-8-sig": return "UTF-8 (BOM)";
            case "utf-16-le": return "UTF-16 LE";
            case "utf-16-be": return "UTF-16 BE";
            case "utf-32-le": return "UTF-32 LE";
            case "utf-32-be": return "UTF-32 BE";
            case "windows-1251": return "Windows-1251";
            case "cp866": return "CP866 (DOS)";
            case "koi8-r": return "KOI8-R";
            case "windows-1252": return "Windows-1252";
            case "iso-8859-1": return "ISO-8859-1";
            case "ascii": return "ASCII";
            default: return canonical ?? "—";
        }
    }
}
