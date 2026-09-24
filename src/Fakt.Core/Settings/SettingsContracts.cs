using System.Collections.Generic;

namespace Fakt.Core.Settings;

public interface ISettingsStore
{
    /// <summary>Каталог общих настроек (по умолчанию %ProgramData%\FAKT).</summary>
    string Directory { get; }

    AppSettings Load();

    void Save(AppSettings settings);

    /// <summary>Ограничить доступ к каталогу настроек владельцем, администраторами и операторами (ACL NTFS).</summary>
    void ApplyAccessControl(AccessSettings access);
}

/// <summary>Защищённое хранилище секретов (DPAPI). Значения не попадают в конфигурацию, журнал и worker.</summary>
public interface ISecretStore
{
    SecretScope Scope { get; }

    void Set(string key, string value);

    /// <summary>Возвращает null, если секрет не сохранён или не может быть расшифрован этой учётной записью.</summary>
    string Get(string key);

    bool Exists(string key);

    void Delete(string key);

    IReadOnlyList<string> ListKeys(string prefix);
}

public interface ISecretStoreFactory
{
    ISecretStore Create(SecretScope scope);
}
