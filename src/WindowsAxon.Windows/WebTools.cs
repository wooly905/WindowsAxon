using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Text.Json;
using WindowsAxon.Agent.Tools;
using Microsoft.Playwright;

namespace WindowsAxon.Windows;

/// <summary>
/// Public web lookup in a hidden browser. The visible browser and its profile are left alone.
/// The hidden browser closes when the request ends, on Stop, on a new conversation, and when the app exits.
/// </summary>
public sealed class WebTools(string dataFolder) : IDisposable
{
    internal const string BlockedMessage = "The site blocked automated reading. Try another address, or ask the user before opening the visible browser.";
    internal const string CaptchaMessage = "This page requires a CAPTCHA. Ask the user before opening it in the visible browser.";
    internal const string SignInMessage = "This page requires sign-in. Ask the user before opening it in the visible browser.";

    private const string SearchResultsScript = """
        () => {
          const items = [];
          const blocks = document.querySelectorAll('#b_results > li.b_algo, li.b_algo');
          for (const block of blocks) {
            const link = block.querySelector('h2 a');
            if (!link || !link.href) continue;
            const snippet = (block.querySelector('.b_caption p, .b_lineclamp2, p')?.innerText || '')
              .replace(/\s+/g, ' ').trim().slice(0, 240);
            items.push({
              title: (link.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 180),
              url: link.href,
              snippet
            });
            if (items.length === 5) break;
          }
          return JSON.stringify(items);
        }
        """;

