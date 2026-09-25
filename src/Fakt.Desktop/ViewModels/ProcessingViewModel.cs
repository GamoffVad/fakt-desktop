using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Fakt.Application.Llm;
using Fakt.Application.Processing;
using Fakt.Application.Structure;
using Fakt.Core.Files;
using Fakt.Core.Llm;
using Fakt.Core.Processing;
using Fakt.Core.Security;
using Fakt.Core.Structure;
using Fakt.Core.Worker;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Services;

namespace Fakt.Desktop.ViewModels;

/// <summary>
/// Страница «Обработка»: выбор папки, потоковое сканирование, анализ структуры по первым пяти строкам,
/// оценка запуска, задание извлечения с паузой, продолжением и остановкой. Операции выполняются вне
/// потока интерфейса; прогресс обновляется с ограниченной частотой.
/// </summary>
public sealed class ProcessingViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<string, FileItemViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStatusEvent> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingGate = new();
    private readonly DispatcherTimer _flushTimer;
    private string _rootPath;
    private bool _showOnlyTabular;
    private FileItemViewModel _currentFile;
    private string _scanStatus;
    private string _jobStatus;
    private string _banner;
    private bool _isScanning;
    private bool _isJobRunning;
    private bool _isPaused;
    private bool _isAnalyzing;
    private JobSession _session;
    private PreviewViewModel _preview;
    private string _structureMessages;
    private long _scanDirectories;
    private long _inaccessible;
    private long _skippedLinks;
    private bool _externalConfirmed;

    public ProcessingViewModel(AppServices services)
    {
        _services = services;
        Files = new ObservableCollection<FileItemViewModel>();
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = item => !ShowOnlyTabular || ((FileItemViewModel)item).Status == FileStatus.Tabular;
        Issues = new ObservableCollection<string>();
        Metrics = new JobMetricsViewModel();
        BrowseCommand = new RelayCommand(Browse, () => !IsBusy);
        ScanCommand = new AsyncCommand(ScanAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(RootPath), OnError);
        ProcessCommand = new AsyncCommand(ProcessAsync, () => !IsBusy && SelectedCount > 0, OnError);
        PauseCommand = new RelayCommand(Pause, () => IsJobRunning && !IsPaused);
        ResumeCommand = new AsyncCommand(ResumeAsync, () => _session != null && IsPaused && !IsJobRunning, OnError);
        StopCommand = new RelayCommand(Stop, () => IsJobRunning || IsAnalyzing || IsScanning || (IsPaused && _session != null));
        ToggleAllCommand = new RelayCommand(ToggleAll, () => !IsBusy);
        ExtendedSampleCommand = new AsyncCommand(ct => ReanalyzeAsync(CurrentFile, true, null, ct), () => !IsBusy && CurrentFile != null && CurrentFile.Status != FileStatus.NotSupported, OnError);
        ChangeEncodingCommand = new AsyncCommand((p, ct) => ReanalyzeAsync(CurrentFile, false, p as string, ct), _ => !IsBusy && CurrentFile != null, OnError);
        EditStructureCommand = new AsyncCommand(EditStructureAsync, () => !IsBusy && CurrentFile != null && CurrentFile.Status != FileStatus.NotSupported, OnError);
        ApplySuggestionCommand = new AsyncCommand(ApplySuggestionAsync, () => !IsBusy && CurrentFile?.Detection?.SuggestedColumns != null, OnError);
        CopyPathCommand = new RelayCommand(p => _services.Dialogs.CopyToClipboard((p as FileItemViewModel)?.FullPath ?? CurrentFile?.FullPath));
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _flushTimer.Tick += (_, _) => FlushEvents();
        _flushTimer.Start();
        UpdateBanner();
        _services.Settings.SettingsChanged += () => UiThread.Post(UpdateBanner);
    }

    public ObservableCollection<FileItemViewModel> Files { get; }
    public ICollectionView FilesView { get; }
    public ObservableCollection<string> Issues { get; }
    public JobMetricsViewModel Metrics { get; }

    public ICommand BrowseCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand ProcessCommand { get; }
    public ICommand PauseCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleAllCommand { get; }
    public AsyncCommand ExtendedSampleCommand { get; }
    public AsyncCommand ChangeEncodingCommand { get; }
    public AsyncCommand EditStructureCommand { get; }
    public AsyncCommand ApplySuggestionCommand { get; }
    public ICommand CopyPathCommand { get; }

    public IReadOnlyList<string> Encodings { get; } = EncodingNames.Supported.Where(e => e != "utf-8-sig").ToList();

    public string RootPath
    {
        get => _rootPath;
        set => SetProperty(ref _rootPath, value);
    }

    public bool ShowOnlyTabular
    {
        get => _showOnlyTabular;
        set
        {
            if (SetProperty(ref _showOnlyTabular, value))
            {
                FilesView.Refresh();
                UpdateCounters();
            }
        }
    }

    /// <summary>Строка, выбранная для предпросмотра (не путать с флажком включения в обработку).</summary>
    public FileItemViewModel CurrentFile
    {
        get => _currentFile;
        set
        {
            if (SetProperty(ref _currentFile, value))
            {
                UpdatePreview();
            }
        }
    }

    public PreviewViewModel Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public string StructureMessages
    {
        get => _structureMessages;
        private set => SetProperty(ref _structureMessages, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertiesChanged(nameof(IsBusy), nameof(EmptyStateText));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsJobRunning
    {
        get => _isJobRunning;
        private set
        {
            if (SetProperty(ref _isJobRunning, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsBusy => IsScanning || IsAnalyzing || IsJobRunning;

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetProperty(ref _scanStatus, value);
    }

    public string JobStatus
    {
        get => _jobStatus;
        private set => SetProperty(ref _jobStatus, value);
    }

    /// <summary>Предупреждение о передаче образцов и записей внешнему провайдеру.</summary>
    public string ProviderBanner
    {
        get => _banner;
        private set => SetProperty(ref _banner, value);
    }

    public bool ProviderIsExternal { get; private set; }

    public int FoundCount => Files.Count;
    public int TabularCount => Files.Count(f => f.Structure != null || f.Status == FileStatus.Completed || f.Status == FileStatus.CompletedWithErrors);
    public int SkippedCount => Files.Count(f => f.Status == FileStatus.NotSupported || f.Status == FileStatus.NotTabular);
    public int SelectedCount => Files.Count(f => f.IsSelected);
    public int SelectedHiddenCount => Files.Count(f => f.IsSelected && !FilesView.Filter(f));

    public string SelectionText
    {
        get
        {
            var text = $"Выбрано: {SelectedCount}";
            var hidden = SelectedHiddenCount;
            return hidden > 0 ? text + $" (скрыто фильтром: {hidden})" : text;
        }
    }

    /// <summary>Состояние общего флажка: true — все видимые доступные выбраны, false — ни один, null — частично.</summary>
    public bool? AllSelected
    {
        get
        {
            var visible = Files.Where(f => FilesView.Filter(f) && f.CanSelect).ToList();
            if (visible.Count == 0 || visible.All(f => !f.IsSelected))
            {
                return false;
            }

            return visible.All(f => f.IsSelected) ? true : (bool?)null;
        }
        set => ToggleAll(value == true);
    }

    public string EmptyStateText =>
        IsScanning ? null :
        string.IsNullOrWhiteSpace(RootPath) ? "Выберите папку с файлами для обработки" :
        Files.Count == 0 ? "В папке нет файлов. Проверьте путь и повторите сканирование (учитываются вложенные папки; скрытые и системные файлы по умолчанию пропускаются)." : null;

    private void OnError(Exception ex)
    {
        IsAnalyzing = false;
        var kind = ex is AccessDeniedException ? MessageKind.Warning : MessageKind.Error;
        _services.Dialogs.Show(ex is AccessDeniedException ? "Недостаточно прав" : "Ошибка", ex.Message, kind);
    }

    private void UpdateBanner()
    {
        var profile = _services.Settings.ActiveProfile;
        if (profile == null)
        {
            ProviderBanner = "Профиль LLM не настроен: определение структуры и извлечение недоступны. Настройте провайдера в «Администрирование → LLM».";
            ProviderIsExternal = false;
        }
        else
        {
            ProviderIsExternal = !UrlBuilder.IsLocalOrPrivate(profile.BaseUrl);
            var provider = _services.Provider(profile.ProviderId)?.DisplayName ?? profile.ProviderId;
            ProviderBanner = ProviderIsExternal
                ? $"Первые строки файлов и записи отправляются внешнему провайдеру {provider} ({UrlBuilder.HostOf(profile.BaseUrl)}), модель {profile.ModelId}."
                : $"Используется локальный/внутренний сервис {provider} ({UrlBuilder.HostOf(profile.BaseUrl)}), модель {profile.ModelId}.";
        }

        OnPropertyChanged(nameof(ProviderIsExternal));
    }

    private void Browse()
    {
        var path = _services.Dialogs.PickFolder("Выберите папку с файлами", RootPath);
        if (!string.IsNullOrEmpty(path))
        {
            RootPath = path;
            ScanCommand.Execute(null);
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        _services.Authorization.Demand(Permission.ProcessData);
        foreach (var file in Files)
        {
            file.SelectionChanged -= OnSelectionChanged;
        }

        Files.Clear();
        _byPath.Clear();
        Issues.Clear();
        CurrentFile = null;
        _scanDirectories = _inaccessible = _skippedLinks = 0;
        IsScanning = true;
        ScanStatus = "Сканирование…";
        var settings = _services.Settings.Current.Processing;
        try
        {
            var summary = await _services.Scanner.ScanAsync(new ScanOptions
                {
                    RootPath = RootPath,
                    Recursive = settings.RecursiveScan,
                    FollowReparsePoints = settings.FollowReparsePoints,
                    IncludeHidden = settings.IncludeHiddenFiles,
                },
                batch => UiThread.Post(() => AddFiles(batch)),
                issue => UiThread.Post(() => Issues.Add($"{issue.Path}: {issue.Message}")),
                new Progress<ScanProgress>(p => ScanStatus = $"Сканирование… найдено {p.FilesFound:N0}, папок {p.DirectoriesVisited:N0}"),
                cancellationToken);
            await Task.Yield();
            _scanDirectories = summary.DirectoriesVisited;
            _inaccessible = summary.InaccessibleDirectories;
            _skippedLinks = summary.SkippedReparsePoints;
            var parts = new List<string> { $"Найдено файлов: {summary.FilesFound:N0}", $"папок: {summary.DirectoriesVisited:N0}" };
            if (summary.InaccessibleDirectories > 0) parts.Add($"недоступных папок: {summary.InaccessibleDirectories}");
            if (summary.SkippedReparsePoints > 0) parts.Add($"пропущено ссылок/junction: {summary.SkippedReparsePoints}");
            if (summary.CyclesDetected > 0) parts.Add($"циклов: {summary.CyclesDetected}");
            if (summary.Cancelled) parts.Add("сканирование прервано");
            ScanStatus = string.Join(", ", parts) + $" за {summary.Elapsed.TotalSeconds:0.0} с";
        }
        finally
        {
            IsScanning = false;
            UpdateCounters();
        }
    }

    private void AddFiles(IReadOnlyList<ScannedFile> batch)
    {
        foreach (var file in batch)
        {
            if (_byPath.ContainsKey(file.FullPath))
            {
                continue;
            }

            var item = new FileItemViewModel(file);
            item.SelectionChanged += OnSelectionChanged;
            _byPath[file.FullPath] = item;
            Files.Add(item);
        }

        UpdateCounters();
    }

    private void OnSelectionChanged(FileItemViewModel item)
    {
        OnPropertiesChanged(nameof(SelectedCount), nameof(SelectedHiddenCount), nameof(SelectionText), nameof(AllSelected));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ToggleAll() => ToggleAll(AllSelected != true);

    private void ToggleAll(bool select)
    {
        foreach (var item in Files.Where(f => FilesView.Filter(f) && f.CanSelect))
        {
            item.IsSelected = select;
        }

        UpdateCounters();
    }

    private void UpdateCounters()
    {
        OnPropertiesChanged(nameof(FoundCount), nameof(TabularCount), nameof(SkippedCount), nameof(SelectedCount), nameof(SelectedHiddenCount),
            nameof(SelectionText), nameof(AllSelected), nameof(EmptyStateText));
    }

    private LlmRuntimeConfig RuntimeConfig(out LlmProfile profile)
    {
        profile = _services.Settings.ActiveProfile ?? throw new InvalidOperationException("Профиль LLM не настроен. Откройте «Администрирование → LLM».");
        return _services.Settings.BuildRuntimeConfig(profile, _services.Provider(profile.ProviderId));
    }

    /// <summary>Режим структурированного ответа: подтверждённый тестом; если не подтверждён — короткая проверка сейчас.</summary>
    private async Task<StructuredOutputMode> ResolveModeAsync(LlmRuntimeConfig runtime, LlmProfile profile, CancellationToken cancellationToken)
    {
        var mode = CapabilityResolver.Resolve(profile);
        if (mode.HasValue)
        {
            return mode.Value;
        }

        JobStatus = "Проверка возможностей модели (тестовый запрос на синтетических данных)…";
        var test = await _services.Profiles.TestExtractionAsync(runtime, cancellationToken);
        if (!test.ModeUsed.HasValue)
        {
            throw new InvalidOperationException("Модель не вернула проверяемый структурированный ответ: " + test.Message);
        }

        if (_services.Authorization.IsAllowed(Permission.ManageSettings))
        {
            _services.Settings.SaveCapabilities(profile.Id, test.Capabilities);
        }

        return test.ModeUsed.Value;
    }

    private bool ConfirmExternal(LlmProfile profile, int files)
    {
        if (!ProviderIsExternal || _externalConfirmed)
        {
            return true;
        }

        var provider = _services.Provider(profile.ProviderId)?.DisplayName ?? profile.ProviderId;
        _externalConfirmed = _services.Dialogs.Confirm("Передача данных внешнему провайдеру",
            $"Для анализа структуры первые строки {files} файл(ов), а затем записи выбранных файлов будут отправлены провайдеру {provider} ({UrlBuilder.HostOf(profile.BaseUrl)}). " +
            "Содержимое файлов может включать персональные данные. Продолжить?", "Отправить и продолжить");
        return _externalConfirmed;
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        _services.Authorization.Demand(Permission.ProcessData);
        var runtime = RuntimeConfig(out var profile);
        var selected = Files.Where(f => f.IsSelected).ToList();
        var toAnalyze = selected.Where(f => f.Structure == null && f.Status != FileStatus.NotSupported).ToList();
        if (!ConfirmExternal(profile, toAnalyze.Count))
        {
            return;
        }

        var threshold = _services.Settings.Current.Processing.ConfirmStructureRequestsAbove;
        if (toAnalyze.Count > threshold &&
            !_services.Dialogs.Confirm("Анализ структуры", $"Для определения структуры будет выполнено около {toAnalyze.Count} запросов к модели (по одному на файл). Продолжить?"))
        {
            return;
        }

        IsAnalyzing = true;
        try
        {
            var mode = await ResolveModeAsync(runtime, profile, cancellationToken);
            if (toAnalyze.Count > 0)
            {
                await AnalyzeAsync(toAnalyze, runtime, mode, cancellationToken);
            }

            var ready = Files.Where(f => f.IsSelected && f.Structure != null).ToList();
            if (ready.Count == 0)
            {
                JobStatus = "Нет выбранных файлов с подтверждённой табличной структурой. Проверьте файлы со статусом «Требует настройки».";
                return;
            }

            // Оценка по небольшому образцу перед запуском.
            var inputs = ready.Select(f => new EstimateInput { File = f.File, Detection = f.Detection }).ToList();
            var estimateVm = new EstimateViewModel(_services, inputs, profile, runtime, mode);
            IsAnalyzing = false;
            if (!_services.Dialogs.ShowDialog(estimateVm))
            {
                JobStatus = "Обработка не запущена.";
                return;
            }

            IsAnalyzing = true;
            JobStatus = "Подготовка задания…";
            var session = await _services.Processing.CreateJobAsync(ready.Select(f => new JobFileInput { File = f.File, Structure = f.Structure }).ToList(), cancellationToken);
            AttachSession(session);
            foreach (var file in ready)
            {
                file.Status = FileStatus.Queued;
                file.Message = null;
            }
        }
        finally
        {
            IsAnalyzing = false;
        }

        await RunSessionAsync();
    }

    private async Task AnalyzeAsync(List<FileItemViewModel> files, LlmRuntimeConfig runtime, StructuredOutputMode mode, CancellationToken cancellationToken)
    {
        var llm = new ResilientLlmClient(_services.Registry.Get(runtime.Profile.ProviderId), runtime, new RetryPolicy(), new BudgetTracker(0, 0), _services.Logger);
        var settings = _services.Settings.Current.Processing;
        foreach (var file in files)
        {
            file.Status = FileStatus.Queued;
        }

        var done = 0;
        using var worker = _services.Workers.Create();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JobStatus = $"Анализ структуры: {done + 1} из {files.Count} — {file.Name}";
            file.Status = FileStatus.AnalyzingStructure;
            try
            {
                var result = await _services.Structure.DetectAsync(file.File, worker, llm, mode, settings, false, file.EncodingOverride, cancellationToken);
                file.ApplyDetection(result);
                file.IsSelected = result.Status == FileStatus.Tabular;
            }
            catch (WorkerException ex)
            {
                file.Status = FileStatus.Error;
                file.Message = ex.Message;
            }
            catch (LlmException ex) when (ex.IsFatalForJob)
            {
                file.Status = FileStatus.Error;
                file.Message = ex.KindText;
                throw new InvalidOperationException(ex.UserMessage, ex);
            }
            catch (LlmException ex)
            {
                file.Status = FileStatus.Error;
                file.Message = ex.UserMessage;
            }

            done++;
            if (ReferenceEquals(file, CurrentFile))
            {
                UpdatePreview();
            }

            UpdateCounters();
        }

        JobStatus = $"Анализ структуры завершён: табличных {files.Count(f => f.Status == FileStatus.Tabular)}, требуют настройки {files.Count(f => f.Status == FileStatus.NeedsConfiguration)}, " +
                    $"не табличных {files.Count(f => f.Status == FileStatus.NotTabular)}, не поддерживается {files.Count(f => f.Status == FileStatus.NotSupported)}, ошибок {files.Count(f => f.Status == FileStatus.Error)}.";
    }

    private void AttachSession(JobSession session)
    {
        _session = session;
        Metrics.Reset(session.JobId);
        session.FileChanged += e =>
        {
            lock (_pendingGate)
            {
                _pendingEvents[e.FullPath] = e;
            }
        };
    }

    private async Task RunSessionAsync()
    {
        IsJobRunning = true;
        IsPaused = false;
        JobStatus = $"Задание {_session.JobId}: выполняется";
        try
        {
            var status = await Task.Run(() => _session.RunAsync());
            FlushEvents();
            IsPaused = status == Fakt.Core.Processing.JobStatus.Paused;
            JobStatus = $"Задание {_session.JobId}: {status.ToText()}" + (string.IsNullOrEmpty(_session.StatusMessage) || _session.StatusMessage == status.ToText() ? string.Empty : $". {_session.StatusMessage}");
            if (status == Fakt.Core.Processing.JobStatus.Failed)
            {
                _services.Dialogs.Show("Задание остановлено", _session.StatusMessage, MessageKind.Error);
            }
        }
        finally
        {
            IsJobRunning = false;
            UpdateCounters();
        }
    }

    private void Pause()
    {
        _session?.Pause();
        JobStatus = $"Задание {_session?.JobId}: пауза — завершаются начатые пакеты…";
    }

    private Task ResumeAsync(CancellationToken cancellationToken) => RunSessionAsync();

    private void Stop()
    {
        if (IsScanning)
        {
            ScanCommand.Cancel();
            return;
        }

        if (IsAnalyzing)
        {
            ProcessCommand.Cancel();
            return;
        }

        if (_session == null)
        {
            return;
        }

        if (!_services.Dialogs.Confirm("Остановить обработку",
                "Начатые запросы будут отменены на стороне приложения; провайдер мог уже принять и оплатить их. Сохранённые результаты не теряются, повторная обработка не создаст дубликатов.",
                "Остановить", "Продолжить работу", danger: true))
        {
            return;
        }

        _session.Stop();
        if (!IsJobRunning)
        {
            IsPaused = false;
            JobStatus = $"Задание {_session.JobId}: остановлено";
            _session = null;
        }
    }

    private void FlushEvents()
    {
        List<FileStatusEvent> events;
        lock (_pendingGate)
        {
            if (_pendingEvents.Count == 0)
            {
                return;
            }

            events = _pendingEvents.Values.ToList();
            _pendingEvents.Clear();
        }

        foreach (var e in events)
        {
            if (!_byPath.TryGetValue(e.FullPath, out var file))
            {
                continue;
            }

            file.Status = e.Status;
            if (e.Message != null || e.Status != FileStatus.Processing)
            {
                file.Message = e.Message;
            }

            if (e.FileCode != null)
            {
                file.FileCode = e.FileCode;
            }

            file.ApplyProgress(e.Progress);
        }

        Metrics.Update(Files, _session);
        UpdateCounters();
    }

    private async Task ReanalyzeAsync(FileItemViewModel file, bool extended, string encoding, CancellationToken cancellationToken)
    {
        if (file == null)
        {
            return;
        }

        _services.Authorization.Demand(Permission.ProcessData);
        if (encoding != null)
        {
            file.EncodingOverride = encoding;
        }

        var runtime = RuntimeConfig(out var profile);
        if (!ConfirmExternal(profile, 1))
        {
            return;
        }

        IsAnalyzing = true;
        try
        {
            var mode = await ResolveModeAsync(runtime, profile, cancellationToken);
            var llm = new ResilientLlmClient(_services.Registry.Get(profile.ProviderId), runtime, new RetryPolicy(), new BudgetTracker(0, 0), _services.Logger);
            using var worker = _services.Workers.Create();
            file.Status = FileStatus.AnalyzingStructure;
            JobStatus = extended ? $"Расширенный образец: {file.Name}" : $"Повторный анализ: {file.Name}";
            var result = await _services.Structure.DetectAsync(file.File, worker, llm, mode, _services.Settings.Current.Processing, extended, file.EncodingOverride, cancellationToken);
            file.ApplyDetection(result);
            file.IsSelected = result.Status == FileStatus.Tabular;
            JobStatus = $"{file.Name}: {file.StatusText}";
        }
        catch
        {
            if (file.Status == FileStatus.AnalyzingStructure)
            {
                file.Status = FileStatus.Error;
            }

            throw;
        }
        finally
        {
            IsAnalyzing = false;
            UpdatePreview();
            UpdateCounters();
        }
    }

    private async Task EditStructureAsync(CancellationToken cancellationToken)
    {
        var file = CurrentFile;
        if (file == null)
        {
            return;
        }

        var editor = new StructureEditorViewModel(file);
        if (!_services.Dialogs.ShowDialog(editor))
        {
            return;
        }

        await ValidateManualAsync(file, editor.ToDescriptor(), cancellationToken);
    }

    private async Task ApplySuggestionAsync(CancellationToken cancellationToken)
    {
        var file = CurrentFile;
        var suggestion = file?.Detection?.SuggestedColumns;
        if (suggestion == null)
        {
            return;
        }

        var structure = (file.Detection.Structure ?? file.Detection.ModelStructure).Clone();
        structure.Columns = suggestion.ToList();
        if (structure.FixedWidths != null && structure.FixedWidths.Count != structure.Columns.Count)
        {
            structure.FixedWidths = null;
        }

        await ValidateManualAsync(file, structure, cancellationToken);
    }

    private async Task ValidateManualAsync(FileItemViewModel file, StructureDescriptor structure, CancellationToken cancellationToken)
    {
        IsAnalyzing = true;
        try
        {
            using var worker = _services.Workers.Create();
            var result = await _services.Structure.ValidateManualAsync(file.File, structure, worker, _services.Settings.Current.Processing, cancellationToken);
            file.ApplyDetection(result);
            file.IsSelected = result.Status == FileStatus.Tabular;
            JobStatus = $"{file.Name}: {file.StatusText}" + (result.Message == null ? string.Empty : ". " + result.Message);
        }
        finally
        {
            IsAnalyzing = false;
            UpdatePreview();
            UpdateCounters();
        }
    }

    private void UpdatePreview()
    {
        var file = CurrentFile;
        if (file == null)
        {
            Preview = null;
            StructureMessages = null;
            return;
        }

        Preview = new PreviewViewModel(file);
        var messages = new List<string>();
        if (file.Detection != null)
        {
            messages.AddRange(file.Detection.Errors.Select(e => "Ошибка: " + e));
            messages.AddRange(file.Detection.Warnings.Select(w => "Предупреждение: " + w));
            if (file.Detection.ModelStructure?.Reason != null)
            {
                messages.Add("Обоснование модели: " + file.Detection.ModelStructure.Reason +
                             (file.Detection.ModelStructure.Confidence.HasValue ? $" (самооценка {file.Detection.ModelStructure.Confidence:0.00}, не является доказательством)" : string.Empty));
            }
        }

        StructureMessages = messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Остановка фоновых операций при закрытии окна.</summary>
    public bool ConfirmClose()
    {
        if (!IsJobRunning)
        {
            return true;
        }

        if (!_services.Dialogs.Confirm("Обработка выполняется", "Остановить обработку и закрыть приложение? Сохранённые результаты не теряются; задание можно продолжить позже из раздела «История».", "Остановить и закрыть", "Не закрывать", danger: true))
        {
            return false;
        }

        _session?.Pause();
        return true;
    }
}

/// <summary>Счётчики задания: файлы найденные/табличные/пропущенные, записи и запросы, фактические токены провайдера.</summary>
public sealed class JobMetricsViewModel : ObservableObject
{
    private long? _jobId;
    private long _read;
    private long _processed;
    private long _saved;
    private long _noFacts;
    private long _errors;
    private long _requests;
    private long _inputTokens;
    private long _outputTokens;

    public long? JobId { get => _jobId; private set => SetProperty(ref _jobId, value); }
    public long RecordsRead { get => _read; private set => SetProperty(ref _read, value); }
    public long RecordsProcessed { get => _processed; private set => SetProperty(ref _processed, value); }
    public long ObservationsSaved { get => _saved; private set => SetProperty(ref _saved, value); }
    public long NoFacts { get => _noFacts; private set => SetProperty(ref _noFacts, value); }
    public long Errors { get => _errors; private set => SetProperty(ref _errors, value); }
    public long Requests { get => _requests; private set => SetProperty(ref _requests, value); }
    public long InputTokens { get => _inputTokens; private set => SetProperty(ref _inputTokens, value); }
    public long OutputTokens { get => _outputTokens; private set => SetProperty(ref _outputTokens, value); }

    public string TokensText => InputTokens + OutputTokens == 0 ? "нет данных от провайдера" : $"{InputTokens:N0} / {OutputTokens:N0}";

    public void Reset(long jobId)
    {
        JobId = jobId;
        RecordsRead = RecordsProcessed = ObservationsSaved = NoFacts = Errors = Requests = InputTokens = OutputTokens = 0;
        OnPropertyChanged(nameof(TokensText));
    }

    public void Update(IEnumerable<FileItemViewModel> files, JobSession session)
    {
        var list = files.ToList();
        RecordsRead = list.Sum(f => f.RecordsRead ?? 0);
        RecordsProcessed = list.Sum(f => f.RecordsProcessed ?? 0);
        ObservationsSaved = list.Sum(f => f.ObservationsSaved ?? 0);
        Errors = list.Sum(f => f.Errors ?? 0);
        if (session != null)
        {
            Requests = session.Budget.Requests;
            InputTokens = session.Budget.InputTokens;
            OutputTokens = session.Budget.OutputTokens;
        }

        OnPropertyChanged(nameof(TokensText));
    }
}
