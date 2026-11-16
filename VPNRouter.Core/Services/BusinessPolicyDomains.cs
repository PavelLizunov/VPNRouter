using System.Globalization;
using System.Net;
using System.Text;

namespace VPNRouter.Core.Services;

internal static class BusinessPolicyDomains
{
    internal const string PortalDomain = "admin.corp.ninitux.com";
    internal static IReadOnlyList<string> ProtectedDirectDomains { get; } =
        Array.AsReadOnly(new[] { "uiscom.ru", "uiscom.co", "comagic.ru", "callbackhunter.com" });
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Lazy<PublicSuffixes> Suffixes = new(ReadSuffixes);

    internal static void ValidateRules(IReadOnlyList<BusinessPolicyRule> rules)
    {
        try
        {
            if (rules.Count > 512) throw Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in rules)
            {
                if (rule == null || rule.Domain != NormalizeDns(rule.Domain) || IsProtected(rule.Domain) ||
                    rule.Action is not ("direct" or "proxy") || !seen.Add(rule.Domain) ||
                    rule.Comment == null || rule.Comment.Any(char.IsControl) ||
                    StrictUtf8.GetByteCount(rule.Comment) > 256) throw Invalid();
            }
            for (var i = 0; i < rules.Count; i++)
                for (var j = i + 1; j < rules.Count; j++)
                    if (rules[i].Action != rules[j].Action && Intersects(rules[i].Domain, rules[j].Domain))
                        throw Invalid();
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or FormatException)
        { throw Invalid(); }
    }

    internal static string NormalizeInput(string input)
    {
        try
        {
            if (input is not { Length: > 0 and <= 4096 } || input.Any(char.IsControl)) throw Invalid();
            var host = input.Trim();
            if (host.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(host, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
                    uri.HostNameType != UriHostNameType.Dns || host.Contains('\\')) throw Invalid();
                host = uri.IdnHost;
            }
            if (host.EndsWith('.')) host = host[..^1];
            return NormalizeDns(host);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or FormatException)
        { throw Invalid(); }
    }

    internal static bool IsProtected(string domain) =>
        Intersects(domain, PortalDomain) || ProtectedDirectDomains.Any(value => Intersects(domain, value));

    internal static bool Intersects(string left, string right) => left == right ||
        left.EndsWith("." + right, StringComparison.Ordinal) || right.EndsWith("." + left, StringComparison.Ordinal);

    internal static string NormalizeDns(string input)
    {
        if (input is not { Length: > 0 and <= 253 } || input.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            input.IndexOfAny(['/', '\\', ':', '?', '#', '@', '[', ']', '%', '*']) >= 0) throw Invalid();
        var idn = new IdnMapping { UseStd3AsciiRules = true };
        var domain = idn.GetAscii(input).ToLowerInvariant();
        if (domain.Length > 253 || domain.IndexOf('.') < 1 || IPAddress.TryParse(domain, out _) ||
            domain.All(c => char.IsAsciiDigit(c) || c == '.')) throw Invalid();
        foreach (var label in domain.Split('.'))
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))) throw Invalid();
        // Validate A-labels too; GetAscii alone may leave malformed xn-- labels unchanged.
        if (idn.GetAscii(idn.GetUnicode(domain)).ToLowerInvariant() != domain || IsPublicSuffix(domain)) throw Invalid();
        return domain;
    }

    private static bool IsPublicSuffix(string domain)
    {
        var rules = Suffixes.Value;
        if (rules.Exceptions.Contains(domain)) return false;
        if (rules.Exact.Contains(domain)) return true;
        var dot = domain.IndexOf('.');
        return dot >= 0 && rules.Wildcards.Contains(domain[(dot + 1)..]);
    }

    private static PublicSuffixes ReadSuffixes()
    {
        using var stream = typeof(BusinessPolicyDomains).Assembly.GetManifestResourceStream(
            "VPNRouter.Business.PublicSuffixList") ?? throw Invalid();
        using var reader = new StreamReader(stream, StrictUtf8);
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var wildcard = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        var idn = new IdnMapping { UseStd3AsciiRules = true };
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var target = exact;
            if (line.StartsWith("*.", StringComparison.Ordinal)) { target = wildcard; line = line[2..]; }
            else if (line.StartsWith('!')) { target = exceptions; line = line[1..]; }
            target.Add(idn.GetAscii(line).ToLowerInvariant());
        }
        if (exact.Count == 0) throw Invalid();
        return new(exact, wildcard, exceptions);
    }

    private sealed record PublicSuffixes(HashSet<string> Exact, HashSet<string> Wildcards, HashSet<string> Exceptions);
    private static InvalidDataException Invalid() => new("Invalid Business policy domains.");
}
