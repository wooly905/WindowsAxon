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

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Readable_check_rejects_a_blocked_status(int status)
    {
        var response = Substitute.For<IResponse>();
        response.Status.Returns(status);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WebTools.EnsureReadableAsync(null, response));

        Assert.Equal(WebTools.BlockedStatusMessage(status), error.Message);
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
        var captcha = await Assert.ThrowsAsync<InvalidOperationException>(() => WebTools.EnsureReadableAsync(page, null));
        Assert.Equal(WebTools.CaptchaMessage, captcha.Message);

        await page.SetContentAsync("<p>Please verify you are human before continuing.</p>");
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => WebTools.EnsureReadableAsync(page, null));
        Assert.Equal(WebTools.BlockedMessage, blocked.Message);

        await page.SetContentAsync("<form><input type='password'></form>");
        var signIn = await Assert.ThrowsAsync<InvalidOperationException>(() => WebTools.EnsureReadableAsync(page, null));
        Assert.Equal(WebTools.SignInMessage, signIn.Message);

        await page.SetContentAsync("<h1>CPU upgrade</h1>");
        await WebTools.EnsureReadableAsync(page, null);
    }

    [Fact]
    public async Task Web_read_keeps_text_beyond_the_observation_limit()
    {
        var catalog = new ToolCatalog();
        var text = new string('y', 3000);
        catalog.Add(AIFunctionFactory.Create(() => text, "web_read"), "web", RiskLevel.Read, policyDriven: true);
        var state = new ToolSelectionState(catalog, 14) { AllToolsActive = true };
        var guard = new GuardedAIFunction(catalog.Find("web_read")!,
                                          state,
                                          new CallTracker(),
                                          new ApprovalLedger(),
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
            tools.TrackForClose(research.Browser!, researchProcess, researchProfile);

            tools.Close();

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
