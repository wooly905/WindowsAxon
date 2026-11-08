using System.Reflection;
using Microsoft.Extensions.AI;

namespace WindowsAxon.Agent.Tools;

public static class ToolRegistration
{
    private const BindingFlags Methods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Add(ToolCatalog catalog, object provider)
    {
        // MetadataToken keeps the declaration order, so the tool list in the prompt stays stable.
        foreach (var method in provider.GetType().GetMethods(Methods).OrderBy(m => m.MetadataToken))
        {
            if (method.GetCustomAttribute<ToolAttribute>() is not { } tool)
            {
                continue;
            }

            Action? cancel = provider is ICancelableToolProvider cancelable ? cancelable.CancelPending : null;
            catalog.Add(new ProviderFunction(AIFunctionFactory.Create(method, method.IsStatic ? null : provider, tool.Name, tool.Description),
                                             cancel),
                        tool.Group,
                        tool.Risk,
                        tool.PolicyDriven,
                        TimeSpan.FromSeconds(tool.TimeoutSeconds),
                        tool.Artifact,
                        tool.AlwaysInclude);
        }
    }

    private sealed class ProviderFunction(AIFunction inner, Action? cancel) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var canceled = 0;
            void CancelOnce()
            {
                if (Interlocked.Exchange(ref canceled, 1) == 0)
                {
                    cancel?.Invoke();
                }
            }

            try
            {
                using CancellationTokenRegistration stop = cancel is null ? default : cancellationToken.Register(CancelOnce);

                return await Task.Run(async () => await base.InvokeCoreAsync(arguments, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The call can finish and dispose the registration before its callback runs.
                CancelOnce();
                throw;
            }
        }
    }
}
