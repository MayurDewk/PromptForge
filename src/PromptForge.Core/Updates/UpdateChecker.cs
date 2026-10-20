using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PromptForge.Core.Updates;

/// <summary>A published release that is newer than the running copy. AssetUrl is this platform's download (exe or zip).</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string AssetUrl, string ChecksumsUrl, string PageUrl);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Checks GitHub Releases for a newer PromptForge and downloads it with its SHA-256 verified.
/// Sends one anonymous request to api.github.com; no prompt text, settings or keys are involved.
/// Installing is left to the caller (the downloaded exe installs itself like a manual download).
/// Downloads are only accepted from this repository's GitHub release URLs, and are size-capped.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string repository = UpdateChecker.DefaultRepository, string assetName = UpdateChecker.ExeAssetName)
{
    public const string DefaultRepository = "Maneet7805/PromptForge";
    public const string ExeAssetName = "PromptForge.exe";
    public const string ChecksumsAssetName = "SHA256SUMS.txt";

    /// <summary>Far above any real release; stops a bad response from filling the disk.</summary>
    internal const long MaxDownloadBytes = 400L * 1024 * 1024;
    private const int MaxChecksumsChars = 64 * 1024;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Returns the latest release if it is newer than <paramref name="currentVersion"/>, otherwise null.</summary>
    public async Task<ReleaseInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        AddHeaders(request, currentVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered {(int)response.StatusCode} when checking for updates.");
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var latest = ParseLatestRelease(json, assetName, repository);
            return latest is not null && IsNewer(latest.Version, currentVersion) ? latest : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateException("Couldn't reach GitHub to check for updates.", ex);
        }
    }

    /// <summary>
    /// Downloads the release asset into <paramref name="directory"/> and checks it against the release's
    /// SHA256SUMS.txt. Throws <see cref="UpdateException"/> (and deletes the file) if they don't match.
    /// </summary>
    public async Task<string> DownloadVerifiedAsync(ReleaseInfo release, string directory, string currentVersion, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, assetName);

        try
        {
            var sums = await GetStringAsync(release.ChecksumsUrl, currentVersion, timeout.Token).ConfigureAwait(false);
            var expected = FindChecksum(sums, assetName)
                ?? throw new UpdateException($"The release has no checksum for {assetName}, so it wasn't installed.");

            using (var request = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl))
            {
                AddHeaders(request, currentVersion);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new UpdateException($"Downloading the update failed ({(int)response.StatusCode}).");
                if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                    throw new UpdateException("The update is unexpectedly large, so it wasn't downloaded.");
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var file = File.Create(path);
                await CopyCappedAsync(source, file, timeout.Token).ConfigureAwait(false);
            }

            string actual;
            await using (var file = File.OpenRead(path))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, timeout.Token).ConfigureAwait(false));

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
                throw new UpdateException("The downloaded update didn't match its published checksum, so it wasn't installed.");
            }
            return path;
        }
        catch (UpdateException)
        {
            TryDelete(path);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            TryDelete(path);
            throw new UpdateException("Downloading the update failed. Try again later.", ex);
        }
    }

    private static async Task CopyCappedAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes) throw new UpdateException("The update is unexpectedly large, so it wasn't downloaded.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the fields we need from GitHub's "latest release" JSON. Null if the release lacks the asset or
    /// checksums, or if either would be downloaded from anywhere other than this repository's releases.
    /// </summary>
    public static ReleaseInfo? ParseLatestRelease(string json, string assetName = ExeAssetName, string repository = DefaultRepository)
    {
        var allowedPrefix = $"https://github.com/{repository}/releases/download/";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
            var tag = Str(root, "tag_name");
            if (tag is null || !TryParseVersion(tag, out var version)) return null;

            string? assetUrl = null, sums = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = Str(asset, "name");
                    var url = Str(asset, "browser_download_url");
                    if (url is null || !url.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase) || url.Contains("..", StringComparison.Ordinal)) continue;
                    if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase)) assetUrl = url;
                    else if (string.Equals(name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase)) sums = url;
                }
            }
            if (assetUrl is null || sums is null) return null;
            return new ReleaseInfo(version, tag, assetUrl, sums, Str(root, "html_url") ?? "");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>True when <paramref name="latest"/> is a higher version than <paramref name="current"/> ("1.0.0", "v1.0.0" or "1.0.0+abc").</summary>
    public static bool IsNewer(Version latest, string current) =>
        TryParseVersion(current, out var running) && Normalize(latest) > Normalize(running);

    public static bool TryParseVersion(string text, out Version version)
    {
        var trimmed = text.Trim().TrimStart('v', 'V');
        var cut = trimmed.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0) trimmed = trimmed[..cut];
        if (Version.TryParse(trimmed, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }
        version = new Version(0, 0);
        return false;
    }

    /// <summary>Finds a file's hash in sha256sum-style text ("hash  name" per line).</summary>
    public static string? FindChecksum(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var hash = line[..space];
            var name = line[space..].Trim().TrimStart('*');
            if (hash.Length == 64 && string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) return hash.ToLowerInvariant();
        }
        return null;
    }

    private async Task<string> GetStringAsync(string url, string currentVersion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request, currentVersion);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"Downloading the release checksums failed ({(int)response.StatusCode}).");
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return text.Length > MaxChecksumsChars ? throw new UpdateException("The release checksums file is malformed.") : text;
    }

    // GitHub rejects API requests without a User-Agent.
    private static void AddHeaders(HttpRequestMessage request, string currentVersion) =>
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PromptForge", TryParseVersion(currentVersion, out var v) ? v.ToString() : "0.0.0"));

    // Version("1.1") has Build = -1, which compares lower than 1.1.0; treat missing parts as 0.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
