using System;
using System.IO;
using System.Linq;
using Fakt.Application.Security;
using Fakt.Application.Settings;
using Fakt.Core.Llm;
using Fakt.Core.Security;
using Fakt.Core.Settings;
using Fakt.Infrastructure.Llm;
using Fakt.Infrastructure.Secrets;
using Fakt.Infrastructure.Settings;
using Fakt.UnitTests.TestSupport;
using Newtonsoft.Json;
using Xunit;

namespace Fakt.UnitTests.Application;

/// <summary>
/// Настоящие JsonSettingsStore и DpapiSecretStore (CurrentUser) во временном каталоге; роль задаёт поддельная
/// учётная запись. Доступ уже инициализирован владельцем, поэтому ACL каталога не меняется.
/// </summary>
internal sealed class SettingsHarness : IDisposable
{
    public SettingsHarness(FakeIdentityProvider identity = null)
    {
        Temp = new TempDirectory();
        Paths = new AppPaths(Temp.Combine("machine"), Temp.Combine("user"));
        Store = new JsonSettingsStore(Paths);
        Store.Save(new AppSettings
        {
            Access = new AccessSettings
            {
                OwnerSid = FakeIdentityProvider.AdminSid,
                OwnerName = "TESTDOM\\admin",
                Operators = { new PrincipalEntry { Sid = FakeIdentityProvider.OperatorSid, DisplayName = "TESTDOM\\operator" } },
                SecretScope = SecretScope.CurrentUser,
            },
        });
        Identity = identity ?? FakeIdentityProvider.Admin();
        Service = new SettingsService(Store, new DpapiSecretStoreFactory(Paths.UserSecretsDirectory, Paths.MachineSecretsDirectory), Identity, Logger);
        Service.Authorization = new AuthorizationService(Identity, () => Service.Current.Access);
    }

    public TempDirectory Temp { get; }

    public AppPaths Paths { get; }

    public JsonSettingsStore Store { get; }

    public FakeIdentityProvider Identity { get; }

    public CapturingLogger Logger { get; } = new();

    public SettingsService Service { get; }

    public string SettingsJson => File.Exists(Paths.SettingsFile) ? File.ReadAllText(Paths.SettingsFile) : string.Empty;

    public void Dispose() => Temp.Dispose();
}

public sealed class SettingsServiceLlmKeyTests : IDisposable
{
    private const string Key = "sk-test-UNIT-settings-0123456789";
    private static readonly LlmProviderDescriptor OpenAi = ProviderCatalog.All.Single(d => d.Id == ProviderCatalog.OpenAi);
    private static readonly LlmProviderDescriptor Compatible = ProviderCatalog.All.Single(d => d.Id == ProviderCatalog.OpenAiCompatible);

    private readonly SettingsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static LlmProfile NewProfile(string baseUrl = "https://api.example.test/v1") =>
        new() { Name = "Тестовый профиль", ProviderId = ProviderCatalog.OpenAi, BaseUrl = baseUrl, ModelId = "test-model" };

    private LlmProfile Stored(Guid id) => _h.Service.Current.LlmProfiles.Single(p => p.Id == id);

    private LlmProfile SavedWithKey()
    {
        var profile = NewProfile();
        _h.Service.SaveProfile(profile, makeActive: true);
        _h.Service.SetApiKey(profile.Id, "  " + Key + "  ");
        return Stored(profile.Id);
    }

    [Fact]
    public void SetApiKey_BindsKeyToHostAndKeepsItOutOfSettingsFile()
    {
        var stored = SavedWithKey();

        Assert.Equal("https://api.example.test", stored.KeyBoundHost);
        Assert.True(_h.Service.HasApiKey(stored.Id));
        Assert.DoesNotContain(Key, _h.SettingsJson);
        Assert.Equal(Key, _h.Service.BuildRuntimeConfig(stored, OpenAi).ApiKey);
    }

