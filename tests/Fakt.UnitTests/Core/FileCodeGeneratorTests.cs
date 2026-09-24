using System.Collections.Generic;
using Fakt.Core.Storage;
using Xunit;

namespace Fakt.UnitTests.Core;

public sealed class FileCodeGeneratorTests
{
    [Fact]
    public void Code_IsPrefixAnd18AsciiDigits()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = FileCodeGenerator.Next();

            Assert.Equal(20, code.Length);
            Assert.StartsWith("T_", code);
            Assert.All(code.Substring(2), ch => Assert.InRange(ch, '0', '9'));
            Assert.True(FileCodeGenerator.IsValid(code));
        }
    }

    [Fact]
    public void Codes_AreUniqueAndUniformlyDistributed()
    {
        const int count = 10_000;
        var codes = new HashSet<string>();
        var digitCounts = new int[10];
        for (var i = 0; i < count; i++)
        {
            var code = FileCodeGenerator.Next();
            Assert.True(codes.Add(code), "Повтор кода " + code);
            foreach (var ch in code.Substring(2))
            {
                digitCounts[ch - '0']++;
            }
        }

        // 180 000 цифр, ожидание 18 000 на цифру; допуск ±8 % (≈ 12 сигм) — проверка отбраковки 250..255.
        Assert.All(digitCounts, n => Assert.InRange(n, 16_500, 19_500));
        // Код — строка: ведущие нули сохраняются, длина не «укорачивается».
        Assert.Contains(codes, c => c[2] == '0');
        Assert.All(codes, c => Assert.Equal(20, c.Length));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("T_12345678901234567")]
    [InlineData("T_1234567890123456789")]
    [InlineData("t_123456789012345678")]
    [InlineData("F_123456789012345678")]
    [InlineData("T_12345678901234567A")]
    [InlineData("T_-12345678901234567")]
    [InlineData(" T_123456789012345678")]
    public void IsValid_RejectsMalformedCodes(string code)
    {
        Assert.False(FileCodeGenerator.IsValid(code));
    }

    [Fact]
    public void IsValid_AcceptsLeadingZeros()
    {
        Assert.True(FileCodeGenerator.IsValid("T_000000000000000001"));
    }
}
