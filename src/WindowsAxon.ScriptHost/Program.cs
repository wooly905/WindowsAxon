namespace WindowsAxon.ScriptHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "WindowsAxon", "work");
            var options = HostOptions.Parse(args, root);
            return await HostRunner.RunAsync(options, timeout.Token);
        }
        catch
        {
            // Invalid arguments must not cause writes to an unvalidated output path.
            return 3;
        }
    }
}
