using System.Collections.Generic;
using System.Linq;
using Fakt.Core.Structure;
using Xunit;

namespace Fakt.UnitTests.Core;

public sealed class StructureContractValidatorTests
{
    private static StructureDescriptor Delimited() => new()
    {
        Classification = "structured",
        Format = "delimited",
        Encoding = "utf-8",
        HasHeader = true,
        HeaderRow = 0,
        SkipRows = 0,
        Delimiter = ";",
        QuoteChar = "\"",
        Columns = new List<string> { "Фамилия", "Имя", "Телефон" },
        Confidence = 0.9,
        Reason = "Разделитель точка с запятой, строка заголовка.",
    };

    private static StructureDescriptor FixedWidth() => new()
    {
        Classification = "structured",
        Format = "fixed_width",
        Encoding = "cp866",
        HasHeader = false,
        SkipRows = 0,
        FixedWidths = new List<int> { 20, 15, 10 },
        Columns = new List<string> { "Фамилия", "Имя", "Дата" },
    };

    private static StructureDescriptor Xml(string path, Dictionary<string, string> namespaces = null) => new()
    {
        Classification = "structured",
        Format = "xml",
        XmlRecordPath = path,
        XmlNamespaces = namespaces,
    };

    private static StructureCheckResult Validate(StructureDescriptor descriptor, string detected = "utf-8") =>
        StructureContractValidator.Validate(descriptor, detected);

