using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fakt.Core.Settings;
using Fakt.Core.Storage;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Services;
using Fakt.Desktop.ViewModels;
using Fakt.Desktop.ViewModels.Admin;
using Fakt.Desktop.Views.Dialogs;
using Fakt.Infrastructure.Sql;

namespace Fakt.Desktop;

/// <summary>Параметры режима проверки интерфейса.</summary>
internal sealed class SnapshotOptions
{
    public string OutputDirectory { get; set; }

    /// <summary>Папка для демонстрации сканирования на странице «Обработка» (необязательно).</summary>
    public string ScanFolder { get; set; }

    /// <summary>Запрос для страницы «Поиск» и карточки первого результата (необязательно, нужна настроенная база).</summary>
    public string SearchText { get; set; }
}

/// <summary>
/// Режим проверки интерфейса: <c>FAKT.exe --config-dir DIR --render-snapshots OUT [--snapshot-scan FOLDER] [--snapshot-search TEXT]</c>.
/// Главное окно и диалоги отрисовываются вне экрана и сохраняются в PNG. Работает только с явно указанным каталогом
/// настроек, чтобы не менять рабочую конфигурацию; модель LLM при этом не вызывается.
/// </summary>
internal static class SnapshotRenderer
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    public static int Run(AppServices services, SnapshotOptions options)
    {
        var output = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(output);
        var log = new List<string>();

        if (!services.Settings.Current.Access.IsInitialized)
        {
            services.Settings.InitializeOwner(Array.Empty<PrincipalEntry>(), Array.Empty<PrincipalEntry>(), SecretScope.CurrentUser);
        }

        var main = new MainViewModel(services);
        var window = new MainWindow
        {
            DataContext = main,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Width = 1440,
            Height = 900,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        Pump();

        void Shot(string name)
        {
            Pump();
            Save(window, Path.Combine(output, name + ".png"));
            log.Add(name);
        }

        Shot("01-processing-empty");

        if (!string.IsNullOrWhiteSpace(options.ScanFolder))
        {
            main.Processing.RootPath = Path.GetFullPath(options.ScanFolder);
            Wait(main.Processing.ScanCommand.ExecuteAsync(null));
            main.Processing.CurrentFile = main.Processing.Files.FirstOrDefault();
            Shot("02-processing-scanned");
        }

        main.CurrentKey = PageKey.Search;
        Shot("03-search-empty");
        if (!string.IsNullOrWhiteSpace(options.SearchText) && services.Settings.Current.Database.IsConfigured)
        {
            main.Search.Text = options.SearchText;
            main.Search.SearchCommand.Execute(null);
            WaitUntil(() => !main.Search.IsSearching);
            Shot("04-search-results");
            var first = main.Search.Results.FirstOrDefault();
            if (first != null)
            {
                main.Search.OpenCardCommand.Execute(first);
                WaitUntil(() => main.Search.Card != null && !main.Search.Card.IsLoading);
                Shot("05-search-card");
                main.Search.CloseCardCommand.Execute(null);
            }
        }

        main.CurrentKey = PageKey.History;
        WaitUntil(() => true);
        Shot("06-history");

        main.CurrentKey = PageKey.Admin;
        var tabs = new[] { "07-admin-llm", "08-admin-database", "09-admin-processing", "10-admin-access" };
        for (var i = 0; i < tabs.Length; i++)
        {
            main.Admin.SelectedTab = i;
            if (i == 1 && services.Settings.Current.Database.IsConfigured)
            {
                Wait(main.Admin.Database.TestCommand.ExecuteAsync(null));
            }

            Shot(tabs[i]);
        }

        main.CurrentKey = PageKey.Log;
        Shot("11-log");

        main.CurrentKey = PageKey.Processing;
        window.Width = 1100;
        window.Height = 760;
        Shot("12-processing-compact");
        window.Close();

        Dialog(new HelpViewModel(services), Path.Combine(output, "20-dialog-help.png"));
        Dialog(new MessageDialogViewModel
        {
            Title = "Отключить проверку сертификата",
            Message = "Без проверки сертификата соединение уязвимо для подмены сервера. Используйте это только для тестового сервера.",
            Kind = MessageKind.Warning,
            PrimaryText = "Отключить проверку",
            SecondaryText = "Оставить проверку",
            IsDanger = true,
        }, Path.Combine(output, "21-dialog-confirm.png"));
        Dialog(new OwnerSetupViewModel(services), Path.Combine(output, "22-dialog-owner.png"));
        var script = MigrationCatalog.All.FirstOrDefault(m => m.AltersUserTables) ?? MigrationCatalog.All.First();
        var migration = new MigrationInfo
        {
            Id = script.Id,
            Title = script.Title,
            Description = script.Description,
            AltersUserTables = script.AltersUserTables,
            Script = new SqlNames(services.Settings.Current.Database).Render(script.Template),
        };
        Dialog(new ScriptViewModel(services, migration) { Title = $"Применить миграцию {migration.Id}?", PrimaryText = "Применить", SecondaryText = "Отмена", IsDanger = migration.AltersUserTables },
            Path.Combine(output, "23-dialog-migration.png"));
        var file = main.Processing.Files.FirstOrDefault(f => f.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
        if (file != null)
        {
            Dialog(new StructureEditorViewModel(file), Path.Combine(output, "24-dialog-structure.png"));
        }

        File.WriteAllLines(Path.Combine(output, "snapshots.txt"), log);
        return 0;
    }

    private static void Dialog(DialogViewModel viewModel, string path)
    {
        var window = new DialogWindow
        {
            DataContext = viewModel,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
        };
        window.Show();
        Pump();
        Save(window, path);
        window.Close();
    }

    /// <summary>Снимок клиентской области окна (корень шаблона окна вместе с фоном).</summary>
    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var root = VisualTreeHelper.GetChildrenCount(window) > 0 ? VisualTreeHelper.GetChild(window, 0) as FrameworkElement : null;
        var element = root ?? (FrameworkElement)window.Content;
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>Обработка очереди диспетчера: привязки, раскладка и отложенные обновления интерфейса.</summary>
    private static void Pump(int milliseconds = 400)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow + OperationTimeout;
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Pump(100);
        }

        Pump();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + OperationTimeout;
        Pump(200);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Pump(100);
        }

        Pump();
    }
}