    [Theory]
    [InlineData("https://evil.example.test/v1")]
    [InlineData("https://api.example.test:8443/v1")]
    [InlineData("https://api.example.test.evil.test/v1")]
    [InlineData("http://api.example.test/v1")] // тот же хост, но без TLS: ключ не уходит открытым текстом
    public void BuildRuntimeConfig_RefusesToSendKeyToAnotherHost(string otherUrl)
    {
        var moved = SavedWithKey().Clone();
        moved.BaseUrl = otherUrl;

        var ex = Assert.Throws<LlmException>(() => _h.Service.BuildRuntimeConfig(moved, OpenAi));

        Assert.Equal(LlmErrorKind.Configuration, ex.Kind);
        Assert.Contains(UrlBuilder.HostOf(otherUrl), ex.Message);
        Assert.DoesNotContain(Key, ex.Message);
    }

    [Fact]
    public void BuildRuntimeConfig_SameHostOtherPath_KeepsKey()
    {
        var sameHost = SavedWithKey().Clone();
        sameHost.BaseUrl = "https://API.example.test/v2";

        Assert.Equal(Key, _h.Service.BuildRuntimeConfig(sameHost, OpenAi).ApiKey);
    }

    [Fact]
    public void SaveProfile_WithNewHost_DeletesStoredKey()
    {
        var moved = SavedWithKey().Clone();
        moved.BaseUrl = "https://other.example.test/v1";

        _h.Service.SaveProfile(moved, makeActive: false);

        var stored = Stored(moved.Id);
        Assert.False(_h.Service.HasApiKey(moved.Id));
        Assert.Null(stored.KeyBoundHost);
        var ex = Assert.Throws<LlmException>(() => _h.Service.BuildRuntimeConfig(stored, OpenAi));
        Assert.Contains("требуется ключ API", ex.Message);
        Assert.Null(_h.Service.BuildRuntimeConfig(stored, Compatible).ApiKey);
        Assert.Contains(_h.Logger.Entries, e => e.Event == "settings.key_unbound");
    }

    [Fact]
    public void SaveProfile_WithSameHost_KeepsStoredKey()
    {
        var renamed = SavedWithKey().Clone();
        renamed.Name = "Переименованный профиль";
        renamed.BaseUrl = "https://api.example.test/v1/";

        _h.Service.SaveProfile(renamed, makeActive: false);

        Assert.True(_h.Service.HasApiKey(renamed.Id));
        Assert.Equal("https://api.example.test", Stored(renamed.Id).KeyBoundHost);
        Assert.Equal("Переименованный профиль", Stored(renamed.Id).Name);
    }

    [Fact]
    public void SaveProfile_ModelChange_ResetsVerifiedCapabilities()
    {
        var stored = SavedWithKey();
        _h.Service.SaveCapabilities(stored.Id, new CapabilityState { JsonSchema = true, VerifiedModelId = "test-model", VerifiedAtUtc = new DateTime(2026, 9, 24) });
        Assert.True(Stored(stored.Id).Capabilities.JsonSchema);

        var changed = Stored(stored.Id).Clone();
        changed.ModelId = "other-model";
        _h.Service.SaveProfile(changed, makeActive: false);

        Assert.Null(Stored(stored.Id).Capabilities.JsonSchema);
        Assert.Null(Stored(stored.Id).Capabilities.VerifiedAtUtc);
    }

    [Fact]
    public void SetApiKey_Blank_DeletesKeyAndBinding()
    {
        var stored = SavedWithKey();

        _h.Service.SetApiKey(stored.Id, "  ");

        Assert.False(_h.Service.HasApiKey(stored.Id));
        Assert.Null(Stored(stored.Id).KeyBoundHost);
    }