    private static void AssertError(StructureCheckResult result, string fragment)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(fragment));
    }

    // ---------- Классификация и общие поля ----------

    [Fact]
    public void MissingStructure_IsError()
    {
        AssertError(StructureContractValidator.Validate(null, "utf-8"), "не содержит структуры");
    }

    [Theory]
    [InlineData("table")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownClassification_IsError(string classification)
    {
        var descriptor = Delimited();
        descriptor.Classification = classification;

        AssertError(Validate(descriptor), "classification");
    }

    [Fact]
    public void ClassificationAndFormat_AreNormalized()
    {
        var descriptor = Delimited();
        descriptor.Classification = " Structured ";
        descriptor.Format = "DELIMITED";

        var result = Validate(descriptor);

        Assert.True(result.IsValid);
        Assert.Equal("structured", result.Normalized.Classification);
        Assert.Equal("delimited", result.Normalized.Format);
    }

    [Theory]
    [InlineData("unstructured")]
    [InlineData("insufficient_sample")]
    public void NonTabularClassification_SkipsFormatChecks(string classification)
    {
        var descriptor = new StructureDescriptor { Classification = classification, Format = "garbage", Encoding = "cp1251" };

        var result = Validate(descriptor, detected: "windows-1251");

        Assert.True(result.IsValid);
        Assert.Equal("windows-1251", result.Normalized.Encoding);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public void ConfidenceOutOfRange_IsDroppedWithWarning(double confidence)
    {
        var descriptor = Delimited();
        descriptor.Confidence = confidence;

        var result = Validate(descriptor);

        Assert.True(result.IsValid);
        Assert.Null(result.Normalized.Confidence);
        Assert.Contains(result.Warnings, w => w.Contains("confidence"));
    }

    [Fact]
    public void LongReason_IsShortened()
    {
        var descriptor = Delimited();
        descriptor.Reason = new string('р', 2500);

        Assert.Equal(2001, Validate(descriptor).Normalized.Reason.Length);
    }

    [Fact]
    public void Validation_DoesNotMutateInput()
    {
        var descriptor = Delimited();
        descriptor.Delimiter = "tab";
        descriptor.Classification = " Structured ";

        var result = Validate(descriptor);

        Assert.Equal("\t", result.Normalized.Delimiter);
        Assert.Equal("tab", descriptor.Delimiter);
        Assert.Equal(" Structured ", descriptor.Classification);
    }

    // ---------- Кодировка ----------

    [Fact]
    public void DetectedEncoding_WinsOverModelGuess()
    {
        var descriptor = Delimited();
        descriptor.Encoding = "cp1251";

        var result = Validate(descriptor, detected: "utf-8");

        Assert.Equal("utf-8", result.Normalized.Encoding);
        Assert.Contains(result.Warnings, w => w.Contains("Windows-1251") && w.Contains("UTF-8"));
    }

    [Fact]
    public void EncodingOfSameFamily_ProducesNoWarning()
    {
        var descriptor = Delimited();
        descriptor.Encoding = "UTF8";

        var result = Validate(descriptor, detected: "utf-8-sig");

        Assert.True(result.IsValid);
        Assert.Equal("utf-8-sig", result.Normalized.Encoding);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ModelEncodingSynonym_IsUsedWhenNothingDetected()
    {
        var descriptor = Delimited();
        descriptor.Encoding = "cp1251";

        Assert.Equal("windows-1251", Validate(descriptor, detected: null).Normalized.Encoding);
    }

    [Fact]
    public void UnsupportedModelEncoding_IsIgnoredWithWarning()
    {
        var descriptor = Delimited();
        descriptor.Encoding = "klingon-8";

        var result = Validate(descriptor, detected: "koi8-r");

        Assert.Equal("koi8-r", result.Normalized.Encoding);
        Assert.Contains(result.Warnings, w => w.Contains("неподдерживаемую кодировку"));
    }

    // ---------- delimited ----------

    [Fact]
    public void ValidDelimitedStructure_Passes()
    {
        var result = Validate(Delimited());

        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
        Assert.Equal(";", result.Normalized.Delimiter);
        Assert.Equal(new[] { "Фамилия", "Имя", "Телефон" }, result.Normalized.Columns);
    }

    [Theory]
    [InlineData("tab", "\t")]
    [InlineData("\\t", "\t")]
    [InlineData("\t", "\t")]
    [InlineData("Табуляция", "\t")]
    [InlineData("comma", ",")]
    [InlineData("запятая", ",")]
    [InlineData("semicolon", ";")]
    [InlineData("точка с запятой", ";")]
    [InlineData("pipe", "|")]
    [InlineData(" | ", "|")]
    [InlineData("пробел", " ")]
    [InlineData(" ", " ")]
    public void DelimiterSynonyms_AreNormalized(string raw, string expected)
    {
        Assert.Equal(expected, StructureContractValidator.NormalizeDelimiter(raw));

        var descriptor = Delimited();
        descriptor.Delimiter = raw;
        descriptor.QuoteChar = null;
        var result = Validate(descriptor);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(expected, result.Normalized.Delimiter);
    }

    [Theory]
    [InlineData(null, "не указан разделитель")]
    [InlineData("", "не указан разделитель")]
    [InlineData("||", "одним символом")]
    [InlineData("\n", "Перевод строки")]
    [InlineData("\r", "Перевод строки")]
    public void InvalidDelimiter_IsError(string delimiter, string fragment)
    {
        var descriptor = Delimited();
        descriptor.Delimiter = delimiter;

        AssertError(Validate(descriptor), fragment);
    }

    [Fact]
    public void QuoteOrEscapeEqualToDelimiter_IsError()
    {
        var quote = Delimited();
        quote.QuoteChar = ";";
        AssertError(Validate(quote), "кавычки совпадает с разделителем");

        var escape = Delimited();
        escape.EscapeChar = ";";
        AssertError(Validate(escape), "Escape-символ совпадает с разделителем");
    }

    [Fact]
    public void MultiCharacterQuoteOrEscape_IsError()
    {
        var quote = Delimited();
        quote.QuoteChar = "''";
        AssertError(Validate(quote), "Символ кавычки");

        var escape = Delimited();
        escape.EscapeChar = "\\\\";
        AssertError(Validate(escape), "Escape-символ");
    }

    [Fact]
    public void EmptyQuoteAndEscape_BecomeNull()
    {
        var descriptor = Delimited();
        descriptor.QuoteChar = "";
        descriptor.EscapeChar = "";

        var result = Validate(descriptor);

        Assert.True(result.IsValid);
        Assert.Null(result.Normalized.QuoteChar);
        Assert.Null(result.Normalized.EscapeChar);
    }

    // ---------- header_row / skip_rows ----------

    [Fact]
    public void HeaderRowDifferentFromSkipRows_IsInconsistent()
    {
        var descriptor = Delimited();
        descriptor.SkipRows = 2;
        descriptor.HeaderRow = 0;

        AssertError(Validate(descriptor), "header_row = 0, skip_rows = 2");
    }

    [Fact]
    public void HeaderRowEqualToSkipRows_IsValid()
    {
        var descriptor = Delimited();
        descriptor.SkipRows = 3;
        descriptor.HeaderRow = 3;

        Assert.True(Validate(descriptor).IsValid);
    }

    [Fact]
    public void MissingHeaderRow_DefaultsToSkipRows()
    {
        var descriptor = Delimited();
        descriptor.SkipRows = 3;
        descriptor.HeaderRow = null;

        var result = Validate(descriptor);

        Assert.True(result.IsValid);
        Assert.Equal(3, result.Normalized.HeaderRow);
        Assert.Contains(result.Warnings, w => w.Contains("header_row не указан"));
    }

    [Fact]
    public void HeaderRowWithoutHeader_IsIgnored()
    {
        var descriptor = Delimited();
        descriptor.HasHeader = false;
        descriptor.HeaderRow = 1;

        var result = Validate(descriptor);

        Assert.True(result.IsValid);
        Assert.Null(result.Normalized.HeaderRow);
        Assert.Contains(result.Warnings, w => w.Contains("проигнорирован"));
    }

    [Fact]
    public void MissingHasHeader_IsError()
    {
        var descriptor = Delimited();
        descriptor.HasHeader = null;

        AssertError(Validate(descriptor), "has_header");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void SkipRowsOutOfRange_IsError(int skipRows)
    {
        var descriptor = Delimited();
        descriptor.SkipRows = skipRows;
        descriptor.HeaderRow = skipRows;

        AssertError(Validate(descriptor), "skip_rows = " + skipRows);
    }

    [Fact]
    public void MissingSkipRows_MeansZero()
    {
        var descriptor = Delimited();
        descriptor.SkipRows = null;

        var result = Validate(descriptor);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.Normalized.SkipRows);
    }

    // ---------- Колонки ----------

    [Fact]
    public void DelimitedWithoutColumns_IsError()
    {
        var descriptor = Delimited();
        descriptor.Columns = null;

        AssertError(Validate(descriptor), "Не указан список колонок");
    }

    [Fact]
    public void EmptyColumnNames_AreRenamed()
    {
        var descriptor = Delimited();
        descriptor.Columns = new List<string> { "Фамилия", "  ", null };

        var result = Validate(descriptor);

        Assert.True(result.IsValid);
        Assert.Equal(new[] { "Фамилия", "Колонка 2", "Колонка 3" }, result.Normalized.Columns);
        Assert.Contains(result.Warnings, w => w.Contains("переименованы"));
    }

    [Fact]
    public void DuplicateColumnNames_AreNumberedCaseInsensitively()
    {
        var descriptor = Delimited();
        descriptor.Columns = new List<string> { "Телефон", "телефон", "Телефон" };

        var result = Validate(descriptor);

        Assert.Equal(new[] { "Телефон", "телефон (2)", "Телефон (3)" }, result.Normalized.Columns);
        Assert.Contains(result.Warnings, w => w.Contains("переименованы"));
    }

    [Theory]
    [InlineData("A|A (2)|A")]
    [InlineData("A (2)|A|A")]
    [InlineData("A|A|a (2)")]
    public void RenamedColumns_NeverCollideWithOtherNames(string names)
    {
        var columns = names.Split('|');
        var descriptor = Delimited();
        descriptor.Columns = columns.ToList();

        var result = Validate(descriptor);

        Assert.True(result.IsValid);
        Assert.Equal(columns.Length, result.Normalized.Columns.Count);
        Assert.Equal(result.Normalized.Columns.Count, result.Normalized.Columns.Distinct(System.StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void OverlongColumnName_IsShortened()
    {
        var descriptor = Delimited();
        descriptor.Columns = new List<string> { new string('к', 300), "Имя" };

        var result = Validate(descriptor);

        Assert.Equal(StructureContractValidator.MaxColumnNameLength, result.Normalized.Columns[0].Length);
        Assert.Contains(result.Warnings, w => w.Contains("сокращено"));
    }

    [Fact]
    public void TooManyColumns_IsError()
    {
        var descriptor = Delimited();
        descriptor.Columns = Enumerable.Range(1, StructureContractValidator.MaxColumns + 1).Select(i => "c" + i).ToList();

        AssertError(Validate(descriptor), "Слишком много колонок");
    }

    // ---------- fixed_width ----------

    [Fact]
    public void ValidFixedWidth_Passes()
    {
        var result = Validate(FixedWidth(), detected: "cp866");

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Null(result.Normalized.HeaderRow);
    }

    [Fact]
    public void FixedWidthWithoutWidths_IsError()
    {
        var descriptor = FixedWidth();
        descriptor.FixedWidths = null;

        AssertError(Validate(descriptor), "fixed_widths");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100001)]
    public void FixedWidthOutOfRange_IsError(int width)
    {
        var descriptor = FixedWidth();
        descriptor.FixedWidths = new List<int> { 10, width, 10 };

        AssertError(Validate(descriptor), "Ширины колонок");
    }

    [Fact]
    public void FixedWidthWithoutColumnNames_GetsGeneratedNames()
    {
        var descriptor = FixedWidth();
        descriptor.Columns = null;

        var result = Validate(descriptor);

        Assert.True(result.IsValid);
        Assert.Equal(new[] { "Колонка 1", "Колонка 2", "Колонка 3" }, result.Normalized.Columns);
    }

    [Fact]
    public void FixedWidthColumnCountMismatch_IsError()
    {
        var descriptor = FixedWidth();
        descriptor.Columns = new List<string> { "Фамилия" };

        AssertError(Validate(descriptor), "не совпадает с числом ширин");
    }

    // ---------- xml ----------

    [Theory]
    [InlineData("/root/items/item")]
    [InlineData("item")]
    [InlineData("/Документы/Запись")]
    [InlineData("data/record-item")]
    public void ValidXmlPath_Passes(string path)
    {
        var result = Validate(Xml(path));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("//item")]
    [InlineData("/root/item[@id='1']")]
    [InlineData("/root/*")]
    [InlineData("/root/@attr")]
    [InlineData("/root/item/text()")]
    public void XPathFeaturesInRecordPath_AreRejected(string path)
    {
        AssertError(Validate(Xml(path)), "недопустим");
    }

    [Fact]
    public void MissingXmlPath_IsError()
    {
        AssertError(Validate(Xml("  ")), "xml_record_path");
    }

    [Fact]
    public void PrefixedXmlPath_RequiresDeclaredNamespace()
    {
        var declared = Validate(Xml("/p:root/p:person", new Dictionary<string, string> { ["p"] = "urn:example:people" }));
        Assert.True(declared.IsValid, string.Join("; ", declared.Errors));

        AssertError(Validate(Xml("/p:root/p:person")), "Префикс «p»");
    }

    [Theory]
    [InlineData("1p", "urn:x")]
    [InlineData("p", "")]
    public void InvalidNamespaceDeclaration_IsError(string prefix, string uri)
    {
        AssertError(Validate(Xml("item", new Dictionary<string, string> { [prefix] = uri })), "пространство имён");
    }

    // ---------- json ----------

    [Theory]
    [InlineData("jsonl")]
    [InlineData("json_array")]
    public void JsonFormats_WithoutRecordPath_AreValid(string format)
    {
        var result = Validate(new StructureDescriptor { Classification = "structured", Format = format });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void JsonRecordPath_IsNotSupported()
    {
        var result = Validate(new StructureDescriptor { Classification = "structured", Format = "json_array", JsonRecordPath = "data.items" });

        AssertError(result, "json_record_path");
        Assert.Null(result.Normalized.JsonRecordPath);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("excel")]
    [InlineData(null)]
    public void UnknownFormat_IsError(string format)
    {
        var descriptor = Delimited();
        descriptor.Format = format;

        AssertError(Validate(descriptor), "Недопустимый формат");
    }
}
