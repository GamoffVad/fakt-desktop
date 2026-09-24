using System.Windows.Input;
using Fakt.Core.Security;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.ViewModels.Admin;

namespace Fakt.Desktop.ViewModels;

public enum PageKey
{
    Processing,
    Search,
    History,
    Admin,
    Log,
}

/// <summary>Оболочка: разделы боковой навигации, текущая страница, учётная запись и роль.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private PageKey _currentKey;
    private ObservableObject _currentPage;
    private bool _isCompact;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Processing = new ProcessingViewModel(services);
        Search = new SearchViewModel(services);
        History = new HistoryViewModel(services, this);
        Admin = new AdminViewModel(services);
        Log = new LogViewModel(services);
        NavigateCommand = new RelayCommand(parameter =>
        {
            if (parameter is PageKey key)
            {
                CurrentKey = key;
            }
            else if (parameter is string text && System.Enum.TryParse<PageKey>(text, out var parsed))
            {
                CurrentKey = parsed;
            }
        });
        OpenAdminCommand = new RelayCommand(() => CurrentKey = PageKey.Admin);
        HelpCommand = new RelayCommand(() => services.Dialogs.ShowDialog(new HelpViewModel(services)));
        CurrentKey = PageKey.Processing;
        _currentPage = Processing;
    }

    public ProcessingViewModel Processing { get; }
    public SearchViewModel Search { get; }
    public HistoryViewModel History { get; }
    public AdminViewModel Admin { get; }
    public LogViewModel Log { get; }

    public ICommand NavigateCommand { get; }
    public ICommand OpenAdminCommand { get; }
    public ICommand HelpCommand { get; }

    public PageKey CurrentKey
    {
        get => _currentKey;
        set
        {
            if (!SetProperty(ref _currentKey, value))
            {
                return;
            }

            CurrentPage = value switch
            {
                PageKey.Search => Search,
                PageKey.History => History,
                PageKey.Admin => Admin,
                PageKey.Log => Log,
                _ => Processing,
            };
            (CurrentPage as IPageActivation)?.OnActivated();
        }
    }

    public ObservableObject CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    /// <summary>Узкое окно (менее 1280 DIP): навигация сворачивается до полосы значков.</summary>
    public bool IsCompact
    {
        get => _isCompact;
        set => SetProperty(ref _isCompact, value);
    }

    public string UserName => _services.Identity.UserName;

    public string RoleTitle => RolePermissions.Title(_services.Authorization.CurrentRole);

    public bool IsAdministrator => _services.Authorization.CurrentRole == Role.Administrator;

    public string AppVersion => "FAKT " + AppServices.Version;
}

/// <summary>Страница, которой нужно обновить данные при открытии.</summary>
public interface IPageActivation
{
    void OnActivated();
}
