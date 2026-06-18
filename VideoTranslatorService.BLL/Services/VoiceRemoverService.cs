using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public sealed class VoiceRemoverService : IVoiceRemoverService
{
    public Task RemoveAsync(VideoJob job, CancellationToken ct = default) =>
        throw new StepNotImplementedException(nameof(IVoiceRemoverService));
}