    [Fact]
    public void SetApiKey_ForUnsavedProfile_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => _h.Service.SetApiKey(Guid.NewGuid(), Key));
    }

    [Fact]
    public void KeysAreStoredPerProfile_AndDeleteProfileRemovesOnlyItsSecrets()
    {
        var first = SavedWithKey();
        var second = NewProfile("https://second.example.test/v1");
        _h.Service.SaveProfile(second, makeActive: false);
        _h.Service.SetApiKey(second.Id, "sk-test-UNIT-second-9876543210");
        _h.Service.SetSecretHeader(first.Id, "X-Secret-Token", "header-secret-value");

        Assert.Equal("sk-test-UNIT-second-9876543210", _h.Service.BuildRuntimeConfig(Stored(second.Id), OpenAi).ApiKey);
        Assert.Equal(Key, _h.Service.BuildRuntimeConfig(Stored(first.Id), OpenAi).ApiKey);

        _h.Service.DeleteProfile(first.Id);

        Assert.Empty(_h.Service.Secrets.ListKeys($"llm/{first.Id:N}/"));
        Assert.True(_h.Service.HasApiKey(second.Id));
        Assert.DoesNotContain(_h.Service.Current.LlmProfiles, p => p.Id == first.Id);
        Assert.Equal(second.Id, _h.Service.Current.ActiveLlmProfileId);
    }

    [Fact]
    public void BuildRuntimeConfig_ResolvesSecretAndPlainHeaders()
    {
        var profile = NewProfile();
        profile.ExtraHeaders.Add(new HeaderSetting { Name = "X-Team", Value = "blue" });
        profile.ExtraHeaders.Add(new HeaderSetting { Name = "X-Secret-Token", IsSecret = true });
        _h.Service.SaveProfile(profile, makeActive: true);
        _h.Service.SetApiKey(profile.Id, Key);
        _h.Service.SetSecretHeader(profile.Id, "X-Secret-Token", "header-secret-value");

        var config = _h.Service.BuildRuntimeConfig(Stored(profile.Id), OpenAi);

        Assert.Equal("blue", config.Headers["X-Team"]);
        Assert.Equal("header-secret-value", config.Headers["x-secret-token"]);
        Assert.DoesNotContain("header-secret-value", _h.SettingsJson);
    }

    [Fact]
    public void BuildRuntimeConfig_WithoutProfile_IsConfigurationError()
    {
        Assert.Equal(LlmErrorKind.Configuration, Assert.Throws<LlmException>(() => _h.Service.BuildRuntimeConfig(null, OpenAi)).Kind);
    }

    [Theory]
    [InlineData("ftp://api.example.test")]
    [InlineData("https://user:pass@api.example.test")]
    [InlineData("")]
    public void ValidateProfile_RejectsInvalidBaseUrl(string url)
    {
        var profile = NewProfile(url);

        Assert.Throws<ArgumentException>(() => SettingsService.ValidateProfile(profile));
    }

    [Fact]
    public void ValidateProfile_RequiresAuthorizationHeaderToBeSecret()
    {
        var profile = NewProfile();
        profile.ExtraHeaders.Add(new HeaderSetting { Name = "Authorization", Value = "Bearer plain" });

        var ex = Assert.Throws<ArgumentException>(() => _h.Service.SaveProfile(profile, makeActive: true));
        Assert.Contains("секретный", ex.Message);
        Assert.Empty(_h.Service.Current.LlmProfiles);
    }
}

public sealed class SettingsServiceDatabaseTests : IDisposable
{
    private const string Password = "P@ssw0rd-синтетика-2026";

    private readonly SettingsHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static DatabaseSettings Copy(DatabaseSettings settings) => JsonConvert.DeserializeObject<DatabaseSettings>(JsonConvert.SerializeObject(settings));

    private DatabaseSettings SaveSqlLogin()
    {
        _h.Service.SaveDatabase(new DatabaseSettings { Server = "SQL01", Database = "FaktDb", Authentication = SqlAuthMode.Sql, UserName = "Fakt_App" }, Password);
        return Copy(_h.Service.Current.Database);
    }

    [Fact]
    public void SqlPassword_IsStoredSecretlyAndBoundToServerAndLogin()
    {
        var saved = SaveSqlLogin();

        Assert.Equal("sql01|fakt_app", saved.PasswordBoundTo);
        Assert.True(saved.PasswordMatchesTarget);
        Assert.Equal(Password, _h.Service.SqlPassword(saved));
        Assert.True(_h.Service.Secrets.Exists(SecretKeys.SqlPassword));
        Assert.DoesNotContain(Password, _h.SettingsJson);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("login")]
    [InlineData("instance")]
    [InlineData("port")]
    public void SqlPassword_IsNotReturnedForAnotherServerOrLogin(string change)
    {
        var target = SaveSqlLogin();
        switch (change)
        {
            case "server":
                target.Server = "SQL02";
                break;
            case "login":
                target.UserName = "sa";
                break;
            case "instance":
                target.Instance = "REPORTS";
                break;
            default:
                target.Port = 14330;
                break;
        }

        Assert.False(target.PasswordMatchesTarget);
        Assert.Null(_h.Service.SqlPassword(target));
    }

