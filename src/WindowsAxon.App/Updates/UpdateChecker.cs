using System.Text.Json;

namespace WindowsAxon.App.Updates;

/// <summary>Reads the latest published GitHub release and decides whether to offer it.</summary>
public sealed class UpdateChecker(HttpClient http)
{
    public static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/wooly905/WindowsAxon/releases/latest");

    private const string ExeName = "WindowsAxon.App.win-x64.exe";

    /// <summary>Returns the latest release, or null when it cannot be read or has no usable assets.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
            request.Headers.UserAgent.ParseAdd("WindowsAxon");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryGetString(root, "tag_name", out var tag)
                || !TryParseVersion(tag, out var version)
                || !TryGetString(root, "html_url", out var page)
                || !TryCreateHttps(page, out var pageUrl)
                || !root.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var urls = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets.EnumerateArray())
            {
                if (TryGetString(asset, "name", out var name)
                    && TryGetString(asset, "browser_download_url", out var url)
                    && TryCreateHttps(url, out var uri))
                {
                    urls.TryAdd(name, uri);
                }
            }

            foreach (var (name, exeUrl) in urls)
            {
                if (string.Equals(name, ExeName, StringComparison.OrdinalIgnoreCase)
                    && urls.TryGetValue($"{name}.sha256", out var shaUrl))
                {
                    return new ReleaseInfo(version, pageUrl, name, exeUrl, shaUrl);
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when the release is newer than the running app and the user has not skipped it.</summary>
    public static bool ShouldPrompt(Version current, ReleaseInfo latest, string? skippedVersion)
    {
        var latestVersion = Normalize(latest.Version);
        if (latestVersion <= Normalize(current))
        {
            return false;
        }

        return !TryParseVersion(skippedVersion, out var skipped) || latestVersion > skipped;
    }

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        var trimmed = text?.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        return true;
    }

    /// <summary>Compares Major.Minor.Build only; assembly versions carry a trailing revision of 0.</summary>
    public static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return value.Length > 0;
    }

    private static bool TryCreateHttps(string text, out Uri uri) =>
        Uri.TryCreate(text, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps;
}
