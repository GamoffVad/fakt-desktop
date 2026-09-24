using System;
using System.Linq;
using System.Text.RegularExpressions;
using Fakt.Core.Settings;
using Fakt.Infrastructure.Sql;
using Xunit;

namespace Fakt.UnitTests.Infrastructure;

public sealed class SqlNamesTests
{
    private static DatabaseSettings Exotic() => new()
    {
        Schema = "my schema]x",
        AuxiliarySchema = "aux'ы",
        SourceFilesTable = "Файлы]'src",
        PersonFactsTable = "Лица ]] 'наблюдения'",
        Columns = new ColumnMap
        {
            SourceFilesId = "Id]",
            FileCode = "Код'файла",
            FileName = "Имя файла",
            PersonFactsId = "PF Id",
            Surname = "Фам]илия",
            Name = "Им'я",
            Patronymic = "Отчество",
            BirthDate = "Дата [рождения]",
            BirthPlace = "Место--рождения",
            All = "ALL'x]",
            FileId = "ID_File;Name",
        },
    };

    [Theory]
    [InlineData("PersonFacts", "[PersonFacts]")]
    [InlineData("a]b", "[a]]b]")]
    [InlineData("]]", "[]]]]]")]
    [InlineData("Дата рождения", "[Дата рождения]")]
    [InlineData("x]; DROP TABLE t;--", "[x]]; DROP TABLE t;--]")]
    public void Quote_WrapsInBracketsAndDoublesClosingBracket(string identifier, string expected)
    {
        Assert.Equal(expected, SqlNames.Quote(identifier));
    }

    [Theory]
    [InlineData("O'Brien", "N'O''Brien'")]
    [InlineData("''", "N''''''")]
    [InlineData("]", "N']'")]
    [InlineData(null, "N''")]
    [InlineData("Тест'); DROP TABLE t;--", "N'Тест''); DROP TABLE t;--'")]
    public void Literal_IsUnicodeLiteralWithDoubledApostrophes(string text, string expected)
    {
        Assert.Equal(expected, SqlNames.Literal(text));
    }

    [Fact]
    public void DefaultSettings_ProduceQualifiedNames()
    {
        var names = new SqlNames(new DatabaseSettings());

        Assert.Equal("[dbo].[SourceFiles]", names.SF);
        Assert.Equal("[dbo].[PersonFacts]", names.PF);
        Assert.Equal("[dbo].[FaktJobs]", names.Aux("FaktJobs"));
        Assert.Equal("[Фамилия]", names.Col(names.ColumnOf("Surname")));
    }

    [Fact]
    public void AuxiliarySchema_IsUsedForAuxiliaryTablesAndNamesAreTrimmed()
    {
        var names = new SqlNames(new DatabaseSettings { Schema = " data ", AuxiliarySchema = "fakt", SourceFilesTable = " Files " });

        Assert.Equal("[data].[Files]", names.SF);
        Assert.Equal("[fakt].[FaktJobs]", names.Aux("FaktJobs"));
    }

    [Theory]
    [InlineData("{{S}}", "[dbo]")]
    [InlineData("{{A}}", "[fakt]")]
    [InlineData("{{S_LIT}}", "N'dbo'")]
    [InlineData("{{A_LIT}}", "N'fakt'")]
    [InlineData("{{S_QUOTED_LIT}}", "N'[dbo]'")]
    [InlineData("{{A_QUOTED_LIT}}", "N'[fakt]'")]
    [InlineData("{{SF}}", "[dbo].[SourceFiles]")]
    [InlineData("{{PF}}", "[dbo].[PersonFacts]")]
    [InlineData("{{SF_LIT}}", "N'[dbo].[SourceFiles]'")]
    [InlineData("{{PF_LIT}}", "N'[dbo].[PersonFacts]'")]
    [InlineData("{{SF_NAME}}", "SourceFiles")]
    [InlineData("{{PF_NAME_LIT}}", "PersonFacts")]
    [InlineData("{{c:Surname}}", "[Фамилия]")]
    [InlineData("{{c:BirthDate}}", "[Дата рождения]")]
    [InlineData("{{c:FileId}}", "[ID_FileName]")]
    [InlineData("{{c_lit:All}}", "N'ALL'")]
    [InlineData("{{AUX:FaktJobs}}", "[fakt].[FaktJobs]")]
    [InlineData("{{AUX_LIT:FaktSearchDocs}}", "N'[fakt].[FaktSearchDocs]'")]
    public void Render_SubstitutesTokens(string template, string expected)
    {
        var names = new SqlNames(new DatabaseSettings { AuxiliarySchema = "fakt" });

        Assert.Equal(expected, names.Render(template));
    }

    [Fact]
    public void Render_EscapesExoticNamesForTheirContext()
    {
        var names = new SqlNames(Exotic());

        Assert.Equal("[my schema]]x].[Файлы]]'src]", names.Render("{{SF}}"));
        Assert.Equal("N'[my schema]]x].[Файлы]]''src]'", names.Render("{{SF_LIT}}"));
        Assert.Equal("[PK_Файлы]]'src]", names.Render("[PK_{{SF_NAME}}]"));
        Assert.Equal("N'UX_Файлы]''src_Version'", names.Render("N'UX_{{SF_NAME_LIT}}_Version'"));
        Assert.Equal("[Фам]]илия]", names.Render("{{c:Surname}}"));
        Assert.Equal("N'ALL''x]'", names.Render("{{c_lit:All}}"));
        Assert.Equal("[aux'ы].[FaktJobs]", names.Render("{{AUX:FaktJobs}}"));
        Assert.Equal("N'[aux''ы].[FaktJobs]'", names.Render("{{AUX_LIT:FaktJobs}}"));
    }

