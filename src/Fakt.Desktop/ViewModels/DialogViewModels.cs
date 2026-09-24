using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fakt.Application.Llm;
using Fakt.Application.Processing;
using Fakt.Core.Llm;
using Fakt.Core.Processing;
using Fakt.Core.Structure;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Services;

namespace Fakt.Desktop.ViewModels;

/// <summary>Оценка запуска по небольшому образцу; денежная оценка — только при заданных тарифах.</summary>
public sealed class EstimateViewModel : DialogViewModel
{
    private readonly AppServices _services;
    private readonly IReadOnlyList<EstimateInput> _inputs;
    private readonly LlmProfile _profile;
    private readonly LlmRuntimeConfig _runtime;
    private readonly StructuredOutputMode _mode;
    private ProcessingEstimate _estimate;
    private string _measureError;

    public EstimateViewModel(AppServices services, IReadOnlyList<EstimateInput> inputs, LlmProfile profile, LlmRuntimeConfig runtime, StructuredOutputMode mode)
    {
        _services = services;
        _inputs = inputs;
        _profile = profile;
        _runtime = runtime;
        _mode = mode;
        Title = "Оценка запуска";
        PrimaryText = "Начать обработку";
        SecondaryText = "Отмена";
        Width = 640;
        MeasureCommand = new AsyncCommand(MeasureAsync, onError: ex => MeasureError = ex.Message);
        _estimate = services.Estimation.Estimate(inputs, profile, null);
    }

    public AsyncCommand MeasureCommand { get; }

    public ProcessingEstimate Estimate
    {
        get => _estimate;
        private set
        {
            SetProperty(ref _estimate, value);
            OnPropertiesChanged(nameof(RecordsText), nameof(RequestsText), nameof(TokensText), nameof(DurationText), nameof(AssumptionsText), nameof(WarningsText));
        }
    }

    public string MeasureError
    {
        get => _measureError;
        private set => SetProperty(ref _measureError, value);
    }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public string FilesText => $"{_inputs.Count} файл(ов), {Converters.FileSizeConverter.Format(_estimate.TotalBytes)}";
    public string ModelText => $"{_services.Provider(_profile.ProviderId)?.DisplayName ?? _profile.ProviderId} · {_profile.ModelId} · режим: {LlmProfileService.ModeTitle(_mode)}";
    public string RecordsText => Estimate.EstimatedRecords.HasValue ? "≈ " + Estimate.EstimatedRecords.Value.ToString("N0", Ru) : "не оценено";
    public string RequestsText => "≈ " + Estimate.EstimatedRequests.ToString("N0", Ru) + $" (до {_profile.BatchRows} записей в запросе, параллельно {_profile.MaxConcurrentRequests})";
    public string TokensText => $"вход ≈ {Estimate.EstimatedInputTokens:N0}, выход ≈ {Estimate.EstimatedOutputTokens:N0}";
    public string DurationText => Estimate.EstimatedDuration.HasValue ? "≈ " + FormatDuration(Estimate.EstimatedDuration.Value) : "не оценено — выполните измерение на образце";
    public string CostText => Estimate.CostText;
    public string SampleText => Estimate.SampleNote;
    public string AssumptionsText => string.Join(Environment.NewLine, Estimate.Assumptions.Select(a => "• " + a));
    public string WarningsText => Estimate.Warnings.Count == 0 ? null : string.Join(Environment.NewLine, Estimate.Warnings.Select(w => "• " + w));
    public string BudgetText
    {
        get
        {
            var processing = _services.Settings.Current.Processing;
            return processing.BudgetMaxRequests > 0 || processing.BudgetMaxTokens > 0
                ? $"Предел задания: {(processing.BudgetMaxRequests > 0 ? processing.BudgetMaxRequests.ToString("N0", Ru) + " запросов" : "без предела запросов")}" +
                  $"{(processing.BudgetMaxTokens > 0 ? ", " + processing.BudgetMaxTokens.ToString("N0", Ru) + " токенов" : string.Empty)}. При достижении обработка ставится на паузу."
                : "Предел бюджета задания не задан.";
        }
    }

