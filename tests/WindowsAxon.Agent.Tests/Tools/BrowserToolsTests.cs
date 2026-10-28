using System.Reflection;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Guard;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.Agent.Tools;
using WindowsAxon.Windows;
using Microsoft.Playwright;

namespace WindowsAxon.Agent.Tests.Tools;

public sealed class BrowserToolsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnecting_tools_keeps_the_independent_browser_and_tabs_alive(bool stopped)
    {
        var profile = Directory.CreateTempSubdirectory("windowsaxon-browser-lifetime-").FullName;
        using var controller = await Playwright.CreateAsync();
        using var verifier = await Playwright.CreateAsync();
        IBrowserContext? verificationContext = null;
        try
        {
            var context = await BrowserTools.ConnectBrowserAsync(controller,
                                                                  profile,
                                                                  "msedge",
                                                                  headless: true,
                                                                  cancellationToken: TestContext.Current.CancellationToken);
            var page = context.Pages[0];
            await page.SetContentAsync("<h1>Independent browser</h1>");
            var tools = Attach(context, page);
            typeof(BrowserTools).GetField("_playwright", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(tools, controller);
            if (stopped)
            {
                tools.CancelPending();
            }
            else
            {
                await tools.DisposeAsync();
            }

            verificationContext = await BrowserTools.ConnectBrowserAsync(verifier,
                                                                          profile,
                                                                          "msedge",
                                                                          headless: true,
                                                                          cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains(verificationContext.Pages, p => !p.IsClosed);
            Assert.Equal("Independent browser", await verificationContext.Pages[0].Locator("h1").InnerTextAsync());
            var reconnected = Attach(verificationContext, verificationContext.Pages[0]);
            Assert.Contains("Independent browser", await reconnected.Read());
            await reconnected.DisposeAsync();
            Assert.False(verificationContext.Pages[0].IsClosed);
        }
        finally
        {
            verificationContext ??= await BrowserTools.ConnectBrowserAsync(verifier,
                                                                            profile,
                                                                            "msedge",
                                                                            headless: true,
                                                                            cancellationToken: TestContext.Current.CancellationToken);
            var cleanup = await verificationContext.Browser!.NewBrowserCDPSessionAsync();
            await cleanup.SendAsync("Browser.close");
            for (var attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    Directory.Delete(profile, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 99)
                {
                    // Wait for the test browser to release its profile files after Browser.close.
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_excludes_scripts_styles_and_hidden_content_without_changing_the_page(bool useReference)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = "msedge",
            Headless = true
        });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await using var tools = Attach(context, page);
        await page.SetContentAsync("""
            <button>
              <span>Faculty: <strong>Jane Doe</strong></span><br>
              <span>Professor &amp; researcher</span>
              <script type="application/json" style="display:block">{"internalScriptMarker":true}</script>
              <style style="display:block">.internalCssMarker { color: red; }</style>
              <template>internalTemplateMarker</template>
              <span hidden>internalHiddenMarker</span>
              <span style="visibility:hidden">internalInvisibleMarker</span>
              <!-- internalCommentMarker -->
            </button>
            """);
        var before = await page.ContentAsync();
        string? reference = null;
        if (useReference)
        {
            await tools.Snapshot();
            var refs = (Dictionary<string, (IPage Page, IElementHandle Element, string Name)>)typeof(BrowserTools)
                .GetField("_refs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tools)!;
            reference = Assert.Single(refs).Key;
        }

        var result = await tools.Read(reference);

        Assert.Contains("Faculty: Jane Doe", result);
        Assert.Contains("Professor & researcher", result);
        Assert.DoesNotContain("internal", result);
        Assert.DoesNotContain("<strong>", result);
        Assert.Equal(before, await page.ContentAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_delivers_text_beyond_1500_characters_to_the_model_and_audit(bool useReference)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = "msedge",
            Headless = true
        });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await using var tools = Attach(context, page);
        var fullText = new string('x', 3000) + " Faculty: Jane Doe, Professor. 4111111111111111";
        await page.SetContentAsync($"<button>{fullText}</button>");
        string? reference = null;
        if (useReference)
        {
            await tools.Snapshot();
            var refs = (Dictionary<string, (IPage Page, IElementHandle Element, string Name)>)typeof(BrowserTools)
                .GetField("_refs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tools)!;
            reference = Assert.Single(refs).Key;
        }
        var catalog = new ToolCatalog();
        ToolRegistration.Add(catalog, tools);
        var state = new ToolSelectionState(catalog, 14) { AllToolsActive = true };
        var audit = new ListAuditLog();
        var guard = new GuardedAIFunction(catalog.Find("browser_read")!,
                                          state,
                                          new CallTracker(),
                                          new ApprovalLedger(),
                                          audit);
        var model = new ScriptedChatClient()
            .ReplyCall("browser_read", new Dictionary<string, object?> { ["reference"] = reference })
            .Reply("Jane Doe is a professor.");
        using var client = new FunctionInvokingChatClient(model);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Read the faculty list.")],
                                      new ChatOptions { Tools = [guard] },
                                      TestContext.Current.CancellationToken);

        var observation = Assert.Single(model.Calls[1].Messages.SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>());
        Assert.Equal(CardNumberMasker.Mask(fullText), observation.Result?.ToString());
        Assert.Equal(fullText, Assert.Single(audit.Entries, e => e.Kind == "call").Result);
    }

    [Theory]
    [InlineData("password", null, 0, false)]
    [InlineData("password", null, 1, false)]
    [InlineData("password", null, 2, false)]
    [InlineData("password", null, 1, true)]
    [InlineData("text", "one-time-code", 1, true)]
    [InlineData("text", "cc-number", 2, true)]
    public async Task Press_refuses_protected_fields_in_the_focused_frame(string type,
                                                                          string? autocomplete,
                                                                          int depth,
                                                                          bool crossOrigin)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = "msedge",
            Headless = true
        });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await using var tools = Attach(context, page);
        var html = $"<input type='{type}' autocomplete='{autocomplete}' value='unchanged'>";
        var frame = page.MainFrame;
        for (var i = 0; i < depth; i++)
        {
            await frame.SetContentAsync($"<iframe {(crossOrigin ? "sandbox='allow-scripts'" : "")}></iframe>");
            var iframe = await frame.Locator("iframe").ElementHandleAsync();
            frame = (await iframe!.ContentFrameAsync())!;
            await iframe.DisposeAsync();
        }
        await frame.SetContentAsync(html);
        var input = frame.Locator("input");
        await input.FocusAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.Press("A"));
        Assert.Equal("unchanged", await input.InputValueAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Press_accepts_an_ordinary_field_in_a_focused_frame(bool crossOrigin)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = "msedge",
            Headless = true
        });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await using var tools = Attach(context, page);
        await page.SetContentAsync($"<iframe {(crossOrigin ? "sandbox='allow-scripts'" : "")} srcdoc=\"<input type='text'>\"></iframe>");
        var input = page.FrameLocator("iframe").Locator("input");
        await input.FocusAsync();

        await tools.Press("A");
        Assert.Equal("A", await input.InputValueAsync());
    }

    [Fact]
    public async Task Press_refuses_a_protected_field_in_a_shadow_root()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = "msedge",
            Headless = true
        });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await using var tools = Attach(context, page);
        await page.SetContentAsync("<div id='host'></div>");
        await page.EvaluateAsync("() => document.querySelector('#host').attachShadow({mode:'open'}).innerHTML = '<input type=\"password\" value=\"unchanged\">'");
        var input = page.Locator("input");
        await input.FocusAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tools.Press("A"));
        Assert.Equal("unchanged", await input.InputValueAsync());
    }

    private static BrowserTools Attach(IBrowserContext context, IPage page)
    {
        var tools = new BrowserTools(Path.GetTempPath());
        typeof(BrowserTools).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tools,
                                                                                                            context);
        typeof(BrowserTools).GetField("_page", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tools, page);
        return tools;
    }
}
