using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using Fakt.Core.Security;

namespace Fakt.Infrastructure.Security;

/// <summary>Учётная запись Windows текущего процесса. Роли FAKT привязываются к SID пользователей и групп.</summary>
public sealed class WindowsIdentityProvider : IIdentityProvider
{
    private readonly WindowsIdentity _identity;
    private readonly WindowsPrincipal _principal;

    public WindowsIdentityProvider()
    {
        _identity = WindowsIdentity.GetCurrent();
        _principal = new WindowsPrincipal(_identity);
        UserName = _identity.Name;
        UserSid = _identity.User?.Value;
        GroupSids = (_identity.Groups ?? new IdentityReferenceCollection())
            .Select(g => g.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string UserName { get; }

    public string UserSid { get; }

    public IReadOnlyCollection<string> GroupSids { get; }

    public bool IsMember(string sid)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return false;
        }

        if (string.Equals(sid, UserSid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return _principal.IsInRole(new SecurityIdentifier(sid));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (SystemException)
        {
            return false;
        }
    }

    public ResolvedPrincipal Resolve(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName))
        {
            return null;
        }

        var name = accountName.Trim();
        try
        {
            SecurityIdentifier sid;
            if (name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
            {
                sid = new SecurityIdentifier(name);
            }
            else
            {
                sid = (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
            }

            return new ResolvedPrincipal
            {
                Sid = sid.Value,
                DisplayName = NameOf(sid.Value),
                IsGroup = !string.Equals(sid.Value, UserSid, StringComparison.OrdinalIgnoreCase) &&
                          (sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
                           GroupSids.Contains(sid.Value, StringComparer.OrdinalIgnoreCase)),
            };
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (SystemException)
        {
            return null;
        }
    }

    public string NameOf(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException || ex is ArgumentException || ex is SystemException)
        {
            return sid;
        }
    }
}
