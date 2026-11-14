using WindowsAxon.Agent.Approval;
using WindowsAxon.Agent.Audit;
using WindowsAxon.Agent.Selection;
using WindowsAxon.Agent.Settings;
using WindowsAxon.Agent.Skills;
using WindowsAxon.Agent.Tools;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent;

public sealed record AgentDependencies(ChatSettings Chat,
                                       IChatClient Model,
                                       ToolCatalog Catalog,
                                       SkillCatalog Skills,
                                       IAuditLog Audit,
                                       IReadOnlyList<IApprovalRule> ApprovalRules,
                                       IStepLimitPrompter StepLimitPrompter,
                                       bool DocsAvailable);
