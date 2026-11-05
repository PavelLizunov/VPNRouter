using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VPNRouter.Core.Services;
using VPNRouter.Core.Services.UpdateSources;
using VPNRouter.Core.Platform;

namespace VPNRouter.App.Services;

// Only the trusted provisioning bundle supplies this control-plane address.
internal sealed class BusinessUpdateSource : IUpdateSource
{
    internal const int ManifestLimit = 16 * 1024;
    internal const long AssetLimit = 512L * 1024 * 1024;
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
    private readonly Uri? _endpoint;
    private readonly string _currentVersion;
    private readonly IDesktopInstaller _installer;
    private readonly HttpClient _http;
    private readonly Func<Task>? _prepare;
    private UpdateSourceInfo? _offer;
    private UpdateSourceInfo? _downloadedOffer;
    private string? _stagedPath;
    internal bool IsConfigured { get; }
    public string SourceId => "business";

    internal BusinessUpdateSource(Uri? endpoint, string currentVersion, IDesktopInstaller installer,
        Func<Task>? prepare = null, HttpClient? http = null, bool? windowsX64 = null)
    {
        if (endpoint != null) RequireHttps(endpoint);
        _endpoint = endpoint;
        _currentVersion = currentVersion;
        _installer = installer;
        _prepare = prepare;
        _http = http ?? SharedHttp;
        IsConfigured = endpoint != null && (windowsX64 ??
            (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64));
    }

    public async Task<UpdateSourceInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            request.Headers.UserAgent.ParseAdd("VPNRouterBusiness");
            using var response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > ManifestLimit ||
                response.Content.Headers.ContentType?.MediaType != "application/json")
                throw InvalidFeed();
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var chunk = new byte[2048];
            int read;
            while ((read = await input.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + read > ManifestLimit) throw InvalidFeed();
                bytes.Write(chunk, 0, read);
            }
            var json = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            _offer = Parse(json);
            return _offer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw InvalidFeed(); } // A private feed URI must never enter exception logs/UI.
    }

    private UpdateSourceInfo? Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 6 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count() ||
            root.GetProperty("schema").GetInt32() != 1 ||
            root.GetProperty("edition").GetString() != "Business" ||
            root.GetProperty("architecture").GetString() != "win-x64") throw InvalidFeed();
        var version = root.GetProperty("version").GetString();
        if (!UpdateChecker.TryParseSemVer(version, out var next) || next.Rc != null || version != next.ToString() ||
            !UpdateChecker.TryParseSemVer(_currentVersion, out var current)) throw InvalidFeed();
        var size = root.GetProperty("size").GetInt64();
        var sha = root.GetProperty("sha256").GetString();
        var name = root.GetProperty("asset").GetString();
        if (size is <= 0 or > AssetLimit || sha?.Length != 64 || !sha.All(Uri.IsHexDigit) ||
            name != $"VPNRouterBusiness-update-v{version}-win-x64.zip") throw InvalidFeed();
        var asset = new Uri(root.GetProperty("url").GetString()!, UriKind.Absolute);
        RequireHttps(asset);
        if (asset.Authority != _endpoint!.Authority || asset.Query.Length != 0 ||
            !asset.AbsolutePath.EndsWith('/' + name, StringComparison.Ordinal)) throw InvalidFeed();
        if (next.CompareTo(current) <= 0) return null;
        return new(version!, string.Empty, name, asset.AbsoluteUri, size, sha.ToLowerInvariant(), false, string.Empty);
    }

    public async Task<string> DownloadAsync(UpdateSourceInfo info, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        RequireOffer(info);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            var path = await _installer.DownloadAndStageAsync(info, progress, deadline.Token).ConfigureAwait(false);
            _downloadedOffer = info;
            _stagedPath = path;
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new IOException(VPNRouter.Core.Localization.Strings.BusinessUpdateDownloadFailed); }
    }

    public async Task<bool> ApplyAsync(UpdateSourceInfo info, string stagedPath, CancellationToken ct = default)
    {
        if (!IsConfigured || info == null || !ReferenceEquals(info, _downloadedOffer) ||
            stagedPath != _stagedPath) throw new InvalidDataException("Unverified Business update refused.");
        ct.ThrowIfCancellationRequested();
        if (_prepare == null) throw new InvalidOperationException("Business update preparation is unavailable.");
        await _prepare().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!await _installer.ApplyStagedAsync(info, stagedPath, ct).ConfigureAwait(false))
                throw new IOException("Business update installation did not start.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new IOException(VPNRouter.Core.Localization.Strings.BusinessUpdateInstallFailed); }
        return true;
    }

    private void RequireOffer(UpdateSourceInfo info)
    {
        if (!IsConfigured || !ReferenceEquals(info, _offer) || info == null)
            throw new InvalidDataException("Unverified Business update refused.");
    }

    private static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw InvalidFeed();
    }

    private static InvalidDataException InvalidFeed() => new("Business update information is unavailable or invalid.");
}
