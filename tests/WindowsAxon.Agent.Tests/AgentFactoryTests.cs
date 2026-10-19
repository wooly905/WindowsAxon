using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Skills;
using WindowsAxon.Agent.Steps;
using WindowsAxon.Agent.Tests.Fakes;
using WindowsAxon.Agent.Tools;
using NSubstitute;

namespace WindowsAxon.Agent.Tests;

public class AgentFactoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static AgentDependencies Dependencies(IChatClient model,
                                                   ChatSettings? chat = null,
                                                   ToolCatalog? catalog = null)
    {
        catalog ??= new ToolCatalog();
        if (catalog.Find("net_ping") is null)
        {
            catalog.Add(AIFunctionFactory.Create((string host) => $"reply from {host}", "net_ping", "Ping a host."),
                        "network",
                        RiskLevel.Read);
            catalog.Add(AIFunctionFactory.Create((string path) => $"wallpaper is {path}",
                                                 "wallpaper_set",
                                                 "Set the wallpaper."),
                        "personalize",
                        RiskLevel.Change);
        }

        var skillRoot = Directory.CreateTempSubdirectory("windowsaxon-skills-").FullName;
        return new AgentDependencies(chat ?? new ChatSettings { AutoApproveTools = false },
                                     model,
                                     catalog,
                                     SkillCatalog.Load([(skillRoot, true)]),
                                     [skillRoot],
                                     new ListAuditLog(),
                                     [],
                                     Substitute.For<IStepLimitPrompter>(),
                                     DocsAvailable: false);
    }

    [Fact]
    public async Task Skill_provider_tools_log_full_results_and_model_call_ids()
    {
        var model = new ScriptedChatClient()
            .ReplyCall("load_skill", new Dictionary<string, object?> { ["skillName"] = "audit-test" }, "skill-call")
            .ReplyWith((_, options) =>
            {
                var tool = Assert.Single(options!.Tools!, t => t.Name == "read_skill_resource");
                var args = ((AIFunction)tool).JsonSchema.GetProperty("properties").EnumerateObject()
                    .ToDictionary(p => p.Name, p => (object?)(p.Name == "skillName" ? "audit-test" : "notes.txt"));
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                                                        [new FunctionCallContent("resource-call",
                                                                                 "read_skill_resource",
                                                                                 args)]));
            })
            .Reply("Done.");
        var deps = Dependencies(model,
                                new ChatSettings { StepMode = StepMode.Native, ToolSelection = ToolSelectionMode.All });
        var folder = Directory.CreateDirectory(Path.Combine(deps.SkillRoots[0], "audit-test")).FullName;
        var content = new string('x', 4000) + " end of resource";
        File.WriteAllText(Path.Combine(folder, "SKILL.md"),
                          "---\nname: audit-test\ndescription: Test auditing.\n---\nAudit skill instructions.\n");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), content);
        var session = await new AgentFactory(deps).CreateSessionAsync(Ct);
        var audit = (ListAuditLog)deps.Audit;
        var runner = new AgentRunner(session, Substitute.For<IApprovalPrompter>(), audit);

        var result = await runner.RunAsync("read skill and resource", cancellationToken: Ct);

        Assert.Equal(RunStatus.Answered, result.Status);
        var skill = Assert.Single(audit.Entries, e => e.Kind == "call" && e.Tool == "load_skill");
        Assert.Equal("skill-call", skill.CallId);
        Assert.Equal("read skill and resource", skill.Request);
        Assert.Contains("Audit skill instructions.", skill.Result);
        var resource = Assert.Single(audit.Entries, e => e.Kind == "call" && e.Tool == "read_skill_resource");
        Assert.Equal("resource-call", resource.CallId);
        Assert.Equal("succeeded", resource.Status);
        Assert.Contains(content, resource.Result);
        Assert.Equal(2, audit.Entries.Count(e => e.Kind == "call_start"));
    }

    [Fact]
    public async Task Structured_session_sends_a_step_schema_with_base_tools_only()
    {
        var model = new ScriptedChatClient().Reply("""{"thought":"t","action":"final_answer","answer":"Hi."}""");
        var session = await new AgentFactory(Dependencies(model)).CreateSessionAsync(Ct);

        var response = await session.Agent.RunAsync(TranscriptWriter.NewRequest("hello"),
                                                    session.Session,
                                                    cancellationToken: Ct);

        Assert.Equal("Hi.", response.Text);
        var (messages, options) = model.Calls[0];
        Assert.IsType<ChatResponseFormatJson>(options!.ResponseFormat);
        Assert.Contains("- enable_tools(group: string)", messages[0].Text);
        Assert.DoesNotContain("net_ping", messages[0].Text.Split("Actions:")[1]);
        Assert.Contains("You are WindowsAxon", messages[0].Text);
    }

    [Fact]
    public async Task Native_mode_sends_native_tools()
    {
        var model = new ScriptedChatClient().Reply("Hi.");
        var deps = Dependencies(model, new ChatSettings { StepMode = StepMode.Native });
        var session = await new AgentFactory(deps).CreateSessionAsync(Ct);

        await session.Agent.RunAsync("hello", session.Session, cancellationToken: Ct);

        Assert.Contains("enable_tools", model.Calls[0].Options!.Tools!.Select(t => t.Name));
        Assert.Null(model.Calls[0].Options!.ResponseFormat);
    }

    [Theory]
    [InlineData("auto", StepMode.Structured, null)]
    [InlineData("auto", StepMode.Native, null)]
    [InlineData("windowsaxon-qwen3.5-9b", StepMode.Structured, 0.1f)]
    [InlineData("windowsaxon-qwen3.5-9b", StepMode.Native, 0.1f)]
    public async Task Auto_model_uses_the_router_sampling_default(string name, StepMode mode, float? temperature)
    {
        var model = new ScriptedChatClient().Reply(mode == StepMode.Structured
            ? """{"thought":"t","action":"final_answer","answer":"OK"}""" : "OK");
        var session = await new AgentFactory(Dependencies(model,
                                                          new ChatSettings
                                                          {
                                                              Model = name,
                                                              StepMode = mode
                                                          })).CreateSessionAsync(Ct);

        await session.Agent.RunAsync("hello", session.Session, cancellationToken: Ct);

        Assert.Equal(temperature, model.Calls[0].Options!.Temperature);
    }

    [Fact]
    public async Task All_mode_has_no_enable_tools_and_sends_every_tool()
    {
        var model = new ScriptedChatClient().Reply("Hi.");
        var deps = Dependencies(model,
                                new ChatSettings { StepMode = StepMode.Native, ToolSelection = ToolSelectionMode.All });
        var session = await new AgentFactory(deps).CreateSessionAsync(Ct);

        await session.Agent.RunAsync("hello", session.Session, cancellationToken: Ct);

        var names = model.Calls[0].Options!.Tools!.Select(t => t.Name).ToList();
        Assert.DoesNotContain("enable_tools", names);
        Assert.Contains("net_ping", names);
        Assert.Contains("wallpaper_set", names);
    }

    [Fact]
    public async Task All_mode_runs_a_non_base_tool_without_enabling_a_group()
    {
        var model = new ScriptedChatClient()
            .ReplyCall("net_ping", new Dictionary<string, object?> { ["host"] = "1.1.1.1" })
            .Reply("Up.");
        var deps = Dependencies(model,
                                new ChatSettings { StepMode = StepMode.Native, ToolSelection = ToolSelectionMode.All });
        var session = await new AgentFactory(deps).CreateSessionAsync(Ct);

        await session.Agent.RunAsync("ping", session.Session, cancellationToken: Ct);

        var result = model.Calls[1].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.Equal("reply from 1.1.1.1", result.Result?.ToString());
    }

    [Fact]
    public async Task Change_tools_are_approval_wrapped_and_each_session_has_its_own_state()
    {
        var factory = new AgentFactory(Dependencies(new ScriptedChatClient()));

        var a = await factory.CreateSessionAsync(Ct);
        var b = await factory.CreateSessionAsync(Ct);

        Assert.IsType<ApprovalRequiredAIFunction>(a.Tools.Single(t => t.Name == "wallpaper_set"));
        Assert.NotSame(a.Selection, b.Selection);
        Assert.NotSame(a.Policy, b.Policy);
    }

    [Fact]
    public void System_prompt_has_the_docs_rule_only_when_docs_are_available()
    {
        Assert.Contains("docs_search", Prompts.System(docsAvailable: true));
        Assert.DoesNotContain("docs_search", Prompts.System(docsAvailable: false));
        Assert.Contains("{skills}", Prompts.SkillsInstruction);
    }

    [Fact]
    public void Native_prompt_requests_tool_calls_and_plain_text_answers()
    {
        var prompt = Prompts.System(docsAvailable: false, stepMode: StepMode.Native);

        Assert.Contains("Use the provided tools", prompt);
        Assert.Contains("reply directly to the user", prompt);
        Assert.DoesNotContain("JSON step", prompt);
        Assert.DoesNotContain("final_answer", prompt);
        Assert.DoesNotContain("ask_user", prompt);
    }

    [Fact]
    public void System_and_orchestrator_prompts_require_powershell_before_ui_automation()
    {
        var system = Prompts.System(docsAvailable: false);
        var orchestrator = Prompts.Orchestrator(docsAvailable: false, "- scripts: test");

        Assert.Contains("script_create with language 'powershell', then script_run", system);
        Assert.Contains("Do not answer with manual instructions", system);
        Assert.Contains("first inspect it with a read tool", system);
        Assert.Contains("If PowerShell is blocked by policy or lacks permission", system);
        Assert.Contains("Do not give the user manual instructions", orchestrator);
        Assert.Contains("delegate to the scripts agent to write and run a PowerShell script", orchestrator);
    }
}
