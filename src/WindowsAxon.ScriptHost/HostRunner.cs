using System.Diagnostics;

namespace WindowsAxon.ScriptHost;

internal static class HostRunner
{
    internal static async Task<int> RunAsync(HostOptions options, CancellationToken token = default,
        Func<ProcessStartInfo, string, CancellationToken, Task<string>>? interpret = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var status = 3;
        var output = "";
        try
        {
            var (start, input) = options.Validate();
            if (File.Exists(options.Cancel))
            {
                timeout.Cancel();
            }
            timeout.Token.ThrowIfCancellationRequested();
            using var polling = new CancellationTokenSource();
            var monitor = PollCancel(options.Cancel, timeout, polling.Token);
            try
            {
                output = await (interpret ?? HostInterpreter.RunAsync)(start, input, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                status = File.Exists(options.Cancel) ? 2 : 0;
            }
            catch (OperationCanceledException)
            {
                status = 2;
                output = "Script stopped.";
            }
            catch (Exception error)
            {
                status = 1;
                output = error.Message;
            }
            finally
            {
                await polling.CancelAsync();
                await monitor;
            }
        }
        catch (OperationCanceledException)
        {
            status = 2;
            output = "Script stopped.";
        }
        catch (Exception error)
        {
            output = error.Message;
        }
        if (timeout.IsCancellationRequested || File.Exists(options.Cancel))
        {
            status = 2;
        }
        await File.WriteAllTextAsync(options.Output, output, CancellationToken.None);
        var temporary = options.Output + ".status." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(temporary, options.Output + ".status", overwrite: true);
        return status;
    }

    private static async Task PollCancel(string path, CancellationTokenSource execution, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (File.Exists(path))
                {
                    await execution.CancelAsync();
                    return;
                }
                await Task.Delay(100, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }
}
