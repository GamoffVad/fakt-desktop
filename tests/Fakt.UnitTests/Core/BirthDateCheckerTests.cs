using System;
using Fakt.Core.Extraction;
using Xunit;

namespace Fakt.UnitTests.Core;

public sealed class BirthDateCheckerTests
{
    private static readonly DateTime Today = new(2026, 9, 24);

    private static BirthDateChecker.Result Check(string value, string evidence) => BirthDateChecker.Check(value, evidence, Today);

    private static void AssertAccepted(string value, string evidence)
    {
        var result = Check(value, evidence);
        Assert.Null(result.Reason);
        Assert.Equal(DateTime.ParseExact(value, "yyyy-MM-dd", null), result.Date);
    }

    private static void AssertRejected(string value, string evidence, string reasonFragment)
    {
        var result = Check(value, evidence);
        Assert.Null(result.Date);
        Assert.Contains(reasonFragment, result.Reason);
    }

    [Theory]
    [InlineData("1990-02-15", "15.02.1990")]
    [InlineData("1990-02-03", "03.02.1990")]
    [InlineData("1990-02-03", "3.2.1990")]
    [InlineData("1990-02-15", "Дата рождения: 15.02.1990 г.")]
    [InlineData("1990-02-15", "1990-02-15")]
    [InlineData("1990-02-15", "род. 1990-2-15")]
    public void NumericDate_ConfirmedByEvidence_IsAccepted(string value, string evidence)
    {
        AssertAccepted(value, evidence);
    }

    [Fact]
    public void DottedDate_IsReadDayFirst()
    {
        // «03.02.1990» — 3 февраля, а не 2 марта.
        AssertAccepted("1990-02-03", "03.02.1990");
        AssertRejected("1990-03-02", "03.02.1990", "дд.мм.гггг");
    }

    [Theory]
    [InlineData("1990-02-15", "15.02.90")]
    [InlineData("1985-04-25", "25/04/85")]
    [InlineData("1985-04-25", "25-04-85")]
    public void TwoDigitYear_IsRejected(string value, string evidence)
    {
        AssertRejected(value, evidence, "Двузначный год");
    }

    [Theory]
    [InlineData("1985-04-03", "03/04/1985")]
    [InlineData("1985-03-04", "03/04/1985")]
    [InlineData("1985-03-04", "03-04-1985")]
    [InlineData("1985-12-01", "1/12/1985")]
    public void SlashDate_WithBothPartsUpTo12_IsAmbiguous(string value, string evidence)
    {
        AssertRejected(value, evidence, "Неоднозначный порядок дня и месяца");
    }

    [Theory]
    [InlineData("1985-04-25", "25/04/1985")]
    [InlineData("1985-04-25", "04/25/1985")]
    [InlineData("1985-05-05", "05/05/1985")]
    [InlineData("1985-04-25", "25-04-1985")]
    public void SlashDate_Unambiguous_IsAccepted(string value, string evidence)
    {
        AssertAccepted(value, evidence);
    }

    [Fact]
    public void SlashDate_NotMatchingValue_IsRejected()
    {
        AssertRejected("1985-04-26", "25/04/1985", "не совпадает");
    }

    [Theory]
    [InlineData("1990-02-15", "15 февраля 1990 г.")]
    [InlineData("1990-02-15", "15 ФЕВРАЛЯ 1990")]
    [InlineData("1985-05-01", "1 мая 1985")]
    [InlineData("1977-03-03", "3 марта 1977 года")]
    [InlineData("1984-06-01", "1 июня 1984")]
    [InlineData("1970-01-10", "10 января 1970")]
    [InlineData("1970-01-10", "10 январь 1970")]
    [InlineData("1965-08-07", "родился 7 августа 1965")]
    [InlineData("1999-09-09", "9 сентябрь 1999")]
    [InlineData("2001-12-31", "31 декабря 2001")]
    public void RussianMonthName_IsAccepted(string value, string evidence)
    {
        AssertAccepted(value, evidence);
    }

    [Theory]
    [InlineData("1990-02-16", "15 февраля 1990")]
    [InlineData("1991-02-15", "15 февраля 1990")]
    [InlineData("1990-03-15", "15 февраля 1990")]
    public void MonthName_NotMatchingValue_IsRejected(string value, string evidence)
    {
        AssertRejected(value, evidence, "названием месяца");
    }

    [Theory]
    [InlineData("1990-03-15", "15 March 1990")]
    [InlineData("1990-02-15", "Feb 15, 1990")]
    [InlineData("1990-12-01", "1 December 1990")]
    public void EnglishMonthName_ExactWord_IsAccepted(string value, string evidence)
    {
        AssertAccepted(value, evidence);
    }

    [Theory]
    [InlineData("1990-01-15", "Jane Doe 15 1990")]
    [InlineData("1990-03-15", "Mark 15 1990")]
    [InlineData("1990-05-15", "Maybe 15 1990")]
    [InlineData("1990-03-15", "Мария 15 1990")]
    [InlineData("1990-03-15", "Марина 15 1990")]
    public void NamesResemblingMonths_AreNotMonths(string value, string evidence)
    {
        AssertRejected(value, evidence, "нет полной даты");
    }

    [Theory]
    [InlineData("1990-05-15", "Майя 15 1990")]
    [InlineData("1990-05-15", "Звание: майор, отдел 15, 1990")]
    [InlineData("1990-05-15", "маяк 15 1990")]
    [InlineData("1990-03-15", "Мартин 15 1990")]
    [InlineData("1990-03-15", "Мартына 15 1990")]
    [InlineData("1990-08-15", "Августин 15 1990")]
    public void RussianWordsStartingWithMonthStem_AreNotMonths(string value, string evidence)
    {
        // Имена и слова, начинающиеся с основы месяца («Майя», «майор», «Мартин»), не подтверждают дату.
        AssertRejected(value, evidence, "нет полной даты");
    }

    [Fact]
    public void FutureDate_IsRejected()
    {
        AssertRejected("2026-09-25", "25.09.2026", "в будущем");
        AssertAccepted("2026-09-24", "24.09.2026");
    }

    [Fact]
    public void DateBefore1850_IsRejected()
    {
        AssertRejected("1849-12-31", "31.12.1849", "раньше 1850");
        AssertAccepted("1850-01-01", "01.01.1850");
    }

    [Theory]
    [InlineData("15.02.1990")]
    [InlineData("1990-02-30")]
    [InlineData("1990-2-15")]
    [InlineData("19900215")]
    [InlineData("дата")]
    public void ModelValueNotIsoDate_IsRejected(string value)
    {
        AssertRejected(value, "15.02.1990", "ГГГГ-ММ-ДД");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyModelValue_HasNoDateAndNoReason(string value)
    {
        var result = Check(value, "15.02.1990");
        Assert.Null(result.Date);
        Assert.Null(result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingEvidence_IsRejected(string evidence)
    {
        AssertRejected("1990-02-15", evidence, "Нет исходного фрагмента");
    }

    [Fact]
    public void YearOnly_IsNotAFullDate()
    {
        AssertRejected("1990-02-15", "1990 г.р.", "нет полной даты");
    }

    [Fact]
    public void UnrecognizedEvidence_IsRejected()
    {
        AssertRejected("1990-02-15", "дата неизвестна", "не распознан");
    }
}
