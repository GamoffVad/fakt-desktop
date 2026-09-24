using System;
using Fakt.Application.Processing;
using Fakt.Application.Structure;
using Fakt.Core.Files;
using Fakt.Core.Structure;
using Fakt.Desktop.Controls;
using Fakt.Desktop.Converters;
using Fakt.Desktop.Mvvm;

namespace Fakt.Desktop.ViewModels;

/// <summary>Строка таблицы найденных файлов. Флажок включения и выбор строки для предпросмотра — разные состояния.</summary>
public sealed class FileItemViewModel : ObservableObject
{
    private bool _isSelected;
    private FileStatus _status;
    private string _message;
    private string _formatText;
    private string _fileCode;
    private long? _read;
    private long? _processed;
    private long? _saved;
    private long? _errors;
    private StructureDetectionResult _detection;
    private StructureDescriptor _structure;

    public FileItemViewModel(ScannedFile file)
    {
        File = file;
        var hint = FileKindClassifier.Classify(file.Extension, out var reason);
        Hint = hint;
        if (hint == FileKindHint.Binary || hint == FileKindHint.AdapterRequired)
        {
            _status = FileStatus.NotSupported;
            _message = reason;
        }
        else
        {
            _status = FileStatus.Discovered;
            _isSelected = hint == FileKindHint.TextCandidate;
        }
    }

    public ScannedFile File { get; }

    public FileKindHint Hint { get; }

    public string Name => File.Name;
    public string RelativePath => File.RelativePath;
    public string FullPath => File.FullPath;
    public string Extension => string.IsNullOrEmpty(File.Extension) ? "—" : File.Extension.TrimStart('.').ToUpperInvariant();
    public long Size => File.Size;
    public string SizeText => FileSizeConverter.Format(File.Size);
    public string TypeSizeText => Extension + " · " + SizeText;
    public DateTime LastWriteTime => File.LastWriteTimeUtc.ToLocalTime();

    /// <summary>Флажок включения в обработку. Недоступен для файлов, которые нельзя обработать до устранения причины.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !CanSelect)
            {
                value = false;
            }

            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke(this);
            }
        }
    }

    public event Action<FileItemViewModel> SelectionChanged;

    public bool CanSelect => Status.IsSelectable() || Status == FileStatus.NeedsConfiguration && Structure != null;

    public string SelectDisabledReason => CanSelect ? null : Message ?? StatusText;

    public FileStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertiesChanged(nameof(StatusText), nameof(Tone), nameof(CanSelect), nameof(SelectDisabledReason), nameof(IsBusy));
                if (!CanSelect && _isSelected)
                {
                    IsSelected = false;
                }
            }
        }
    }

    public string StatusText => Status.ToText();

    public BadgeTone Tone => (BadgeTone)new ToneConverter().Convert(Status, typeof(BadgeTone), null, null);

    public bool IsBusy => Status.IsBusy();

    public string Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertiesChanged(nameof(SelectDisabledReason), nameof(ErrorsText));
            }
        }
    }

    public string FormatText
    {
        get => _formatText;
        set => SetProperty(ref _formatText, value);
    }

    public string FileCode
    {
        get => _fileCode;
        set => SetProperty(ref _fileCode, value);
    }

    public long? RecordsRead
    {
        get => _read;
        set
        {
            if (SetProperty(ref _read, value))
            {
                OnPropertyChanged(nameof(CountsText));
            }
        }
    }

    public long? RecordsProcessed
    {
        get => _processed;
        set
        {
            if (SetProperty(ref _processed, value))
            {
                OnPropertyChanged(nameof(CountsText));
            }
        }
    }

    public long? ObservationsSaved
    {
        get => _saved;
        set
        {
            if (SetProperty(ref _saved, value))
            {
                OnPropertyChanged(nameof(CountsText));
            }
        }
    }

    public long? Errors
    {
        get => _errors;
        set
        {
            if (SetProperty(ref _errors, value))
            {
                OnPropertyChanged(nameof(ErrorsText));
            }
        }
    }

    /// <summary>Колонка «Ошибки и краткая причина»: число записей с ошибками и последнее сообщение.</summary>
    public string ErrorsText => Errors > 0
        ? Format(Errors) + (string.IsNullOrEmpty(Message) ? string.Empty : " · " + Message)
        : Message;

    public string CountsText => RecordsRead == null && RecordsProcessed == null ? "—" : $"{Format(RecordsRead)} / {Format(RecordsProcessed)} / {Format(ObservationsSaved)}";

    private static string Format(long? value) => value?.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) ?? "—";

    public StructureDetectionResult Detection
    {
        get => _detection;
        set => SetProperty(ref _detection, value);
    }

    /// <summary>Структура, подтверждённая парсером (или исправленная пользователем и проверенная).</summary>
    public StructureDescriptor Structure
    {
        get => _structure;
        set
        {
            if (SetProperty(ref _structure, value))
            {
                OnPropertiesChanged(nameof(CanSelect), nameof(SelectDisabledReason));
            }
        }
    }

    public string EncodingOverride { get; set; }

    public void ApplyDetection(StructureDetectionResult result)
    {
        Detection = result;
        Structure = result.Status == FileStatus.Tabular ? result.Structure : null;
        FormatText = result.FormatText ?? (result.Status == FileStatus.NotTabular ? "Нет табличной структуры" : null);
        Message = result.Message;
        Status = result.Status;
    }

    public void ApplyProgress(PipelineProgress progress)
    {
        if (progress == null)
        {
            return;
        }

        RecordsRead = progress.RecordsSkipped + progress.RecordsRead;
        RecordsProcessed = progress.Committed;
        ObservationsSaved = progress.Observations;
        Errors = progress.Errors;
        OnPropertyChanged(nameof(CountsText));
    }
}
