using System.ComponentModel;
using System.Diagnostics;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Guard;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Windows;
using Microsoft.Playwright;
using NSubstitute;

namespace WindowsAxon.Agent.Tests.Tools;

public sealed class WebToolsTests
{
    private static readonly Uri Source = new("https://public.invalid/page");

    [Fact]
    public async Task Offline_page_uses_the_ten_second_timeout()
    {
        var browser = Substitute.For<IBrowser>();
        var context = Substitute.For<IBrowserContext>();
        browser.NewContextAsync(Arg.Any<BrowserNewContextOptions>()).Returns(context);
        context.NewPageAsync().Returns(Substitute.For<IPage>());

        await WebTools.OfflinePageAsync(browser);

        context.Received(1).SetDefaultTimeout(10000);
    }

    [Fact]
    public async Task Missing_browser_has_a_clear_connection_error()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WebTools.OfflinePageAsync(null));

        Assert.Equal("The hidden browser did not connect.", error.Message);
    }

    [Theory]
    [InlineData("", "https://public.invalid/two")]
    [InlineData("<base href='https://cdn.invalid/assets/'>", "https://cdn.invalid/two")]
    [InlineData("<base href='/assets/'>", "https://public.invalid/two")]
    [InlineData("<base href='http://['>", "https://public.invalid/two")]
    public async Task Offline_content_preserves_standards_mode_and_resolves_relative_links(string baseTag, string expected)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = "msedge",
            Headless = true
        });
        var page = await WebTools.OfflinePageAsync(browser);

        await WebTools.SetPageContentAsync(page, new Uri("https://public.invalid/articles/one"),
            $"<!DOCTYPE html><html><head>{baseTag}</head><body><a href='../two'>Article</a></body></html>");

        Assert.Equal("CSS1Compat", await page.EvaluateAsync<string>("() => document.compatMode"));
        Assert.Equal(expected, await page.EvaluateAsync<string>("() => document.querySelector('a').href"));
        Assert.Equal("Article", await BrowserTools.ReadVisibleTextAsync(page));
    }

    [Theory]
    [InlineData("https://accounts.google.com/v3/signin", true)]
    [InlineData("https://login.microsoftonline.com/common", true)]
    [InlineData("https://public.invalid/account/login?next=/", true)]
    [InlineData("https://public.invalid/sign-in/", true)]
    [InlineData("https://public.invalid/blog/login-tips", false)]
    public void Sign_in_detection_uses_the_fetched_url(string url, bool expected)
    {
        Assert.Equal(expected, WebTools.IsSignInUrl(new Uri(url)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Search results could not be parsed.")]
    public void Search_fallback_names_the_fetched_url_instead_of_the_offline_page(string text)
    {
        var result = WebTools.SearchFallback(new Uri("https://www.bing.com/search?q=fixture"), text);

        Assert.Contains("https://www.bing.com/search?q=fixture", result);
        Assert.DoesNotContain("about:blank", result);
    }

    [Fact]
    public async Task Offline_parser_blocks_scripts_and_subresource_requests()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = "msedge",
            Headless = true
        });
        var page = await WebTools.OfflinePageAsync(browser);
        var failedRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.RequestFailed += (_, request) =>
        {
            if (request.Url.Contains("TEST_CANARY_9", StringComparison.Ordinal))
            {
                failedRequest.TrySetResult();
            }
        };

        await page.SetContentAsync("""
            <h1>Article</h1>
            <script>window.injected = true; fetch('https://attacker.invalid/script');</script>
            <img src="http://127.0.0.1/TEST_CANARY_9">
            """);
        await failedRequest.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(await page.EvaluateAsync<bool>("() => window.injected === true"));
        Assert.Contains("Article", await BrowserTools.ReadVisibleTextAsync(page));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Search_rejects_a_blank_query(string? query)
    {
        using var tools = new WebTools(Path.GetTempPath());

        var error = await Assert.ThrowsAsync<ArgumentException>(() => tools.Search(query!,
                                                                                   TestContext.Current.CancellationToken));

        Assert.Equal("Query is required.", error.Message);
    }

    [Fact]
    public async Task Read_rejects_a_non_web_url_before_starting_a_browser()
    {
        using var tools = new WebTools(Path.GetTempPath());

        await Assert.ThrowsAsync<ArgumentException>(() => tools.Read("file:///C:/secrets",
                                                                      TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Search_url_and_profile_stay_separate_from_the_visible_browser()
    {
        Assert.Equal("https://www.bing.com/search?q=best%20CPU", WebTools.SearchUrl(" best CPU "));
        Assert.Equal(Path.Combine("data", "browser-profile", "msedge-research"), WebTools.Profile("data", "msedge"));
    }

    [Fact]
    public void Search_results_skip_incomplete_hits()
    {
        var json = """
            [
              {"title":"One","url":"https://example.com/1","snippet":"first"},
              {"title":" ","url":"https://example.com/skip"},
              {"title":"Two","url":"https://example.com/2","snippet":"second"}
            ]
            """;

        var formatted = WebTools.FormatSearchResults(json);

        Assert.Contains("1. One", formatted);
        Assert.Contains("https://example.com/1", formatted);
        Assert.Contains("2. Two", formatted);
        Assert.DoesNotContain("skip", formatted);
    }

    [Fact]
    public async Task Readable_check_rejects_captcha_sign_in_and_human_verification()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = "msedge",
            Headless = true
        });
        var page = await (await browser.NewContextAsync()).NewPageAsync();

        await page.SetContentAsync("<div class='g-recaptcha'></div>");
        var captcha = await Assert.ThrowsAsync<InvalidOperationException>(() => WebTools.EnsureReadableAsync(page, Source));
        Assert.Equal(WebTools.CaptchaMessage, captcha.Message);

        await page.SetContentAsync("<p>Please verify you are human before continuing.</p>");
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => WebTools.EnsureReadableAsync(page, Source));
        Assert.Equal(WebTools.BlockedMessage, blocked.Message);

        await page.SetContentAsync("<form><input type='password'></form>");
        var signIn = await Assert.ThrowsAsync<InvalidOperationException>(() => WebTools.EnsureReadableAsync(page, Source));
        Assert.Equal(WebTools.SignInMessage, signIn.Message);

        await page.SetContentAsync("<h1>CPU upgrade</h1>");
        await WebTools.EnsureReadableAsync(page, Source);
    }

    [Fact]
    public async Task Web_read_keeps_text_beyond_the_observation_limit()
    {
        var catalog = new ToolCatalog();
        var text = new string('y', 3000);
        catalog.Add(AIFunctionFactory.Create(() => text, "web_read"), "web", RiskLevel.Read, policyDriven: true);
        var state = new ToolSelectionState(catalog, 14) { AllToolsActive = true };
        var ledger = new ApprovalLedger();
        ledger.Record(new FunctionCallContent("", "web_read"), true);
        var guard = new GuardedAIFunction(catalog.Find("web_read")!,
                                          state,
                                          new CallTracker(),
                                          ledger,
                                          new ListAuditLog());

        var result = await guard.InvokeAsync(new AIFunctionArguments(), TestContext.Current.CancellationToken);

        Assert.Equal(text, result?.ToString());
        Assert.DoesNotContain("cut:", result?.ToString());
    }

    [Fact]
    public async Task Closing_research_stops_that_browser_and_leaves_the_other_browser_open()
    {
        var researchProfile = Directory.CreateTempSubdirectory("windowsaxon-research-").FullName;
        var visibleProfile = Directory.CreateTempSubdirectory("windowsaxon-visible-").FullName;
        using var playwright = await Playwright.CreateAsync();
        using var tools = new WebTools(Path.GetTempPath());
        IBrowserContext? visible = null;
        Process? researchProcess = null;
        try
        {
            visible = await BrowserTools.ConnectBrowserAsync(playwright,
                                                              visibleProfile,
                                                              "msedge",
                                                              headless: true,
                                                              cancellationToken: TestContext.Current.CancellationToken);
            await visible.Pages[0].SetContentAsync("<h1>Visible</h1>");
            var research = await BrowserTools.ConnectBrowserAsync(playwright,
                                                                   researchProfile,
                                                                   "msedge",
                                                                   headless: true,
                                                                   started: process => researchProcess = process,
                                                                   cancellationToken: TestContext.Current.CancellationToken);
            var researchPage = research.Pages[0];
            await researchPage.SetContentAsync("<h1>Research</h1>");
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            research.Browser!.Disconnected += (_, _) => disconnected.TrySetResult();
            tools.TrackForClose(research.Browser!, researchProcess, researchProfile);

            tools.Close();

            // Browser shutdown and the CDP disconnect notification complete asynchronously.
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotNull(researchProcess);
            await Assert.ThrowsAnyAsync<PlaywrightException>(() => researchPage.TitleAsync());
            Assert.Equal("Visible", await visible.Pages[0].Locator("h1").InnerTextAsync());
        }
        finally
        {
            tools.Close();
            if (researchProcess is not null)
            {
                try
                {
                    if (!researchProcess.HasExited)
                    {
                        researchProcess.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or Win32Exception)
                {
                    // Close already exited and disposed the research browser.
                }
            }

            if (visible?.Browser is { IsConnected: true })
            {
                var cleanup = await visible.Browser.NewBrowserCDPSessionAsync();
                await cleanup.SendAsync("Browser.close");
            }

            await DeleteProfileAsync(researchProfile);
            await DeleteProfileAsync(visibleProfile);
        }
    }

    private static async Task DeleteProfileAsync(string profile)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                Directory.Delete(profile, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 99)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }
}
