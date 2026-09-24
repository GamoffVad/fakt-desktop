using Fakt.Core.Logging;
using Xunit;

namespace Fakt.UnitTests.Core;

public sealed class LogSanitizerTests
{
    [Theory]
    [InlineData("Ключ sk-proj-AbCdEf1234567890XYZ не принят", "Ключ *** не принят")]
    [InlineData("key=sk-ant-api03-Zz9Yy8Xx7Ww6Vv5Uu4", "key=***")]
    [InlineData("OpenRouter sk-or-v1-0a1b2c3d4e5f6a7b8c9d", "OpenRouter ***")]
    [InlineData("Google AIzaSyA1b2C3d4E5f6G7h8I9j0KLMNOPQ", "Google ***")]
    [InlineData("groq gsk_AbCdEf1234567890ghij", "groq ***")]
    [InlineData("xai-AbCdEf1234567890ghijKL", "***")]
    public void ProviderApiKeys_AreMasked(string text, string expected)
    {
        Assert.Equal(expected, LogSanitizer.Sanitize(text));
    }

    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig-123", "Authorization: ***")]
    [InlineData("{\"authorization\":\"bearer abcdefgh12345678\"}", "{\"authorization\":\"***\"}")]
    public void BearerTokens_AreMasked(string text, string expected)
    {
        Assert.Equal(expected, LogSanitizer.Sanitize(text));
    }

    [Theory]
    [InlineData("x-api-key: 0123456789abcdef", "x-api-key: ***")]
    [InlineData("x-goog-api-key: AbCdEfGhIjKl", "x-goog-api-key: ***")]
    [InlineData("{\"api_key\": \"abcdef123456\"}", "{\"api_key\": \"***\"}")]
    [InlineData("api-key=abcdef123456&x=1", "api-key=***")]
    [InlineData("token=abcdef123456", "token=***")]
    [InlineData("client secret: abcdef123456", "client secret: ***")]
    public void NamedSecrets_KeepNameAndMaskValue(string text, string expected)
    {
        Assert.Equal(expected, LogSanitizer.Sanitize(text));
    }

    [Theory]
    [InlineData("Server=sql01;User Id=fakt_app;Password=S3cr3t!Pa55;Encrypt=True", "Server=sql01;User Id=fakt_app;***;Encrypt=True")]
    [InlineData("Data Source=sql01;pwd = hunter2hunter2", "Data Source=sql01;***")]
    public void ConnectionStringPasswords_AreMasked(string text, string expected)
    {
        Assert.Equal(expected, LogSanitizer.Sanitize(text));
    }

    [Theory]
    [InlineData("password: hunter2hunter2", "password: ***")]
    [InlineData("{\"user\":\"fakt\",\"password\":\"S3cr3t!\",\"db\":\"FAKT\"}", "{\"user\":\"fakt\",\"password\":\"***\",\"db\":\"FAKT\"}")]
    [InlineData("PWD : qwerty12", "PWD : ***")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "Authorization: Basic ***")]
    [InlineData("\"Authorization\": \"Basic dXNlcjpwYXNz\"", "\"Authorization\": \"Basic ***\"")]
    public void PasswordsInOtherNotations_AndBasicAuth_AreMasked(string text, string expected)
    {
        Assert.Equal(expected, LogSanitizer.Sanitize(text));
    }

    [Fact]
    public void WordsContainingPassword_WithoutValue_AreKept()
    {
        Assert.Equal("Введите пароль SQL (password) в настройках", LogSanitizer.Sanitize("Введите пароль SQL (password) в настройках"));
    }

    [Fact]
    public void Emails_KeepFirstCharacterAndDomain()
    {
        Assert.Equal("Пишите i***@example.test", LogSanitizer.Sanitize("Пишите ivan.petrov@example.test"));
    }

    [Theory]
    [InlineData("Телефон +7 (900) 123-45-67", "Телефон +* (***) ***-**-67")]
    [InlineData("Телефон 8 900 123 45 67", "Телефон * *** *** ** 67")]
    [InlineData("Счёт 40817810099910004312", "Счёт ****************4312")]
    [InlineData("Карта 4276 1234 5678 9012", "Карта **** **** **** 9012")]
    [InlineData("СНИЛС 123-456-789 01", "СНИЛС ***-***-*** 01")]
    [InlineData("Запись 1234567", "Запись *****67")]
    public void LongDigitSequences_AreMaskedKeepingLastDigits(string text, string expected)
    {
        Assert.Equal(expected, LogSanitizer.Sanitize(text));
    }

    [Theory]
    [InlineData("Обработано 12345 записей")]
    [InlineData("Начало 2026-09-24 10:00:00")]
    [InlineData("Дата 24.09.2026")]
    [InlineData("HTTP 429, повтор через 7 с")]
    [InlineData("Задание 42, файл 7, пакет 3 из 10")]
    public void ShortNumbersAndDates_AreKept(string text)
    {
        Assert.Equal(text, LogSanitizer.Sanitize(text));
    }

    [Theory]
    [InlineData("Платёж 01.02.2020 40817810099910004312", "Платёж 01.02.2020 ****************4312")]
    [InlineData("2026-09-24 79001234567", "2026-09-24 *********67")]
    public void DigitsFollowingADate_AreStillMasked(string text, string expected)
    {
        // Дата не маскируется, но идущий за ней через пробел номер счёта или телефона — маскируется.
        Assert.Equal(expected, LogSanitizer.Sanitize(text));
    }

    [Fact]
    public void NullAndEmpty_AreReturnedAsIs()
    {
        Assert.Null(LogSanitizer.Sanitize(null));
        Assert.Equal(string.Empty, LogSanitizer.Sanitize(string.Empty));
    }

    [Fact]
    public void TextWithoutSecrets_IsUnchanged()
    {
        const string text = "Файл «Реестр.csv»: структура определена, 3 столбца, заголовок.";

        Assert.Equal(text, LogSanitizer.Sanitize(text));
    }
}
