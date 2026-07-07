namespace RB.VideoTranslator.Domain.Enums;

public enum JobState
{
    // ── Intake ──────────────────────────────────────────────────────────────
    Queued,

    // ── Step 1 : media separation ───────────────────────────────────────────
    SeparatingMedia,
    AudioExtracted,

    // ── Step 2 : subtitle extraction ────────────────────────────────────────
    ExtractingVtt,
    VttExtracted,

    // ── Step 3 : VTT translation (GPT-4o-mini) ───────────────────────────────
    TranslatingVtt,
    VttTranslated,

    // ── Step 4 : Azure TTS synthesis (translated VTT → WAV) ─────────────────
    SynthesisingAzureTts,
    AzureTtsSynthesised,

    // ── Step 5 : voice removal (Demucs) ─────────────────────────────────────
    RemovingVoice,
    VoiceRemoved,

    // ── Step 5b : per-speaker sample extraction (from isolated vocals track) ─
    ExtractingSpeakerSamples,
    SpeakerSamplesExtracted,

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
