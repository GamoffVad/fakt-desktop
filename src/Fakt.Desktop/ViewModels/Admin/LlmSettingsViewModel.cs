using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using Fakt.Application.Llm;
using Fakt.Application.Settings;
using Fakt.Core.Llm;
using Fakt.Core.Security;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Services;
using Fakt.Infrastructure.Llm;

namespace Fakt.Desktop.ViewModels.Admin;

public sealed class ProviderFieldViewModel : ObservableObject
{
    private string _value;

    public ProviderFieldViewModel(ProviderField field, string value)
    {
        Field = field;
        _value = value ?? field.DefaultValue;
    }

    public ProviderField Field { get; }
    public string Key => Field.Key;
    public string Label => Field.Label + (Field.Required ? " *" : string.Empty);
    public string Help => Field.Help;
    public bool IsChoice => Field.Kind == ProviderFieldKind.Choice;
    public IReadOnlyList<string> Choices => Field.Choices;

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                Changed?.Invoke(this);
            }
        }
    }

    public event Action<ProviderFieldViewModel> Changed;
}

public sealed class HeaderViewModel : ObservableObject
{
    private string _name;
    private string _value;
    private bool _isSecret;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public bool IsSecret { get => _isSecret; set => SetProperty(ref _isSecret, value); }

    /// <summary>Для секретного заголовка значение уже сохранено в защищённом хранилище.</summary>
    public bool HasSavedSecret { get; set; }
}

