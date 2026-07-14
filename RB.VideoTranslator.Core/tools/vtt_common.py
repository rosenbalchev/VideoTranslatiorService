#!/usr/bin/env python3
"""Shared helpers for the wav-to-VTT diarization/formatting tools.

Both tool_wavToVttVoiceMark.py (WhisperX transcription + diarization, used when
UseOnnxTranscription is off) and tool_diarizeVtt.py (diarization only — transcription
runs natively in C#, see RB.VideoTranslator.WhisperOnnx) produce the same VTT contract
(WEBVTT + NOTE speaker headers) consumed by SpeakerSampleExtractorService.cs and
VttTranslatorService.cs, so the formatting/diarization-shaping logic must stay identical
between them. This module is that single source of truth.
"""

import numpy as np
import librosa

SAMPLE_RATE = 16000  # whisperx.load_audio always resamples to 16kHz mono
MALE_FEMALE_F0_THRESHOLD_HZ = 165.0

# Mirrors MinSampleDuration in SpeakerSampleExtractorService.cs — must stay identical so
# the segment picked here always matches the one the C# side re-derives from the VTT.
MIN_SAMPLE_DURATION_SECONDS = 4.0


def format_time(seconds: float) -> str:
    milliseconds = int((seconds % 1) * 1000)
    seconds = int(seconds)

    minutes, seconds = divmod(seconds, 60)
    hours, minutes = divmod(minutes, 60)

    return f"{hours:02}:{minutes:02}:{seconds:02}.{milliseconds:03}"


def format_time_short(seconds: float) -> str:
    seconds = int(seconds)
    minutes, seconds = divmod(seconds, 60)
    hours, minutes = divmod(minutes, 60)
    return f"{hours:02}:{minutes:02}:{seconds:02}"


def find_speaker_sample_segment(segments: list) -> dict:
    """For each speaker, the sample segment used for the header/speaker-sample extraction:
    the FIRST segment (chronologically) longer than MIN_SAMPLE_DURATION_SECONDS, so the
    sample is available as early as possible rather than wherever the single longest
    utterance happens to fall. Falls back to that speaker's longest segment overall if
    none of their segments clear the threshold."""
    first_over_threshold: dict[str, dict] = {}
    longest: dict[str, dict] = {}
    for segment in segments:
        speaker = segment.get("speaker")
        if speaker is None:
            continue
        duration = segment["end"] - segment["start"]

        if speaker not in first_over_threshold and duration > MIN_SAMPLE_DURATION_SECONDS:
            first_over_threshold[speaker] = {"duration": duration, "start": segment["start"], "end": segment["end"]}

        current_longest = longest.get(speaker)
        if current_longest is None or duration > current_longest["duration"]:
            longest[speaker] = {"duration": duration, "start": segment["start"], "end": segment["end"]}

    return {**longest, **first_over_threshold}


def split_segments_by_speaker(segments: list) -> list:
    """WhisperX's diarization only assigns one speaker to an entire transcribed segment
    (by majority time-overlap) unless word-level timestamps are available, in which case it
    also assigns a speaker per word. A segment can span a quick back-and-forth between two
    people with no long-enough pause for Whisper's VAD to split on, which would otherwise
    glue both speakers' lines into a single VTT cue under whichever speaker had more overlap.
    This splits every segment at each word-level speaker change, so each output cue contains
    only one speaker's words. Segments without word timestamps (e.g. no alignment model was
    available for the detected language) pass through unchanged."""
    result: list[dict] = []
    for segment in segments:
        words = segment.get("words")
        if not words:
            result.append(segment)
            continue

        runs: list[tuple] = []  # (speaker, [word, ...])
        current_speaker = segment.get("speaker")
        for word in words:
            speaker = word.get("speaker") or current_speaker
            if not runs or speaker != current_speaker:
                runs.append((speaker, []))
            runs[-1][1].append(word)
            current_speaker = speaker

        for speaker, run_words in runs:
            text = " ".join(w["word"].strip() for w in run_words if w.get("word", "").strip())
            if not text:
                continue
            starts = [w["start"] for w in run_words if "start" in w]
            ends = [w["end"] for w in run_words if "end" in w]
            result.append({
                "start": starts[0] if starts else segment["start"],
                "end": ends[-1] if ends else segment["end"],
                "text": text,
                "speaker": speaker,
            })

    return result


