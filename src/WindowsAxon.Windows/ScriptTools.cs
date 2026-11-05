using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Win32;

namespace WindowsAxon.Windows;

public sealed class ScriptTools(IChatClient model, string dataFolder, string session) : IApprovalRule
{
    private sealed record Draft(string Code, string Summary, string[] Packages);

    private sealed record Script(string Path, string Language, string Hash, string Summary);

    private readonly Dictionary<string, Script> _scripts = [];

    internal Func<bool> IsElevated { get; init; } = ScriptHostLaunch.IsElevated;

    internal Action<ProcessStartInfo> LaunchHost { get; init; } = ScriptHostLaunch.Start;

    internal Func<string> HostPath { get; init; } = ScriptHostLaunch.Resolve;

    internal TimeSpan HostTimeout { get; init; } = TimeSpan.FromSeconds(120);

    private string Workspace => Path.Combine(dataFolder, "work", session);

    private string Python => Path.Combine(dataFolder, "python", ".venv", "Scripts", "python.exe");

    public static void CheckPowerShellPolicy()
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey("Software\\Policies\\Microsoft\\Windows\\PowerShell");
            if (key?.GetValue("ExecutionPolicy") is not null || key?.GetValue("EnableScripts") is not null)
            {
                throw new UnauthorizedAccessException("An administrator PowerShell policy is configured. WindowsAxon will not run PowerShell scripts. Do the task another way, for example in the application or Settings window.");
            }
        }
    }

    [Tool("script_create",
          "scripts",
          "Generate a PowerShell or Python script for a task that other tools cannot perform. Checks syntax and saves it for review; does not execute it. Returns its ID, summary, packages, and code.",
          timeoutSeconds: 120,
          artifact: true)]
    public async Task<object> Create(string language, string task, CancellationToken cancellationToken)
    {
        if (language is not ("powershell" or "python"))
        {
            throw new ArgumentException("language must be powershell or python.");
        }

        if (language == "powershell")
        {
            CheckPowerShellPolicy();
        }

        var response = await model.GetResponseAsync([
new ChatMessage(ChatRole.System,
                "Write a complete Windows current-user script for the task. Return only JSON with Code, Summary, Packages (string array). No tools, no user input, no infinite loops. Never request elevation, change execution policy, Defender, firewall, or security settings. Do not delete files; use a _backup folder when necessary. Do not access the network unless asked. Target Windows PowerShell 5.1 or Python 3.14. Write new files in the workspace unless the task names a folder."),
            new ChatMessage(ChatRole.User, $"Language: {language}\nWorkspace: {Workspace}\nTask: {task}")

],
                                                    new ChatOptions { Tools = null },
                                                    cancellationToken);
        var json = response.Text.Trim();
        if (json.StartsWith("```"))
        {
            var first = json.IndexOf('\n');
            json = json[(first + 1)..json.LastIndexOf("```", StringComparison.Ordinal)].Trim();
        }
        var draft = JsonSerializer.Deserialize<Draft>(json,
                                                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("ScriptWriter returned no script.");
        if (string.IsNullOrWhiteSpace(draft.Code) || draft.Code.Length > 100000)
        {
            throw new InvalidOperationException("ScriptWriter returned invalid code.");
        }

        Directory.CreateDirectory(Workspace);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Workspace, id + (language == "python" ? ".py" : ".ps1"));
        await File.WriteAllTextAsync(path, draft.Code, new UTF8Encoding(false), cancellationToken);
        try
        {
            await Validate(language, path, cancellationToken);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        _scripts.Add(id, new(path, language, Hash(path), draft.Summary));
        return new
        {
            Id = id,
            draft.Summary,
            draft.Packages,
            Code = draft.Code,
            Path = path,
            Executed = false
        };
    }

    private async Task Validate(string language, string path, CancellationToken token)
    {
        if (language == "python")
        {
            if (!File.Exists(Python))
            {
                throw new InvalidOperationException("Run python_ensure before creating Python scripts.");
            }

            await WindowsCommand.RunAsync(Python, ["-I", "-m", "py_compile", path], token, workingDirectory: Workspace);
            return;
        }
        var code = Convert.ToBase64String(await File.ReadAllBytesAsync(path, token));
        var check = "$ErrorActionPreference='Stop'\n$code=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + code + "'))\n$tokens=$null\n$errors=$null\n[void][System.Management.Automation.Language.Parser]::ParseInput($code,[ref]$tokens,[ref]$errors)\nif($errors.Count){$errors | ForEach-Object { Write-Error $_.Message };exit 1}\n\n";
        await WindowsCommand.RunAsync("powershell.exe",
                                      ["-NoProfile", "-NonInteractive", "-Command", "-"],
                                      token,
                                      check,
                                      Workspace);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private Script Get(string id)
    {
        if (!_scripts.TryGetValue(id, out var script))
        {
            throw new ArgumentException("Unknown script ID. Use script_create first.");
        }

        if (Hash(script.Path) != script.Hash)
        {
            throw new InvalidOperationException("The script changed after creation or approval. Create and approve a new script.");
        }

        return script;
    }

    [Tool("script_run",
          "scripts",
          "Execute a previously created script after approval of its full code. Refuses modified scripts. Set elevated true only for a change that needs administrator rights; Windows asks for approval through UAC. Runs in its workspace with a 120-second timeout. Successful completion preserves child processes; Stop, timeout, or failure terminates the job. Exit code 0 confirms only script completion, not an open application window. Use dedicated launch tools for applications and management consoles. No automatic undo.",
          RiskLevel.Danger,
          policyDriven: true,
          timeoutSeconds: 120)]
    public async Task<string> Run(string id, CancellationToken cancellationToken, bool elevated = false)
    {
        var script = Get(id);
        if (script.Language == "powershell")
        {
            CheckPowerShellPolicy();
        }

        if (elevated && !IsElevated())
        {
            return await RunElevated(script, cancellationToken);
        }

        await Validate(script.Language, script.Path, cancellationToken);
        var bytes = await File.ReadAllBytesAsync(script.Path, cancellationToken);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != script.Hash)
        {
            throw new InvalidOperationException("The approved script was modified.");
        }

        var outputPath = Path.ChangeExtension(script.Path, ".out.txt");
        string output;
        try
        {
            output = script.Language == "powershell"
                ? await WindowsCommand.RunAsync("powershell.exe",
                                                ["-NoProfile", "-NonInteractive", "-Command", "-"],
                                                cancellationToken,
                                                "[Console]::OutputEncoding=[Text.Encoding]::UTF8\n$ErrorActionPreference='Stop'\ntry { & ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(bytes) + "')))) } catch { [Console]::Error.WriteLine($_); exit 1 }\n\n",
                                                Workspace,
                                                isolate: true)
                : await WindowsCommand.RunAsync(Python,
                                                ["-I", "-X", "utf8", "-"],
                                                cancellationToken,
                                                Encoding.UTF8.GetString(bytes),
                                                Workspace,
                                                isolate: true);
        }
        catch (OperationCanceledException)
        {
            await File.WriteAllTextAsync(outputPath, "Script stopped.", CancellationToken.None);
            throw;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(outputPath, error.Message, cancellationToken);
            throw new InvalidOperationException($"Script failed. Output file: {outputPath}\n{error.Message[..Math.Min(error.Message.Length, 250)]}",
                                                error);
        }
        await File.WriteAllTextAsync(outputPath, output, cancellationToken);
        return Success(outputPath, output);
    }

    private static string Success(string outputPath, string output) =>
        $"Exit code 0. Output file: {outputPath}\n{output[..Math.Min(output.Length, 1500)]}"
            + "\nOnly script completion was verified. Child processes may continue running; "
            + "an application window was not verified. Verify the window before reporting an application as open.";

    private async Task<string> RunElevated(Script script, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(HostTimeout);
        token = timeout.Token;
        // Each launch has its own completion files, including launches still waiting for UAC.
        var exchange = Path.Combine(Workspace, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exchange);
        var output = Path.Combine(exchange, "output.txt");
        var cancel = Path.Combine(exchange, "cancel");
        var outputPath = Path.ChangeExtension(script.Path, ".out.txt");
        void SignalCancel()
        {
            using var file = File.OpenHandle(cancel, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        }
        using var registration = token.Register(SignalCancel);
        try
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(HostPath()) { UseShellExecute = true, Verb = "runas" };
            string[] arguments = ["--workspace", Workspace, "--script", script.Path,
                "--language", script.Language, "--hash", script.Hash, "--output", output, "--cancel", cancel];
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
            if (script.Language == "python")
            {
                start.ArgumentList.Add("--python");
                start.ArgumentList.Add(Python);
            }
            if (Hash(script.Path) != script.Hash)
            {
                throw new InvalidOperationException("The approved script was modified.");
            }
            token.ThrowIfCancellationRequested();
            var launch = Task.Run(() => LaunchHost(start), CancellationToken.None);
            // Observe a late launch failure even when cancellation stops the wait for UAC.
            _ = launch.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                                   TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await launch.WaitAsync(token);
            while (!File.Exists(output + ".status"))
            {
                await Task.Delay(100, token);
            }
            token.ThrowIfCancellationRequested();
            var status = await File.ReadAllTextAsync(output + ".status", token);
            var text = await File.ReadAllTextAsync(output, token);
            await File.WriteAllTextAsync(outputPath, text, token);
            token.ThrowIfCancellationRequested();
            return status switch
            {
                "0" => Success(outputPath, text),
                "1" => throw new InvalidOperationException($"Script failed. Output file: {outputPath}\n{text[..Math.Min(text.Length, 250)]}"),
                "2" => throw new OperationCanceledException("Script stopped.", token),
                "3" => throw new InvalidOperationException($"The elevated host rejected the script and did not run it. Output file: {outputPath}\n{text[..Math.Min(text.Length, 250)]}"),
                _ => throw new InvalidOperationException("The elevated host returned an invalid status.")
            };
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The user denied the administrator prompt. The script did not run.", error);
        }
        catch (OperationCanceledException)
        {
            SignalCancel();
            await File.WriteAllTextAsync(outputPath, "Script stopped.", CancellationToken.None);
            throw;
        }
    }

    [Tool("python_ensure",
          "scripts",
          "Find Python and create WindowsAxon's isolated virtual environment. If missing, installs Python.Python.3.14 via winget for the current user after approval. Accepts package/source licenses. Does not request elevation.",
          RiskLevel.Danger,
          policyDriven: true,
          timeoutSeconds: 300)]
    public async Task<string> EnsurePython(CancellationToken cancellationToken)
    {
        if (File.Exists(Python))
        {
            return await WindowsCommand.RunAsync(Python, ["-I", "--version"], cancellationToken);
        }

        string? executable = null;
        try
        {
            executable = await WindowsCommand.RunAsync("py.exe",
                                                       ["-3", "-c", "import sys; print(sys.executable)"],
                                                       cancellationToken);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            // Python may be missing; the approved installation below is the fallback.
        }
        if (executable is null)
        {
            await WindowsCommand.RunAsync("winget.exe",
                                          [
"install",
                "--id",
                "Python.Python.3.14",
                "--exact",
                "--source",
                "winget",
                "--scope",
                "user",
                "--silent",
                "--accept-package-agreements",
                "--accept-source-agreements",
                "--disable-interactivity"
],
                                          cancellationToken);
            executable = await WindowsCommand.RunAsync("py.exe",
                                                       ["-3.14", "-c", "import sys; print(sys.executable)"],
                                                       cancellationToken);
        }
        if (!File.Exists(executable) || executable.Contains("\\Microsoft\\WindowsApps\\",
                                                            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A usable Python executable was not found.");
        }

        await WindowsCommand.RunAsync(executable,
                                      ["-m", "venv", Path.GetDirectoryName(Path.GetDirectoryName(Python))!],
                                      cancellationToken);
        return await WindowsCommand.RunAsync(Python, ["-I", "--version"], cancellationToken);
    }

    [Tool("python_install_package",
          "scripts",
          "Install one named Python package in WindowsAxon's virtual environment after approval. No arbitrary pip options, URLs, paths, or alternative indexes.",
          RiskLevel.Danger,
          policyDriven: true,
          timeoutSeconds: 300)]
    public Task<string> InstallPackage(string name, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,100}(==[A-Za-z0-9][A-Za-z0-9._-]{0,50})?$"))
        {
            throw new ArgumentException("Use a package name, optionally with ==version.");
        }

        if (!File.Exists(Python))
        {
            throw new InvalidOperationException("Use python_ensure first.");
        }

        return WindowsCommand.RunAsync(Python,
                                       ["-I", "-m", "pip", "install", "--disable-pip-version-check", name],
                                       cancellationToken);
    }

    public bool AppliesTo(string toolName) => toolName is "script_run" or "python_ensure" or "python_install_package";

    public ApprovalDecision Decide(FunctionCallContent call)
    {
        if (call.Name == "script_run" && call.Arguments is { } args && args.TryGetValue("id", out var id)
            && id?.ToString() is { } scriptId
            && _scripts.ContainsKey(scriptId))
        {
            var script = Get(scriptId);
            return new(ApprovalOutcome.Ask,
                       false,
                       $"Run {script.Language}: {script.Summary}. "
                       + (args.TryGetValue("elevated", out var elevated) && bool.TryParse(elevated?.ToString(), out var admin) && admin
                           ? "Run with administrator rights. Windows will ask you to approve administrator rights. " : "")
                       + $"No undo. Review the complete code:\n{File.ReadAllText(script.Path)}");
        }
        return new(ApprovalOutcome.Ask,
                   false,
                   call.Name == "python_ensure" ? "May install Python.Python.3.14, publisher Python Software Foundation, source winget, scope current user. This accepts package and source agreements." : "Install a package in WindowsAxon's Python environment. The package and its dependencies may execute installation code. No undo.");
    }
}
