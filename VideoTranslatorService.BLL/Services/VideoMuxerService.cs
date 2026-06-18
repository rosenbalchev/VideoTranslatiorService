using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public sealed class VideoMuxerService : IVideoMuxerService
{
    public Task MuxAsync(VideoJob job, string ffmpegPath, CancellationToken ct = default) =>
        throw new StepNotImplementedException(nameof(IVideoMuxerService));
}