    [Fact]
    public void SqlPassword_IgnoresCaseAndWhitespaceOfServerAndLogin()
    {
        var target = SaveSqlLogin();
        target.Server = "  sql01 ";
        target.UserName = "FAKT_APP";
        target.Database = "OtherDb";

        Assert.Equal(Password, _h.Service.SqlPassword(target));
    }

    [Fact]
    public void SaveDatabase_ChangedServerWithoutNewPassword_DeletesStoredPassword()
    {
        var moved = SaveSqlLogin();
        moved.Server = "SQL02";

        _h.Service.SaveDatabase(moved, null);

        Assert.Null(_h.Service.Current.Database.PasswordBoundTo);
        Assert.False(_h.Service.Secrets.Exists(SecretKeys.SqlPassword));
        var back = Copy(_h.Service.Current.Database);
        back.Server = "SQL01";
        Assert.Null(_h.Service.SqlPassword(back));
    }

    [Fact]
    public void SaveDatabase_SameTargetWithoutNewPassword_KeepsPassword()
    {
        var same = SaveSqlLogin();
        same.Database = "FaktArchive";
        same.CommandTimeoutSeconds = 300;

        _h.Service.SaveDatabase(same, null);

        Assert.Equal("sql01|fakt_app", _h.Service.Current.Database.PasswordBoundTo);
        Assert.Equal(Password, _h.Service.SqlPassword(_h.Service.Current.Database));
    }

    [Fact]
    public void SaveDatabase_NewPasswordForNewTarget_RebindsPassword()
    {
        var moved = SaveSqlLogin();
        moved.Server = "SQL02";

        _h.Service.SaveDatabase(moved, "new-синтетический-пароль");

        Assert.Equal("sql02|fakt_app", _h.Service.Current.Database.PasswordBoundTo);
        Assert.Equal("new-синтетический-пароль", _h.Service.SqlPassword(_h.Service.Current.Database));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ignored-password")]
    public void WindowsAuthentication_DeletesSavedPassword(string passwordOrNull)
    {
        var windows = SaveSqlLogin();
        windows.Authentication = SqlAuthMode.Windows;

        _h.Service.SaveDatabase(windows, passwordOrNull);

        Assert.False(_h.Service.Secrets.Exists(SecretKeys.SqlPassword));
        Assert.Null(_h.Service.Current.Database.PasswordBoundTo);
        Assert.Null(_h.Service.SqlPassword(_h.Service.Current.Database));
        var sqlAgain = Copy(_h.Service.Current.Database);
        sqlAgain.Authentication = SqlAuthMode.Sql;
        Assert.Null(_h.Service.SqlPassword(sqlAgain));
    }

    [Fact]
    public void CredentialTarget_NormalizesAddressAndLogin()
    {
        var settings = new DatabaseSettings { Server = " SQL01.corp.test ", Instance = " Inst ", Port = 1433, UserName = " Fakt_App " };

        Assert.Equal("sql01.corp.test\\inst,1433|fakt_app", settings.CredentialTarget());
        settings.Port = 0;
        settings.Instance = null;
        Assert.Equal("sql01.corp.test|fakt_app", settings.CredentialTarget());
        Assert.False(settings.PasswordMatchesTarget);
    }
}

public sealed class SettingsServiceAuthorizationTests
{
    [Theory]
    [InlineData("SaveProfile", Permission.ManageSettings)]
    [InlineData("SetActiveProfile", Permission.ManageSettings)]
    [InlineData("DeleteProfile", Permission.ManageSettings)]
    [InlineData("SaveCapabilities", Permission.ManageSettings)]
    [InlineData("SetApiKey", Permission.ManageSettings)]
    [InlineData("SetSecretHeader", Permission.ManageSettings)]
    [InlineData("SaveDatabase", Permission.ManageSettings)]
    [InlineData("SaveProcessing", Permission.ManageSettings)]
    [InlineData("SaveAccess", Permission.ManageAccess)]
    [InlineData("SetVerboseDiagnostics", Permission.ManageDiagnostics)]
    [InlineData("SaveDiagnostics", Permission.ManageDiagnostics)]
    public void Operator_CannotChangeSettings(string operation, Permission expected)
    {
        using var h = new SettingsHarness(FakeIdentityProvider.Operator());
        var before = h.SettingsJson;
        var service = h.Service;
        var id = Guid.NewGuid();

        Action act = operation switch
        {
            "SaveProfile" => () => service.SaveProfile(new LlmProfile { Name = "x", ProviderId = "openai", BaseUrl = "https://api.example.test" }, true),
            "SetActiveProfile" => () => service.SetActiveProfile(id),
            "DeleteProfile" => () => service.DeleteProfile(id),
            "SaveCapabilities" => () => service.SaveCapabilities(id, new CapabilityState()),
            "SetApiKey" => () => service.SetApiKey(id, "sk-test-UNIT-denied-0123456789"),
            "SetSecretHeader" => () => service.SetSecretHeader(id, "X-Token", "value"),
            "SaveDatabase" => () => service.SaveDatabase(new DatabaseSettings { Server = "SQL01", Authentication = SqlAuthMode.Sql, UserName = "u" }, "p"),
            "SaveProcessing" => () => service.SaveProcessing(new ProcessingSettings()),
            "SaveAccess" => () => service.SaveAccess(new PrincipalEntry[0], new PrincipalEntry[0], SecretScope.CurrentUser),
            "SetVerboseDiagnostics" => () => service.SetVerboseDiagnostics(TimeSpan.FromHours(1)),
            _ => () => service.SaveDiagnostics(5, 1),
        };

        var ex = Assert.Throws<AccessDeniedException>(act);

        Assert.Equal(expected, ex.Permission);
        Assert.Equal(before, h.SettingsJson);
        Assert.False(Directory.Exists(h.Paths.UserSecretsDirectory) && Directory.EnumerateFiles(h.Paths.UserSecretsDirectory).Any());
    }