def estimate_speaker_genders(audio: np.ndarray, segments: list) -> dict:
    """Median-F0 heuristic per speaker: low pitch -> male, high pitch -> female."""
    segments_by_speaker: dict[str, list] = {}
    for segment in segments:
        speaker = segment.get("speaker")
        if speaker is None:
            continue
        segments_by_speaker.setdefault(speaker, []).append((segment["start"], segment["end"]))

    genders = {}
    for speaker, spans in segments_by_speaker.items():
        f0_values = []
        for start, end in spans:
            start_sample = int(start * SAMPLE_RATE)
            end_sample = int(end * SAMPLE_RATE)
            clip = audio[start_sample:end_sample]
            if len(clip) < SAMPLE_RATE * 0.1:
                continue

            f0, voiced_flag, _ = librosa.pyin(
                clip,
                fmin=librosa.note_to_hz("C2"),
                fmax=librosa.note_to_hz("C7"),
                sr=SAMPLE_RATE,
            )
            voiced = f0[voiced_flag]
            if len(voiced) > 0:
                f0_values.extend(voiced.tolist())

        if f0_values:
            median_f0 = float(np.median(f0_values))
            gender = "male" if median_f0 < MALE_FEMALE_F0_THRESHOLD_HZ else "female"
            genders[speaker] = (gender, median_f0)
        else:
            genders[speaker] = ("unknown", None)

    return genders


def write_vtt(
    output_path,
    segments: list,
    diarize: bool,
    speaker_labels: dict,
    speaker_genders: dict,
    sample_segments: dict,
):
    with open(output_path, "w", encoding="utf-8") as f:
        f.write("WEBVTT\n\n")

        if diarize and speaker_labels:
            noun = "speaker" if len(speaker_labels) == 1 else "speakers"
            f.write("NOTE\n")
            f.write(f"The conversation contains {len(speaker_labels)} {noun}.\n")
            for speaker, label in speaker_labels.items():
                gender, pitch_hz = speaker_genders.get(speaker, ("unknown", None))
                sample = sample_segments.get(speaker)
                start_short = format_time_short(sample["start"]) if sample else "00:00:00"
                end_short = format_time_short(sample["end"]) if sample else "00:00:00"
                pitch_str = f"{pitch_hz:.0f}Hz" if pitch_hz is not None else "N/A"
                f.write(f"{label}|{gender.capitalize()}|{start_short}|{end_short}|{pitch_str}\n")
            f.write("\n")

        for index, segment in enumerate(segments, start=1):
            start = format_time(segment["start"])
            end = format_time(segment["end"])
            text = segment["text"].strip()

            speaker = segment.get("speaker")
            if speaker is not None:
                label = speaker_labels.get(speaker, speaker)
                gender, _ = speaker_genders.get(speaker, ("unknown", None))
                f.write(f"NOTE {label} (estimated: {gender})\n\n")

            f.write(f"{index}\n")
            f.write(f"{start} --> {end}\n")
            f.write(f"{text}\n\n")


def build_speaker_labels(segments: list) -> dict:
    """Human-friendly labels (Speaker1, Speaker2, ...) in order of first appearance."""
    speaker_labels: dict[str, str] = {}
    for segment in segments:
        speaker = segment.get("speaker")
        if speaker is not None and speaker not in speaker_labels:
            speaker_labels[speaker] = f"Speaker{len(speaker_labels) + 1}"
    return speaker_labels


def diarize_and_shape(
    *,
    audio: np.ndarray,
    segments: list,
    language: str,
    device: str,
    hf_token: str,
    min_speakers: int | None,
    max_speakers: int | None,
):
    """Runs whisperx word-alignment + pyannote diarization over already-transcribed
    segments, then reshapes them the same way regardless of which ASR backend produced
    the segments. Returns (segments, speaker_labels, speaker_genders, sample_segments).

    Kept backend-agnostic on purpose: this is the one part of the pipeline still tied to
    whisperx/pyannote/torch (diarization has no ONNX/C# conversion yet), so both
    tool_wavToVttVoiceMark.py and tool_diarizeVtt.py call this same function afterwards.
    """
    import whisperx
    import whisperx.diarize  # noqa: F401 - whisperx.diarize isn't guaranteed populated by
    # `import whisperx` alone; tool_wavToVttVoiceMark.py's own top-level whisperx calls
    # happen to trigger it as a side effect, but tool_diarizeVtt.py never calls those (its
    # transcription comes in pre-computed, from the native C# transcriber), so relying on
    # that side effect fails there with "module 'whisperx' has no attribute 'diarize'".
    # Import it explicitly instead.

    try:
        align_model, align_metadata = whisperx.load_align_model(language_code=language, device=device)
        result = whisperx.align(segments, align_model, align_metadata, audio, device=device)
        segments = result["segments"]
    except ValueError as e:
        print(f"No word-alignment model for language '{language}' ({e}); "
              "speakers within a single segment cannot be split apart.")

    diarize_model = whisperx.diarize.DiarizationPipeline(use_auth_token=hf_token, device=device)
    diarize_segments = diarize_model(audio, min_speakers=min_speakers, max_speakers=max_speakers)
    result = whisperx.assign_word_speakers(diarize_segments, {"segments": segments})
    segments = split_segments_by_speaker(result["segments"])

    speaker_genders = estimate_speaker_genders(audio, segments)
    speaker_labels = build_speaker_labels(segments)
    sample_segments = find_speaker_sample_segment(segments)

    return segments, speaker_labels, speaker_genders, sample_segments
