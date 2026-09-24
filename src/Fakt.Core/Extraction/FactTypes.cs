using System;
using System.Collections.Generic;
using System.Linq;

namespace Fakt.Core.Extraction;

/// <summary>Типы дополнительных фактов. Список расширяемый: неизвестный тип сохраняется как other с меткой.</summary>
public static class FactTypes
{
    public const string Phone = "phone";
    public const string Email = "email";
    public const string Address = "address";
    public const string Workplace = "workplace";
    public const string Position = "position";
    public const string Vehicle = "vehicle";
    public const string LicensePlate = "license_plate";
    public const string Vin = "vin";
    public const string BankAccount = "bank_account";
    public const string BankCard = "bank_card";
    public const string Document = "document";
    public const string Inn = "inn";
    public const string Snils = "snils";
    public const string Website = "website";
    public const string SocialAccount = "social_account";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Phone, Email, Address, Workplace, Position, Vehicle, LicensePlate, Vin, BankAccount, BankCard,
        Document, Inn, Snils, Website, SocialAccount, Other,
    };

    private static readonly Dictionary<string, string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        [Phone] = "Телефон",
        [Email] = "Электронная почта",
        [Address] = "Адрес",
        [Workplace] = "Место работы",
        [Position] = "Должность",
        [Vehicle] = "Автомобиль",
        [LicensePlate] = "Госномер",
        [Vin] = "VIN",
        [BankAccount] = "Банковский счёт",
        [BankCard] = "Банковская карта",
        [Document] = "Документ",
        [Inn] = "ИНН",
        [Snils] = "СНИЛС",
        [Website] = "Сайт",
        [SocialAccount] = "Аккаунт в соцсети",
        [Other] = "Прочее",
    };

    public static bool IsKnown(string type) => type != null && Titles.ContainsKey(type);

    public static string Title(string type) => type != null && Titles.TryGetValue(type, out var title) ? title : type ?? "—";

    /// <summary>Типы, для которых ведётся индекс точного и префиксного поиска по нормализованному значению.</summary>
    public static bool IsIdentifier(string type) =>
        type == Phone || type == Email || type == LicensePlate || type == Vin || type == BankAccount ||
        type == BankCard || type == Document || type == Inn || type == Snils || type == Website || type == SocialAccount;

    public static IEnumerable<KeyValuePair<string, string>> Choices() => All.Select(t => new KeyValuePair<string, string>(t, Title(t)));
}