    private const string PageBlockScript = """
        () => {
          if (document.querySelector('.g-recaptcha,.h-captcha,iframe[src*=recaptcha],iframe[src*=hcaptcha]')) return 'captcha';
          const text = (document.body?.innerText || '').slice(0, 800).toLowerCase();
          if (text.includes('verify you are human') || text.includes('unusual traffic')) return 'blocked';
          const url = location.href.toLowerCase();
          if (url.includes('login.microsoftonline.com') || url.includes('accounts.google.com')
              || /\/(signin|sign-in|login|logon)(\/|$|\?)/.test(url)) return 'signin';
          const password = document.querySelector('input[type=password]');
          if (password && (password.offsetWidth || password.offsetHeight || password.getClientRects().length)) return 'signin';
          return '';
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly object _sync = new();
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private Process? _process;
    private string? _profile;
    private int _generation;

    [WindowsTool("web_search", "web", policyDriven: true, timeoutSeconds: 60)]
    [Description("Search the public web in a hidden browser and return up to 5 titles, URLs, and snippets. No window is shown. The hidden browser closes when the request ends. If the search is blocked or requires a CAPTCHA, ask the user before opening the visible browser. Remote endpoints require approval to receive content.")]
    public async Task<string> Search(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Query is required.");
        }

        var page = await PageAsync(cancellationToken);
        var response = await page.GotoAsync(SearchUrl(query),
                                            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
        await EnsureReadableAsync(page, response);
        var json = await page.EvaluateAsync<string>(SearchResultsScript) ?? "[]";
        var formatted = FormatSearchResults(json);
        if (formatted.Length > 0)
        {
            return formatted;
        }

        var text = await BrowserTools.ReadVisibleTextAsync(page);
        if (text.Length == 0)
        {
            return $"No search results on {page.Url}. Try a more specific query.";
        }

        var excerpt = text.Length <= 1200 ? text : text[..1200];
        return $"No structured results on {page.Url}. Page text:\n{excerpt}";
    }

    [WindowsTool("web_read", "web", policyDriven: true, timeoutSeconds: 60)]
    [Description("Read the visible text of an http or https page in a hidden browser, excluding HTML markup, scripts, styles, and hidden content. No window is shown. If the page requires sign-in or a CAPTCHA, or the site blocks automated reading, ask the user before opening the visible browser. Remote endpoints require approval to receive content.")]
    public async Task<string> Read(string url, CancellationToken cancellationToken = default)
    {
        var uri = BrowserTools.WebUrl(url);
        cancellationToken.ThrowIfCancellationRequested();
        var page = await PageAsync(cancellationToken);
        var response = await page.GotoAsync(uri.AbsoluteUri,
                                            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
        await EnsureReadableAsync(page, response);
        var text = await BrowserTools.ReadVisibleTextAsync(page);
        return text.Length == 0 ? "The page has no readable text." : text;
    }

    internal static string Profile(string dataFolder, string channel) =>
        Path.Combine(dataFolder, "browser-profile", channel + "-research");

    internal static string SearchUrl(string query) =>
        "https://www.bing.com/search?q=" + Uri.EscapeDataString(query.Trim());

    internal static string BlockedStatusMessage(int status) =>
        $"The site returned HTTP {status} and blocked automated reading. Try another address, or ask the user before opening the visible browser.";

    internal static async Task EnsureReadableAsync(IPage? page, IResponse? response)
    {
        if (response is { Status: 403 or 429 or 503 })
        {
            throw new InvalidOperationException(BlockedStatusMessage(response.Status));
        }

        ArgumentNullException.ThrowIfNull(page);
        var blocked = await page.EvaluateAsync<string>(PageBlockScript);
        if (blocked == "captcha")
        {
            throw new InvalidOperationException(CaptchaMessage);
        }

        if (blocked == "blocked")
        {
            throw new InvalidOperationException(BlockedMessage);
        }

        if (blocked == "signin")
        {
            throw new InvalidOperationException(SignInMessage);
        }
    }

    internal static string FormatSearchResults(string json)
    {
        List<SearchHit>? hits;
        try
        {
            hits = JsonSerializer.Deserialize<List<SearchHit>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The search page did not return results.", ex);
        }

        var lines = new List<string>();
        var index = 0;
        foreach (var hit in hits ?? [])
        {
            if (string.IsNullOrWhiteSpace(hit.Url) || string.IsNullOrWhiteSpace(hit.Title))
            {
                continue;
            }

            index++;
            lines.Add($"{index}. {hit.Title.Trim()}");
            lines.Add(hit.Url.Trim());
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
            {
                var snippet = hit.Snippet.Trim();
                lines.Add(snippet.Length <= 240 ? snippet : snippet[..240]);
            }

            lines.Add("");
            if (index == 5)
            {
                break;
            }
        }

        return index == 0 ? "" : string.Join('\n', lines).TrimEnd();
    }

    internal void TrackForClose(IBrowser browser, Process? process, string profile)
    {
        lock (_sync)
        {
            _browser = browser;
            _process = process;
            _profile = profile;
        }
    }

    public void Dispose() => Close();

    /// <summary>Closes only the hidden research browser. The visible browser keeps its windows.</summary>
    public void Close()
    {
        Interlocked.Increment(ref _generation);
        IBrowser? browser;
        Process? process;
        IPlaywright? playwright;
        string? profile;
        lock (_sync)
        {
            browser = _browser;
            process = _process;
            playwright = _playwright;
            profile = _profile;
            _browser = null;
            _process = null;
            _playwright = null;
            _profile = null;
            _page = null;
        }

        try
        {
            CloseConnectedBrowser(browser);
        }
        finally
        {
            Kill(process);
            KillProfileProcesses(profile);
            playwright?.Dispose();
            process?.Dispose();
        }
    }

    private async Task<IPage> PageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = Volatile.Read(ref _generation);
        var current = CurrentPage();
        if (current is not null)
        {
            return current;
        }

        _playwright ??= await Playwright.CreateAsync();
        if (generation != Volatile.Read(ref _generation))
        {
            throw new OperationCanceledException("Web lookup was stopped.");
        }

        var channel = BrowserTools.PreferredChannel();
        Process? process = null;
        IBrowserContext? context = null;
        try
        {
            context = await BrowserTools.ConnectBrowserAsync(_playwright,
                                                              Profile(dataFolder, channel),
                                                              channel,
                                                              headless: true,
                                                              started: p => process = p,
                                                              cancellationToken);
            context.SetDefaultTimeout(10000);
            var page = context.Pages.FirstOrDefault(p => !p.IsClosed) ?? await context.NewPageAsync();
            lock (_sync)
            {
                if (generation != _generation)
                {
                    throw new OperationCanceledException("Web lookup was stopped.");
                }

                _browser = context.Browser ?? throw new InvalidOperationException("The hidden browser did not connect.");
                _process = process;
                _profile = Profile(dataFolder, channel);
                _page = page;
                process = null;
                context = null;
            }

            return page;
        }
        catch
        {
            CloseConnectedBrowser(context?.Browser);
            Kill(process);
            KillProfileProcesses(Profile(dataFolder, channel));
            throw;
        }
    }

    private IPage? CurrentPage()
    {
        lock (_sync)
        {
            return _page is { IsClosed: false } ? _page : null;
        }
    }

    private static void CloseConnectedBrowser(IBrowser? browser)
    {
        if (browser is not { IsConnected: true })
        {
            return;
        }

        try
        {
            // Stop can run on the UI thread. Playwright work stays on a pool thread, and the wait is bounded.
            Task.Run(() =>
            {
                var session = browser.NewBrowserCDPSessionAsync().GetAwaiter().GetResult();
                session.SendAsync("Browser.close").GetAwaiter().GetResult();
            }).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is AggregateException or PlaywrightException or System.TimeoutException or InvalidOperationException)
        {
            // Browser.close can lose the socket as the process exits. The profile kill is the backup.
        }
    }

    /// <summary>
    /// Edge and Chrome may exit the process we started and keep running as a child. Closing by profile finds that child
    /// without touching the visible browser, which uses a different profile.
    /// </summary>
    private static void KillProfileProcesses(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return;
        }

        const string query = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe' OR Name = 'chrome.exe'";
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var command = item["CommandLine"]?.ToString();
                    if (command is null || !command.Contains(profile, StringComparison.OrdinalIgnoreCase) || item["ProcessId"] is not uint id)
                    {
                        continue;
                    }

                    try
                    {
                        using var process = Process.GetProcessById((int)id);
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
                    {
                        // The process exited between the query and the kill.
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // WMI could not list browser processes. The owned process kill already ran.
        }
    }

    private static void Kill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The research browser already exited.
        }
    }

    private sealed record SearchHit(string? Title, string? Url, string? Snippet);
}
