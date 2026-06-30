using RB.VideoTranslator.Data.Entities;

namespace RB.VideoTranslator.BLL.Services;

public interface IAudioMixerService
{
    /// <summary>
    /// Mixes the voice-removed audio bed with the synthesised voice track.
    /// Populates <see cref="VideoJob.MixedAudioPath"/> and transitions state to
    /// <see cref="JobState.MixedNoVoiceWithSyntheticVoice"/>.
    /// </summary>
    Task MixAsync(VideoJob job, string ffmpegPath = "ffmpeg", CancellationToken ct = default);
}
