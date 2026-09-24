using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Fakt.Core.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Fakt.Infrastructure.Settings;

/// <summary>
/// Общие настройки в JSON без секретов. Запись атомарная (временный файл + замена) с резервной копией.
/// После назначения владельца каталог защищается ACL: изменение — владелец, администраторы FAKT,
/// SYSTEM и локальные администраторы; чтение — операторы FAKT.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        Formatting = Formatting.Indented,
        Converters = { new StringEnumConverter() },
        NullValueHandling = NullValueHandling.Include,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
    };

    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public JsonSettingsStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string Directory => _paths.MachineDirectory;

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_paths.SettingsFile))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_paths.SettingsFile, Encoding.UTF8);
            try
            {
                return JsonConvert.DeserializeObject<AppSettings>(json, SerializerSettings) ?? new AppSettings();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Файл настроек {_paths.SettingsFile} повреждён: {ex.Message}. Восстановите его из резервной копии settings.json.bak.", ex);
            }
        }
    }

    public void Save(AppSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(_paths.MachineDirectory);
            settings.UpdatedAtUtc = DateTime.UtcNow;
            var json = JsonConvert.SerializeObject(settings, SerializerSettings);
            var temp = _paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(_paths.SettingsFile))
            {
                File.Replace(temp, _paths.SettingsFile, _paths.SettingsFile + ".bak");
            }
            else
            {
                File.Move(temp, _paths.SettingsFile);
            }
        }
    }

    public void ApplyAccessControl(AccessSettings access)
    {
        if (access == null || !access.IsInitialized)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(_paths.MachineDirectory);
        System.IO.Directory.CreateDirectory(_paths.MachineSecretsDirectory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        void Grant(string sid, FileSystemRights rights)
        {
            if (string.IsNullOrEmpty(sid))
            {
                return;
            }

            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        Grant(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value, FileSystemRights.FullControl);
        Grant(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value, FileSystemRights.FullControl);
        Grant(access.OwnerSid, FileSystemRights.FullControl);
        foreach (var admin in access.Administrators)
        {
            Grant(admin.Sid, FileSystemRights.Modify);
        }

        foreach (var op in access.Operators)
        {
            Grant(op.Sid, FileSystemRights.ReadAndExecute);
        }

        System.IO.Directory.SetAccessControl(_paths.MachineDirectory, security);
    }
}
