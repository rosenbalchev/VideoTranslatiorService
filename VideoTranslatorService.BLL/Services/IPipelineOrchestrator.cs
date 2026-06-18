using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public interface IPipelineOrchestrator
{
    /// <summary>
    /// Advances <paramref name="job"/> through as many pipeline steps as currently
    /// implemented, stopping when the job reaches a terminal state or a step is
    /// not yet available.
    /// </summary>
    Task AdvanceAsync(VideoJob job, PipelineOptions options, CancellationToken ct = default);
}
