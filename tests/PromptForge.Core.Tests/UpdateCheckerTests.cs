using System.Net;
using System.Security.Cryptography;
using System.Text;
using PromptForge.Core.Updates;

namespace PromptForge.Core.Tests;

public class UpdateCheckerTests
{
    private const string ExeUrl = "https://github.com/Maneet7805/PromptForge/releases/download/v1.1.0/PromptForge.exe";
    private const string SumsUrl = "https://github.com/Maneet7805/PromptForge/releases/download/v1.1.0/SHA256SUMS.txt";

    private static string ReleaseJson(string tag = "v1.1.0", bool prerelease = false, bool withExe = true)
    {
        var exeAsset = withExe ? "{\"name\":\"PromptForge.exe\",\"browser_download_url\":\"" + ExeUrl + "\"}," : "";
        return "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/Maneet7805/PromptForge/releases/tag/" + tag + "\"," +
               "\"draft\":false,\"prerelease\":" + (prerelease ? "true" : "false") + "," +
               "\"assets\":[" + exeAsset +
               "{\"name\":\"PromptForge-1.1.0-win-x64.zip\",\"browser_download_url\":\"https://github.com/x.zip\"}," +
               "{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"" + SumsUrl + "\"}]}";
    }

    [Theory]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", false)]
    [InlineData("v1.0.0", "1.1.0", false)]
    [InlineData("1.10.0", "1.9.3", true)]
    [InlineData("v1.1", "1.0.9", true)]
    [InlineData("v1.1.0", "1.1.0+abc123", false)]
    public void Compares_versions(string tag, string current, bool newer)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var latest));
        Assert.Equal(newer, UpdateChecker.IsNewer(latest, current));
    }

    [Fact]
    public void Parses_the_latest_release()
    {
        var release = UpdateChecker.ParseLatestRelease(ReleaseJson());
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 1, 0), release.Version);
        Assert.Equal(ExeUrl, release.AssetUrl);
        Assert.Equal(SumsUrl, release.ChecksumsUrl);
    }

    [Theory]
    [InlineData(true, true)]   // prerelease
    [InlineData(false, false)] // missing exe asset
    public void Ignores_prereleases_and_incomplete_releases(bool prerelease, bool withExe)
    {
        Assert.Null(UpdateChecker.ParseLatestRelease(ReleaseJson(prerelease: prerelease, withExe: withExe)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"tag_name":"latest"}""")]
    public void Bad_responses_are_ignored(string json)
    {
        Assert.Null(UpdateChecker.ParseLatestRelease(json));
    }

    [Theory]
    [InlineData("https://evil.example/releases/download/v1.1.0/PromptForge.exe")]
    [InlineData("https://github.com/someone-else/PromptForge/releases/download/v1.1.0/PromptForge.exe")]
    [InlineData("http://github.com/Maneet7805/PromptForge/releases/download/v1.1.0/PromptForge.exe")]
    [InlineData("https://github.com/Maneet7805/PromptForge/releases/download/../../../x/PromptForge.exe")]
    public void Ignores_assets_hosted_outside_this_repository(string url)
    {
        var json = ReleaseJson().Replace(ExeUrl, url);
        Assert.Null(UpdateChecker.ParseLatestRelease(json));
    }

    [Fact]
    public void Picks_the_platform_asset_by_name()
    {
        const string zipUrl = "https://github.com/Maneet7805/PromptForge/releases/download/v1.1.0/PromptForge-macOS-arm64.zip";
        var json = ReleaseJson().Replace("\"assets\":[", "\"assets\":[{\"name\":\"PromptForge-macOS-arm64.zip\",\"browser_download_url\":\"" + zipUrl + "\"},");
        Assert.Equal(zipUrl, UpdateChecker.ParseLatestRelease(json, "PromptForge-macOS-arm64.zip")!.AssetUrl);
        Assert.Null(UpdateChecker.ParseLatestRelease(json, "PromptForge-macOS-x64.zip"));
    }

    [Fact]
    public async Task Download_refuses_an_oversized_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pf-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var checker = new UpdateChecker(new HttpClient(new FakeHandler(r =>
            {
                if (r.RequestUri!.ToString() == SumsUrl) return Text($"{new string('0', 64)}  PromptForge.exe\n");
                var content = new ByteArrayContent([1]);
                content.Headers.ContentLength = UpdateChecker.MaxDownloadBytes + 1;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            })));
            var release = UpdateChecker.ParseLatestRelease(ReleaseJson())!;

            var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadVerifiedAsync(release, dir, "1.0.0"));

            Assert.Contains("large", ex.Message);
            Assert.False(File.Exists(Path.Combine(dir, "PromptForge.exe")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Finds_a_checksum_line()
    {
        var hash = new string('a', 64);
        var sums = $"{new string('b', 64)}  PromptForge-1.1.0-win-x64.zip\r\n{hash}  PromptForge.exe\r\n";
        Assert.Equal(hash, UpdateChecker.FindChecksum(sums, "PromptForge.exe"));
        Assert.Null(UpdateChecker.FindChecksum(sums, "other.exe"));
    }

    [Fact]
    public async Task Check_returns_the_release_only_when_newer()
    {
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(_ => Json(ReleaseJson()))));
        Assert.NotNull(await checker.CheckAsync("1.0.0"));
        Assert.Null(await checker.CheckAsync("1.1.0"));
    }

    [Fact]
    public async Task Check_sends_a_user_agent_and_treats_no_releases_as_up_to_date()
    {
        HttpRequestMessage? seen = null;
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(r => { seen = r; return new HttpResponseMessage(HttpStatusCode.NotFound); })));
        Assert.Null(await checker.CheckAsync("1.0.0"));
        Assert.Equal("https://api.github.com/repos/Maneet7805/PromptForge/releases/latest", seen!.RequestUri!.ToString());
        Assert.Contains("PromptForge", seen.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Network_failure_becomes_an_update_exception()
    {
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline"))));
        await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync("1.0.0"));
    }

    [Fact]
    public async Task Download_keeps_a_file_that_matches_its_checksum()
    {
        var bytes = Encoding.UTF8.GetBytes("pretend exe");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var dir = Path.Combine(Path.GetTempPath(), "pf-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var checker = new UpdateChecker(new HttpClient(new FakeHandler(r =>
                r.RequestUri!.ToString() == SumsUrl ? Text($"{hash}  PromptForge.exe\n") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })));
            var release = UpdateChecker.ParseLatestRelease(ReleaseJson())!;

            var path = await checker.DownloadVerifiedAsync(release, dir, "1.0.0");

            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Download_rejects_and_deletes_a_file_that_does_not_match()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pf-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var checker = new UpdateChecker(new HttpClient(new FakeHandler(r =>
                r.RequestUri!.ToString() == SumsUrl ? Text($"{new string('0', 64)}  PromptForge.exe\n") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) })));
            var release = UpdateChecker.ParseLatestRelease(ReleaseJson())!;

            var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadVerifiedAsync(release, dir, "1.0.0"));

            Assert.Contains("checksum", ex.Message);
            Assert.False(File.Exists(Path.Combine(dir, "PromptForge.exe")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
