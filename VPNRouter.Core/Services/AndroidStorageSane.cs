using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

public static class AndroidStorageSane
{
    public sealed record EnumKeySpec(
        string Key,
        IReadOnlyCollection<string> Allowed,
        string DefaultValue);

    public sealed record RepairResult(IReadOnlyList<string> Changes);

    public static RepairResult RepairAllOnLoad(
        Func<string, string?> get,
        Action<string, string?> set,
        IEnumerable<EnumKeySpec> enumKeys,
        Action<string, string?>? quarantine = null)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(enumKeys);

        new AppSettings().EnsureSane();

        var changes = new List<string>();

        foreach (var spec in enumKeys)
        {
            string? raw;
            try { raw = get(spec.Key); }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(raw)) continue;

            var match = spec.Allowed.FirstOrDefault(v =>
                string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                if (!string.Equals(match, raw, StringComparison.Ordinal))
                {
                    try { set(spec.Key, match); } catch {  }
                }
                continue;
            }

            try { quarantine?.Invoke(spec.Key, raw); } catch {  }
            try { set(spec.Key, spec.DefaultValue); } catch {  }
            changes.Add(
                $"setting '{spec.Key}' had unknown value '{raw}'; reset to '{spec.DefaultValue}'");
        }

        return new RepairResult(changes);
    }
}
