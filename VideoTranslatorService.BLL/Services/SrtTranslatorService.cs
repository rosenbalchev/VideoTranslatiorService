using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public sealed class SrtTranslatorService : ISrtTranslatorService
{
    public Task TranslateAsync(VideoJob job, CancellationToken ct = default) =>
        throw new StepNotImplementedException(nameof(ISrtTranslatorService));
}
