using System.Buffers.Binary;
using System.IO.Compression;

namespace VPNRouter.Core.Services;

public static class RuleSetFile
{
    // RuleSetVersionCurrent of the pinned sing-box core; that core exits at startup on a newer file.
    private const byte MaxVersion = 5;
    private const int HeaderLength = 4;
    private const int ChecksumLength = 4;
    internal const long MaxFileBytes = 64L * 1024 * 1024;
    private const long MaxInflatedBytes = 256L * 1024 * 1024;

    // Envelope and decompression bounds are checked before handing the local file to the runtime parser.
    public static bool IsValidBinary(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes)
            return false;
        return IsValidBinary(File.ReadAllBytes(path));
    }

    public static bool IsValidBinary(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Length <= HeaderLength + ChecksumLength)
            return false;
        if (file[0] != (byte)'S' || file[1] != (byte)'R' || file[2] != (byte)'S')
            return false;
        if (file[3] is 0 or > MaxVersion)
            return false;

        uint checksum = 1;
        long inflated = 0;
        try
        {
            using var body = new MemoryStream(file, HeaderLength, file.Length - HeaderLength, writable: false);
            using var zlib = new ZLibStream(body, CompressionMode.Decompress);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
            {
                inflated += read;
                if (inflated > MaxInflatedBytes)
                    return false;
                checksum = Adler32(checksum, buffer.AsSpan(0, read));
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        // .NET does not report a zlib stream that was cut short, so the trailing Adler-32 is compared here.
        var expected = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(file.Length - ChecksumLength));
        return inflated > 0 && checksum == expected;
    }

    internal static Func<string, CancellationToken, Task<bool>> ForRuntime(Models.SingBoxSettings settings) =>
        (path, ct) => CheckContentsAsync(path, ct, new ProcessRunner(), settings);

    internal static Task<bool> CheckContentsAsync(string path, CancellationToken ct) =>
        CheckContentsAsync(path, ct, new ProcessRunner());

    internal static async Task<bool> CheckContentsAsync(string path, CancellationToken ct, IProcessRunner runner,
        Models.SingBoxSettings? settings = null)
    {
        // Constructing this config loads the local rule-set with the runtime parser.
        // There are no inbounds, remote rule-sets, or running services in a config check.
        var config = new System.Text.Json.Nodes.JsonObject
        {
            ["route"] = new System.Text.Json.Nodes.JsonObject
            {
                ["rule_set"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "local", ["tag"] = "candidate", ["format"] = "binary", ["path"] = Path.GetFullPath(path),
                })
            }
        }.ToJsonString();
        try
        {
#if PLATFORM_ANDROID
            ct.ThrowIfCancellationRequested();
            // Worker threads need the application's shared loader to resolve the gomobile AAR.
            var loader = global::Android.App.Application.Context.ClassLoader;
            if (loader == null) return false;
            using var libbox = loader.LoadClass("io.nekohasekai.libbox.Libbox");
            using var stringClass = loader.LoadClass("java.lang.String");
            if (libbox == null || stringClass == null) return false;
            using var check = libbox.GetMethod("checkConfig", stringClass);
            if (check == null) return false;
            using var content = new Java.Lang.String(config);
            using var result = check.Invoke(null, content);
            return true;
#else
            var policy = SingBoxRuntimePolicy.Current;
            policy?.Authorize(SingBoxRuntimeOperation.Verify);
            var executable = policy?.SelectedExecutablePath ?? AppPaths.SingBoxDeploymentSource(
                AppPaths.BundledSingBoxExePath, AppPaths.DeployedSingBoxExePath(settings));
            // Pin relative configured deployments to the file startup checks, without searching PATH.
            var request = new ProcessRequest(Path.GetFullPath(executable), ["check", "-c", "stdin"], StdinInput: config,
                Timeout: RuleSetCacheManager.FetchTimeout);
            var result = await runner.RunAsync(request, ct).ConfigureAwait(false);
            return result.ExitCode == 0 && !result.TimedOut;
#endif
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static uint Adler32(uint checksum, ReadOnlySpan<byte> data)
    {
        const uint Modulus = 65521;
        uint low = checksum & 0xFFFF;
        uint high = checksum >> 16;
        foreach (var value in data)
        {
            low = (low + value) % Modulus;
            high = (high + low) % Modulus;
        }
        return (high << 16) | low;
    }
}
