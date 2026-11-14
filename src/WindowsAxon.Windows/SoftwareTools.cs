using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Windows;

public sealed class SoftwareTools : IApprovalRule
{
    [Tool("winget_search",
          "apps",
          "Search the winget source for software. Does not install packages.",
          timeoutSeconds: 60)]
    public Task<string> Search(string query, CancellationToken cancellationToken) => WindowsCommand.RunAsync("winget.exe",
                                                                                                             [
"search",
        query,
        "--source",
        "winget",
        "--accept-source-agreements",
        "--disable-interactivity"
],
                                                                                                             cancellationToken);

    [Tool("winget_install",
          "apps",
          "Install an exact winget package ID for the current user. Always requires approval. Accepts package and source agreements. Does not request administrator rights or override hash checks. No undo.",
          RiskLevel.Danger,
          policyDriven: true,
          timeoutSeconds: 300)]
    public Task<string> Install(string id, CancellationToken cancellationToken) => WindowsCommand.RunAsync("winget.exe",
                                                                                                           [
"install",
        "--id",
        id,
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

    public bool AppliesTo(string toolName) => toolName == "winget_install";

    public ApprovalDecision Decide(FunctionCallContent call)
    {
        var id = call.Arguments is { } arguments
            && arguments.TryGetValue("id", out var value) ? value?.ToString() ?? "" : "";
        // The publisher and name help the user spot a look-alike package ID. ApprovalLoop runs Decide off the UI thread.
        string details;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            details = WindowsCommand.RunAsync("winget.exe",
                                              [
"show",
                "--id",
                id,
                "--exact",
                "--source",
                "winget",
                "--accept-source-agreements",
                "--disable-interactivity"
],
                                              timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            details = "Package metadata unavailable: " + error.Message;
        }
        return new ApprovalDecision(ApprovalOutcome.Ask,
                                    false,
                                    $"Install {id}, source winget, current user. This accepts package and source agreements. No undo.\n{details}");
    }
}
