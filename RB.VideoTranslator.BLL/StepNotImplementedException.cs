namespace RB.VideoTranslator.BLL;

/// <summary>
/// Thrown by a pipeline step service that is defined but not yet implemented.
/// The orchestrator catches this to pause the job without marking it as failed.
/// </summary>
public sealed class StepNotImplementedException : Exception
{
    public StepNotImplementedException(string stepName)
        : base($"Pipeline step '{stepName}' is not yet implemented.") { }
}