    [Fact]
    public void Administrator_CanChangeSettings()
    {
        using var h = new SettingsHarness(FakeIdentityProvider.Admin());

        h.Service.SaveProcessing(new ProcessingSettings { ChunkSize = 2000 });
        h.Service.SetVerboseDiagnostics(TimeSpan.FromHours(1));

        Assert.Equal(2000, h.Service.Current.Processing.ChunkSize);
        Assert.Equal("TESTDOM\\admin", h.Service.Current.UpdatedBy);
        Assert.Equal("TESTDOM\\admin", h.Service.Current.Diagnostics.VerboseEnabledBy);
    }

    [Fact]
    public void ServiceWithoutAuthorization_RefusesChanges()
    {
        var service = new SettingsService(new InMemorySettingsStore(), new DpapiSecretStoreFactory("unused-user", "unused-machine"), FakeIdentityProvider.Admin(), null);

        Assert.Throws<InvalidOperationException>(() => service.SaveProcessing(new ProcessingSettings()));
    }

    [Fact]
    public void InitializeOwner_WorksOnceAndAppliesAcl()
    {
        var store = new InMemorySettingsStore();
        var identity = FakeIdentityProvider.Admin();
        var service = new SettingsService(store, new DpapiSecretStoreFactory("unused-user", "unused-machine"), identity, null);
        service.Authorization = new AuthorizationService(identity, () => service.Current.Access);
        Assert.Equal(Role.None, service.Authorization.CurrentRole);

        service.InitializeOwner(
            new[] { new PrincipalEntry { Sid = FakeIdentityProvider.AdminGroupSid }, new PrincipalEntry { Sid = "" } },
            new[] { new PrincipalEntry { Sid = FakeIdentityProvider.OperatorSid } },
            SecretScope.LocalMachine);

        var access = service.Current.Access;
        Assert.Equal(FakeIdentityProvider.AdminSid, access.OwnerSid);
        Assert.Single(access.Administrators);
        Assert.Single(access.Operators);
        Assert.Equal(SecretScope.LocalMachine, access.SecretScope);
        Assert.Equal(1, store.AclApplications);
        Assert.Equal(Role.Administrator, service.Authorization.CurrentRole);
        Assert.Throws<InvalidOperationException>(() => service.InitializeOwner(null, null, SecretScope.CurrentUser));
    }
}

public sealed class AuthorizationServiceTests
{
    private static AccessSettings Access() => new()
    {
        OwnerSid = FakeIdentityProvider.AdminSid,
        Administrators = { new PrincipalEntry { Sid = FakeIdentityProvider.AdminGroupSid, IsGroup = true } },
        Operators = { new PrincipalEntry { Sid = FakeIdentityProvider.OperatorSid }, new PrincipalEntry { Sid = FakeIdentityProvider.OperatorGroupSid, IsGroup = true } },
    };

