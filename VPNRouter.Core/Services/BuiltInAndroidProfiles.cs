using VPNRouter.Core.Models;

namespace VPNRouter.Core.Services;

/// <summary>
/// Android counterpart to <see cref="BuiltInProfiles"/>. The desktop catalog
/// keys on <c>process_name</c> ("Discord.exe", "chrome.exe"); Android's
/// VpnService API operates on package IDs ("com.discord", "com.android.chrome"),
/// so the two catalogs share the same category names and intent but cannot
/// share rule data — process names don't translate to Android's app
/// sandboxing model.
///
/// <para>Mirrors the JSON file <c>profiles/default-android.json</c> verbatim
/// so the desktop reference catalog stays in lockstep with what the Android
/// app surfaces. The file is the canonical source for review; this code is
/// the canonical source for runtime — the Android port reads this directly
/// to avoid bundling assets and copying them into FilesDir on first launch.</para>
/// </summary>
public static class BuiltInAndroidProfiles
{
    public static ProfileCollection Get() => new()
    {
        Profiles = new List<Profile>
        {
            new()
            {
                Name = "Discord_Privacy",
                Description = "Discord voice & chat",
                AndroidPackages = new List<string> { "com.discord" },
                DnsMode = "vpn_only",
                BlockOnVpnFail = true,
            },
            new()
            {
                Name = "Messengers",
                Description = "Telegram, Signal, WhatsApp",
                AndroidPackages = new List<string>
                {
                    "org.telegram.messenger",
                    "org.telegram.messenger.web",
                    "org.telegram.plus",
                    "org.thoughtcrime.securesms",
                    "com.whatsapp",
                    "com.whatsapp.w4b",
                },
                DnsMode = "vpn_only",
                BlockOnVpnFail = false,
            },
            new()
            {
                Name = "AI_Tools",
                Description = "ChatGPT, Claude, Gemini, Perplexity",
                AndroidPackages = new List<string>
                {
                    "com.openai.chatgpt",
                    "com.anthropic.claude",
                    "com.google.android.apps.bard",
                    "com.perplexity.app.android",
                },
                DnsMode = "vpn_only",
                BlockOnVpnFail = false,
            },
            new()
            {
                Name = "Browsers",
                Description = "Chrome, Firefox, Brave, Edge, Opera, Tor",
                AndroidPackages = new List<string>
                {
                    "com.android.chrome",
                    "com.chrome.beta",
                    "com.chrome.dev",
                    "com.chrome.canary",
                    "org.mozilla.firefox",
                    "org.mozilla.firefox_beta",
                    "org.mozilla.fenix",
                    "com.brave.browser",
                    "com.brave.browser_beta",
                    "com.microsoft.emmx",
                    "com.opera.browser",
                    "com.opera.mini.native",
                    "org.torproject.torbrowser",
                    "com.duckduckgo.mobile.android",
                    "com.vivaldi.browser",
                    "com.yandex.browser",
                },
                DnsMode = "vpn_only",
                BlockOnVpnFail = false,
            },
            new()
            {
                Name = "Work_Suite",
                Description = "Slack, Zoom, Teams, Notion, Obsidian",
                AndroidPackages = new List<string>
                {
                    "com.Slack",
                    "us.zoom.videomeetings",
                    "com.microsoft.teams",
                    "com.microsoft.office.outlook",
                    "notion.id",
                    "md.obsidian",
                    "com.figma.mirror",
                },
                DnsMode = "vpn_only",
                BlockOnVpnFail = false,
            },
            new()
            {
                Name = "Streaming",
                Description = "YouTube, Spotify, Twitch, Netflix",
                AndroidPackages = new List<string>
                {
                    "com.google.android.youtube",
                    "com.google.android.apps.youtube.music",
                    "com.spotify.music",
                    "tv.twitch.android.app",
                    "com.netflix.mediaclient",
                    "com.amazon.avod.thirdpartyclient",
                },
                DnsMode = "vpn_only",
                BlockOnVpnFail = false,
            },
            new()
            {
                Name = "Gaming",
                Description = "Steam Link, Battle.net, Epic Games (companion apps)",
                AndroidPackages = new List<string>
                {
                    "com.valvesoftware.android.steam.community",
                    "com.valvesoftware.steamlink",
                    "com.blizzard.messenger",
                    "com.epicgames.portal",
                },
                DnsMode = "smart",
                BlockOnVpnFail = false,
            },
            new()
            {
                Name = "Privacy_Shell",
                Description = "Password managers, secure notes, ProtonMail/Drive/Pass",
                AndroidPackages = new List<string>
                {
                    "com.bitwarden.x8",
                    "im.molly.app",
                    "com.protonvpn.android",
                    "ch.protonmail.android",
                    "ch.protonpass.android",
                    "ch.protondrive.android",
                },
                DnsMode = "vpn_only",
                BlockOnVpnFail = true,
            },
        }
    };
}
