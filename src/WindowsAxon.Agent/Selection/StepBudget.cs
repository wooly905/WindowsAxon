namespace WindowsAxon.Agent.Selection;

/// <summary>The step counter and limit of the current request, shared by the main agent and its sub-agents.</summary>
public sealed class StepBudget
{
    public const int BaseLimit = 50;
    public const int AppControlLimit = 50;
    public const int ExtensionSteps = 10;

    public int StepCount
    {
        get; private set;
    }

    public int StepLimit => (AppControlUsed ? AppControlLimit : BaseLimit) + (ExtensionUsed ? ExtensionSteps : 0);

    public bool ExtensionUsed
    {
        get; private set;
    }

    /// <summary>The user did not extend the limit: the model may only give a final answer.</summary>
    public bool MustFinish
    {
        get; set;
    }

    /// <summary>A browser or desktop_ui tool or group was used in this request.</summary>
    public bool AppControlUsed
    {
        get; set;
    }

    public void CountStep() => StepCount++;

    public void Extend()
    {
        if (ExtensionUsed)
        {
            throw new InvalidOperationException("The step limit was already extended for this request.");
        }

        ExtensionUsed = true;
    }

    public void Reset()
    {
        StepCount = 0;
        ExtensionUsed = false;
        MustFinish = false;
        AppControlUsed = false;
    }
}
