using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.Agent.Tools;
using WindowsAxon.ScriptHost;
using WindowsAxon.Windows;
using NSubstitute;

namespace WindowsAxon.Agent.Tests.Tools;

public sealed class ScriptElevationTests : IDisposable
{
    private const string FakeHost = @"C:\fake\WindowsAxon.ScriptHost.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(),
                                                   "windowsaxon-elevation-test-" + Guid.NewGuid().ToString("N"));

    public ScriptElevationTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public async Task Elevated_false_does_not_call_the_elevated_launcher()
    {
        var scripts = Tools("Write-Output 'plain'", NeverLaunch, isElevated: false);
        var id = await Create(scripts);

        var result = await scripts.Run(id, TestContext.Current.CancellationToken);

        Assert.Contains("plain", result);
    }

    [Fact]
    public async Task Elevated_true_in_an_elevated_process_runs_directly_without_the_host()
    {
        var scripts = Tools("Write-Output 'direct'", NeverLaunch, isElevated: true);
        var id = await Create(scripts);

        var result = await scripts.Run(id, TestContext.Current.CancellationToken, elevated: true);

        Assert.Contains("direct", result);
    }

    [Fact]
    public async Task Elevated_launcher_receives_runas_script_path_hash_and_user_data()
    {
        ProcessStartInfo? captured = null;
        var scripts = Tools("Write-Output 'x'",
                            start =>
                            {
                                captured = start;
                                Complete(start, "0", "elevated-output");
                                return null;
                            });
        var draft = await CreateDraft(scripts);
        var path = draft.GetProperty("Path").GetString()!;

        var result = await scripts.Run(draft.GetProperty("Id").GetString()!,
                                       TestContext.Current.CancellationToken,
                                       elevated: true);

        Assert.NotNull(captured);
        Assert.True(captured.UseShellExecute);
        Assert.Equal("runas", captured.Verb);
        Assert.Equal(FakeHost, captured.FileName);
        Assert.Equal(path, Argument(captured, "--script"));
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
                     Argument(captured, "--hash"));
        Assert.Equal(_folder, Argument(captured, "--user-data"));
        Assert.Contains("Exit code 0", result);
        Assert.Contains("elevated-output", result);
    }

    [Fact]
    public async Task Elevated_approval_note_contains_full_code_and_administrator_wording()
    {
        var scripts = Tools("Write-Output 'note-code'", NeverLaunch);
        var id = await Create(scripts);

        var elevated = scripts.Decide(Call(id, true));
        var plain = scripts.Decide(Call(id, false));
        var json = scripts.Decide(Call(id, JsonSerializer.SerializeToElement(true)));

        Assert.False(elevated.AllowForConversation);
        Assert.Contains("Write-Output 'note-code'", elevated.Note);
        Assert.Contains("administrator rights", elevated.Note);
        Assert.Contains("Windows will ask the user to approve", elevated.Note);
        Assert.Contains("administrator rights", json.Note);
        Assert.Equal($"Run powershell: Test script. No undo. Review the complete code:\nWrite-Output 'note-code'",
                     plain.Note);
        Assert.False(plain.AllowForConversation);
    }

    [Fact]
    public async Task Auto_approve_cannot_skip_any_script_execution_card()
    {
        var scripts = Tools("Write-Output 'x'", NeverLaunch);
        var catalog = new ToolCatalog();
        ToolRegistration.Add(catalog, scripts);
        var policy = new ApprovalPolicy(catalog, [scripts], autoApprove: true);
        var id = await Create(scripts);

        var elevated = policy.Decide(Call(id, true));
        var plain = policy.Decide(Call(id, false));
        var unknown = policy.Decide(Call("missing", JsonSerializer.SerializeToElement(true)));

        Assert.Equal(ApprovalOutcome.Ask, elevated.Outcome);
        Assert.False(elevated.AllowForConversation);
        Assert.Contains("administrator rights", elevated.Note);
        Assert.Equal(ApprovalOutcome.Ask, plain.Outcome);
        Assert.Equal(ApprovalOutcome.Ask, unknown.Outcome);
    }

    [Fact]
    public async Task Hash_mismatch_throws_before_the_launcher_is_called()
    {
        var scripts = Tools("Write-Output 'x'", NeverLaunch);
        var draft = await CreateDraft(scripts);
        File.AppendAllText(draft.GetProperty("Path").GetString()!, "\nWrite-Output 'tampered'");

        await Assert.ThrowsAsync<InvalidOperationException>(() => scripts.Run(draft.GetProperty("Id").GetString()!,
                                                                              TestContext.Current.CancellationToken,
                                                                              elevated: true));
    }

    [Fact]
    public async Task Denied_UAC_prompt_reports_that_the_script_did_not_run()
    {
        var scripts = Tools("Write-Output 'x'", _ => throw new Win32Exception(1223));
        var id = await Create(scripts);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scripts.Run(id,
                                                                                            TestContext.Current.CancellationToken,
                                                                                            elevated: true));

        Assert.Contains("denied", error.Message);
        Assert.Contains("did not run", error.Message);
        Assert.DoesNotContain("Exit code 0", error.Message);
    }

    [Fact]
    public async Task Stop_while_UAC_is_open_creates_the_cancel_file_and_cancels()
    {
        using var release = new ManualResetEventSlim();
        var launched = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scripts = Tools("Write-Output 'x'",
                            start =>
                            {
                                launched.SetResult(Argument(start, "--cancel"));
                                release.Wait(TimeSpan.FromSeconds(30));
                                return null;
                            });
        var id = await Create(scripts);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = scripts.Run(id, stop.Token, elevated: true);
        var cancelPath = await launched.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(File.Exists(cancelPath), "Stop did not create the cancel file.");
        release.Set();
    }

    [Theory]
    [InlineData("1", typeof(InvalidOperationException), "Script failed. Output file:")]
    [InlineData("2", typeof(OperationCanceledException), "stopped or timed out")]
    [InlineData("3", typeof(InvalidOperationException), "rejected the script and did not run it")]
    public async Task Host_status_maps_to_the_tool_result(string status, Type expected, string message)
    {
        var scripts = Tools("Write-Output 'x'",
                            start =>
                            {
                                Complete(start, status, "host-output");
                                return null;
                            });
        var id = await Create(scripts);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => scripts.Run(id,
                                                                             TestContext.Current.CancellationToken,
                                                                             elevated: true));

        Assert.IsType(expected, error);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Host_validator_accepts_a_user_data_root_other_than_its_own_profile()
    {
        var request = HostRequest.Parse(HostArgs());

        Assert.Equal(@"C:\Users\standard\AppData\Local\WindowsAxon", request.UserData);
        Assert.NotEqual(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\WindowsAxon",
                        request.UserData);
    }

    [Theory]
    [InlineData("--script", @"C:\Users\standard\AppData\Local\WindowsAxon\work\s1-other\a.ps1")]
    [InlineData("--output", @"C:\Users\standard\AppData\Local\Temp\a.out.txt")]
    [InlineData("--cancel", @"C:\Users\standard\AppData\Local\WindowsAxon\work\s1\..\..\a.cancel")]
    [InlineData("--user-data", @"C:\ProgramData\WindowsAxon")]
    [InlineData("--workspace", @"C:\Users\standard\AppData\Local\WindowsAxon\other\s1")]
    [InlineData("--language", "cmd")]
    [InlineData("--script", @"relative\a.ps1")]
    public void Host_validator_rejects_paths_outside_the_rules(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => HostRequest.Parse(HostArgs((name, value))));
    }

    [Fact]
    public void Host_validator_rejects_missing_unknown_and_extra_arguments()
    {
        var args = HostArgs();
        Assert.Throws<ArgumentException>(() => HostRequest.Parse(args.Take(args.Length - 2).ToArray()));
        Assert.Throws<ArgumentException>(() => HostRequest.Parse([.. args, "--unknown", "x"]));
        Assert.Throws<ArgumentException>(() => HostRequest.Parse([.. args, "--python", @"C:\python.exe"]));
        Assert.Throws<ArgumentException>(() => HostRequest.Parse([.. args, "--hash"]));
    }

    [Fact]
    public void Host_verifier_rejects_hash_mismatch_and_unexpected_python()
    {
        var script = Path.Combine(_folder, "a.py");
        File.WriteAllText(script, "print('x')");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(script)));
        var python = Path.Combine(_folder, "python", ".venv", "Scripts", "python.exe");

        Assert.Throws<InvalidDataException>(() => Request(script, "python", "00", python).Verify(@"C:\Windows\System32"));
        Assert.Throws<InvalidDataException>(() => Request(script, "python", hash, @"C:\Python\python.exe")
                                                .Verify(@"C:\Windows\System32"));
        Assert.Equal(python, Request(script, "python", hash, python).Verify(@"C:\Windows\System32").Interpreter);
        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                     Request(script, "powershell", hash, null).Verify(@"C:\Windows\System32").Interpreter);
    }

    [Fact]
    public void Host_runner_returns_stopped_without_starting_when_cancel_file_exists()
    {
        var script = Path.Combine(_folder, "a.ps1");
        var request = Request(script, "powershell", "", null);
        File.WriteAllText(request.Cancel, "");

        var (status, _) = ScriptRunner.Run(request,
                                           [],
                                           "powershell.exe",
                                           _ => throw new InvalidOperationException("The interpreter must not start."),
                                           ScriptRunner.Timeout);

        Assert.Equal(ScriptRunner.Stopped, status);
    }

    [Fact]
    public void Host_publishes_output_before_the_status_file()
    {
        var output = Path.Combine(_folder, "a.out.txt");

        ScriptRunner.Publish(output, ScriptRunner.Success, "done");

        Assert.Equal("done", File.ReadAllText(output));
        Assert.Equal("0", File.ReadAllText(output + ".status"));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void App_stays_as_invoker_and_host_requires_administrator()
    {
        Assert.Contains("level=\"asInvoker\"", File.ReadAllText(RepoPaths.File("src/WindowsAxon.App/app.manifest")));
        Assert.Contains("level=\"requireAdministrator\"",
                        File.ReadAllText(RepoPaths.File("src/WindowsAxon.ScriptHost/app.manifest")));
    }

    [Fact]
    public void Operational_catalog_has_90_tools()
    {
        var catalog = new ToolCatalog();
        var windowTools = new WindowsAxon.App.WindowsWindowTools();
        ToolRegistration.Add(catalog, new WindowsAxon.App.WindowsAppTools());
        ToolRegistration.Add(catalog, windowTools);
        using var toolSet = new WindowsToolSet(catalog,
                                               Substitute.For<IChatClient>(),
                                               _folder,
                                               "test",
                                               windowTools.ResolveHandle,
                                               () => { },
                                               _ => { });

        Assert.Equal(90, catalog.Entries.Count);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A terminated script may hold the workspace briefly; the temp folder is disposable.
        }
    }

    private static Process? NeverLaunch(ProcessStartInfo start)
    {
        Assert.Fail("The elevated launcher must not be called.");
        return null;
    }

    private ScriptTools Tools(string code, Func<ProcessStartInfo, Process?> launch, bool isElevated = false) =>
        new(Writer(code), _folder, "test")
        {
            Elevation = new ScriptElevation(() => isElevated, () => FakeHost, launch)
        };

    private static async Task<JsonElement> CreateDraft(ScriptTools scripts) =>
        JsonSerializer.SerializeToElement(await scripts.Create("powershell", "test", TestContext.Current.CancellationToken));

    private static async Task<string> Create(ScriptTools scripts) =>
        (await CreateDraft(scripts)).GetProperty("Id").GetString()!;

    private static FunctionCallContent Call(string id, object elevated) =>
        new("1", "script_run", new Dictionary<string, object?> { ["id"] = id, ["elevated"] = elevated });

    private static string Argument(ProcessStartInfo start, string name) =>
        start.ArgumentList[start.ArgumentList.IndexOf(name) + 1];

    // Simulates the host's completion protocol: output first, then the status file.
    private static void Complete(ProcessStartInfo start, string status, string output)
    {
        var path = Argument(start, "--output");
        File.WriteAllText(path, output);
        File.WriteAllText(path + ".status", status);
    }

    private static string[] HostArgs(params (string Name, string Value)[] overrides)
    {
        const string workspace = @"C:\Users\standard\AppData\Local\WindowsAxon\work\s1";
        var values = new List<(string Name, string Value)>
        {
            ("--workspace", workspace),
            ("--script", workspace + @"\a.ps1"),
            ("--language", "powershell"),
            ("--hash", "ABC"),
            ("--output", workspace + @"\a.out.txt"),
            ("--cancel", workspace + @"\a.1.cancel"),
            ("--user-data", @"C:\Users\standard\AppData\Local\WindowsAxon")
        };
        foreach (var (name, value) in overrides)
        {
            values[values.FindIndex(v => v.Name == name)] = (name, value);
        }

        return values.SelectMany(v => new[] { v.Name, v.Value }).ToArray();
    }

    private HostRequest Request(string script, string language, string hash, string? python) =>
        new(_folder,
            script,
            language,
            hash,
            Path.Combine(_folder, "a.out.txt"),
            Path.Combine(_folder, "a.cancel"),
            _folder,
            python);

    private static IChatClient Writer(string code)
    {
        var model = Substitute.For<IChatClient>();
        model.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(),
                               Arg.Any<ChatOptions?>(),
                               Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                                                      JsonSerializer.Serialize(new
                                                      {
                                                          Code = code,
                                                          Summary = "Test script",
                                                          Packages = Array.Empty<string>()
                                                      }))));
        return model;
    }
}