    public static TheoryData<string, Role> Identities => new()
    {
        { "owner", Role.Administrator },
        { "admin-group-member", Role.Administrator },
        { "operator", Role.Operator },
        { "operator-group-member", Role.Operator },
        { "outsider", Role.None },
    };

    private static FakeIdentityProvider Identity(string who) => who switch
    {
        "owner" => FakeIdentityProvider.Admin(),
        "admin-group-member" => new FakeIdentityProvider("TESTDOM\\deputy", "S-1-5-21-1111111111-2222222222-3333333333-1010", FakeIdentityProvider.AdminGroupSid),
        "operator" => FakeIdentityProvider.Operator(),
        "operator-group-member" => new FakeIdentityProvider("TESTDOM\\analyst", "S-1-5-21-1111111111-2222222222-3333333333-1011", FakeIdentityProvider.OperatorGroupSid),
        _ => FakeIdentityProvider.Outsider(),
    };

    [Theory]
    [MemberData(nameof(Identities))]
    public void Role_IsResolvedBySidAndGroupMembership(string who, Role expected)
    {
        var authorization = new AuthorizationService(Identity(who), Access);

        Assert.Equal(expected, authorization.CurrentRole);
    }

    [Fact]
    public void UninitializedAccess_GivesNoRole()
    {
        var authorization = new AuthorizationService(FakeIdentityProvider.Admin(), () => new AccessSettings());

        Assert.Equal(Role.None, authorization.CurrentRole);
        Assert.Throws<AccessDeniedException>(() => authorization.Demand(Permission.SearchData));
    }

    [Theory]
    [InlineData(Permission.ProcessData, true)]
    [InlineData(Permission.SearchData, true)]
    [InlineData(Permission.ViewHistory, true)]
    [InlineData(Permission.ViewLog, true)]
    [InlineData(Permission.ManageSettings, false)]
    [InlineData(Permission.ManageDatabase, false)]
    [InlineData(Permission.ManageAccess, false)]
    [InlineData(Permission.ManageDiagnostics, false)]
    public void PermissionMatrix_ByRole(Permission permission, bool operatorAllowed)
    {
        Assert.True(RolePermissions.Allows(Role.Administrator, permission));
        Assert.Equal(operatorAllowed, RolePermissions.Allows(Role.Operator, permission));
        Assert.False(RolePermissions.Allows(Role.None, permission));

        var operatorAuthorization = new AuthorizationService(FakeIdentityProvider.Operator(), Access);
        if (operatorAllowed)
        {
            operatorAuthorization.Demand(permission);
        }
        else
        {
            var ex = Assert.Throws<AccessDeniedException>(() => operatorAuthorization.Demand(permission));
            Assert.Equal(permission, ex.Permission);
            Assert.Contains("TESTDOM\\operator", ex.Message);
        }
    }

    [Fact]
    public void RoleChanges_AreSeenImmediately()
    {
        var access = Access();
        var authorization = new AuthorizationService(FakeIdentityProvider.Outsider(), () => access);
        Assert.Equal(Role.None, authorization.CurrentRole);

        access.Operators.Add(new PrincipalEntry { Sid = FakeIdentityProvider.OutsiderSid });

        Assert.Equal(Role.Operator, authorization.CurrentRole);
    }
}
