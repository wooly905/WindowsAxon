using System.ComponentModel;
using System.Diagnostics;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Playwright;
using Microsoft.Win32;

namespace WindowsAxon.Windows;

public sealed class BrowserTools(string dataFolder) : IApprovalRule, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;
    private readonly Dictionary<string, (IPage Page, IElementHandle Element, string Name)> _refs = [];
    private readonly Dictionary<int, IPage> _tabs = [];
    private int _nextTab;
    private int _stopGeneration;

    private async Task<IPage> PageAsync()
    {
        var generation = _stopGeneration;
        if (_context is null)
        {
            _playwright ??= await Playwright.CreateAsync();
            using var choice = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\https\\UserChoice");
            var channel = choice?.GetValue("ProgId")?.ToString()?.StartsWith("Chrome",
                                                                             StringComparison.OrdinalIgnoreCase) == true ? "chrome" : "msedge";
            var context = await ConnectBrowserAsync(_playwright,
                                                     Path.Combine(dataFolder, "browser-profile", channel),
                                                     channel);
            context.SetDefaultTimeout(10000);
            _context = context;
            if (generation != _stopGeneration)
            {
                CancelPending();
                throw new OperationCanceledException("Browser startup was stopped.");
            }
        }
        if (_page is null || _page.IsClosed)
        {
            _page = _context.Pages.FirstOrDefault(page => !page.IsClosed) ?? await _context.NewPageAsync();
        }

        return _page;
    }

    internal static async Task<IBrowserContext> ConnectBrowserAsync(IPlaywright playwright,
                                                                    string profile,
                                                                    string channel,
                                                                    bool headless = false)
    {
        Directory.CreateDirectory(profile);
        var timer = Stopwatch.StartNew();
        var launched = false;
        Exception? lastError = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            var endpoint = ReadBrowserEndpoint(profile);
            if (endpoint is not null)
            {
                try
                {
                    var browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint, new()
                    {
                        Timeout = 5000
                    });
                    var downloads = await browser.NewBrowserCDPSessionAsync();
                    await downloads.SendAsync("Browser.setDownloadBehavior", new Dictionary<string, object>
                    {
                        ["behavior"] = "deny"
                    });
                    await downloads.DetachAsync();
                    return browser.Contexts[0];
                }
                // A cold-starting browser can accept the websocket before the CDP handshake completes,
                // which Playwright reports as System.TimeoutException rather than PlaywrightException.
                catch (Exception error) when (error is PlaywrightException or System.TimeoutException)
                {
                    lastError = error;
                }
            }

            if (!launched)
            {
                var start = new ProcessStartInfo(BrowserExecutable(channel))
                {
                    UseShellExecute = true,
                    WindowStyle = headless ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
                };
                foreach (var argument in new[]
                {
                    "--remote-debugging-port=0",
                    "--remote-debugging-address=127.0.0.1",
                    "--user-data-dir=" + profile,
                    "--no-first-run",
                    "--no-default-browser-check"
                })
                {
                    start.ArgumentList.Add(argument);
                }
                if (headless)
                {
                    start.ArgumentList.Add("--headless=new");
                }
                start.ArgumentList.Add("about:blank");
                using var process = Process.Start(start)
                    ?? throw new InvalidOperationException("Could not start the browser.");
                launched = true;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException("Could not connect to WindowsAxon's browser. Close an older WindowsAxon browser window and try again.",
                                            lastError);
    }

    private static string? ReadBrowserEndpoint(string profile)
    {
        var path = Path.Combine(profile, "DevToolsActivePort");
        if (!File.Exists(path))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            // Chromium may still be writing its endpoint file; the connection loop retries.
            return null;
        }
        return lines.Length >= 2 && int.TryParse(lines[0], out var port) && port is > 0 and <= 65535
            && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal)
            && Guid.TryParse(lines[1]["/devtools/browser/".Length..], out _)
            ? $"ws://127.0.0.1:{port}{lines[1]}" : null;
    }

    private static string BrowserExecutable(string channel)
    {
        var executable = channel == "chrome" ? "chrome.exe" : "msedge.exe";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + executable);
            if (key?.GetValue(null) is string path && File.Exists(path))
            {
                return path;
            }
        }

        var relativePath = channel == "chrome" ? @"Google\Chrome\Application\chrome.exe"
            : @"Microsoft\Edge\Application\msedge.exe";
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData
        })
        {
            var path = Path.Combine(Environment.GetFolderPath(folder), relativePath);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("The selected browser is not installed.", executable);
    }

    public static Uri WebUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0)
        {
            throw new ArgumentException("Use an http or https URL without credentials.");
        }

        return uri;
    }

    [WindowsTool("browser_open", "browser", timeoutSeconds: 60, artifact: true)]
    [Description("Open an http or https URL in WindowsAxon's separate, visible browser profile. The browser runs independently and stays open after WindowsAxon disconnects or exits. newTab opens a new tab. The user handles sign-in.")]
    public async Task<string> Open(string url, bool newTab = false, CancellationToken cancellationToken = default)
    {
        var uri = WebUrl(url);
        cancellationToken.ThrowIfCancellationRequested();
        var page = await PageAsync();
        if (newTab)
        {
            _page = page = await _context!.NewPageAsync();
        }

        await page.GotoAsync(uri.AbsoluteUri,
                             new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
        await page.BringToFrontAsync();
        return $"Opened {page.Url}: {await page.TitleAsync()}.";
    }

    private static async Task CheckPageAsync(IPage page)
    {
        if (!await page.EvaluateAsync<bool>("() => !document.querySelector('.g-recaptcha,.h-captcha,iframe[src*=recaptcha],iframe[src*=hcaptcha]')"))
        {
            throw new InvalidOperationException("A CAPTCHA requires the user's response in the browser.");
        }
    }

    [WindowsTool("browser_snapshot", "browser", policyDriven: true)]
    [Description("Read up to 40 visible interactive elements with fresh ref IDs, names, and roles. Password and payment values are never included. Remote endpoints require approval to receive content.")]
    public Task<object> Snapshot() => SnapshotCore(null, 40);

    [WindowsTool("browser_find", "browser", policyDriven: true)]
    [Description("Find up to 40 visible elements by text. Returns fresh refs; previously returned refs expire. Remote endpoints require approval to receive content.")]
    public Task<object> Find(string text) => SnapshotCore(text, 40);

    private async Task<object> SnapshotCore(string? text, int limit)
    {
        var page = await PageAsync();
        await CheckPageAsync(page);
        await ClearRefsAsync();
        var elements = await page.QuerySelectorAllAsync("a,button,input,textarea,select,[role=button],[role=link],[role=checkbox],[role=searchbox],[role=textbox],h1,h2");
        var results = new List<object>();
        foreach (var element in elements)
        {
            if (!await element.IsVisibleAsync())
            {
                await element.DisposeAsync();
                continue;
            }
            var name = await element.EvaluateAsync<string>("e => (e.getAttribute('aria-label') || e.labels?.[0]?.innerText || e.innerText || e.getAttribute('placeholder') || e.getAttribute('title') || '').trim().slice(0,160)");
            if (text is not null && !name.Contains(text, StringComparison.OrdinalIgnoreCase) || results.Count == limit)
            {
                await element.DisposeAsync();
                continue;
            }
            var role = await element.EvaluateAsync<string>("e => e.getAttribute('role') || e.tagName.toLowerCase()");
            var id = Guid.NewGuid().ToString("N")[..8];
            _refs.Add(id, (page, element, name));
            results.Add(new
            {
                Ref = id,
                Role = role,
                Name = name
            });
        }
        return new
        {
            Url = page.Url,
            Title = await page.TitleAsync(),
            Elements = results
        };
    }

    private async Task<IElementHandle> Element(string reference, bool input = false)
    {
        var page = await PageAsync();
        await CheckPageAsync(page);
        if (!_refs.TryGetValue(reference, out var target) || target.Page != page
            || !await target.Element.EvaluateAsync<bool>("e => e.isConnected"))
        {
            throw new ArgumentException("Take a new browser_snapshot; the ref is expired or unknown.");
        }

        if (input)
        {
            await CheckInput(target.Element);
        }

        return target.Element;
    }

    private static async Task CheckInput(IElementHandle element)
    {
        var blocked = await element.EvaluateAsync<bool>("""
            e => { const a=(e.getAttribute('autocomplete')||'').toLowerCase();
              const n=[e.type,e.name,e.id,e.getAttribute('aria-label')].join(' ').toLowerCase();
              return e.type==='password' || a.includes('password') || a.includes('one-time-code') || a.startsWith('cc-') || /\b(otp|cvv|cvc|password|passcode)\b|credit.?card|card.?number|verification.?code|security.?code/.test(n); }
            """);
        if (blocked)
        {
            throw new UnauthorizedAccessException("Password, one-time-code, and payment fields require the user.");
        }
    }

    [WindowsTool("browser_read", "browser", policyDriven: true)]
    [Description("Read the full visible text of the page or a referenced element, excluding HTML markup, scripts, styles, and hidden content. Remote endpoints require approval to receive content.")]
    public async Task<string> Read(string? reference = null)
    {
        var page = await PageAsync();
        await CheckPageAsync(page);
        const string readText = """
            root => {
              const excluded = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'HEAD']);
              function read(node) {
                if (node.nodeType === Node.TEXT_NODE) {
                  const style = getComputedStyle(node.parentElement);
                  if (style.visibility === 'hidden' || style.visibility === 'collapse') return '';
                  return style.whiteSpace.startsWith('pre') || style.whiteSpace === 'break-spaces'
                    ? node.nodeValue : node.nodeValue.replace(/\s+/g, ' ');
                }
                if (node.nodeType !== Node.ELEMENT_NODE || excluded.has(node.tagName)) return '';
                const style = getComputedStyle(node);
                if (style.display === 'none' || style.contentVisibility === 'hidden') return '';
                if (node.tagName === 'BR') return '\n';
                const text = Array.from(node.childNodes, read).join('');
                if (style.display === 'table-cell') return text + '\t';
                return /^(block|flow-root|flex|grid|list-item|table|table-row|table-caption)$/.test(style.display)
                  ? '\n' + text + '\n' : text;
              }
              return read(root).replace(/\n{3,}/g, '\n\n').trim();
            }
            """;
        return reference is null ? await page.Locator("body").EvaluateAsync<string>(readText)
            : await (await Element(reference)).EvaluateAsync<string>(readText);
    }

    [WindowsTool("browser_click", "browser", RiskLevel.Danger, policyDriven: true)]
    [Description("Click a ref from the last snapshot. Requires approval each time, including send, buy, delete, and submit actions.")]
    public async Task<string> Click(string reference)
    {
        await (await Element(reference)).ClickAsync();
        return "Clicked the selected element. Verify the resulting page before claiming the task is complete.";
    }

    [WindowsTool("browser_type", "browser", RiskLevel.Change, policyDriven: true)]
    [Description("Fill a text field by ref. Password, one-time-code, and payment fields are refused. submit presses Enter and requires approval.")]
    public async Task<string> Type(string reference, string text, bool submit = false)
    {
        var element = await Element(reference, true);
        await element.FillAsync(text);
        if (await element.InputValueAsync() != text)
        {
            throw new InvalidOperationException("Field value could not be verified.");
        }

        if (submit)
        {
            await element.PressAsync("Enter");
        }

        return submit ? "Text verified; Enter was pressed. Verify the resulting page." : "Field text verified.";
    }

    [WindowsTool("browser_press", "browser", RiskLevel.Change, policyDriven: true)]
    [Description("Press a key or shortcut in the focused browser field, for example Enter, Tab, Control+A. Refuses protected fields.")]
    public async Task<string> Press(string key)
    {
        var page = await PageAsync();
        await CheckPageAsync(page);
        var frame = page.MainFrame;
        while (true)
        {
            var handle = await frame.EvaluateHandleAsync("() => { let active = document.activeElement; while (active?.shadowRoot?.activeElement) active = active.shadowRoot.activeElement; return active; }");
            try
            {
                var active = handle.AsElement() ?? throw new UnauthorizedAccessException("The focused browser element could not be verified.");
                if (await active.EvaluateAsync<bool>("e => e.tagName === 'IFRAME' || e.tagName === 'FRAME'"))
                {
                    frame = await active.ContentFrameAsync() ?? throw new UnauthorizedAccessException("The focused browser frame could not be verified.");
                }
                else
                {
                    await CheckInput(active);
                    break;
                }
            }
            finally
            {
                await handle.DisposeAsync();
            }
        }
        await page.Keyboard.PressAsync(key);
        return "Key sent. Verify the resulting page.";
    }

    [WindowsTool("browser_scroll", "browser")]
    [Description("Scroll up or down one viewport in the current browser page.")]
    public async Task<string> Scroll(string direction = "down")
    {
        if (direction is not ("up" or "down"))
        {
            throw new ArgumentException("direction must be up or down.");
        }

        var page = await PageAsync();
        await CheckPageAsync(page);
        await page.EvaluateAsync("d => window.scrollBy(0, innerHeight*(d==='up'?-1:1))", direction);
        return "Scrolled.";
    }

    [WindowsTool("browser_back", "browser")]
    [Description("Navigate back in the current tab.")]
    public async Task<string> Back()
    {
        var page = await PageAsync();
        await page.GoBackAsync();
        await ClearRefsAsync();
        return page.Url;
    }

    [WindowsTool("browser_tabs", "browser")]
    [Description("List open WindowsAxon browser tabs with numeric IDs and URLs.")]
    public async Task<object> Tabs()
    {
        await PageAsync();
        foreach (var page in _context!.Pages)
        {
            if (!_tabs.Values.Contains(page))
            {
                _tabs.Add(_nextTab++, page);
            }
        }

        return _tabs.Where(tab => !tab.Value.IsClosed).Select(tab => new
        {
            Id = tab.Key,
            tab.Value.Url,
            Selected = tab.Value == _page
        })
            .ToArray();
    }

    [WindowsTool("browser_tab_select", "browser")]
    [Description("Select a tab ID returned by browser_tabs.")]
    public async Task<string> SelectTab(int id)
    {
        await PageAsync();
        if (!_tabs.TryGetValue(id, out var page) || page.IsClosed)
        {
            throw new ArgumentException("Unknown or closed tab ID. Use browser_tabs again.");
        }

        _page = page;
        await _page.BringToFrontAsync();
        await ClearRefsAsync();
        return _page.Url;
    }

    [WindowsTool("browser_close", "browser")]
    [Description("Close the current WindowsAxon browser tab. Does not close the user's separate browser windows.")]
    public async Task<string> Close()
    {
        var page = await PageAsync();
        await page.CloseAsync();
        _page = null;
        await ClearRefsAsync();
        return "Tab closed.";
    }

    public bool AppliesTo(string toolName) => toolName is "browser_click" or "browser_type" or "browser_press";

    public ApprovalDecision Decide(FunctionCallContent call)
    {
        var reference = call.Arguments is { } arguments
            && arguments.TryGetValue("reference", out var value) ? value?.ToString() : null;
        var target = reference is not null
            && _refs.TryGetValue(reference, out var element) ? element.Name : "focused browser element";
        return new ApprovalDecision(ApprovalOutcome.Ask,
                                    false,
                                    $"Website: {_page?.Url}\nElement: {target}. Verify any send, submit, purchase, or deletion before allowing.");
    }

    private async Task ClearRefsAsync()
    {
        foreach (var target in _refs.Values)
        {
            await target.Element.DisposeAsync();
        }

        _refs.Clear();
    }

    public void CancelPending()
    {
        Interlocked.Increment(ref _stopGeneration);
        var playwright = _playwright;
        _playwright = null;
        _context = null;
        _page = null;
        _refs.Clear();
        _tabs.Clear();
        // The browser was launched independently; releasing the CDP client must not close its windows.
        playwright?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ClearRefsAsync();
        }
        finally
        {
            CancelPending();
        }
    }
}