/// <summary>
/// Вкладка «LLM». При выборе провайдера подставляются его значения по умолчанию и загружается сохранённый
/// профиль именно этого провайдера; ключ другого профиля не переносится и не отправляется на другой адрес.
/// </summary>
public sealed class LlmSettingsViewModel : ObservableObject
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly AppServices _services;
    private LlmProfile _editing;
    private bool _suppressProviderSwitch;
    private string _name;
    private string _providerId;
    private string _baseUrl;
    private string _apiKey;
    private bool _hasSavedKey;
    private string _modelId;
    private bool _manualModel;
    private string _modelFilter;
    private string _modelsStatus;
    private string _timeout;
    private string _maxOutput;
    private string _concurrency;
    private string _rpm;
    private string _tpm;
    private string _batchRows;
    private string _maxInput;
    private string _temperature;
    private StructuredOutputMode _outputMode;
    private string _priceIn;
    private string _priceOut;
    private string _priceCurrency;
    private string _priceDate;
    private string _priceSource;
    private string _message;
    private MessageKind _messageKind;
    private ExtractionTestResult _testResult;
    private bool _isDirty;
    private LlmProfile _selectedProfile;

    public LlmSettingsViewModel(AppServices services)
    {
        _services = services;
        Providers = services.Registry.Providers.ToList();
        Profiles = new ObservableCollection<LlmProfile>();
        Fields = new ObservableCollection<ProviderFieldViewModel>();
        Headers = new ObservableCollection<HeaderViewModel>();
        Models = new ObservableCollection<ModelInfo>();
        ModelsView = CollectionViewSource.GetDefaultView(Models);
        ModelsView.Filter = m => string.IsNullOrWhiteSpace(ModelFilter) || ((ModelInfo)m).ToString().IndexOf(ModelFilter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        NewProfileCommand = new RelayCommand(() => StartDraft(ProviderId ?? ProviderCatalog.OpenAiCompatible), () => CanEdit);
        SaveCommand = new RelayCommand(Save, () => CanEdit);
        DeleteCommand = new RelayCommand(Delete, () => CanEdit && _editing != null && services.Settings.Current.LlmProfiles.Any(p => p.Id == _editing.Id));
        MakeActiveCommand = new RelayCommand(MakeActive, () => CanEdit && _editing != null && services.Settings.Current.LlmProfiles.Any(p => p.Id == _editing.Id) && !IsActive);
        ClearKeyCommand = new RelayCommand(ClearKey, () => CanEdit && HasSavedKey);
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync, () => CanUseEndpoint, OnError);
        LoadModelsCommand = new AsyncCommand(LoadModelsAsync, () => CanUseEndpoint, OnError);
        TestExtractionCommand = new AsyncCommand(TestExtractionAsync, () => CanUseEndpoint && !string.IsNullOrWhiteSpace(ModelId), OnError);
        AddHeaderCommand = new RelayCommand(() => Headers.Add(new HeaderViewModel()), () => CanEdit);
        RemoveHeaderCommand = new RelayCommand(p => Headers.Remove(p as HeaderViewModel), _ => CanEdit);
        Reload();
    }

    public IReadOnlyList<LlmProviderDescriptor> Providers { get; }
    public ObservableCollection<LlmProfile> Profiles { get; }
    public ObservableCollection<ProviderFieldViewModel> Fields { get; }
    public ObservableCollection<HeaderViewModel> Headers { get; }
    public ObservableCollection<ModelInfo> Models { get; }
    public ICollectionView ModelsView { get; }

    public ICommand NewProfileCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand MakeActiveCommand { get; }
    public ICommand ClearKeyCommand { get; }
    public AsyncCommand TestConnectionCommand { get; }
    public AsyncCommand LoadModelsCommand { get; }
    public AsyncCommand TestExtractionCommand { get; }
    public ICommand AddHeaderCommand { get; }
    public ICommand RemoveHeaderCommand { get; }

    public IReadOnlyList<KeyValuePair<StructuredOutputMode, string>> OutputModes { get; } = new[]
    {
        new KeyValuePair<StructuredOutputMode, string>(StructuredOutputMode.Auto, "Автоматически (по результату теста)"),
        new KeyValuePair<StructuredOutputMode, string>(StructuredOutputMode.JsonSchema, "JSON Schema"),
        new KeyValuePair<StructuredOutputMode, string>(StructuredOutputMode.JsonObject, "JSON mode (схема в промпте)"),
        new KeyValuePair<StructuredOutputMode, string>(StructuredOutputMode.ToolCall, "Вызов инструмента"),
        new KeyValuePair<StructuredOutputMode, string>(StructuredOutputMode.PromptOnly, "Только схема в промпте"),
    };

    public bool CanEdit => _services.Authorization.IsAllowed(Permission.ManageSettings);

    public bool IsReadOnly => !CanEdit;

    public LlmProfile SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value == null || ReferenceEquals(value, _selectedProfile))
            {
                return;
            }

            _selectedProfile = value;
            OnPropertyChanged();
            Load(value);
        }
    }

    public LlmProviderDescriptor Provider => Providers.FirstOrDefault(p => p.Id == ProviderId);

    public string Name { get => _name; set => SetDirty(ref _name, value); }

    public string ProviderId
    {
        get => _providerId;
        set
        {
            if (_providerId == value)
            {
                return;
            }

            _providerId = value;
            OnPropertiesChanged(nameof(ProviderId), nameof(Provider), nameof(ModelLabel), nameof(ModelListingNote), nameof(ProviderNotes), nameof(KeyHint));
            if (!_suppressProviderSwitch && value != null)
            {
                SwitchProvider(value);
            }
        }
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set
        {
            if (SetDirty(ref _baseUrl, value))
            {
                OnPropertiesChanged(nameof(DataNotice), nameof(KeyBindingWarning), nameof(CanUseEndpoint));
            }
        }
    }

    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetDirty(ref _apiKey, value))
            {
                OnPropertyChanged(nameof(CanUseEndpoint));
            }
        }
    }

    public bool HasSavedKey
    {
        get => _hasSavedKey;
        private set
        {
            if (SetProperty(ref _hasSavedKey, value))
            {
                OnPropertiesChanged(nameof(KeyHint), nameof(CanUseEndpoint));
            }
        }
    }

    public string KeyHint
    {
        get
        {
            var requirement = Provider?.ApiKey ?? ApiKeyRequirement.Required;
            var saved = HasSavedKey ? "Ключ сохранён в защищённом хранилище (DPAPI); значение не отображается. Введите новый, чтобы заменить." : "Ключ не сохранён.";
            return requirement == ApiKeyRequirement.Required ? saved : saved + " Для этого провайдера ключ необязателен.";
        }
    }

    public string KeyBindingWarning
    {
        get
        {
            if (_editing == null || !HasSavedKey)
            {
                return null;
            }

            var host = UrlBuilder.KeyBindingOf(BaseUrl);
            return host != null && !string.Equals(host, _editing.KeyBoundHost, StringComparison.OrdinalIgnoreCase)
                ? $"Base URL изменён ({host}): сохранённый ключ привязан к {_editing.KeyBoundHost} и не будет отправлен на новый адрес. При сохранении он будет удалён — введите ключ заново."
                : null;
        }
    }

    public string ModelId { get => _modelId; set { if (SetDirty(ref _modelId, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool ManualModel { get => _manualModel; set => SetDirty(ref _manualModel, value); }
    public string ModelFilter { get => _modelFilter; set { if (SetProperty(ref _modelFilter, value)) ModelsView.Refresh(); } }
    public string ModelsStatus { get => _modelsStatus; private set => SetProperty(ref _modelsStatus, value); }
    public string ModelLabel => Provider?.ModelFieldLabel ?? "Модель";
    public string ModelListingNote => Provider?.ModelListingNote;
    public string ProviderNotes => Provider?.Notes;

    public ModelInfo SelectedModel
    {
        get => Models.FirstOrDefault(m => m.Id == ModelId);
        set
        {
            if (value != null)
            {
                ModelId = value.Id;
                ManualModel = false;
            }
        }
    }

    public string TimeoutSeconds { get => _timeout; set => SetDirty(ref _timeout, value); }
    public string MaxOutputTokens { get => _maxOutput; set => SetDirty(ref _maxOutput, value); }
    public string MaxConcurrent { get => _concurrency; set => SetDirty(ref _concurrency, value); }
    public string RequestsPerMinute { get => _rpm; set => SetDirty(ref _rpm, value); }
    public string TokensPerMinute { get => _tpm; set => SetDirty(ref _tpm, value); }
    public string BatchRows { get => _batchRows; set => SetDirty(ref _batchRows, value); }
    public string MaxInputTokens { get => _maxInput; set => SetDirty(ref _maxInput, value); }
    public string Temperature { get => _temperature; set => SetDirty(ref _temperature, value); }
    public StructuredOutputMode OutputMode { get => _outputMode; set => SetDirty(ref _outputMode, value); }
    public string PriceIn { get => _priceIn; set => SetDirty(ref _priceIn, value); }
    public string PriceOut { get => _priceOut; set => SetDirty(ref _priceOut, value); }
    public string PriceCurrency { get => _priceCurrency; set => SetDirty(ref _priceCurrency, value); }
    public string PriceDate { get => _priceDate; set => SetDirty(ref _priceDate, value); }
    public string PriceSource { get => _priceSource; set => SetDirty(ref _priceSource, value); }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public MessageKind MessageKind { get => _messageKind; private set => SetProperty(ref _messageKind, value); }
    public ExtractionTestResult TestResult { get => _testResult; private set => SetProperty(ref _testResult, value); }
    public bool IsDirty { get => _isDirty; private set => SetProperty(ref _isDirty, value); }

    public bool IsActive => _editing != null && _services.Settings.Current.ActiveLlmProfileId == _editing.Id;

    public string CapabilitiesText
    {
        get
        {
            var caps = _editing?.Capabilities;
            if (caps == null || !caps.VerifiedAtUtc.HasValue)
            {
                return "Возможности модели не проверены: выполните «Тест извлечения».";
            }

            string Flag(bool? value) => value == true ? "да" : value == false ? "нет" : "не проверено";
            var stale = caps.IsVerifiedFor(ModelId) ? string.Empty : " (проверка относится к другой модели — повторите тест)";
            return $"Проверено {caps.VerifiedAtUtc.Value.ToLocalTime():dd.MM.yyyy HH:mm} для {caps.VerifiedModelId}{stale}: JSON Schema — {Flag(caps.JsonSchema)}, JSON mode — {Flag(caps.JsonObject)}, " +
                   $"инструменты — {Flag(caps.ToolCall)}, temperature — {Flag(caps.Temperature)}.";
        }
    }

    /// <summary>Явное предупреждение о том, куда отправляются образцы и записи.</summary>
    public string DataNotice
    {
        get
        {
            var host = UrlBuilder.HostOf(BaseUrl);
            if (host == null)
            {
                return null;
            }

            return UrlBuilder.IsLocalOrPrivate(BaseUrl)
                ? $"Локальный или внутренний адрес ({host}): образцы и записи не покидают вашу сеть, если сервер находится в ней."
                : $"Внешний провайдер: первые строки файлов и записи будут отправляться на {host}. Записи могут содержать персональные данные.";
        }
    }

    public bool CanUseEndpoint
    {
        get
        {
            if (!UrlBuilder.IsValidHttpUrl(BaseUrl, out _) || BaseUrl.Contains("<"))
            {
                return false;
            }

            var requirement = Provider?.ApiKey ?? ApiKeyRequirement.Required;
            return requirement != ApiKeyRequirement.Required || !string.IsNullOrEmpty(ApiKey) || (HasSavedKey && KeyBindingWarning == null);
        }
    }

    private bool SetDirty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
    {
        if (!SetProperty(ref field, value, name))
        {
            return false;
        }

        IsDirty = true;
        return true;
    }

    private void OnError(Exception ex) => SetMessage(ex is AccessDeniedException ? "Недостаточно прав: " + ex.Message : ex.Message, MessageKind.Error);

    private void SetMessage(string text, MessageKind kind)
    {
        Message = text;
        MessageKind = kind;
    }

    public void Reload()
    {
        Profiles.Clear();
        foreach (var profile in _services.Settings.Current.LlmProfiles.OrderBy(p => p.Name))
        {
            Profiles.Add(profile);
        }

        var active = _services.Settings.ActiveProfile;
        if (active != null)
        {
            _selectedProfile = Profiles.First(p => p.Id == active.Id);
            OnPropertyChanged(nameof(SelectedProfile));
            Load(_selectedProfile);
        }
        else
        {
            StartDraft(ProviderCatalog.OpenAiCompatible);
        }
    }

    private void Load(LlmProfile profile)
    {
        _editing = profile.Clone();
        _suppressProviderSwitch = true;
        ProviderId = profile.ProviderId;
        _suppressProviderSwitch = false;
        _name = profile.Name;
        _baseUrl = profile.BaseUrl;
        _apiKey = null;
        _modelId = profile.ModelId;
        _manualModel = profile.ManualModelId;
        _timeout = profile.TimeoutSeconds.ToString(Invariant);
        _maxOutput = profile.MaxOutputTokens.ToString(Invariant);
        _concurrency = profile.MaxConcurrentRequests.ToString(Invariant);
        _rpm = profile.RequestsPerMinute.ToString(Invariant);
        _tpm = profile.TokensPerMinute.ToString(Invariant);
        _batchRows = profile.BatchRows.ToString(Invariant);
        _maxInput = profile.MaxInputTokensPerRequest.ToString(Invariant);
        _temperature = profile.Temperature?.ToString("0.##", Invariant);
        _outputMode = profile.OutputMode;
        _priceIn = profile.Pricing?.InputPerMillionTokens?.ToString(Invariant);
        _priceOut = profile.Pricing?.OutputPerMillionTokens?.ToString(Invariant);
        _priceCurrency = profile.Pricing?.Currency ?? "USD";
        _priceDate = profile.Pricing?.AsOfDate?.ToString("dd.MM.yyyy", Invariant);
        _priceSource = profile.Pricing?.Source;
        HasSavedKey = _services.Settings.Current.LlmProfiles.Any(p => p.Id == profile.Id) && SafeHasKey(profile.Id);
        BuildFields(profile);
        Headers.Clear();
        foreach (var header in profile.ExtraHeaders ?? new List<HeaderSetting>())
        {
            Headers.Add(new HeaderViewModel { Name = header.Name, Value = header.IsSecret ? null : header.Value, IsSecret = header.IsSecret, HasSavedSecret = header.IsSecret });
        }

        Models.Clear();
        ModelsStatus = null;
        TestResult = null;
        Message = null;
        IsDirty = false;
        OnPropertiesChanged(nameof(Name), nameof(BaseUrl), nameof(ApiKey), nameof(ModelId), nameof(ManualModel), nameof(TimeoutSeconds), nameof(MaxOutputTokens),
            nameof(MaxConcurrent), nameof(RequestsPerMinute), nameof(TokensPerMinute), nameof(BatchRows), nameof(MaxInputTokens), nameof(Temperature),
            nameof(OutputMode), nameof(PriceIn), nameof(PriceOut), nameof(PriceCurrency), nameof(PriceDate), nameof(PriceSource), nameof(IsActive),
            nameof(CapabilitiesText), nameof(DataNotice), nameof(KeyBindingWarning), nameof(CanUseEndpoint));
    }

    private bool SafeHasKey(Guid profileId)
    {
        try
        {
            return _services.Settings.HasApiKey(profileId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.IO.IOException)
        {
            return false;
        }
    }

    private void BuildFields(LlmProfile profile)
    {
        Fields.Clear();
        foreach (var field in Provider?.Fields ?? Array.Empty<ProviderField>())
        {
            var vm = new ProviderFieldViewModel(field, profile.GetOption(field.Key));
            vm.Changed += OnFieldChanged;
            Fields.Add(vm);
        }
    }

    private void OnFieldChanged(ProviderFieldViewModel field)
    {
        IsDirty = true;
        if (field.Key == "region" && ProviderId == ProviderCatalog.Qwen)
        {
            // Регион Qwen подставляет публичный Base URL; «custom» — адрес рабочего пространства вводится вручную.
            switch (field.Value)
            {
                case "intl": BaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1"; break;
                case "cn-beijing": BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1"; break;
                case "us": BaseUrl = "https://dashscope-us.aliyuncs.com/compatible-mode/v1"; break;
            }
        }
    }

    /// <summary>Смена провайдера: сохранённый профиль этого провайдера или новый черновик со значениями по умолчанию.</summary>
    private void SwitchProvider(string providerId)
    {
        var saved = _services.Settings.Current.LlmProfiles.FirstOrDefault(p => p.ProviderId == providerId);
        if (saved != null)
        {
            _selectedProfile = Profiles.FirstOrDefault(p => p.Id == saved.Id) ?? saved;
            OnPropertyChanged(nameof(SelectedProfile));
            Load(saved);
            SetMessage($"Загружен сохранённый профиль «{saved.Name}» провайдера {Provider?.DisplayName}.", MessageKind.Info);
            return;
        }

        StartDraft(providerId);
    }

    private void StartDraft(string providerId)
    {
        var descriptor = Providers.FirstOrDefault(p => p.Id == providerId) ?? Providers.First();
        var draft = new LlmProfile
        {
            Name = descriptor.DisplayName,
            ProviderId = descriptor.Id,
            BaseUrl = descriptor.DefaultBaseUrl,
            MaxConcurrentRequests = 2,
        };
        foreach (var field in descriptor.Fields)
        {
            if (field.DefaultValue != null)
            {
                draft.Options[field.Key] = field.DefaultValue;
            }
        }

        _selectedProfile = null;
        OnPropertyChanged(nameof(SelectedProfile));
        Load(draft);
        IsDirty = true;
        SetMessage($"Новый профиль {descriptor.DisplayName}: подставлены публичные значения по умолчанию. Ключ другого провайдера не переносится.", MessageKind.Info);
    }

    private LlmProfile BuildProfile()
    {
        int Int(string text, string what)
        {
            if (!int.TryParse(text?.Trim(), NumberStyles.Integer, Invariant, out var value))
            {
                throw new ArgumentException($"{what}: введите целое число.");
            }

            return value;
        }

        decimal? Money(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Number, Invariant, out var value) ? value : throw new ArgumentException("Тариф: введите число.");
        }

        var profile = _editing?.Clone() ?? new LlmProfile();
        profile.Name = Name?.Trim();
        profile.ProviderId = ProviderId;
        profile.BaseUrl = BaseUrl?.Trim();
        profile.ModelId = ModelId?.Trim();
        profile.ManualModelId = ManualModel;
        profile.TimeoutSeconds = Int(TimeoutSeconds, "Тайм-аут");
        profile.MaxOutputTokens = Int(MaxOutputTokens, "Максимальный объём ответа");
        profile.MaxConcurrentRequests = Int(MaxConcurrent, "Одновременных запросов");
        profile.RequestsPerMinute = Int(RequestsPerMinute, "Запросов в минуту");
        profile.TokensPerMinute = Int(TokensPerMinute, "Токенов в минуту");
        profile.BatchRows = Int(BatchRows, "Записей в пакете");
        profile.MaxInputTokensPerRequest = Int(MaxInputTokens, "Предел входных токенов");
        profile.Temperature = string.IsNullOrWhiteSpace(Temperature)
            ? (double?)null
            : double.TryParse(Temperature.Trim().Replace(',', '.'), NumberStyles.Float, Invariant, out var t) ? t : throw new ArgumentException("Температура: введите число или оставьте поле пустым.");
        profile.OutputMode = OutputMode;
        profile.Options = Fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
        profile.ExtraHeaders = Headers.Where(h => !string.IsNullOrWhiteSpace(h.Name))
            .Select(h => new HeaderSetting { Name = h.Name.Trim(), IsSecret = h.IsSecret, Value = h.IsSecret ? null : h.Value }).ToList();
        profile.Pricing = new PricingInfo
        {
            InputPerMillionTokens = Money(PriceIn),
            OutputPerMillionTokens = Money(PriceOut),
            Currency = string.IsNullOrWhiteSpace(PriceCurrency) ? "USD" : PriceCurrency.Trim(),
            AsOfDate = string.IsNullOrWhiteSpace(PriceDate) ? (DateTime?)null :
                DateTime.TryParseExact(PriceDate.Trim(), "dd.MM.yyyy", Invariant, DateTimeStyles.None, out var date) ? date : throw new ArgumentException("Дата тарифа: формат ДД.ММ.ГГГГ."),
            Source = PriceSource,
        };
        SettingsService.ValidateProfile(profile);
        return profile;
    }

    private void Save()
    {
        try
        {
            var profile = BuildProfile();
            if (Provider?.ApiKey == ApiKeyRequirement.Required && string.IsNullOrEmpty(ApiKey) && !HasSavedKey)
            {
                SetMessage("Введите ключ API: для этого провайдера он обязателен.", MessageKind.Warning);
                return;
            }

            _services.Settings.SaveProfile(profile, makeActive: !_services.Settings.Current.LlmProfiles.Any());
            if (!string.IsNullOrEmpty(ApiKey))
            {
                _services.Settings.SetApiKey(profile.Id, ApiKey);
            }

            foreach (var header in Headers.Where(h => h.IsSecret && !string.IsNullOrEmpty(h.Value)))
            {
                _services.Settings.SetSecretHeader(profile.Id, header.Name.Trim(), header.Value);
            }

            var saved = _services.Settings.Current.LlmProfiles.First(p => p.Id == profile.Id);
            Reload();
            _selectedProfile = Profiles.First(p => p.Id == saved.Id);
            OnPropertyChanged(nameof(SelectedProfile));
            Load(saved);
            SetMessage($"Профиль «{saved.Name}» сохранён. Ключ хранится в защищённом хранилище и не записывается в настройки.", MessageKind.Success);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is AccessDeniedException || ex is InvalidOperationException)
        {
            SetMessage(ex.Message, MessageKind.Error);
        }
    }

    private void Delete()
    {
        if (_editing == null || !_services.Dialogs.Confirm("Удалить профиль", $"Удалить профиль «{_editing.Name}» и его ключ из защищённого хранилища?", "Удалить", danger: true))
        {
            return;
        }

        _services.Settings.DeleteProfile(_editing.Id);
        Reload();
    }

    private void MakeActive()
    {
        _services.Settings.SetActiveProfile(_editing.Id);
        OnPropertyChanged(nameof(IsActive));
        SetMessage($"Профиль «{_editing.Name}» используется для обработки. Уже выполняющиеся задания не изменяются.", MessageKind.Success);
    }

    private void ClearKey()
    {
        if (_editing != null && _services.Dialogs.Confirm("Удалить ключ", "Удалить сохранённый ключ API этого профиля?", "Удалить", danger: true))
        {
            _services.Settings.SetApiKey(_editing.Id, null);
            HasSavedKey = false;
        }
    }

    /// <summary>
    /// Параметры для проверки текущих (возможно, несохранённых) значений. Сохранённый ключ используется
    /// только если адрес не изменился; иначе нужен заново введённый ключ.
    /// </summary>
    private LlmRuntimeConfig TestConfig()
    {
        var profile = BuildProfile();
        var key = string.IsNullOrEmpty(ApiKey) ? null : ApiKey.Trim();
        if (key == null && HasSavedKey)
        {
            if (KeyBindingWarning != null)
            {
                throw new InvalidOperationException("Base URL изменён: сохранённый ключ не будет отправлен на новый адрес. Введите ключ заново.");
            }

            key = _services.Settings.Secrets.Get(SecretKeys.ApiKey(profile.Id));
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in Headers.Where(h => !string.IsNullOrWhiteSpace(h.Name)))
        {
            headers[header.Name.Trim()] = header.IsSecret && string.IsNullOrEmpty(header.Value)
                ? _services.Settings.Secrets.Get(SecretKeys.Header(profile.Id, header.Name.Trim()))
                : header.Value;
        }

        return new LlmRuntimeConfig(profile, key, headers);
    }

    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        SetMessage("Проверка соединения…", MessageKind.Info);
        var config = TestConfig();
        var result = await Task.Run(() => _services.Profiles.TestConnectionAsync(config, cancellationToken), cancellationToken);
        if (result.Models != null)
        {
            ApplyModels(result.Models);
        }

        SetMessage($"{result.Message} ({result.Elapsed.TotalMilliseconds:0} мс{(result.RequestId != null ? ", request " + result.RequestId : string.Empty)})",
            result.Success ? MessageKind.Success : MessageKind.Error);
    }

    private async Task LoadModelsAsync(CancellationToken cancellationToken)
    {
        ModelsStatus = "Загрузка…";
        Models.Clear();
        var config = TestConfig();
        var result = await Task.Run(() => _services.Profiles.ListModelsAsync(config, cancellationToken), cancellationToken);
        ApplyModels(result);
    }

    private void ApplyModels(ModelListResult result)
    {
        Models.Clear();
        foreach (var model in result.Models)
        {
            Models.Add(model);
        }

        ModelsStatus = result.Status == ModelListStatus.Loaded
            ? $"Загружено моделей: {result.Models.Count}{(result.PagesFetched > 1 ? $" (страниц: {result.PagesFetched})" : string.Empty)}. {result.Message}"
            : ModelListResult.StatusText(result.Status) + (string.IsNullOrEmpty(result.Message) ? string.Empty : ": " + result.Message);
        if (result.Status != ModelListStatus.Loaded)
        {
            ManualModel = true;
        }

        OnPropertyChanged(nameof(SelectedModel));
    }

    private async Task TestExtractionAsync(CancellationToken cancellationToken)
    {
        SetMessage("Тест извлечения на синтетических данных…", MessageKind.Info);
        TestResult = null;
        var config = TestConfig();
        var result = await Task.Run(() => _services.Profiles.TestExtractionAsync(config, cancellationToken), cancellationToken);
        TestResult = result;
        var saved = _services.Settings.Current.LlmProfiles.FirstOrDefault(p => p.Id == config.Profile.Id);
        if (saved != null && CanEdit && result.Capabilities?.VerifiedAtUtc != null && string.Equals(saved.ModelId, config.Profile.ModelId, StringComparison.Ordinal))
        {
            _services.Settings.SaveCapabilities(saved.Id, result.Capabilities);
            _editing.Capabilities = result.Capabilities;
        }
        else if (_editing != null)
        {
            _editing.Capabilities = result.Capabilities;
        }

        OnPropertyChanged(nameof(CapabilitiesText));
        SetMessage(result.Message + $" ({result.Elapsed.TotalSeconds:0.0} с" + (result.InputTokens.HasValue ? $", токены {result.InputTokens}/{result.OutputTokens}" : string.Empty) + ")",
            result.Success ? MessageKind.Success : result.ModeUsed.HasValue ? MessageKind.Warning : MessageKind.Error);
    }
}
