using System.Diagnostics;

namespace WindowsAxon.ScriptHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        var started = Stopwatch.StartNew();
        HostRequest request;
        try
        {
            request = HostRequest.Parse(args);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            // No output path is trusted yet, so no status file is written; the parent sees the host exit.
            return ScriptRunner.Rejected;
        }

        byte[] code;
        string interpreter;
        try
        {
            (code, interpreter) = request.Verify(Environment.SystemDirectory);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ScriptRunner.Publish(request.Output, ScriptRunner.Rejected, error.Message);

            return ScriptRunner.Rejected;
        }

        int status;
        string output;
        try
        {
            (status, output) = ScriptRunner.Run(request,
                                                code,
                                                interpreter,
                                                info => Process.Start(info)
                                                    ?? throw new InvalidOperationException($"Could not start {interpreter}."),
                                                ScriptRunner.Timeout - started.Elapsed);
        }
        catch (Exception error)
        {
            // Report every failure through the status file; otherwise the parent waits until its timeout.
            (status, output) = (ScriptRunner.Failed, error.Message);
        }

        ScriptRunner.Publish(request.Output, status, output);

        return status;
    }
}
