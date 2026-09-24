using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Fakt.Application.Processing;
using Fakt.Core.Files;
using Fakt.Core.Processing;
using Fakt.Core.Security;
using Fakt.Core.Storage;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Services;
using Newtonsoft.Json;

namespace Fakt.Desktop.ViewModels;

public sealed class JobItemViewModel
{
    public JobItemViewModel(JobRecord job)
    {
        Job = job;
        try
        {
            var snapshot = JsonConvert.DeserializeObject<JobSnapshot>(job.SnapshotJson);
            ModelText = snapshot?.Llm == null ? null : $"{snapshot.Llm.ProviderId} · {snapshot.Llm.ModelId}";
        }
        catch (JsonException)
        {
        }
    }

    public JobRecord Job { get; }
    public long JobId => Job.JobId;
    public string StatusText => Job.Status.ToText();
    public Fakt.Core.Files.StatusTone Tone => Job.Status.ToTone();
    public DateTime CreatedAt => Job.CreatedAtUtc.ToLocalTime();
    public DateTime? FinishedAt => Job.FinishedAtUtc?.ToLocalTime();
    public string CreatedBy => Job.CreatedBy;
    public int FileCount => Job.FileCount;
    public long RecordsProcessed => Job.RecordsProcessed;
    public long ObservationsSaved => Job.ObservationsSaved;
    public long RecordsNoFacts => Job.RecordsNoFacts;
    public long RecordsError => Job.RecordsError;
    public long Requests => Job.Requests;
    public string TokensText => Job.InputTokens + Job.OutputTokens == 0 ? "—" : $"{Job.InputTokens:N0} / {Job.OutputTokens:N0}";
    public string Summary => Job.Summary;
    public string ModelText { get; }
    public bool CanResume => Job.Status == JobStatus.Paused || Job.Status == JobStatus.Interrupted || Job.Status == JobStatus.Failed || Job.Status == JobStatus.Cancelled;
}

public sealed class HistoryViewModel : ObservableObject, IPageActivation
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private JobItemViewModel _selectedJob;
    private JobFileRecord _selectedFile;
    private string _status;
    private string _error;
    private bool _isLoading;
    private JobSession _resumed;

    public HistoryViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Jobs = new ObservableCollection<JobItemViewModel>();
        Files = new ObservableCollection<JobFileRecord>();
        Errors = new ObservableCollection<RowErrorRecord>();
        RefreshCommand = new AsyncCommand(LoadAsync, () => !IsLoading, ex => Error = ex.Message);
        ResumeCommand = new AsyncCommand(ResumeAsync, () => SelectedJob?.CanResume == true && _resumed?.IsRunning != true, ex => _services.Dialogs.Show("Продолжение невозможно", ex.Message, MessageKind.Warning));
        PauseCommand = new RelayCommand(() => _resumed?.Pause(), () => _resumed?.IsRunning == true);
    }

    public ObservableCollection<JobItemViewModel> Jobs { get; }
    public ObservableCollection<JobFileRecord> Files { get; }
    public ObservableCollection<RowErrorRecord> Errors { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ResumeCommand { get; }
    public ICommand PauseCommand { get; }

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    public JobItemViewModel SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (SetProperty(ref _selectedJob, value))
            {
                _ = LoadFilesAsync();
            }
        }
    }

    public JobFileRecord SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                _ = LoadErrorsAsync();
            }
        }
    }

    public string EmptyStateText => Error == null && !IsLoading && Jobs.Count == 0 ? "Заданий пока нет. Запустите обработку на странице «Обработка»." : null;

    public void OnActivated() => RefreshCommand.Execute(null);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        Error = null;
        try
        {
            await Task.Run(() => _services.History.MarkInterruptedAsync(cancellationToken), cancellationToken);
            var jobs = await Task.Run(() => _services.History.ListJobsAsync(0, 200, cancellationToken), cancellationToken);
            var selectedId = SelectedJob?.JobId;
            Jobs.Clear();
            foreach (var job in jobs)
            {
                Jobs.Add(new JobItemViewModel(job));
            }

            SelectedJob = Jobs.FirstOrDefault(j => j.JobId == selectedId) ?? Jobs.FirstOrDefault();
            Status = $"Заданий: {Jobs.Count}";
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            Error = Fakt.Infrastructure.Sql.SqlConnectionFactory.Describe(ex).Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(EmptyStateText));
        }
    }

    private async Task LoadFilesAsync()
    {
        Files.Clear();
        Errors.Clear();
        if (SelectedJob == null)
        {
            return;
        }

        try
        {
            var files = await Task.Run(() => _services.History.ListFilesAsync(SelectedJob.JobId, CancellationToken.None));
            foreach (var file in files)
            {
                Files.Add(file);
            }

            SelectedFile = Files.FirstOrDefault(f => f.RecordsError > 0) ?? Files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Error = Fakt.Infrastructure.Sql.SqlConnectionFactory.Describe(ex).Message;
        }
    }

    private async Task LoadErrorsAsync()
    {
        Errors.Clear();
        if (SelectedFile == null)
        {
            return;
        }

        try
        {
            var errors = await Task.Run(() => _services.History.ListErrorsAsync(SelectedFile.JobFileId, CancellationToken.None));
            foreach (var error in errors)
            {
                Errors.Add(error);
            }
        }
        catch (Exception ex)
        {
            Error = Fakt.Infrastructure.Sql.SqlConnectionFactory.Describe(ex).Message;
        }
    }

    private async Task ResumeAsync(CancellationToken cancellationToken)
    {
        _services.Authorization.Demand(Permission.ProcessData);
        var job = SelectedJob;
        if (job == null)
        {
            return;
        }

        _resumed = await Task.Run(() => _services.Processing.ResumeJobAsync(job.JobId, cancellationToken), cancellationToken);
        _resumed.FileChanged += e => UiThread.Post(() => Status = $"Задание {job.JobId}: {System.IO.Path.GetFileName(e.FullPath)} — {e.Status.ToText()}" +
                                                             (e.Progress != null ? $", обработано записей {e.Progress.Committed:N0}" : string.Empty));
        Status = $"Задание {job.JobId} продолжено.";
        CommandManager.InvalidateRequerySuggested();
        var status = await Task.Run(() => _resumed.RunAsync(), CancellationToken.None);
        Status = $"Задание {job.JobId}: {status.ToText()}. {_resumed.StatusMessage}";
        CommandManager.InvalidateRequerySuggested();
        await LoadAsync(CancellationToken.None);
    }
}
