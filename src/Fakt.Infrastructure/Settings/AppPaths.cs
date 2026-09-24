using System;
using System.IO;

namespace Fakt.Infrastructure.Settings;

/// <summary>
/// Расположение данных приложения. Переменная окружения FAKT_CONFIG_DIR (или аргумент --config-dir)
/// переопределяет общий каталог — для тестов и переносного режима.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string machineDirectory = null, string userDirectory = null)
    {
        MachineDirectory = machineDirectory ??
                           Environment.GetEnvironmentVariable("FAKT_CONFIG_DIR") ??
                           Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FAKT");
        UserDirectory = userDirectory ??
                        Environment.GetEnvironmentVariable("FAKT_USER_DIR") ??
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FAKT");
    }

    /// <summary>Общие настройки компьютера: %ProgramData%\FAKT.</summary>
    public string MachineDirectory { get; }

    /// <summary>Данные пользователя: %LOCALAPPDATA%\FAKT (журнал, секреты CurrentUser, предпочтения окна).</summary>
    public string UserDirectory { get; }

    public string SettingsFile => Path.Combine(MachineDirectory, "settings.json");

    public string MachineSecretsDirectory => Path.Combine(MachineDirectory, "secrets");

    public string UserSecretsDirectory => Path.Combine(UserDirectory, "secrets");

    public string LogDirectory => Path.Combine(UserDirectory, "logs");

    public string UserPreferencesFile => Path.Combine(UserDirectory, "preferences.json");

    public static string ApplicationDirectory => AppDomain.CurrentDomain.BaseDirectory;
}
