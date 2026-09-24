using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Fakt.Core.Settings;
using Fakt.Desktop.Composition;
using Fakt.Desktop.Mvvm;
using Fakt.Desktop.Services;
using Fakt.Desktop.ViewModels.Admin;

namespace Fakt.Desktop.ViewModels;

/// <summary>
/// Первый запуск: текущая учётная запись Windows явно назначается владельцем (роль «Администратор»).
/// Пароли вида admin/admin не создаются: роли привязаны к пользователям и группам Windows.
/// </summary>
public sealed class OwnerSetupViewModel : DialogViewModel
{
    private readonly AppServices _services;
    private string _operatorName;
    private string _adminName;
    private bool _machineScope;
    private string _error;

    public OwnerSetupViewModel(AppServices services)
    {
        _services = services;
        Title = "Первый запуск FAKT: назначение владельца";
        PrimaryText = "Назначить меня владельцем";
        SecondaryText = "Выйти";
        Width = 680;
        Operators = new ObservableCollection<PrincipalItemViewModel>();
        Administrators = new ObservableCollection<PrincipalItemViewModel>();
        AddOperatorCommand = new RelayCommand(() => Add(OperatorName, Operators, () => OperatorName = null), () => !string.IsNullOrWhiteSpace(OperatorName));
        AddAdminCommand = new RelayCommand(() => Add(AdminName, Administrators, () => AdminName = null), () => !string.IsNullOrWhiteSpace(AdminName));
        RemoveCommand = new RelayCommand(p =>
        {
            Operators.Remove(p as PrincipalItemViewModel);
            Administrators.Remove(p as PrincipalItemViewModel);
        });
    }

    public string CurrentUser => _services.Identity.UserName;
    public string CurrentSid => _services.Identity.UserSid;
    public string ConfigFolder => _services.Paths.MachineDirectory;
    public ObservableCollection<PrincipalItemViewModel> Operators { get; }
    public ObservableCollection<PrincipalItemViewModel> Administrators { get; }
    public ICommand AddOperatorCommand { get; }
    public ICommand AddAdminCommand { get; }
    public ICommand RemoveCommand { get; }

    public string OperatorName { get => _operatorName; set => SetProperty(ref _operatorName, value); }
    public string AdminName { get => _adminName; set => SetProperty(ref _adminName, value); }
    public bool MachineScope { get => _machineScope; set => SetProperty(ref _machineScope, value); }
    public string Error { get => _error; private set => SetProperty(ref _error, value); }

    private void Add(string name, ObservableCollection<PrincipalItemViewModel> target, Action clear)
    {
        var resolved = _services.Identity.Resolve(name);
        if (resolved == null)
        {
            Error = $"«{name}» не найдено. Укажите ДОМЕН\\имя, КОМПЬЮТЕР\\имя или SID.";
            return;
        }

        if (target.All(p => p.Sid != resolved.Sid))
        {
            target.Add(new PrincipalItemViewModel(new PrincipalEntry { Sid = resolved.Sid, DisplayName = resolved.DisplayName, IsGroup = resolved.IsGroup }));
        }

        Error = null;
        clear();
    }

    public override bool OnConfirm()
    {
        try
        {
            _services.Settings.InitializeOwner(Administrators.Select(a => a.Entry), Operators.Select(o => o.Entry),
                MachineScope ? SecretScope.LocalMachine : SecretScope.CurrentUser);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.IO.IOException)
        {
            Error = "Не удалось сохранить настройки владельца: " + ex.Message;
            return false;
        }
    }
}
