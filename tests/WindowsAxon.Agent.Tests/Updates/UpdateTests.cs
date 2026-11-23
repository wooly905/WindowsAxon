using System.Net;
using System.Security.Cryptography;
using System.Text;
using WindowsAxon.App.Updates;

namespace WindowsAxon.Agent.Tests.Updates;

public sealed class UpdateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"windowsaxon-update-{Guid.NewGuid():N}");

    private static string ReleaseJson(string tag = "v0.1.7", string? exeName = "WindowsAxon-0.1.7-win-x64.exe", bool withSha = true)
    {
        var assets = new List<string>();
        if (exeName is not null)
        {
            assets.Add($$"""{"name":"{{exeName}}","browser_download_url":"https://example.com/{{exeName}}"}""");
            if (withSha)
            {
                assets.Add($$"""{"name":"{{exeName}}.sha256","browser_download_url":"https://example.com/{{exeName}}.sha256"}""");
            }
        }

        return $$"""
            {"tag_name":"{{tag}}","html_url":"https://github.com/wooly905/WindowsAxon/releases/tag/{{tag}}",
             "assets":[{{string.Join(",", assets)}}]}
            """;
    }

    private static ReleaseInfo Release(string version = "0.1.7") =>
        new(Version.Parse(version),
            new Uri("https://github.com/wooly905/WindowsAxon/releases"),
            $"WindowsAxon-{version}-win-x64.exe",
            new Uri($"https://example.com/WindowsAxon-{version}-win-x64.exe"),
            new Uri($"https://example.com/WindowsAxon-{version}-win-x64.exe.sha256"));

    [Fact]
    public void Parse_reads_version_and_matching_assets()
    {
        var release = UpdateChecker.Parse(ReleaseJson());

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 1, 7), release.Version);
        Assert.Equal("WindowsAxon-0.1.7-win-x64.exe", release.ExeName);
        Assert.Equal("https://example.com/WindowsAxon-0.1.7-win-x64.exe.sha256", release.Sha256Url.AbsoluteUri);
    }

    [Fact]
    public void Parse_ignores_hub_exe_and_selects_app_exe()
    {
        var json = """
            {"tag_name":"v0.1.7","html_url":"https://github.com/wooly905/WindowsAxon/releases/tag/v0.1.7",
             "assets":[
               {"name":"WindowsAxon.Hub-0.1.7-win-x64.exe","browser_download_url":"https://example.com/WindowsAxon.Hub-0.1.7-win-x64.exe"},
               {"name":"WindowsAxon.Hub-0.1.7-win-x64.exe.sha256","browser_download_url":"https://example.com/WindowsAxon.Hub-0.1.7-win-x64.exe.sha256"},
               {"name":"WindowsAxon-0.1.7-win-x64.exe","browser_download_url":"https://example.com/WindowsAxon-0.1.7-win-x64.exe"},
               {"name":"WindowsAxon-0.1.7-win-x64.exe.sha256","browser_download_url":"https://example.com/WindowsAxon-0.1.7-win-x64.exe.sha256"}
             ]}
            """;

        var release = UpdateChecker.Parse(json);

        Assert.NotNull(release);
        Assert.Equal("WindowsAxon-0.1.7-win-x64.exe", release.ExeName);
        Assert.Equal("https://example.com/WindowsAxon-0.1.7-win-x64.exe.sha256", release.Sha256Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("not-a-version", "WindowsAxon-0.1.7-win-x64.exe", true)]
    [InlineData("v0.1.7", null, true)]
    [InlineData("v0.1.7", "WindowsAxon-0.1.7-win-x64.exe", false)]
    [InlineData("v0.1.7", "WindowsAxon-0.1.7.zip", true)]
    public void Parse_returns_null_without_a_valid_tag_or_both_assets(string tag, string? exeName, bool withSha) =>
        Assert.Null(UpdateChecker.Parse(ReleaseJson(tag, exeName, withSha)));

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"message":"API rate limit exceeded"}""")]
    public void Parse_returns_null_for_malformed_json(string json) => Assert.Null(UpdateChecker.Parse(json));

    [Fact]
    public void Parse_rejects_non_https_download_urls()
    {
        var json = ReleaseJson().Replace("https://example.com", "http://example.com");

        Assert.Null(UpdateChecker.Parse(json));
    }

    [Theory]
    [InlineData("0.1.6.0", null, true)]
    [InlineData("0.1.0.0", null, true)]
    [InlineData("0.1.7.0", null, false)]
    [InlineData("0.1.8.0", null, false)]
    [InlineData("0.1.6.0", "0.1.7", false)]
    [InlineData("0.1.6.0", "0.1.9", false)]
    [InlineData("0.1.6.0", "0.1.5", true)]
    [InlineData("0.1.6.0", "garbage", true)]
    public void ShouldPrompt_offers_only_newer_unskipped_versions(string current, string? skipped, bool expected) =>
        Assert.Equal(expected, UpdateChecker.ShouldPrompt(Version.Parse(current), Release("0.1.7"), skipped));

    [Fact]
    public async Task GetLatest_returns_the_release_from_the_github_api()
    {
        using var http = new HttpClient(new FakeHandler(request =>
        {
            Assert.Equal(UpdateChecker.LatestReleaseUri, request.RequestUri);
            Assert.Contains("WindowsAxon", request.Headers.UserAgent.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()) };
        }));

        var release = await new UpdateChecker(http).GetLatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Version(0, 1, 7), release?.Version);
    }

    [Fact]
    public async Task GetLatest_returns_null_on_http_error_or_network_failure()
    {
        using var notFound = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var offline = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline")));

        Assert.Null(await new UpdateChecker(notFound).GetLatestAsync(TestContext.Current.CancellationToken));
        Assert.Null(await new UpdateChecker(offline).GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void UpdateState_round_trips_the_skipped_version()
    {
        var state = new UpdateState(Path.Combine(_folder, "update.json"));

        Assert.Null(state.LoadSkippedVersion());
        state.SaveSkippedVersion(new Version(0, 1, 7));

        Assert.Equal("0.1.7", new UpdateState(Path.Combine(_folder, "update.json")).LoadSkippedVersion());
    }

    [Fact]
    public void UpdateState_ignores_a_corrupt_file()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "update.json");
        File.WriteAllText(path, "{not json");

        Assert.Null(new UpdateState(path).LoadSkippedVersion());
    }

    [Fact]
    public void ParseSha256_reads_the_hash_from_a_checksum_line()
    {
        var hash = new string('A', 64);

        Assert.Equal(new string('a', 64), UpdateInstaller.ParseSha256($"{hash}  WindowsAxon-0.1.7-win-x64.exe\r\n"));
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.ParseSha256("not-a-hash file.exe"));
    }

    [Fact]
    public async Task Download_keeps_a_file_whose_hash_matches()
    {
        var payload = Encoding.UTF8.GetBytes("new exe");
        using var http = DownloadClient(payload, Convert.ToHexStringLower(SHA256.HashData(payload)));

        var path = await new UpdateInstaller(http).DownloadAsync(Release(), _folder, TestContext.Current.CancellationToken);

        Assert.Equal(payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_rejects_and_deletes_a_file_whose_hash_does_not_match()
    {
        using var http = DownloadClient(Encoding.UTF8.GetBytes("tampered"), new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateInstaller(http).DownloadAsync(Release(), _folder, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Replace_swaps_the_executable_and_keeps_the_old_one_for_cleanup()
    {
        Directory.CreateDirectory(_folder);
        var current = Path.Combine(_folder, "WindowsAxon.exe");
        var downloaded = Path.Combine(_folder, "download.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(downloaded, "new");

        UpdateInstaller.Replace(current, downloaded);

        Assert.Equal("new", File.ReadAllText(current));
        Assert.Equal("old", File.ReadAllText($"{current}.old"));
        Assert.False(File.Exists(downloaded));
        Assert.False(File.Exists($"{current}.new"));

        UpdateInstaller.CleanupOld(current);
        Assert.False(File.Exists($"{current}.old"));
    }

    [Fact]
    public void Install_launches_the_new_executable()
    {
        var (current, downloaded) = CreateExecutables();
        string? launched = null;

        UpdateInstaller.Install(current, downloaded, path => launched = path);

        Assert.Equal(current, launched);
        Assert.Equal("new", File.ReadAllText(current));
    }

    [Fact]
    public void Install_restores_the_original_executable_when_launch_fails()
    {
        var (current, downloaded) = CreateExecutables();

        Assert.Throws<InvalidOperationException>(() =>
            UpdateInstaller.Install(current, downloaded, _ => throw new InvalidOperationException("launch failed")));

        Assert.Equal("old", File.ReadAllText(current));
        Assert.False(File.Exists($"{current}.old"));
        Assert.False(File.Exists($"{current}.new"));
    }

    private (string Current, string Downloaded) CreateExecutables()
    {
        Directory.CreateDirectory(_folder);
        var current = Path.Combine(_folder, "WindowsAxon.exe");
        var downloaded = Path.Combine(_folder, "download.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(downloaded, "new");
        return (current, downloaded);
    }

    [Fact]
    public void IsSingleFile_is_false_beside_the_app_dll()
    {
        Directory.CreateDirectory(_folder);
        var exe = Path.Combine(_folder, "WindowsAxon.App.exe");

        Assert.True(UpdateInstaller.IsSingleFile(exe));
        File.WriteAllText(Path.Combine(_folder, "WindowsAxon.App.dll"), "");
        Assert.False(UpdateInstaller.IsSingleFile(exe));
    }

    private static HttpClient DownloadClient(byte[] payload, string hash) =>
        new(new FakeHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent($"{hash}  WindowsAxon-0.1.7-win-x64.exe\n")
                : new ByteArrayContent(payload),
        }));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