    [Fact]
    public void Render_LeavesNonTokenTextIntact()
    {
        var names = new SqlNames(new DatabaseSettings());
        const string sql = "SELECT N'{}' AS j, '{ x }' AS t, N'{{ not a token }}' FROM {{PF}} WHERE {{c:FileId}} = 1";

        Assert.Equal("SELECT N'{}' AS j, '{ x }' AS t, N'{{ not a token }}' FROM [dbo].[PersonFacts] WHERE [ID_FileName] = 1", names.Render(sql));
    }

    [Fact]
    public void Render_UnknownTokenOrColumn_Throws()
    {
        var names = new SqlNames(new DatabaseSettings());

        Assert.Throws<InvalidOperationException>(() => names.Render("{{DROP}}"));
        Assert.Throws<ArgumentException>(() => names.Render("{{c:Nope}}"));
        Assert.Throws<ArgumentException>(() => names.Render("{{c}}"));
        Assert.Throws<ArgumentException>(() => names.Render("{{AUX}}"));
    }

    [Theory]
    [InlineData("schema", "")]
    [InlineData("schema", "   ")]
    [InlineData("table", "")]
    [InlineData("table", "bad\tname")]
    [InlineData("column", "")]
    [InlineData("column", "bad\u0000name")]
    public void InvalidNames_AreRejected(string what, string value)
    {
        var settings = new DatabaseSettings();
        switch (what)
        {
            case "schema":
                settings.Schema = value;
                break;
            case "table":
                settings.PersonFactsTable = value;
                break;
            default:
                settings.Columns.Surname = value;
                break;
        }

        Assert.Throws<ArgumentException>(() => new SqlNames(settings));
    }

    [Fact]
    public void TooLongName_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new SqlNames(new DatabaseSettings { SourceFilesTable = new string('t', SqlNames.MaxIdentifierLength + 1) }));
        Assert.Equal("[" + new string('t', SqlNames.MaxIdentifierLength) + "]",
            new SqlNames(new DatabaseSettings { SourceFilesTable = new string('t', SqlNames.MaxIdentifierLength) }).SF.Split('.')[1]);
    }

    [Fact]
    public void AllEmbeddedMigrations_RenderCompletelyForDefaultAndExoticNames()
    {
        Assert.NotEmpty(MigrationCatalog.All);
        foreach (var settings in new[] { new DatabaseSettings(), Exotic() })
        {
            var names = new SqlNames(settings);
            foreach (var migration in MigrationCatalog.All)
            {
                var rendered = names.Render(migration.Template);

                Assert.DoesNotMatch(@"\{\{[A-Za-z_]+(:[A-Za-z_]+)?\}\}", rendered);
                var batches = SqlNames.SplitBatches(rendered);
                Assert.NotEmpty(batches);
                Assert.All(batches, b => Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline), b));
            }
        }
    }

    // ---------- SplitBatches ----------

    [Fact]
    public void SplitBatches_SplitsOnGoLines()
    {
        var batches = SqlNames.SplitBatches("SELECT 1\nGO\nSELECT 2");

        Assert.Equal(new[] { "SELECT 1\n", "SELECT 2\n" }, batches);
    }

    [Fact]
    public void SplitBatches_HandlesCrLfCaseWhitespaceAndTrailingComment()
    {
        var batches = SqlNames.SplitBatches("SELECT 1\r\n  go  \r\nSELECT 2\r\nGo -- конец пакета\r\nSELECT 3");

        Assert.Equal(new[] { "SELECT 1\n", "SELECT 2\n", "SELECT 3\n" }, batches);
    }

    [Theory]
    [InlineData("GOTO done")]
    [InlineData("SELECT 'GO'")]
    [InlineData("SELECT N'x' AS [GO]")]
    [InlineData("-- GO")]
    [InlineData("/* GO */")]
    [InlineData("EXEC dbo.GO_Process")]
    [InlineData("GO;")]
    public void SplitBatches_DoesNotSplitOnGoInsideOtherText(string line)
    {
        var script = "SELECT 1\n" + line + "\nSELECT 2";

        var batch = Assert.Single(SqlNames.SplitBatches(script));
        Assert.Contains(line, batch);
    }

    [Fact]
    public void SplitBatches_SkipsEmptyBatches()
    {
        Assert.Equal(new[] { "SELECT 1\n" }, SqlNames.SplitBatches("GO\n\nGO\nSELECT 1\nGO\n   \nGO\n"));
        Assert.Empty(SqlNames.SplitBatches(" \r\n GO \r\n"));
    }

    [Fact]
    public void SplitBatches_KeepsMultilineStatementsTogether()
    {
        var script = "CREATE TABLE t (\n  a INT,\n  b NVARCHAR(10) DEFAULT N'GO'\n);\nGO\nINSERT INTO t (a) VALUES (1);";

        var batches = SqlNames.SplitBatches(script);

        Assert.Equal(2, batches.Count);
        Assert.Equal(4, batches[0].Count(ch => ch == '\n'));
    }
}
