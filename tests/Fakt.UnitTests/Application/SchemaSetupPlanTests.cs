using System.Linq;
using Fakt.Application.Services;
using Fakt.Core.Storage;
using Xunit;

namespace Fakt.UnitTests.Application;

/// <summary>Какие миграции применяются автоматически при подключении к существующей базе.</summary>
public sealed class SchemaSetupPlanTests
{
    private static SchemaReport Report(bool mainTablesExist, bool fullText = true, params string[] applied)
    {
        MigrationInfo M(string id, bool alters, bool required, bool noTransaction = false, string script = "") => new()
        {
            Id = id, Title = id, AltersUserTables = alters, RequiredForProcessing = required, RequiresNoTransaction = noTransaction,
            Script = script, Applied = applied.Contains(id),
        };

        return new SchemaReport
        {
            Connected = true,
            SourceFiles = new TableInfo { Exists = mainTablesExist },
            PersonFacts = new TableInfo { Exists = mainTablesExist },
            FullText = new FullTextInfo { Installed = fullText },
            Migrations =
            {
                M("V001", false, true), M("V002", true, true), M("V003", true, true), M("V004", true, false),
                M("V005", false, true), M("V006", false, true), M("V007", false, false, true, "CREATE FULLTEXT CATALOG"), M("V008", true, false),
            },
        };
    }

    [Fact]
    public void WithoutMainTablesEverythingIsCreated()
    {
        var plan = SchemaSetupPlan.AutoApply(Report(mainTablesExist: false));

        Assert.Equal(new[] { "V001", "V002", "V003", "V004", "V005", "V006", "V007", "V008" }, plan.Select(m => m.Id));
        Assert.Empty(SchemaSetupPlan.LeftForAdministrator(Report(mainTablesExist: false)));
    }

    [Fact]
    public void ExistingMainTablesGetOnlyRequiredChanges()
    {
        var report = Report(mainTablesExist: true);

        Assert.Equal(new[] { "V001", "V002", "V003", "V005", "V006", "V007" }, SchemaSetupPlan.AutoApply(report).Select(m => m.Id));
        Assert.Equal(new[] { "V004", "V008" }, SchemaSetupPlan.LeftForAdministrator(report).Select(m => m.Id));
        Assert.DoesNotContain("• V001", SchemaSetupPlan.Describe(report, SchemaSetupPlan.AutoApply(report)));
    }

    [Fact]
    public void FullTextIsSkippedWhenNotInstalled()
    {
        Assert.DoesNotContain(SchemaSetupPlan.AutoApply(Report(false, fullText: false)), m => m.Id == "V007");
    }

    [Fact]
    public void AppliedMigrationsAreNotRepeated()
    {
        var report = Report(true, true, "V001", "V002", "V003", "V005", "V006", "V007");

        Assert.Empty(SchemaSetupPlan.AutoApply(report));
    }

    [Fact]
    public void NothingIsPlannedWithoutConnection()
    {
        Assert.Empty(SchemaSetupPlan.AutoApply(new SchemaReport { Connected = false }));
    }
}
