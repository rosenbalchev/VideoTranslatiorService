using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public interface IVoiceRemoverService
{
    /// <summary>
    /// Produces a new audio stream with the voice track removed (music bed only).
    /// Populates <see cref="VideoJob.VoiceRemovedAudioPath"/> and transitions state to
    /// <see cref="JobState.VoiceRemoved"/>.
    /// </summary>
    Task RemoveAsync(VideoJob job, CancellationToken ct = default);
}
