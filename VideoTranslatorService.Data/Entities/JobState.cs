namespace VideoTranslatorService.Data.Entities;

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

    // ── Step 3 : voice removal ──────────────────────────────────────────────
    RemovingVoice,
    VoiceRemoved,

    // ── Step 4 : SRT translation ─────────────────────────────────────────────
    TranslatingSrt,
    SrtTranslated,

    // ── Step 5 : voice synthesis ─────────────────────────────────────────────
    SynthesisingVoice,
    VoiceSynthesised,

    // ── Step 6 : audio mix (music-bed + synthetic voice) ─────────────────────
    MixingAudio,
    MixedNoVoiceWithSyntheticVoice,

    // ── Step 7 : final mux into video ────────────────────────────────────────
    AddingToVideo,
    AddedToOriginalVideo,

    // ── Terminal ─────────────────────────────────────────────────────────────
    Completed,
    Failed
}
