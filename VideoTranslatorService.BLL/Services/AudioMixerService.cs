using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public sealed class AudioMixerService : IAudioMixerService
{
    public Task MixAsync(VideoJob job, CancellationToken ct = default) =>
        throw new StepNotImplementedException(nameof(IAudioMixerService));
}
