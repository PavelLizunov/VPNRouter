using System.Text.RegularExpressions;

namespace VPNRouter.Core.Platform.macOS;

/// <summary>
/// The single renderer for the managed macOS sudoers policy. No other code in
/// the app may build a NOPASSWD line.
/// </summary>
public static partial class MacPrivilegePolicy
{
    public const string TemplateFileName = "sudoers.template";

    public const string DefaultTemplate =
        "# vpnrouter policy v2 (managed; do not edit)\n" +
        "# Root-owned launcher; no user-writable executables or wildcard arguments.\n" +
        "{{USER}} ALL=(root) NOPASSWD: " + MacPrivilegePaths.HelperPath + "\n";

    private static readonly Regex UserNamePattern = new(
        "^[A-Za-z0-9._-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Render(string userName) => RenderFromTemplate(DefaultTemplate, userName);

    public static string RenderFromTemplate(string template, string userName)
    {
        ValidateUserName(userName);
        if (string.IsNullOrWhiteSpace(template) ||
            !template.Contains("{{USER}}", StringComparison.Ordinal))
            throw new ArgumentException("The sudoers template has no {{USER}} placeholder.", nameof(template));
        return template.Replace("{{USER}}", userName, StringComparison.Ordinal);
    }

    public static void ValidateUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName) || !UserNamePattern.IsMatch(userName))
            throw new ArgumentException(
                "The invoking macOS user name is not a safe sudoers principal.",
                nameof(userName));
    }

    /// <summary>
    /// Returns true when rendered policy text is acceptable to install. This is
    /// a contract test guard as well as a last-moment app-side check.
    /// </summary>
    public static bool IsManagedPolicy(string policy)
    {
        return IsManagedPolicy(policy, userName: null);
    }

    public static bool IsManagedPolicy(string policy, string? userName)
    {
        if (string.IsNullOrWhiteSpace(policy)) return false;
        if (!policy.Contains(MacPrivilegePaths.PolicyHeader, StringComparison.Ordinal))
            return false;

        var lines = policy.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var rules = lines.Where(line => !line.StartsWith('#')).ToArray();
        if (rules.Length != 1) return false;

        var rule = rules[0];
        if (userName is not null)
        {
            ValidateUserName(userName);
            if (!string.Equals(
                    rule,
                    $"{userName} ALL=(root) NOPASSWD: {MacPrivilegePaths.HelperPath}",
                    StringComparison.Ordinal))
                return false;
        }

        return rule.EndsWith(" ALL=(root) NOPASSWD: " + MacPrivilegePaths.HelperPath,
                   StringComparison.Ordinal)
               && !rule.Contains('*')
               && !rule.Contains("/Users/", StringComparison.Ordinal)
               && !rule.Contains("sing-box", StringComparison.Ordinal)
               && !rule.Contains("pfctl", StringComparison.Ordinal)
               && !rule.Contains("/bin/kill", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detects the known pre-migration shapes, including the DMG guide variant
    /// that had no marker. Used only to explain a migration, never as authority.
    /// </summary>
    public static bool LooksLikeLegacyPolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy)) return false;
        return policy.Contains("NOPASSWD", StringComparison.Ordinal)
               && (policy.Contains("/Users/", StringComparison.Ordinal)
                   || policy.Contains("bin/sing-box", StringComparison.Ordinal)
                   || policy.Contains("pkill", StringComparison.Ordinal)
                   || policy.Contains("pfctl", StringComparison.Ordinal)
                   || policy.Contains("/bin/kill", StringComparison.Ordinal));
    }
}
