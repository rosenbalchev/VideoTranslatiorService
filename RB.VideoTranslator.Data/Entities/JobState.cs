namespace RB.VideoTranslator.Data.Entities;

public enum JobState
{
    // ── Intake ──────────────────────────────────────────────────────────────
    Queued,

    // ── Step 1 : media separation ───────────────────────────────────────────
    SeparatingMedia,
    AudioExtracted,

    // ── Step 2 : subtitle extraction ────────────────────────────────────────
    ExtractingSrt,
    SrtExtracted,

    // ── Step 3 : SRT translation (GPT-4o-mini) ───────────────────────────────
    TranslatingSrt,
    SrtTranslated,

    // ── Step 4 : Azure TTS synthesis (translated SRT → WAV) ─────────────────
    SynthesisingAzureTts,
    AzureTtsSynthesised,

    // ── Step 5 : voice removal (Demucs) ─────────────────────────────────────
    RemovingVoice,
    VoiceRemoved,

    // ── Step 6 (legacy, unused) ──────────────────────────────────────────────
    SynthesisingVoice,
    VoiceSynthesised,

    // ── Step 6 : audio mix (music-bed + synthetic voice) ─────────────────────
    MixingAudio,
    MixedNoVoiceWithSyntheticVoice,

    // ── Step 7 : final mux into video (with embedded subtitles) ─────────────
    AddingToVideo,
    AddedToOriginalVideo,

    // ── Terminal ─────────────────────────────────────────────────────────────
    Completed,
    Failed
}
