using Fakt.Core.Extraction;
using Xunit;

namespace Fakt.UnitTests.Core;

public sealed class FactNormalizerTests
{
    [Theory]
    [InlineData("+7 (900) 123-45-67", "+79001234567")]
    [InlineData("8 (900) 123-45-67", "89001234567")]
    [InlineData("900 123-45-67", "9001234567")]
    [InlineData("  +7 900 1234567 ", "+79001234567")]
    [InlineData("+44 20 7946 0000", "+442079460000")]
    [InlineData("123-45", "12345")]
    public void Phone_Storage_KeepsDigitsAndSourcePlus(string raw, string expected)
    {
        Assert.Equal(expected, FactNormalizer.NormalizeForStorage(FactTypes.Phone, raw));
    }

    [Theory]
    [InlineData("9001234567")]
    [InlineData("(900) 123-45-67")]
    [InlineData("900.123.45.67")]
    public void Phone_NeverGetsACountryCodeAdded(string raw)
    {
        Assert.Equal("9001234567", FactNormalizer.NormalizeForStorage(FactTypes.Phone, raw));
        Assert.Equal("9001234567", FactNormalizer.SearchKey(FactTypes.Phone, raw));
        Assert.Equal("9001234567", FactNormalizer.SearchKeyForAnyIdentifier(raw));
    }

