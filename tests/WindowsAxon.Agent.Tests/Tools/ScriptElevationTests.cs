using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using NSubstitute;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;
using WindowsAxon.App;
using WindowsAxon.ScriptHost;
using WindowsAxon.Windows;

namespace WindowsAxon.Agent.Tests.Tools;

public sealed class ScriptElevationTests : IDisposable
{
    private const string Code = "Write-Output 'approved-script'";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "axon-elevation-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public ScriptElevationTests() => Directory.CreateDirectory(_folder);

    private ScriptTools Tools(Action<ProcessStartInfo> launch, bool elevated = false, TimeSpan? timeout = null)
    {
        var model = Substitute.For<IChatClient>();
        model.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                JsonSerializer.Serialize(new { Code, Summary = "Test script", Packages = Array.Empty<string>() }))));
        return new ScriptTools(model, _folder, "session")
        {
            LaunchHost = launch,
            IsElevated = () => elevated,
            HostPath = () => "WindowsAxon.ScriptHost.exe",
            HostTimeout = timeout ?? TimeSpan.FromSeconds(10)
        };
    }

    private async Task<(string Id, string Path)> Create(ScriptTools tools)
    {
        var draft = JsonSerializer.SerializeToElement(await tools.Create("powershell", "test", Ct));
        return (draft.GetProperty("Id").GetString()!, draft.GetProperty("Path").GetString()!);
    }

    private static string Argument(ProcessStartInfo start, string name) =>
        start.ArgumentList[start.ArgumentList.IndexOf(name) + 1];

    private static void Complete(ProcessStartInfo start, string status, string text)
    {
        var output = Argument(start, "--output");
        File.WriteAllText(output, text);
        var temporary = output + ".tmp";
        File.WriteAllText(temporary, status);
        File.Move(temporary, output + ".status");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Ordinary_or_already_elevated_runs_do_not_launch_host(bool processElevated, bool requestElevated)
    {
        var tools = Tools(_ => throw new Xunit.Sdk.XunitException("Host must not start."), processElevated);
        var (id, _) = await Create(tools);
        Assert.Contains("approved-script", await tools.Run(id, Ct, requestElevated));
        if (requestElevated)
        {
            Assert.Contains("administrator rights", tools.Decide(Call(id, true)).Note);
        }
    }

    private static FunctionCallContent Call(string id, bool elevated) =>
        new("c1", "script_run", new Dictionary<string, object?> { ["id"] = id, ["elevated"] = elevated });

    [Fact]
    public async Task Elevated_launch_uses_runas_and_argument_list_without_redirection()
    {
        var count = 0;
        string? expectedPath = null;
        var tools = Tools(start =>
        {
            count++;
            Assert.True(start.UseShellExecute);
            Assert.Equal("runas", start.Verb);
            Assert.Equal("", start.Arguments);
            Assert.Equal("", start.WorkingDirectory);
            Assert.False(start.RedirectStandardInput || start.RedirectStandardOutput || start.RedirectStandardError);
            Assert.Equal(expectedPath, Argument(start, "--script"));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expectedPath!))), Argument(start, "--hash"));
            Complete(start, "0", "administrator-output");
        });
        var (id, path) = await Create(tools);
        expectedPath = path;
        var result = await tools.Run(id, Ct, elevated: true);
        Assert.Equal(1, count);
        Assert.StartsWith("Exit code 0. Output file: " + Path.ChangeExtension(path, ".out.txt"), result);
        Assert.Contains("administrator-output", result);
    }

    [Fact]
    public async Task Approval_preserves_ordinary_note_and_includes_full_code_for_administrator_run()
    {
        var tools = Tools(_ => { });
        var (id, _) = await Create(tools);
        var ordinary = tools.Decide(Call(id, false));
        Assert.Equal($"Run powershell: Test script. No undo. Review the complete code:\n{Code}", ordinary.Note);
        var admin = tools.Decide(Call(id, true));
        Assert.False(admin.AllowForConversation);
        Assert.Contains(Code, admin.Note);
        Assert.Contains("Windows will ask you to approve administrator rights", admin.Note);
        Assert.Contains("No undo", admin.Note);
    }

    [Fact]
    public async Task Tampering_fails_before_launcher()
    {
        var calls = 0;
        var tools = Tools(_ => calls++);
        var (id, path) = await Create(tools);
        File.AppendAllText(path, "\nWrite-Output 'changed'");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.Run(id, Ct, true));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Uac_denial_reports_no_execution_without_retry()
    {
        var calls = 0;
        var tools = Tools(_ => { calls++; throw new Win32Exception(1223); });
        var (id, _) = await Create(tools);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.Run(id, Ct, true));
        Assert.Contains("denied the administrator prompt", error.Message);
        Assert.Contains("script did not run", error.Message);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("1", "Script failed")]
    [InlineData("3", "rejected the script and did not run it")]
    [InlineData("invalid", "invalid status")]
    public async Task Host_failure_and_rejection_never_report_success(string status, string message)
    {
        var tools = Tools(start => Complete(start, status, "host diagnostic"));
        var (id, _) = await Create(tools);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.Run(id, Ct, true));
        Assert.Contains(message, error.Message);
        if (status != "invalid")
        {
            Assert.Contains("Output file:", error.Message);
            Assert.Contains("host diagnostic", error.Message);
        }
    }

    [Fact]
    public async Task Host_cancel_status_propagates_cancellation()
    {
        var tools = Tools(start => Complete(start, "2", "Script stopped."));
        var (id, _) = await Create(tools);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.Run(id, Ct, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_status_or_crashed_host_times_out_even_if_output_exists(bool crash)
    {
        string? cancel = null;
        var tools = Tools(start =>
        {
            cancel = Argument(start, "--cancel");
            if (crash)
            {
                return;
            }
            File.WriteAllText(Argument(start, "--output"), "not final");
        }, timeout: TimeSpan.FromMilliseconds(400));
        var (id, path) = await Create(tools);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.Run(id, Ct, true).WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.True(File.Exists(cancel));
        Assert.Equal("Script stopped.", File.ReadAllText(Path.ChangeExtension(path, ".out.txt")));
    }

    [Fact]
    public async Task Parent_waits_for_status_before_reading_output()
    {
        var entered = new TaskCompletionSource<ProcessStartInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = Tools(start =>
        {
            File.WriteAllText(Argument(start, "--output"), "unfinished");
            entered.SetResult(start);
        });
        var (id, _) = await Create(tools);
        var running = tools.Run(id, Ct, true);
        var start = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await Task.Delay(200, Ct);
        Assert.False(running.IsCompleted);
        Complete(start, "0", "complete");
        Assert.Contains("\ncomplete\n", await running);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_or_timeout_creates_cancel_while_Uac_is_open_and_late_approval_cannot_execute(bool timedOut)
    {
        var entered = new TaskCompletionSource<ProcessStartInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var approve = new ManualResetEventSlim();
        var late = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = Tools(start =>
        {
            entered.SetResult(start);
            approve.Wait(Ct);
            var options = HostOptions.Parse(start.ArgumentList.ToArray(), Path.Combine(_folder, "work"));
            late.SetResult(HostRunner.RunAsync(options, Ct, (_, _, _) =>
                throw new Xunit.Sdk.XunitException("Late approval must not start the interpreter.")).GetAwaiter().GetResult());
        }, timeout: TimeSpan.FromMilliseconds(timedOut ? 700 : 10000));
        var (id, path) = await Create(tools);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var running = tools.Run(id, stop.Token, true);
        var start = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        try
        {
            if (!timedOut)
            {
                stop.Cancel();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            Assert.True(File.Exists(Argument(start, "--cancel")));
        }
        finally
        {
            approve.Set();
        }
        Assert.Equal(2, await late.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal("2", File.ReadAllText(Argument(start, "--output") + ".status"));
        Assert.Equal("Script stopped.", File.ReadAllText(Path.ChangeExtension(path, ".out.txt")));
    }

    [Fact]
    public async Task AutoApprove_skips_card_but_still_uses_elevated_launcher()
    {
        var calls = 0;
        var tools = Tools(start => { calls++; Complete(start, "0", "ok"); });
        var (id, _) = await Create(tools);
        var catalog = new ToolCatalog();
        ToolRegistration.Add(catalog, tools);
        var entry = catalog.Find("script_run")!;
        Assert.Equal(RiskLevel.Danger, entry.Risk);
        Assert.True(entry.PolicyDriven);
        Assert.Equal(TimeSpan.FromSeconds(120), entry.Timeout);
        var schema = entry.Function.JsonSchema;
        Assert.Equal("boolean", schema.GetProperty("properties").GetProperty("elevated").GetProperty("type").GetString());
        Assert.DoesNotContain(schema.GetProperty("required").EnumerateArray(), item => item.GetString() == "elevated");
        var policy = new ApprovalPolicy(catalog, [tools], autoApprove: true);
        Assert.Equal(ApprovalOutcome.AutoApprove, policy.Decide(Call(id, true)).Outcome);
        await tools.Run(id, Ct, true);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Extraction_verifies_hash_and_repairs_modified_bytes()
    {
        byte[] bytes = [1, 2, 3, 4];
        var path = ScriptHostLaunch.Extract(bytes, _folder);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        File.WriteAllText(path, "changed");
        Assert.Equal(path, ScriptHostLaunch.Extract(bytes, _folder));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "WindowsAxon.slnx")))
        {
            folder = folder.Parent;
        }
        return folder?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Manifests_preserve_app_privileges_and_host_has_no_Windows_project_reference()
    {
        var root = RepositoryRoot();
        var app = XDocument.Load(Path.Combine(root, "src", "WindowsAxon.App", "app.manifest"));
        var host = XDocument.Load(Path.Combine(root, "src", "WindowsAxon.ScriptHost", "app.manifest"));
        Assert.Equal("asInvoker", app.Descendants().Single(e => e.Name.LocalName == "requestedExecutionLevel").Attribute("level")?.Value);
        Assert.Equal("requireAdministrator", host.Descendants().Single(e => e.Name.LocalName == "requestedExecutionLevel").Attribute("level")?.Value);
        Assert.Equal(app.Descendants().Single(e => e.Name.LocalName == "supportedOS").Attribute("Id")?.Value,
                     host.Descendants().Single(e => e.Name.LocalName == "supportedOS").Attribute("Id")?.Value);
        var project = XDocument.Load(Path.Combine(root, "src", "WindowsAxon.ScriptHost", "WindowsAxon.ScriptHost.csproj"));
        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Equal("WinExe", project.Descendants("OutputType").Single().Value);
        Assert.Equal("WindowsAxon Script Host", project.Descendants("Product").Single().Value);
    }

    [Fact]
    public void Catalog_preserves_all_existing_tools_without_an_elevation_tool()
    {
        var catalog = new ToolCatalog();
        ToolRegistration.Add(catalog, new WindowsAppTools(() => [], (_, _) => Task.FromResult("opened")));
        ToolRegistration.Add(catalog, new WindowsWindowTools(() => [], _ => { }, _ => false, TimeSpan.Zero));
        using var tools = new WindowsToolSet(catalog, Substitute.For<IChatClient>(), _folder, "session",
                                            _ => nint.Zero, () => { }, _ => { });
        Assert.Equal(90, catalog.Entries.Count);
        Assert.Single(catalog.Entries, entry => entry.Name == "script_run");
        Assert.DoesNotContain(catalog.Entries, entry => entry.Name.Contains("elevated", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
