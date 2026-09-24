using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Fakt.Core.Structure;

namespace Fakt.Testing;

/// <summary>
/// Небольшие синтетические CSV для тестов конвейера — та же схема, что у большого файла генератора
/// tools/testdata/generate_testdata.py --large (Номер;ФИО;Дата рождения;Место рождения;Телефон;Email;Номер счёта),
/// UTF-8 с BOM, CRLF. Все значения вымышлены: фамилии от слов «тест/пример/образец», телефоны с кодом 000,
/// e-mail на example.*; генерация детерминирована по seed.
/// </summary>
public static class SyntheticPeople
{
    public static readonly IReadOnlyList<string> Columns = new[] { "Номер", "ФИО", "Дата рождения", "Место рождения", "Телефон", "Email", "Номер счёта" };

    private static readonly string[] Stems =
    {
        "Тестов", "Примеров", "Образцов", "Шаблонов", "Макетов", "Эталонов", "Черновиков", "Выборкин", "Проверкин", "Условнов",
        "Модельнов", "Опытов", "Пробников", "Синтетиков", "Архивов", "Реестров", "Строкин", "Столбцов",
    };

    private static readonly string[] Male = { "Иван", "Пётр", "Сергей", "Алексей", "Дмитрий", "Андрей", "Михаил", "Павел", "Олег", "Глеб" };
    private static readonly string[] Female = { "Анна", "Мария", "Елена", "Ольга", "Наталья", "Ирина", "Ксения", "Дарья", "Вера", "Юлия" };

    private static readonly (string Male, string Female)[] Patronymics =
    {
        ("Иванович", "Ивановна"), ("Петрович", "Петровна"), ("Сергеевич", "Сергеевна"), ("Андреевич", "Андреевна"), ("Олегович", "Олеговна"),
    };

    private static readonly string[] Cities = { "г. Тестовск", "г. Примерск", "г. Образцовск", "пос. Шаблоново", "с. Макетовка", "г. Эталонск", "с. Опытное" };
    private static readonly string[] Domains = { "example.com", "example.org", "example.net" };

    /// <summary>Структура файла для ручной проверки парсером (как задал бы пользователь).</summary>
    public static StructureDescriptor CsvStructure() => new()
    {
        Classification = StructureDescriptor.ClassStructured,
        Format = StructureDescriptor.FormatDelimited,
        Encoding = "utf-8-sig",
        HasHeader = true,
        HeaderRow = 0,
        SkipRows = 0,
        Delimiter = ";",
        QuoteChar = "\"",
        Columns = new List<string>(Columns),
    };

    public static void WriteCsv(string path, int count, int seed = 20260924)
    {
        var random = new Random(seed);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true), 1 << 16) { NewLine = "\r\n" };
        writer.WriteLine(string.Join(";", Columns));
        for (var i = 1; i <= count; i++)
        {
            var female = random.Next(2) == 1;
            var stem = Stems[random.Next(Stems.Length)];
            var surname = female ? stem + "а" : stem;
            var name = female ? Female[random.Next(Female.Length)] : Male[random.Next(Male.Length)];
            var pair = Patronymics[random.Next(Patronymics.Length)];
            var patronymic = female ? pair.Female : pair.Male;
            var birth = new DateTime(1950, 1, 1).AddDays(random.Next(20000));
            var city = Cities[random.Next(Cities.Length)];
            var phone = string.Format(CultureInfo.InvariantCulture, "+7 000 {0:000}-{1:00}-{2:00}", random.Next(1000), random.Next(100), random.Next(100));
            var email = string.Format(CultureInfo.InvariantCulture, "user{0}.{1}@{2}", i, random.Next(100), Domains[random.Next(Domains.Length)]);
            var account = "40817810" + "0000" + random.Next(100000000).ToString("00000000", CultureInfo.InvariantCulture);
            writer.WriteLine(string.Join(";",
                i.ToString("000000000", CultureInfo.InvariantCulture), surname + " " + name + " " + patronymic,
                birth.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), city, phone, email, account));
        }
    }
}