    [Theory]
    [InlineData("12-34")]
    [InlineData("доб. 12")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Phone_WithFewerThanFiveDigits_IsNotNormalized(string raw)
    {
        Assert.Null(FactNormalizer.NormalizeForStorage(FactTypes.Phone, raw));
        Assert.Null(FactNormalizer.SearchKey(FactTypes.Phone, raw));
    }

    [Fact]
    public void Phone_SearchKey_DropsPlusButKeepsTrunkPrefix8()
    {
        var plusSeven = FactNormalizer.SearchKey(FactTypes.Phone, "+7 000 123-45-67");
        var bareSeven = FactNormalizer.SearchKey(FactTypes.Phone, "7(000)1234567");
        var eight = FactNormalizer.SearchKey(FactTypes.Phone, "8 000 123-45-67");

        Assert.Equal("70001234567", plusSeven);
        Assert.Equal(plusSeven, bareSeven);
        // «8…» — другой номер: код страны не подставляется и не заменяется.
        Assert.Equal("80001234567", eight);
        Assert.NotEqual(plusSeven, eight);
        // В хранимом значении «+» из источника сохраняется.
        Assert.Equal("+70001234567", FactNormalizer.NormalizeForStorage(FactTypes.Phone, "+7 000 123-45-67"));
    }

    [Theory]
    [InlineData(" Ivan.Petrov@Example.TEST ", "ivan.petrov@example.test")]
    [InlineData("a.b+tag@mail.example.test", "a.b+tag@mail.example.test")]
    public void Email_IsTrimmedAndLowercased(string raw, string expected)
    {
        Assert.Equal(expected, FactNormalizer.NormalizeForStorage(FactTypes.Email, raw));
        Assert.Equal(expected, FactNormalizer.SearchKey(FactTypes.Email, raw));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("user@localhost")]
    [InlineData("two words@example.test")]
    public void Email_Invalid_HasNoStorageValue(string raw)
    {
        Assert.Null(FactNormalizer.NormalizeForStorage(FactTypes.Email, raw));
    }

    [Theory]
    [InlineData(FactTypes.BankAccount, "0000 1234 5678 9012 3456", "00001234567890123456")]
    [InlineData(FactTypes.BankAccount, "40817-810-0-9999-1000431", "40817810099991000431")]
    [InlineData(FactTypes.BankCard, "0012 3456 7890 1234", "0012345678901234")]
    [InlineData(FactTypes.Inn, "007712345678", "007712345678")]
    [InlineData(FactTypes.Snils, "012-345-678 90", "01234567890")]
    public void NumericIdentifiers_AreDigitStringsWithLeadingZeros(string type, string raw, string expected)
    {
        Assert.Equal(expected, FactNormalizer.NormalizeForStorage(type, raw));
        Assert.Equal(expected, FactNormalizer.SearchKey(type, raw));
    }

    [Theory]
    [InlineData(FactTypes.BankAccount)]
    [InlineData(FactTypes.Inn)]
    public void NumericIdentifier_WithoutDigits_IsNotNormalized(string type)
    {
        Assert.Null(FactNormalizer.NormalizeForStorage(type, "н/д"));
        Assert.Null(FactNormalizer.SearchKey(type, "н/д"));
    }

    [Theory]
    [InlineData("а123вс77", "A123BC77")]
    [InlineData("А 123 ВС 777", "A123BC777")]
    [InlineData("A123BC77", "A123BC77")]
    [InlineData("a123bc 77", "A123BC77")]
    [InlineData("ЕКХ 001 199", "EKX001199")]
    public void LicensePlate_CyrillicLookalikeLettersBecomeLatin(string raw, string expected)
    {
        Assert.Equal(expected, FactNormalizer.NormalizeForStorage(FactTypes.LicensePlate, raw));
        Assert.Equal(expected, FactNormalizer.SearchKey(FactTypes.LicensePlate, raw));
    }

    [Fact]
    public void LicensePlate_CyrillicAndLatinSpellingsShareOneSearchKey()
    {
        Assert.Equal(
            FactNormalizer.SearchKey(FactTypes.LicensePlate, "O777OO99"),
            FactNormalizer.SearchKey(FactTypes.LicensePlate, "о777оо 99"));
    }

    [Fact]
    public void LicensePlate_CyrillicLettersWithoutLatinTwin_ArePreserved()
    {
        Assert.Equal("Д123ЖЗ77", FactNormalizer.NormalizeForStorage(FactTypes.LicensePlate, "д123жз77"));
    }

    [Theory]
    [InlineData(FactTypes.Vin, "wvw zzz 1jz xw 000001", "WVWZZZ1JZXW000001")]
    [InlineData(FactTypes.Document, "45 06 № 012345", "4506012345")]
    [InlineData(FactTypes.Document, "паспорт 45 06", "ПАСПОРТ4506")]
    public void CodeLikeValues_AreUppercaseAlphanumeric(string type, string raw, string expected)
    {
        Assert.Equal(expected, FactNormalizer.NormalizeForStorage(type, raw));
        Assert.Equal(expected, FactNormalizer.SearchKey(type, raw));
    }

    [Theory]
    [InlineData(FactTypes.Website)]
    [InlineData(FactTypes.SocialAccount)]
    public void WebsiteAndSocial_AreTrimmedAndLowercased(string type)
    {
        Assert.Equal("https://example.test/page", FactNormalizer.NormalizeForStorage(type, " HTTPS://Example.TEST/Page "));
    }

    [Theory]
    [InlineData(FactTypes.Address)]
    [InlineData(FactTypes.Workplace)]
    [InlineData(FactTypes.Position)]
    [InlineData(FactTypes.Other)]
    [InlineData("unknown_type")]
    public void FreeText_HasNoStorageNormalization(string type)
    {
        Assert.Null(FactNormalizer.NormalizeForStorage(type, "г. Тестовск, ул. Примерная, 1"));
    }

    [Fact]
    public void FreeText_SearchKey_CollapsesWhitespaceLowercasesAndFoldsYo()
    {
        Assert.Equal("г. тестовск, ул. теплая", FactNormalizer.SearchKey(FactTypes.Address, "  Г. Тестовск,   ул.\tТЁплая  "));
    }

    [Fact]
    public void SearchKey_LongerThanLimit_IsNotIndexed()
    {
        Assert.NotNull(FactNormalizer.SearchKey(FactTypes.Address, new string('а', FactNormalizer.MaxSearchValueLength)));
        Assert.Null(FactNormalizer.SearchKey(FactTypes.Address, new string('а', FactNormalizer.MaxSearchValueLength + 1)));
    }

    [Theory]
    [InlineData("+7 (000) 123-45-67", "70001234567")]
    [InlineData("8 000 123 45 67", "80001234567")]
    [InlineData("0012 3456 7890", "001234567890")]
    [InlineData(" Ivan.Petrov@Example.TEST ", "ivan.petrov@example.test")]
    [InlineData("A123BC 77", "A123BC77")]
    [InlineData("wvw-zzz-1jz", "WVWZZZ1JZ")]
    [InlineData("12-34", "1234")]
    public void SearchKeyForAnyIdentifier_UsesDigitsForNumbersOtherwiseAlphanumeric(string input, string expected)
    {
        Assert.Equal(expected, FactNormalizer.SearchKeyForAnyIdentifier(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SearchKeyForAnyIdentifier_Empty_IsNull(string input)
    {
        Assert.Null(FactNormalizer.SearchKeyForAnyIdentifier(input));
    }

    [Fact]
    public void AnyIdentifierKey_MatchesTypedKeyForPhonesAndAccounts()
    {
        // Поиск без указания типа находит те же значения, что индексируются для телефона и счёта.
        Assert.Equal(FactNormalizer.SearchKey(FactTypes.Phone, "+7 000 123-45-67"), FactNormalizer.SearchKeyForAnyIdentifier("+7 000 123-45-67"));
        Assert.Equal(FactNormalizer.SearchKey(FactTypes.BankAccount, "0000 1234 5678"), FactNormalizer.SearchKeyForAnyIdentifier("0000 1234 5678"));
    }
}
