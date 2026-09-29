namespace VPNRouter.Core.Services.UpdateSources;

internal readonly record struct ParsedRelease(GitHubRelease Release, string Tag, UpdateChecker.SemVer? Parsed);

internal static class ReleaseCandidates
{
    internal static IEnumerable<ParsedRelease> Parse(IEnumerable<GitHubRelease> releases) =>
        releases.Select(r =>
        {
            var tag = (r.TagName ?? string.Empty).TrimStart('v');
            return new ParsedRelease(r, tag, UpdateChecker.TryParseSemVer(tag, out var v) ? v : (UpdateChecker.SemVer?)null);
        });

    internal static List<ParsedRelease> NewerThan(
        GitHubRelease[] releases, bool includePrereleases, UpdateChecker.SemVer current) =>
        Parse(releases.Where(r => !r.Draft && (includePrereleases || !r.Prerelease)))
            .Where(r => r.Parsed != null && r.Parsed.Value.CompareTo(current) > 0)
            .OrderByDescending(r => r.Parsed!.Value)
            .ToList();
}
