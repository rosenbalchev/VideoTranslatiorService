using RB.VideoTranslator.Data.Entities;

namespace RB.VideoTranslator.BLL.Services;

public interface IPipelineOrchestrator
{
    /// <summary>
    /// Advances <paramref name="job"/> through as many pipeline steps as currently
    /// implemented, stopping when the job reaches a terminal state or a step is
    /// not yet available.
    /// </summary>
    Task AdvanceAsync(VideoJob job, CancellationToken ct = default);
}
