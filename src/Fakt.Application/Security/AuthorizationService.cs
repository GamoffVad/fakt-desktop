using System;
using System.Linq;
using Fakt.Core.Security;
using Fakt.Core.Settings;

namespace Fakt.Application.Security;

/// <summary>
/// Роль текущей учётной записи Windows по настройкам доступа: владелец и администраторы — «Администратор»,
/// операторы — «Оператор». Проверка выполняется в сервисах команд (Demand), а не только видимостью кнопок.
/// </summary>
public sealed class AuthorizationService : IAuthorizationService
{
    private readonly IIdentityProvider _identity;
    private readonly Func<AccessSettings> _access;

    public AuthorizationService(IIdentityProvider identity, Func<AccessSettings> access)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _access = access ?? throw new ArgumentNullException(nameof(access));
    }

    public string CurrentUserName => _identity.UserName;

    public string CurrentUserSid => _identity.UserSid;

    public Role CurrentRole
    {
        get
        {
            var access = _access();
            if (access == null || !access.IsInitialized)
            {
                return Role.None;
            }

            if (string.Equals(access.OwnerSid, _identity.UserSid, StringComparison.OrdinalIgnoreCase) ||
                access.Administrators.Any(p => _identity.IsMember(p.Sid)))
            {
                return Role.Administrator;
            }

            return access.Operators.Any(p => _identity.IsMember(p.Sid)) ? Role.Operator : Role.None;
        }
    }

    public bool IsAllowed(Permission permission) => RolePermissions.Allows(CurrentRole, permission);

    public void Demand(Permission permission)
    {
        if (!IsAllowed(permission))
        {
            throw new AccessDeniedException(permission, _identity.UserName);
        }
    }
}
