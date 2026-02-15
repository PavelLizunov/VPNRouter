using VPNRouter.Core.Interfaces;
using VPNRouter.Core.Models;
using VPNRouter.Core.Services;

namespace VPNRouter.CLI.Commands;

public static class ProfileSourceFactory
{
    public static List<IProfileSource> Create(AppSettings settings)
    {
        var sources = new List<IProfileSource>();
        int priority = 10;

        foreach (var src in settings.ProfileSources)
        {
            switch (src.Type?.ToLowerInvariant())
            {
                case "github" when !string.IsNullOrEmpty(src.Url):
                    sources.Add(new GitHubProfileSource(src.Url, priority));
                    break;

                case "local" when !string.IsNullOrEmpty(src.Path):
                    sources.Add(new LocalProfileSource(src.Path, priority + 10));
                    break;
            }
            priority += 10;
        }

        // Always include default.json from app directory as fallback
        var appDir = AppContext.BaseDirectory;
        var defaultJson = Path.Combine(appDir, "profiles", "default.json");
        if (File.Exists(defaultJson))
            sources.Add(new LocalProfileSource(defaultJson, 80));

        // Also check %ProgramData% profiles dir
        var programDataProfiles = Environment.ExpandEnvironmentVariables(
            @"%ProgramData%\VPNRouter\profiles\default.json");
        if (File.Exists(programDataProfiles))
            sources.Add(new LocalProfileSource(programDataProfiles, 85));

        // Built-in is always last resort
        sources.Add(new BuiltInProfileSource());

        return sources;
    }
}
