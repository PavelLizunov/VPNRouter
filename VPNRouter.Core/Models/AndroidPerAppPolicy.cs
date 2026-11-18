using System;
using System.Collections.Generic;

namespace VPNRouter.Core.Models;

/// <summary>The app filter Android established and the picker settings used to start that session.</summary>
public sealed record AndroidPerAppPolicy(string ActiveMode, string[] ActivePackages, string RequestedMode, string[] RequestedPackages)
{
    public bool HasSavedChanges(string mode, IEnumerable<string> packages)
    {
        if (!string.Equals(RequestedMode, mode, StringComparison.OrdinalIgnoreCase)) return true;
        // Off remembers the list for a future split session without applying it to this connection.
        return PerAppFilterMode.IsSplit(mode)
            && !new HashSet<string>(RequestedPackages, StringComparer.Ordinal).SetEquals(packages);
    }
}
