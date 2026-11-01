namespace WindowsAxon.Agent.Tools;

/// <summary>A tool provider that stops its pending work when a tool call is canceled.</summary>
public interface ICancelableToolProvider
{
    void CancelPending();
}
