using System.Text.Json;
using NSubstitute;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.App;
using WindowsAxon.Mesh;

namespace WindowsAxon.Agent.Tests.Mesh;

public sealed class RemoteConversationSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Final(string answer, string action = "final_answer") =>
        JsonSerializer.Serialize(new { thought = "t", action, answer });

    private static DesktopChatService CreateService(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"WindowsAxon-RemoteSession-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var approvals = Substitute.For<IApprovalPrompter>();
        var stepLimits = Substitute.For<IStepLimitPrompter>();
        return new DesktopChatService(approvals,
                                      stepLimits,
                                      settingsPath: Path.Combine(tempDir, "settings.json"),
                                      chatHistoryFolder: Path.Combine(tempDir, "history"));
    }

    [Fact]
    public async Task SameConversationIdPreservesContextAcrossRemoteTurns()
    {
        using var service = CreateService(out var tempDir);
        try
        {
            var model = new ScriptedChatClient()
                .Reply(Final("I could not find duck.pdf. Where is it?", "ask_user"))
                .Reply(Final("Removed D:\\temp\\duck.pdf."));
            var factoryCalls = 0;
            service.RemoteModelFactory = _ =>
            {
                factoryCalls++;
                return Task.FromResult<IChatClient>(model);
            };

            var turn1 = await service.HandleIncomingRemoteTaskAsync(
                new IncomingRemoteTask("task-1",
                                       "Computer A",
                                       "remove duck.pdf",
                                       "conv-1",
                                       ForceNewRequest: false,
                                       _ => { },
                                       (_, _) => Task.FromResult(RemoteApprovalAnswer.Deny)),
                Ct);

            var turn2 = await service.HandleIncomingRemoteTaskAsync(
                new IncomingRemoteTask("task-2",
                                       "Computer A",
                                       "it is d:\\temp folder",
                                       "conv-1",
                                       ForceNewRequest: false,
                                       _ => { },
                                       (_, _) => Task.FromResult(RemoteApprovalAnswer.Deny)),
                Ct);

            Assert.Equal(RemoteRunStatus.AskedUser, turn1.Status);
            Assert.Equal(RemoteRunStatus.Answered, turn2.Status);
            Assert.Equal("Removed D:\\temp\\duck.pdf.", turn2.Text);
            Assert.Equal(1, factoryCalls);
            Assert.Equal(1, service.RemoteSessionCount);

            // Second turn's LLM call must include the first turn's user prompt, assistant reply, and the follow-up.
            Assert.Equal(2, model.Calls.Count);
            var secondTurnText = string.Join("\n", model.Calls[1].Messages.Select(m => m.Text));
            Assert.Contains("remove duck.pdf", secondTurnText);
            Assert.Contains("could not find duck.pdf", secondTurnText);
            Assert.Contains("it is d:\\temp folder", secondTurnText);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DifferentConversationIdStartsFreshRemoteSession()
    {
        using var service = CreateService(out var tempDir);
        try
        {
            var model1 = new ScriptedChatClient().Reply(Final("I could not find duck.pdf."));
            var model2 = new ScriptedChatClient().Reply(Final("What do you want me to do?"));
            var models = new Queue<IChatClient>([model1, model2]);
            service.RemoteModelFactory = _ => Task.FromResult(models.Dequeue());

            await service.HandleIncomingRemoteTaskAsync(
                new IncomingRemoteTask("task-1",
                                       "Computer A",
                                       "remove duck.pdf",
                                       "conv-1",
                                       ForceNewRequest: false,
                                       _ => { },
                                       (_, _) => Task.FromResult(RemoteApprovalAnswer.Deny)),
                Ct);

            await service.HandleIncomingRemoteTaskAsync(
                new IncomingRemoteTask("task-2",
                                       "Computer A",
                                       "it is d:\\temp folder",
                                       "conv-2",
                                       ForceNewRequest: false,
                                       _ => { },
                                       (_, _) => Task.FromResult(RemoteApprovalAnswer.Deny)),
                Ct);

            Assert.Equal(2, service.RemoteSessionCount);
            var conv2Text = string.Join("\n", model2.Calls[0].Messages.Select(m => m.Text));
            Assert.DoesNotContain("duck.pdf", conv2Text);
            Assert.Contains("it is d:\\temp folder", conv2Text);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task PruneExpiredRemoteSessionsDisposesIdleEntries()
    {
        using var service = CreateService(out var tempDir);
        try
        {
            var model = new ScriptedChatClient().Reply(Final("ok"));
            service.RemoteModelFactory = _ => Task.FromResult<IChatClient>(model);

            await service.HandleIncomingRemoteTaskAsync(
                new IncomingRemoteTask("task-1",
                                       "Computer A",
                                       "hello",
                                       "conv-expire",
                                       ForceNewRequest: false,
                                       _ => { },
                                       (_, _) => Task.FromResult(RemoteApprovalAnswer.Deny)),
                Ct);

            Assert.Equal(1, service.RemoteSessionCount);
            service.PruneExpiredRemoteSessions(TimeSpan.Zero);
            Assert.Equal(0, service.RemoteSessionCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
