using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WindowsAxon.ScriptHost;

internal static class ScriptRunner
{
    public const int Success = 0;
    public const int Failed = 1;
    public const int Stopped = 2;
    public const int Rejected = 3;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private const int MaxChars = 1024 * 1024;

    public static (int Status, string Output) Run(HostRequest request,
                                                  byte[] code,
                                                  string interpreter,
                                                  Func<ProcessStartInfo, Process> start,
                                                  TimeSpan timeout)
    {
        if (File.Exists(request.Cancel))
        {
            return (Stopped, "Script stopped before it started.");
        }

        var info = new ProcessStartInfo(interpreter)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.Workspace
        };
        string input;
        if (request.Language == "powershell")
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "-" })
            {
                info.ArgumentList.Add(argument);
            }

            input = "[Console]::OutputEncoding=[Text.Encoding]::UTF8\n$ErrorActionPreference='Stop'\ntry { & ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
                + Convert.ToBase64String(code)
                + "')))) } catch { [Console]::Error.WriteLine($_); exit 1 }\n\n";
        }
        else
        {
            foreach (var argument in new[] { "-I", "-X", "utf8", "-" })
            {
                info.ArgumentList.Add(argument);
            }

            input = Encoding.UTF8.GetString(code);
        }

        using var process = start(info);
        using var job = ProcessJob.Attach(process);
        var output = Capture(process.StandardOutput);
        var error = Capture(process.StandardError);
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var timer = Stopwatch.StartNew();
        while (!process.WaitForExit(250))
        {
            if (File.Exists(request.Cancel) || timer.Elapsed >= timeout)
            {
                job.Terminate();
                process.WaitForExit();

                return (Stopped, "Script stopped.");
            }
        }

        process.WaitForExit();
        var text = output.GetAwaiter().GetResult();
        var errors = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            job.Terminate();

            return (Failed, $"{interpreter} exited with {process.ExitCode}: {errors} {text}");
        }

        // A successful script may have launched an application that should outlive the script.
        job.AllowChildProcessesToContinue();

        return (Success, (text + (errors.Length > 0 ? "\n" + errors : "")).Trim());
    }

    // The status file appears only after the output file is complete; the parent treats its arrival as completion.
    public static void Publish(string outputPath, int status, string output)
    {
        File.WriteAllText(outputPath, output, new UTF8Encoding(false));
        var temporary = $"{outputPath}.status.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, status.ToString(CultureInfo.InvariantCulture));
        File.Move(temporary, outputPath + ".status", overwrite: true);
    }

    private static async Task<string> Capture(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        var cut = false;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            var remaining = MaxChars - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(count, remaining));
            }

            if (count > remaining)
            {
                cut = true;
            }
        }

        return result + (cut ? "\n(output truncated at 1 MB)" : "");
    }
}
