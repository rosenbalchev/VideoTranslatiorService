================================================================================
  AI VIDEO TRANSLATION — PROOF OF CONCEPT
  Executive Summary & Viewer Guide
================================================================================

WHAT WAS DEMONSTRATED
--------------------------------------------------------------------------------
This proof of concept automatically translates spoken video content into
multiple languages, producing dubbed video files with synchronized voices and
subtitles — without any human translators or manual editing.

Three source videos (originally in German, English, and Ukrainian) were
processed and delivered in the following target languages:

  • Bulgarian (BG)       • English (EN)
  • French (FR)          • Spanish (ES)
  • Danish (DA)          • Swedish (SV)
  • Finnish (FI)         • Norwegian (NO)
  • Ukrainian (UK)

Each video was delivered in two forms:
  1. A single multi-language file containing all dubbed audio tracks and
     subtitle tracks simultaneously (recommended for review).
  2. A set of individual per-language files, one per target language.


HOW THE PROCESS WORKS
--------------------------------------------------------------------------------
The pipeline runs entirely automatically in six stages:

  Stage 1 — Audio Separation (LOCAL AI MODEL)
  The original voice track is cleanly separated from background music and
  ambient sound using Demucs, a deep-learning audio source separation model
  that runs locally on the processing machine. This ensures the dubbed voice
  can later be mixed back over the original ambience without double-voice
  artifacts.

  Stage 2 — Speech Transcription (LOCAL AI MODEL)
  The isolated voice track is transcribed to text with precise timestamps
  using OpenAI Whisper, a state-of-the-art speech recognition model that also
  runs locally. The result is a subtitle file (SRT format) in the original
  language, serving as the translation source.

  Stage 3 — Translation (AZURE-HOSTED AI — GPT-4o-mini)
  The subtitle file is sent to Microsoft Azure AI Foundry, where the
  GPT-4o-mini large language model translates the text into each target
  language. The translation is performed segment by segment, preserving the
  original timing and line breaks so that dubbed speech stays in sync with the
  on-screen action.

  Stage 4 — Voice Synthesis (AZURE-HOSTED AI — Azure Neural TTS)
  Each translated subtitle file is converted to spoken audio using Microsoft
  Azure Cognitive Services Text-to-Speech with Neural voices — high-quality,
  human-sounding AI voices available for all nine target languages. Each
  language uses a dedicated Neural voice tuned for natural prosody.

  Stage 5 — Audio Mixing
  The synthesized dubbed voice is mixed with the original background audio
  (music, ambience) that was isolated in Stage 1. The result is a natural-
  sounding dubbed track that preserves the atmosphere of the original video.

  Stage 6 — Video Assembly
  All dubbed audio tracks and subtitle tracks are combined into the final
  video files using FFmpeg, a professional-grade media processing tool.


AI MODELS USED
--------------------------------------------------------------------------------
  Local (run on the processing machine — no cloud cost, no data leaves the
  network for these stages):
    • Demucs          — voice/music separation (Meta Research)
    • OpenAI Whisper  — speech-to-text transcription (OpenAI)

  Azure-hosted (Microsoft cloud — data is sent over HTTPS and not retained
  for model training under the enterprise agreement):
    • GPT-4o-mini     — text translation (Azure AI Foundry)
    • Neural TTS      — voice synthesis, one Neural voice per language
                        (Azure Cognitive Services)


OUTPUT FILES EXPLAINED
--------------------------------------------------------------------------------
For each processed video you will find the following files in the output folder:

  video_multiAudio.mp4
    The master file. Contains the original video image plus ALL dubbed audio
    tracks (one per language) and ALL subtitle tracks. This is the recommended
    file for management review — you can switch language freely during playback.

  video_Bulgarian.mp4
  video_English.mp4
  video_French.mp4  ... (one per language)
    Individual single-language files for distribution or upload to platforms
    that do not support multi-track video (e.g. most social media).


HOW TO WATCH THE MULTI-LANGUAGE FILE (VLC Media Player)
--------------------------------------------------------------------------------
VLC Media Player (free, available at https://www.videolan.com) is recommended
for reviewing the multi-language master file.

  Switching the dubbed audio track:
    1. Open the _multiAudio.mp4 file in VLC.
    2. Click the menu: Audio → Audio Track
    3. Select the desired language from the list.
       (Tracks are labelled: Original, Bulgarian, English, French, etc.)

  Switching subtitles:
    1. Click the menu: Subtitle → Sub Track
    2. Select the desired subtitle language from the list.
    3. To hide subtitles, select "Disable".

  Tip: You can combine any audio track with any subtitle track — for example,
  play the English dub while displaying Bulgarian subtitles, or watch with
  the original audio and French subtitles.

  Windows 11 built-in player and most smart TV apps do not support multi-track
  video reliably. Use VLC or a professional media player for review.


LIMITATIONS AND KNOWN CONSTRAINTS (PoC Stage)
--------------------------------------------------------------------------------
This is a Proof of Concept. The results are suitable for internal review and
stakeholder demonstrations. The following limitations apply at this stage:

  Voice Quality
    AI Neural voices are fluent and natural but do not replicate the specific
    voice, tone, or emotional style of the original speaker. Each language has
    a single assigned voice — custom voice cloning is not included in this PoC.

  Timing Synchronisation
    Dubbed speech is generated from translated text. Because translated
    sentences are sometimes shorter or longer than the original, audio may
    occasionally overlap with on-screen cuts or run slightly longer than the
    original. This is inherent to text-to-speech dubbing and improves with
    post-processing tools not included in this PoC.

  Translation Accuracy
    GPT-4o-mini provides high-quality general translation. Domain-specific
    terminology, brand names, or highly idiomatic expressions may require
    human review before use in public-facing material.

  Source Language Detection
    Transcription works best for clearly spoken audio. Videos with heavy
    background noise, strong accents, or multiple overlapping speakers may
    yield lower transcription accuracy.

  Subtitle Formatting
    Subtitles are generated from the translated speech and are line-wrapped
    automatically. Manual adjustment may be needed for broadcast-quality
    deliverables.

  Processing Time
    A typical 5–10 minute video takes approximately 10–20 minutes to process
    end-to-end, depending on the number of target languages. The pipeline runs
    sequentially on a single machine.

  No Content Moderation Layer
    AI translation output is not automatically reviewed for sensitive content.
    Human review is recommended before any external publication.


CONCLUSION
--------------------------------------------------------------------------------
This PoC demonstrates that end-to-end AI video translation is technically
feasible with commercially available cloud and local AI services, at a fraction
of the cost and time of traditional dubbing production. The quality is
sufficient for internal communications, training materials, and stakeholder
presentations.

For an MVP (Minimum Viable Product) release, the pipeline would benefit from:
  • A web-based submission interface
  • Automated quality scoring and flagging of low-confidence segments
  • Custom voice profiles per presenter
  • Integration with existing content management systems

================================================================================
  Generated by: AI Video Translation Service — Internal PoC Build
  Processing date: June 2026
================================================================================
