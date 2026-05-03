using Android.App;
using Android.Content;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 Phase 1.F (2026-05-04) — minimal persistence for the
/// subscription URL / VLESS URI on Android. Uses
/// <c>SharedPreferences</c> rather than the desktop's
/// <c>%ProgramData%\VPNRouter\config.yaml</c> path because:
///
/// <list type="bullet">
///   <item>Android apps live in a sandboxed
///   <c>/data/data/&lt;pkg&gt;/files</c> dir that <c>AppPaths.ResolveDataDir</c>
///   doesn't currently understand (falls into the Linux branch and tries
///   to use <c>$XDG_CONFIG_HOME</c> which doesn't exist on Android).</item>
///   <item>SharedPreferences is the canonical Android key-value store —
///   atomic commits, survives app restart, OS-managed encryption on
///   modern devices.</item>
///   <item>Phase 1.F only needs ONE knob (the VLESS URI). Pulling in
///   YamlDotNet + the SettingsLoader file-watcher would be overkill.</item>
/// </list>
///
/// <para>Phase 3 (full UI port) replaces this with a proper port of
/// <c>SettingsLoader</c> using <c>Android.App.Application.FilesDir</c>
/// + <c>YamlDotNet</c> so the Android app shares its
/// <c>config.yaml</c> structure with desktop. For now SharedPreferences
/// is the path of least resistance.</para>
/// </summary>
public static class AndroidStorage
{
    private const string PrefsName = "vpnrouter_settings";
    private const string KeyVlessUri = "vless_uri";

    /// <summary>
    /// Read the persisted VLESS URI, or null if none has been stored.
    /// Returns null silently on any error (corrupt prefs, no
    /// Application context yet during early init, etc.) so callers
    /// can fall back to a placeholder URI.
    /// </summary>
    public static string? GetVlessUri()
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return null;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            var v = prefs?.GetString(KeyVlessUri, null);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Persist the VLESS URI. Empty / null clears the entry. Caller
    /// should validate the URI parses with <see cref="VPNRouter.Core.Services.VlessUriParser.Parse"/>
    /// before calling — this method just stores the string verbatim.
    /// </summary>
    /// <returns><c>true</c> on successful commit.</returns>
    public static bool SetVlessUri(string? vlessUri)
    {
        try
        {
            var ctx = Application.Context;
            if (ctx == null) return false;
            var prefs = ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            if (prefs == null) return false;
            using var editor = prefs.Edit();
            if (editor == null) return false;
            if (string.IsNullOrWhiteSpace(vlessUri))
                editor.Remove(KeyVlessUri);
            else
                editor.PutString(KeyVlessUri, vlessUri);
            return editor.Commit();
        }
        catch (System.Exception)
        {
            return false;
        }
    }
}
