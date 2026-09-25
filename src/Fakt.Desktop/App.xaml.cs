using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Fakt.Core.Logging;
using Fakt.Core.Security;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Services;
using Fakt.Desktop.ViewModels;

namespace Fakt.Desktop;

public partial class App : System.Windows.Application
{
    public static AppServices Services { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("app.crash", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("app.unobserved_task", args.Exception);
            args.SetObserved();
        };

        string configDir = null;
        SnapshotOptions snapshots = null;
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            switch (e.Args[i])
            {
                case "--config-dir":
                    configDir = e.Args[i + 1];
                    break;
                case "--render-snapshots":
                    (snapshots ??= new SnapshotOptions()).OutputDirectory = e.Args[i + 1];
                    break;
                case "--snapshot-scan":
                    (snapshots ??= new SnapshotOptions()).ScanFolder = e.Args[i + 1];
                    break;
                case "--snapshot-search":
                    (snapshots ??= new SnapshotOptions()).SearchText = e.Args[i + 1];
                    break;
                case "--snapshot-db":
                    (snapshots ??= new SnapshotOptions()).Database = e.Args[i + 1];
                    break;
                case "--snapshot-process":
                    (snapshots ??= new SnapshotOptions()).ProcessFolder = e.Args[i + 1];
                    break;
            }
        }

        if (snapshots != null && (configDir == null || snapshots.OutputDirectory == null))
        {
            // Режим снимков меняет настройки (назначает владельца), поэтому только с отдельным каталогом настроек.
            Console.Error.WriteLine("--render-snapshots требует --config-dir с отдельным каталогом настроек.");
            Shutdown(2);
            return;
        }

        try
        {
            Services = AppServices.Create(configDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось запустить FAKT: " + ex.Message, "FAKT", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        Services.Logger.Info("app.started", $"FAKT {AppServices.Version} запущен", entry =>
        {
            entry.User = Services.Identity.UserName;
            entry.Data = new Dictionary<string, object>
            {
                ["os"] = Environment.OSVersion.VersionString,
                ["clr"] = Environment.Version.ToString(),
                ["tls"] = Services.TlsMode,
                ["x64"] = Environment.Is64BitProcess,
            };
        });

        if (snapshots != null)
        {
            int exitCode;
            try
            {
                exitCode = SnapshotRenderer.Run(Services, snapshots);
            }
            catch (Exception ex)
            {
                Log("app.snapshots", ex);
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }

            Shutdown(exitCode);
            return;
        }

        if (!Services.Settings.Current.Access.IsInitialized)
        {
            var setup = new OwnerSetupViewModel(Services);
            if (!Services.Dialogs.ShowDialog(setup))
            {
                Shutdown(0);
                return;
            }
        }

        if (Services.Authorization.CurrentRole == Role.None)
        {
            Services.Dialogs.Show("Нет доступа",
                $"Учётная запись {Services.Identity.UserName} не входит в роли FAKT («Администратор» или «Оператор»). Обратитесь к администратору приложения.",
                MessageKind.Error);
            Shutdown(0);
            return;
        }

        var window = new MainWindow { DataContext = new MainViewModel(Services) };
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services?.Logger.Info("app.exit", "FAKT завершён");
        Services?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("app.unhandled", e.Exception);
        e.Handled = true;
        Services?.Dialogs.Show("Непредвиденная ошибка",
            "Операция завершилась ошибкой. Подробности записаны в журнал (раздел «Журнал»).", MessageKind.Error, LogSanitizer.Sanitize(e.Exception.Message));
    }

    private static void Log(string eventName, Exception exception)
    {
        if (exception == null)
        {
            return;
        }

        Services?.Logger.Error(eventName, exception.Message, ErrorCategory.Internal, exception, entry =>
        {
            entry.Data ??= new Dictionary<string, object>();
            entry.Data["stack"] = exception.StackTrace;
        });
    }
}