    public bool IsExternal => !UrlBuilder.IsLocalOrPrivate(_profile.BaseUrl);

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return $"{span.TotalSeconds:0} с";
        if (span.TotalHours < 1) return $"{span.TotalMinutes:0} мин";
        if (span.TotalDays < 1) return $"{Math.Floor(span.TotalHours):0} ч {span.Minutes} мин";
        return $"{Math.Floor(span.TotalDays):0} дн {span.Hours} ч";
    }

    private async Task MeasureAsync(CancellationToken cancellationToken)
    {
        MeasureError = null;
        var first = _inputs.FirstOrDefault(i => i.Detection?.Validation?.Preview?.Rows.Count > 0);
        if (first == null)
        {
            MeasureError = "Нет записей предпросмотра для измерения.";
            return;
        }

        var llm = new ResilientLlmClient(_services.Registry.Get(_profile.ProviderId), _runtime, new RetryPolicy { MaxAttempts = 3 }, new BudgetTracker(0, 0), _services.Logger);
        var measurement = await Task.Run(() => _services.Estimation.MeasureAsync(first, llm, _mode, Math.Min(_profile.BatchRows, 10), cancellationToken), cancellationToken);
        if (measurement == null)
        {
            MeasureError = "Измерение не выполнено: нет данных.";
            return;
        }

        Estimate = _services.Estimation.Estimate(_inputs, _profile, measurement);
        OnPropertiesChanged(nameof(SampleText), nameof(CostText));
    }
}

/// <summary>Ручная настройка структуры (файлы «Требует настройки»). Проверка — парсером, без обращения к модели.</summary>
public sealed class StructureEditorViewModel : DialogViewModel
{
    private string _format;
    private string _encoding;
    private bool _hasHeader;
    private string _skipRows;
    private string _delimiter;
    private string _quoteChar;
    private string _escapeChar;
    private string _columns;
    private string _widths;
    private string _xmlPath;
    private string _xmlNamespaces;
    private string _error;

    public StructureEditorViewModel(FileItemViewModel file)
    {
        Title = "Параметры разбора: " + file.Name;
        PrimaryText = "Проверить и применить";
        SecondaryText = "Отмена";
        Width = 640;
        var source = file.Detection?.Structure ?? file.Detection?.ModelStructure ?? new StructureDescriptor { Format = StructureDescriptor.FormatDelimited, Delimiter = ";", QuoteChar = "\"", HasHeader = true, SkipRows = 0 };
        _format = source.Format ?? StructureDescriptor.FormatDelimited;
        _encoding = EncodingNames.Normalize(source.Encoding) ?? EncodingNames.Normalize(file.Detection?.Sample?.Encoding) ?? "utf-8";
        _hasHeader = source.HasHeader ?? true;
        _skipRows = (source.SkipRows ?? 0).ToString(CultureInfo.InvariantCulture);
        _delimiter = source.Delimiter == "\t" ? "\\t" : source.Delimiter;
        _quoteChar = source.QuoteChar;
        _escapeChar = source.EscapeChar;
        _columns = source.Columns == null ? string.Empty : string.Join(Environment.NewLine, source.Columns);
        _widths = source.FixedWidths == null ? string.Empty : string.Join(", ", source.FixedWidths);
        _xmlPath = source.XmlRecordPath;
        _xmlNamespaces = source.XmlNamespaces == null ? string.Empty : string.Join(Environment.NewLine, source.XmlNamespaces.Select(p => p.Key + "=" + p.Value));
        SampleLines = file.Detection?.Sample?.Lines?.ToList() ?? new List<string>();
    }

    public IReadOnlyList<string> Formats { get; } = new[] { StructureDescriptor.FormatDelimited, StructureDescriptor.FormatFixedWidth, StructureDescriptor.FormatXml, StructureDescriptor.FormatJsonl, StructureDescriptor.FormatJsonArray };
    public IReadOnlyList<string> Encodings { get; } = EncodingNames.Supported.Where(e => e != "utf-8-sig").ToList();
    public List<string> SampleLines { get; }

