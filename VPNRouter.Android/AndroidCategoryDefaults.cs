using System.Collections.Generic;

namespace VPNRouter.Android;

internal static class AndroidCategoryDefaults
{
    public const string CustomCatchAllId = "Custom";

    public sealed class CategoryDef
    {
        public string Id { get; set; } = string.Empty;
        public IReadOnlyCollection<string> PackageHints { get; set; } = System.Array.Empty<string>();
    }

    public static readonly IReadOnlyList<CategoryDef> All = new[]
    {
        new CategoryDef
        {
            Id = "Discord_Privacy",
            PackageHints = new[]
            {
                "com.discord",
                "com.discord.canary",
                "com.discord.beta",
            },
        },
        new CategoryDef
        {
            Id = "Messengers",
            PackageHints = new[]
            {
                "org.telegram.messenger",
                "org.telegram.messenger.web",
                "org.thunderdog.challegram",
                "org.thoughtcrime.securesms",
                "com.whatsapp",
                "com.whatsapp.w4b",
                "com.viber.voip",
                "ru.mail.mailapp",
                "ru.mail.my",
                "com.vkontakte.android",
                "com.facebook.orca",
                "com.facebook.mlite",
                "com.skype.raider",
                "com.tencent.mm",
                "jp.naver.line.android",
                "im.threema.app",
                "im.threema.app.work",
                "org.session.app",
                "chat.delta",
                "com.element.android",
            },
        },
        new CategoryDef
        {
            Id = "AI_Tools",
            PackageHints = new[]
            {
                "com.openai.chatgpt",
                "com.anthropic.claude",
                "com.google.android.apps.bard",
                "com.microsoft.bing",
                "com.perplexity.app.android",
                "co.poe.android",
                "ai.character.app",
                "com.deepseek.chat",
                "com.x.grok",
            },
        },
        new CategoryDef
        {
            Id = "Browsers",
            PackageHints = new[]
            {
                "com.android.chrome",
                "com.chrome.beta",
                "com.chrome.canary",
                "com.chrome.dev",
                "org.mozilla.firefox",
                "org.mozilla.firefox_beta",
                "org.mozilla.fennec_fdroid",
                "org.mozilla.focus",
                "com.brave.browser",
                "com.opera.browser",
                "com.opera.browser.beta",
                "com.opera.mini.native",
                "com.opera.gx",
                "com.microsoft.emmx",
                "com.vivaldi.browser",
                "com.duckduckgo.mobile.android",
                "com.kiwibrowser.browser",
                "com.sec.android.app.sbrowser",
                "com.yandex.browser",
                "com.UCMobile.intl",
                "org.torproject.torbrowser",
                "org.torproject.torbrowser_alpha",
            },
        },
        new CategoryDef
        {
            Id = "Work_Suite",
            PackageHints = new[]
            {
                "com.Slack",
                "us.zoom.videomeetings",
                "com.microsoft.teams",
                "com.microsoft.office.outlook",
                "com.microsoft.office.word",
                "com.microsoft.office.excel",
                "com.microsoft.office.powerpoint",
                "com.notion.id",
                "md.obsidian",
                "com.google.android.apps.docs",
                "com.google.android.gm",
                "com.google.android.calendar",
                "com.atlassian.android.jira.core",
                "com.atlassian.confluence.server",
                "com.todoist",
                "com.trello",
                "com.asana.app",
                "com.linear",
                "com.linkedin.android",
            },
        },
        new CategoryDef
        {
            Id = "Streaming",
            PackageHints = new[]
            {
                "com.spotify.music",
                "com.google.android.youtube",
                "com.google.android.apps.youtube.music",
                "com.netflix.mediaclient",
                "com.amazon.avod.thirdpartyclient",
                "com.disney.disneyplus",
                "tv.twitch.android.app",
                "com.hbo.hbonow",
                "com.hbo.hbomax",
                "com.apple.android.music",
                "com.deezer.android.app",
                "com.soundcloud.android",
                "com.tidal.musicstreaming",
                "com.plexapp.android",
                "org.videolan.vlc",
            },
        },
        new CategoryDef
        {
            Id = "Gaming",
            PackageHints = new[]
            {
                "com.valvesoftware.android.steam.community",
                "com.epicgames.fortnite",
                "com.epicgames.portal",
                "com.blizzard.bnet.app",
                "com.riotgames.league.wildrift",
                "com.activision.callofduty.shooter",
                "com.ea.gp.fifamobile",
                "com.miHoYo.GenshinImpact",
                "com.HoYoverse.hkrpgoversea",
                "com.roblox.client",
                "com.mojang.minecraftpe",
                "com.mojang.minecraftedu",
                "com.supercell.clashofclans",
                "com.supercell.brawlstars",
                "com.king.candycrushsaga",
                "com.gameloft.android.ANMP.GloftA9HM",
            },
        },
        new CategoryDef
        {
            Id = "Virtualization",
            PackageHints = new[]
            {
                "com.virtualbox.app",
                "com.vmware.view.client.android",
                "com.parallels.access",
                "com.microsoft.rdc.androidx",
                "com.realvnc.viewer.android",
                "com.android.virtmgr",
                "com.termux",
                "com.qemu.app",
                "com.kingoapp.apk",
            },
        },
        new CategoryDef
        {
            Id = "Privacy_Shell",
            PackageHints = new[]
            {
                "com.termux",
                "com.termux.api",
                "com.termux.styling",
                "com.termux.tasker",
                "com.termux.x11",
                "org.kde.kdeconnect_tp",
                "com.tutanota.tutanota",
                "ch.protonmail.android",
                "ch.protonvpn.android",
                "ch.proton.pass.android",
                "com.bitwarden.authenticator",
                "com.bitwarden.android",
                "com.x8bit.bitwarden",
                "com.duckduckgo.mobile.android",
                "org.mozilla.focus",
                "org.fdroid.fdroid",
                "io.github.muntashirakon.AppManager",
            },
        },
        new CategoryDef
        {
            Id = CustomCatchAllId,
            PackageHints = System.Array.Empty<string>(),
        },
    };

    public static bool IsCustomCatchAll(string? id)
        => string.Equals(id, CustomCatchAllId, System.StringComparison.OrdinalIgnoreCase);

    public static CategoryDef? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var c in All)
            if (string.Equals(c.Id, id, System.StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }

    public static HashSet<string> AllBuiltInPackages()
    {
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var c in All)
        {
            if (IsCustomCatchAll(c.Id)) continue;
            foreach (var p in c.PackageHints) set.Add(p);
        }
        return set;
    }
}
