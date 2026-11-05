using System.Security.Cryptography;
using System.Text;
using WindowsAxon.ScriptHost;

namespace WindowsAxon.Agent.Tests.Tools;

public sealed class ScriptHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "axon-host-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_root, "session");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public ScriptHostTests()
    {
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(Path.Combine(Workspace, "script.ps1"), "Write-Output 'host-output'", new UTF8Encoding(false));
    }

    private string[] Arguments(string language = "powershell") =>
        ["--workspace", Workspace, "--script", Path.Combine(Workspace, "script.ps1"), "--language", language,
         "--hash", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Workspace, "script.ps1")))),
         "--output", Path.Combine(Workspace, "output.txt"), "--cancel", Path.Combine(Workspace, "cancel")];

    private HostOptions Options() => HostOptions.Parse(Arguments(), _root);

    [Theory]
    [InlineData("--script")]
    [InlineData("--output")]
    [InlineData("--cancel")]
    public void Paths_outside_workspace_including_prefix_siblings_are_rejected(string name)
    {
        var args = Arguments();
        args[Array.IndexOf(args, name) + 1] = Path.Combine(Workspace + "-outside", "file");
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(args, _root));
        args[Array.IndexOf(args, name) + 1] = Path.Combine(Workspace, "..", "file");
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(args, _root));
    }

    [Fact]
    public void Entry_point_workspace_root_is_enforced()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(Arguments(), Path.Combine(_root, "other")));
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsAxon", "work");
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(Arguments(), root));
    }

    [Theory]
    [InlineData("--workspace")]
    [InlineData("--script")]
    [InlineData("--language")]
    [InlineData("--hash")]
    [InlineData("--output")]
    [InlineData("--cancel")]
    public void Missing_required_arguments_are_rejected(string name)
    {
        var args = Arguments().ToList();
        args.RemoveRange(args.IndexOf(name), 2);
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(args.ToArray(), _root));
    }

    [Fact]
    public void Unknown_duplicate_extra_and_invalid_language_arguments_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => HostOptions.Parse([.. Arguments(), "--unknown", "value"], _root));
        Assert.Throws<ArgumentException>(() => HostOptions.Parse([.. Arguments(), "--language", "python"], _root));
        Assert.Throws<ArgumentException>(() => HostOptions.Parse([.. Arguments(), "extra"], _root));
        Assert.Throws<ArgumentException>(() => HostOptions.Parse(Arguments("cmd"), _root));
    }

    [Fact]
    public void Validator_requires_uppercase_approved_hash()
    {
        var options = Options();
        Assert.Throws<ArgumentException>(() => (options with { Hash = "00" }).Validate());
        Assert.Throws<ArgumentException>(() => (options with { Hash = options.Hash.ToLowerInvariant() }).Validate());
        File.AppendAllText(options.Script, "\nWrite-Output 'changed'");
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void PowerShell_uses_only_System32_and_stdin_without_policy_flags()
    {
        var (start, input) = Options().Validate();
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), start.FileName);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-Command", "-"], start.ArgumentList);
        Assert.Contains(Convert.ToBase64String(File.ReadAllBytes(Options().Script)), input);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.Equal(Encoding.UTF8, start.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, start.StandardErrorEncoding);
    }

    [Fact]
    public void Python_requires_exact_isolated_interpreter_and_uses_stdin()
    {
        var options = HostOptions.Parse(Arguments("python"), _root);
        Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Throws<ArgumentException>(() => (options with { Python = "python.exe" }).Validate());
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "WindowsAxon", "python", ".venv", "Scripts", "python.exe");
        Assert.Throws<ArgumentException>(() => (options with { Python = expected + ".other" }).Validate());
        var (start, input) = (options with { Python = expected }).Validate();
        Assert.Equal(expected, start.FileName);
        Assert.Equal(["-I", "-X", "utf8", "-"], start.ArgumentList);
        Assert.Equal(File.ReadAllText(options.Script), input);
    }

    [Fact]
    public async Task Preexisting_cancel_file_prevents_interpreter_start()
    {
        var options = Options();
        File.WriteAllText(options.Cancel, "cancel");
        var called = false;
        var status = await HostRunner.RunAsync(options, Ct, (_, _, _) => { called = true; return Task.FromResult("unexpected"); });
        Assert.False(called);
        Assert.Equal(2, status);
        Assert.Equal("2", File.ReadAllText(options.Output + ".status"));
    }

    [Fact]
    public async Task Validation_rejection_publishes_status_without_interpreter()
    {
        var options = Options() with { Hash = "wrong" };
        var called = false;
        Assert.Equal(3, await HostRunner.RunAsync(options, Ct, (_, _, _) => { called = true; return Task.FromResult(""); }));
        Assert.False(called);
        Assert.Equal("3", File.ReadAllText(options.Output + ".status"));
        Assert.Contains("modified", File.ReadAllText(options.Output));
    }

    [Fact]
    public async Task Output_is_not_final_until_atomic_status_publication()
    {
        var options = Options();
        Assert.Equal(0, await HostRunner.RunAsync(options, Ct, (_, _, _) =>
        {
            Assert.False(File.Exists(options.Output + ".status"));
            return Task.FromResult("complete output");
        }));
        Assert.Equal("0", File.ReadAllText(options.Output + ".status"));
        Assert.Equal("complete output", File.ReadAllText(options.Output));
        Assert.Empty(Directory.GetFiles(Workspace, "*.tmp"));
    }

    [Fact]
    public async Task Interpreter_failure_publishes_failure_and_output()
    {
        var options = Options();
        Assert.Equal(1, await HostRunner.RunAsync(options, Ct, (_, _, _) => throw new InvalidOperationException("interpreter failure")));
        Assert.Equal("1", File.ReadAllText(options.Output + ".status"));
        Assert.Equal("interpreter failure", File.ReadAllText(options.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_polls_cancel_and_propagates_timeout(bool timedOut)
    {
        var options = Options();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        if (timedOut)
        {
            timeout.CancelAfter(200);
        }
        var status = await HostRunner.RunAsync(options, timeout.Token, async (_, _, token) =>
        {
            if (!timedOut)
            {
                File.WriteAllText(options.Cancel, "cancel");
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unexpected";
        }).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(2, status);
        Assert.Equal("2", File.ReadAllText(options.Output + ".status"));
    }

    [Fact]
    public async Task Interpreter_captures_utf8_stderr_and_truncates_both_streams()
    {
        var options = Options();
        File.WriteAllText(options.Script,
            "[Console]::Write('台灣');[Console]::Write(('x' * (1024*1024)));[Console]::Error.Write(('e' * (1024*1024+1)))",
            new UTF8Encoding(false));
        options = options with { Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.Script))) };
        Assert.Equal(0, await HostRunner.RunAsync(options, Ct));
        var output = File.ReadAllText(options.Output);
        Assert.StartsWith("台灣", output);
        Assert.Equal(2, output.Split("(output truncated at 1 MB)").Length - 1);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
