using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Fakt.Core.Settings;
using Newtonsoft.Json;

namespace Fakt.Infrastructure.Secrets;

/// <summary>
/// Хранилище секретов на DPAPI (ProtectedData) — поддерживается Windows 7.
/// CurrentUser: расшифровать может только эта учётная запись Windows (%LOCALAPPDATA%\FAKT\secrets).
/// LocalMachine: расшифровать может любой процесс компьютера, поэтому доступ к файлам ограничивается ACL
/// каталога %ProgramData%\FAKT\secrets (владелец, администраторы и операторы FAKT).
/// Каждый секрет — отдельный файл (имя — SHA-256 ключа), зашифрованный с дополнительной энтропией, привязанной к ключу.
/// Имена ключей несекретны и перечислены в keys.json для просмотра состава хранилища.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] EntropyPrefix = Encoding.UTF8.GetBytes("FAKT.secrets.v1:");
    private readonly string _directory;
    private readonly DataProtectionScope _scope;
    private readonly object _gate = new();

    public DpapiSecretStore(SecretScope scope, string directory)
    {
        Scope = scope;
        _scope = scope == SecretScope.LocalMachine ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public SecretScope Scope { get; }

    public string Directory => _directory;

    private string IndexPath => Path.Combine(_directory, "keys.json");

    public void Set(string key, string value)
    {
        ValidateKey(key);
        if (value == null)
        {
            Delete(key);
            return;
        }

        var payload = Encoding.UTF8.GetBytes(value);
        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(payload, Entropy(key), _scope);
        }
        finally
        {
            Array.Clear(payload, 0, payload.Length);
        }

        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(_directory);
            WriteAtomic(PathFor(key), protectedBytes);
            var keys = ReadIndex();
            if (!keys.Contains(key))
            {
                keys.Add(key);
                WriteIndex(keys);
            }
        }
    }

    public string Get(string key)
    {
        ValidateKey(key);
        lock (_gate)
        {
            var path = PathFor(key);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy(key), _scope);
                try
                {
                    return Encoding.UTF8.GetString(bytes);
                }
                finally
                {
                    Array.Clear(bytes, 0, bytes.Length);
                }
            }
            catch (CryptographicException)
            {
                // Секрет сохранён другой учётной записью Windows или на другом компьютере.
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public bool Exists(string key)
    {
        ValidateKey(key);
        return File.Exists(PathFor(key));
    }

    public void Delete(string key)
    {
        ValidateKey(key);
        lock (_gate)
        {
            var path = PathFor(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var keys = ReadIndex();
            if (keys.Remove(key))
            {
                WriteIndex(keys);
            }
        }
    }

    public IReadOnlyList<string> ListKeys(string prefix)
    {
        lock (_gate)
        {
            return ReadIndex()
                .Where(k => prefix == null || k.StartsWith(prefix, StringComparison.Ordinal))
                .Where(k => File.Exists(PathFor(k)))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();
        }
    }

    private List<string> ReadIndex()
    {
        try
        {
            return File.Exists(IndexPath)
                ? JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(IndexPath, Encoding.UTF8)) ?? new List<string>()
                : new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    private void WriteIndex(List<string> keys)
    {
        WriteAtomic(IndexPath, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(keys.Distinct().OrderBy(k => k, StringComparer.Ordinal), Formatting.Indented)));
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private string PathFor(string key)
    {
        using var sha = SHA256.Create();
        var name = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", string.Empty).ToLowerInvariant();
        return Path.Combine(_directory, name + ".bin");
    }

    private static byte[] Entropy(string key) => EntropyPrefix.Concat(Encoding.UTF8.GetBytes(key)).ToArray();

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512)
        {
            throw new ArgumentException("Недопустимый ключ секрета.", nameof(key));
        }
    }
}

public sealed class DpapiSecretStoreFactory : ISecretStoreFactory
{
    private readonly string _userDirectory;
    private readonly string _machineDirectory;

    public DpapiSecretStoreFactory(string userDirectory, string machineDirectory)
    {
        _userDirectory = userDirectory;
        _machineDirectory = machineDirectory;
    }

    public ISecretStore Create(SecretScope scope) =>
        new DpapiSecretStore(scope, scope == SecretScope.LocalMachine ? _machineDirectory : _userDirectory);
}
