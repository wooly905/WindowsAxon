using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Skills;
using WindowsAxon.Agent.SubAgents;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent;

/// <param name="ExtraAgents">Tool groups added at run time, for example MCP servers: a sub-agent in SubAgents mode,
/// and the group description for enable_tools in Groups mode.</param>
public sealed record AgentDependencies(ChatSettings Chat,
                                       IChatClient Model,
                                       ToolCatalog Catalog,
                                       SkillCatalog Skills,
                                       IAuditLog Audit,
                                       IReadOnlyList<IApprovalRule> ApprovalRules,
                                       IStepLimitPrompter StepLimitPrompter,
                                       IReadOnlyList<SubAgentDefinition>? ExtraAgents = null);
