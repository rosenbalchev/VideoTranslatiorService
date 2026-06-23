using VideoTranslatorService.Data.Entities;

namespace VideoTranslatorService.BLL.Services;

public interface IAudioMixerService
{
    /// <summary>
    /// Mixes the voice-removed audio bed with the synthesised voice track.
    /// Populates <see cref="VideoJob.MixedAudioPath"/> and transitions state to
    /// <see cref="JobState.MixedNoVoiceWithSyntheticVoice"/>.
    /// </summary>
    Task MixAsync(VideoJob job, string ffmpegPath = "ffmpeg", CancellationToken ct = default);
}
