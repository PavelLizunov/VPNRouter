using System.Runtime.InteropServices;

namespace VPNRouter.Core.Platform.macOS;

/// <summary>
/// Paths and constants of the managed macOS privilege boundary. The policy
/// renderer, installer and helper contract all read these values so there is
/// one source of truth.
/// </summary>
public static class MacPrivilegePaths
{
    public const string HelperPath = "/Library/PrivilegedHelperTools/com.vpnrouter.privileged";
    public const string RootDir = "/Library/Application Support/VPNRouter";
    public const string RootSingBoxPath = RootDir + "/bin/sing-box";
    public const string ManifestPath = RootDir + "/install.json";
    public const string RunDir = RootDir + "/run";
    public const string AssetRoot = RootDir + "/assets";
    public const string InstallLockPath = RootDir + "/.install.lock";
    public const string PolicyDirectory = "/private/etc/sudoers.d";
    public const string LegacyPolicyPath = PolicyDirectory + "/vpnrouter";
    public const string SudoPath = "/usr/bin/sudo";
    public const string HelperBundleName = "vpnrouter-privileged";
    public const int PolicyVersion = 2;
    public const string PolicyHeader = "# vpnrouter policy v2";

    public static string PolicyPath(int uid) => $"{PolicyDirectory}/vpnrouter-{uid}";

    public static string UserRunDir(int uid) => $"{RunDir}/{uid}";

    public static string UserStagedConfigPath(int uid) => $"{UserRunDir(uid)}/current.json";

    public static string UserAssetDir(int uid) => $"{AssetRoot}/{uid}";

    public static string UserAssetPath(int uid, string fileName) => $"{UserAssetDir(uid)}/{fileName}";

    public static bool IsPrivilegedSingBoxPath(string? path) =>
        string.Equals(path, RootSingBoxPath, StringComparison.Ordinal);

    public static bool IsRootAssetPath(string? path, int uid)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var prefix = UserAssetDir(uid) + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var name = path[prefix.Length..];
        return name.Length > 0 && name != "." && name != ".." && !name.Contains('/');
    }

    public static int CurrentUid => unchecked((int)NativeMethods.GetUid());

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "getuid")]
        internal static extern uint GetUid();
    }
}
