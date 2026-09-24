using System;
using System.Collections.Generic;

namespace Fakt.Core.Security;

public enum Role
{
    None,
    Operator,
    Administrator,
}

public enum Permission
{
    /// <summary>Сканирование, анализ структуры, обработка, пауза/остановка.</summary>
    ProcessData,

    SearchData,

    ViewHistory,

    ViewLog,

    /// <summary>Изменение профилей LLM, подключения к БД, параметров обработки, секретов.</summary>
    ManageSettings,

    /// <summary>Применение миграций схемы и перестроение поисковой проекции.</summary>
    ManageDatabase,

    /// <summary>Назначение ролей.</summary>
    ManageAccess,

    /// <summary>Подробный диагностический журнал.</summary>
    ManageDiagnostics,
}

public static class RolePermissions
{
    public static bool Allows(Role role, Permission permission)
    {
        switch (role)
        {
            case Role.Administrator:
                return true;
            case Role.Operator:
                return permission == Permission.ProcessData || permission == Permission.SearchData ||
                       permission == Permission.ViewHistory || permission == Permission.ViewLog;
            default:
                return false;
        }
    }

    public static string Title(Role role)
    {
        switch (role)
        {
            case Role.Administrator: return "Администратор";
            case Role.Operator: return "Оператор";
            default: return "Нет роли";
        }
    }
}

public sealed class AccessDeniedException : Exception
{
    public AccessDeniedException(Permission permission, string userName)
        : base($"Недостаточно прав: действие требует роли «Администратор» ({permission}). Учётная запись: {userName}.")
    {
        Permission = permission;
    }

    public Permission Permission { get; }
}

/// <summary>Текущая учётная запись Windows: SID пользователя и групп.</summary>
public interface IIdentityProvider
{
    string UserName { get; }

    string UserSid { get; }

    IReadOnlyCollection<string> GroupSids { get; }

    /// <summary>
    /// Текущий пользователь совпадает с SID или входит в группу с этим SID (проверка по токену Windows:
    /// группы «только для запрета» при UAC не дают членства).
    /// </summary>
    bool IsMember(string sid);

    /// <summary>Разрешить имя пользователя или группы (DOMAIN\name) в SID. null — не найдено.</summary>
    ResolvedPrincipal Resolve(string accountName);

    /// <summary>Отображаемое имя для SID (DOMAIN\name) или сам SID, если перевод невозможен.</summary>
    string NameOf(string sid);
}

public sealed class ResolvedPrincipal
{
    public string Sid { get; set; }

    public string DisplayName { get; set; }

    public bool IsGroup { get; set; }
}

public interface IAuthorizationService
{
    Role CurrentRole { get; }

    string CurrentUserName { get; }

    bool IsAllowed(Permission permission);

    /// <summary>Проверка в сервисе команды: при отсутствии прав бросает <see cref="AccessDeniedException"/>.</summary>
    void Demand(Permission permission);
}
