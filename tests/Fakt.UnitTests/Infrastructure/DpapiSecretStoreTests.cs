using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Fakt.Core.Settings;
using Fakt.Infrastructure.Secrets;
using Fakt.UnitTests.TestSupport;
using Xunit;

namespace Fakt.UnitTests.Infrastructure;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private const string Secret = "sk-test-UNIT-dpapi-Секрет-0123456789";

    private readonly TempDirectory _temp = new();
    private readonly DpapiSecretStore _store;

    public DpapiSecretStoreTests()
    {
        _store = new DpapiSecretStore(SecretScope.CurrentUser, _temp.Combine("secrets"));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Имя файла секрета — SHA-256 ключа (как в хранилище), чтобы тест мог подменить файлы.</summary>
    private string FileFor(string key)
    {
        using var sha = SHA256.Create();
        var name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", string.Empty).ToLowerInvariant();
        return Path.Combine(_store.Directory, name + ".bin");
    }

    [Fact]
    public void Secret_RoundTrips()
    {
        _store.Set("llm/aaa/api-key", Secret);

        Assert.True(_store.Exists("llm/aaa/api-key"));
        Assert.Equal(Secret, _store.Get("llm/aaa/api-key"));
        Assert.Equal(SecretScope.CurrentUser, _store.Scope);
    }

    [Fact]
    public void Files_DoNotContainPlaintextOrKeyNamesAsFileNames()
    {
        _store.Set("llm/aaa/api-key", Secret);

        var files = Directory.GetFiles(_store.Directory);
        Assert.Contains(FileFor("llm/aaa/api-key"), files);
        foreach (var file in files)
        {
            Assert.DoesNotContain("api-key", Path.GetFileName(file));
            var bytes = File.ReadAllBytes(file);
            Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(Secret)), file);
            Assert.False(Contains(bytes, Encoding.Unicode.GetBytes(Secret)), file);
        }

        // Имена ключей несекретны и перечислены в keys.json; значения туда не попадают.
        var index = File.ReadAllText(Path.Combine(_store.Directory, "keys.json"));
        Assert.Contains("llm/aaa/api-key", index);
        Assert.DoesNotContain(Secret, index);
    }

    [Fact]
    public void BlobCopiedToAnotherKey_DoesNotDecrypt()
    {
        _store.Set("llm/aaa/api-key", Secret);
        Directory.CreateDirectory(_store.Directory);
        File.Copy(FileFor("llm/aaa/api-key"), FileFor("llm/bbb/api-key"));

        Assert.True(_store.Exists("llm/bbb/api-key"));
        Assert.Null(_store.Get("llm/bbb/api-key"));
        Assert.Equal(Secret, _store.Get("llm/aaa/api-key"));
    }

    [Fact]
    public void TamperedBlob_ReturnsNull()
    {
        _store.Set("db/sql-password", Secret);
        var path = FileFor("db/sql-password");
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length - 1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Null(_store.Get("db/sql-password"));
    }

    [Fact]
    public void Overwrite_KeepsSingleKeyWithLatestValue()
    {
        _store.Set("llm/aaa/api-key", "first-value");
        _store.Set("llm/aaa/api-key", "second-value");

        Assert.Equal("second-value", _store.Get("llm/aaa/api-key"));
        Assert.Equal(new[] { "llm/aaa/api-key" }, _store.ListKeys(null));
    }

    [Fact]
    public void ListKeys_FiltersByPrefixAndSorts()
    {
        _store.Set("llm/bbb/api-key", "b");
        _store.Set("llm/aaa/header/x-token", "h");
        _store.Set("llm/aaa/api-key", "a");
        _store.Set("db/sql-password", "p");

        Assert.Equal(new[] { "llm/aaa/api-key", "llm/aaa/header/x-token" }, _store.ListKeys("llm/aaa/"));
        Assert.Equal(new[] { "db/sql-password", "llm/aaa/api-key", "llm/aaa/header/x-token", "llm/bbb/api-key" }, _store.ListKeys(null));
        Assert.Empty(_store.ListKeys("llm/ccc/"));
    }

    [Fact]
    public void Delete_RemovesFileAndIndexEntry()
    {
        _store.Set("llm/aaa/api-key", Secret);
        _store.Set("llm/bbb/api-key", "other");

        _store.Delete("llm/aaa/api-key");
        _store.Delete("llm/never-set/api-key");

        Assert.False(_store.Exists("llm/aaa/api-key"));
        Assert.Null(_store.Get("llm/aaa/api-key"));
        Assert.False(File.Exists(FileFor("llm/aaa/api-key")));
        Assert.Equal(new[] { "llm/bbb/api-key" }, _store.ListKeys(null));
    }

    [Fact]
    public void SetNull_DeletesSecret()
    {
        _store.Set("llm/aaa/api-key", Secret);

        _store.Set("llm/aaa/api-key", null);

        Assert.False(_store.Exists("llm/aaa/api-key"));
    }

    [Fact]
    public void MissingSecret_IsNull()
    {
        Assert.Null(_store.Get("llm/none/api-key"));
        Assert.False(_store.Exists("llm/none/api-key"));
        Assert.Empty(_store.ListKeys(null));
    }

    [Fact]
    public void KeyIndexWithoutFile_IsNotListed()
    {
        _store.Set("llm/aaa/api-key", Secret);
        File.Delete(FileFor("llm/aaa/api-key"));

        Assert.Empty(_store.ListKeys(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void InvalidKey_IsRejected(string key)
    {
        Assert.Throws<ArgumentException>(() => _store.Set(key, "x"));
        Assert.Throws<ArgumentException>(() => _store.Get(key));
    }

    [Fact]
    public void TooLongKey_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => _store.Set(new string('k', 513), "x"));
    }

    [Fact]
    public void SecretsFromOtherDirectory_AreNotVisible()
    {
        _store.Set("llm/aaa/api-key", Secret);
        var other = new DpapiSecretStore(SecretScope.CurrentUser, _temp.Combine("other-secrets"));

        Assert.Null(other.Get("llm/aaa/api-key"));
        Assert.Empty(other.ListKeys(null));
    }

    [Fact]
    public void LocalMachineScope_RoundTrips()
    {
        var machine = new DpapiSecretStore(SecretScope.LocalMachine, _temp.Combine("machine-secrets"));

        machine.Set("db/sql-password", Secret);

        Assert.Equal(Secret, machine.Get("db/sql-password"));
        Assert.Equal(SecretScope.LocalMachine, machine.Scope);
    }

    [Fact]
    public void Factory_UsesDirectoryOfScope()
    {
        var factory = new DpapiSecretStoreFactory(_temp.Combine("user"), _temp.Combine("machine"));

        Assert.Equal(_temp.Combine("user"), ((DpapiSecretStore)factory.Create(SecretScope.CurrentUser)).Directory);
        Assert.Equal(_temp.Combine("machine"), ((DpapiSecretStore)factory.Create(SecretScope.LocalMachine)).Directory);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Skip(i).Take(needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