    public string Format { get => _format; set { if (SetProperty(ref _format, value)) OnPropertiesChanged(nameof(IsDelimited), nameof(IsFixedWidth), nameof(IsXml), nameof(IsTabularText)); } }
    public string Encoding { get => _encoding; set => SetProperty(ref _encoding, value); }
    public bool HasHeader { get => _hasHeader; set => SetProperty(ref _hasHeader, value); }
    public string SkipRows { get => _skipRows; set => SetProperty(ref _skipRows, value); }
    public string Delimiter { get => _delimiter; set => SetProperty(ref _delimiter, value); }
    public string QuoteChar { get => _quoteChar; set => SetProperty(ref _quoteChar, value); }
    public string EscapeChar { get => _escapeChar; set => SetProperty(ref _escapeChar, value); }
    public string Columns { get => _columns; set => SetProperty(ref _columns, value); }
    public string Widths { get => _widths; set => SetProperty(ref _widths, value); }
    public string XmlPath { get => _xmlPath; set => SetProperty(ref _xmlPath, value); }
    public string XmlNamespaces { get => _xmlNamespaces; set => SetProperty(ref _xmlNamespaces, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    public bool IsDelimited => Format == StructureDescriptor.FormatDelimited;
    public bool IsFixedWidth => Format == StructureDescriptor.FormatFixedWidth;
    public bool IsXml => Format == StructureDescriptor.FormatXml;
    public bool IsTabularText => IsDelimited || IsFixedWidth;

    public override bool OnConfirm()
    {
        try
        {
            var check = StructureContractValidator.Validate(ToDescriptor(), Encoding);
            if (!check.IsValid)
            {
                Error = string.Join(Environment.NewLine, check.Errors);
                return false;
            }

            Error = null;
            return true;
        }
        catch (FormatException ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    public StructureDescriptor ToDescriptor()
    {
        if (!int.TryParse(SkipRows?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var skip))
        {
            throw new FormatException("Пропуск строк должен быть целым числом.");
        }

        var descriptor = new StructureDescriptor
        {
            Classification = StructureDescriptor.ClassStructured,
            Format = Format,
            Encoding = Encoding,
            HasHeader = IsTabularText ? HasHeader : (bool?)null,
            SkipRows = IsTabularText ? skip : 0,
            HeaderRow = IsTabularText && HasHeader ? skip : (int?)null,
            Delimiter = IsDelimited ? StructureContractValidator.NormalizeDelimiter(Delimiter) : null,
            QuoteChar = IsDelimited && !string.IsNullOrEmpty(QuoteChar) ? QuoteChar : null,
            EscapeChar = IsDelimited && !string.IsNullOrEmpty(EscapeChar) ? EscapeChar : null,
            Columns = (Columns ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Where(c => c.Length > 0).ToList(),
            XmlRecordPath = IsXml ? XmlPath?.Trim() : null,
            XmlNamespaces = new Dictionary<string, string>(),
            Reason = "Задано вручную",
        };
        if (IsFixedWidth)
        {
            descriptor.FixedWidths = (Widths ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"Ширина «{w}» не является числом.")).ToList();
        }

        if (IsXml)
        {
            foreach (var line in (XmlNamespaces ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = line.IndexOf('=');
                if (eq > 0)
                {
                    descriptor.XmlNamespaces[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
        }

        if (descriptor.Columns.Count == 0)
        {
            descriptor.Columns = null;
        }

        return descriptor;
    }
}

public sealed class HelpViewModel : DialogViewModel
{
    public HelpViewModel(AppServices services)
    {
        Title = "Справка FAKT";
        PrimaryText = "Закрыть";
        Width = 640;
        Version = "FAKT " + AppServices.Version + " · .NET " + Environment.Version + " · " + Environment.OSVersion.VersionString;
        DataFolder = services.Paths.MachineDirectory;
        LogFolder = services.Paths.LogDirectory;
        Tls = services.TlsMode;
    }

    public string Version { get; }
    public string DataFolder { get; }
    public string LogFolder { get; }
    public string Tls { get; }
}
