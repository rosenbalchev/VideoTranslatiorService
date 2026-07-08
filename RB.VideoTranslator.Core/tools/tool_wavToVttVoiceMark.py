#!/usr/bin/env python3

# Required installation:
#   pip install whisperx librosa
#
# For NVIDIA GPU acceleration, additionally:
#   pip install nvidia-cublas-cu12 nvidia-cudnn-cu12
# (also needs an NVIDIA driver + CUDA/cuDNN compatible with faster-whisper/CTranslate2).
# See scripts/install.bat (Windows) / scripts/install.sh (Linux/macOS).
#
# This script auto-detects a usable CUDA GPU at startup (torch.cuda.is_available())
# and picks the model size/precision for whichever device it finds — no separate
# CPU/GPU variants needed. The install scripts already pin the venv's torch build
# (CPU-only vs. CUDA) to match the hardware, so detection here just follows that.
#
# Speaker diarization ("voice marks": per-speaker NOTE headers + gender estimate)
# uses gated pyannote models and can be disabled with --no-voice-marks. Before first
# use:
#   1. Accept terms at https://huggingface.co/pyannote/speaker-diarization-3.1
#   2. Accept terms at https://huggingface.co/pyannote/segmentation-3.0
#   3. Create a read-scoped token at https://huggingface.co/settings/tokens
#   4. Put it in RBVideoTranslator.HfToken in appsettings.json and re-run the
#      install script (it caches the login), or pass --hf-token / set HF_TOKEN

import argparse
import os
import sys
from pathlib import Path

# ctranslate2 (whisperx's transcription backend) lazily dlopens cuBLAS/cuDNN
# on first inference call, using search flags that ignore os.add_dll_directory.
# When this script is launched as a subprocess (not via venv activate.bat),
# PATH doesn't include the venv's nvidia-*-cu12 packages, so that load fails
# with "Could not locate cudnn_ops_infer64_8.dll" even though the file exists.
# Prepending to PATH is the one mechanism every DLL loader on Windows honours.
if sys.platform == "win32":
    _site_packages = Path(sys.executable).parent.parent / "Lib" / "site-packages"
    _nvidia_dir = _site_packages / "nvidia"
    if _nvidia_dir.is_dir():
        _nvidia_bins = [str(p) for p in _nvidia_dir.glob("*/bin")]
        os.environ["PATH"] = os.pathsep.join(_nvidia_bins) + os.pathsep + os.environ.get("PATH", "")

import numpy as np
import librosa
import torch
import whisperx
from huggingface_hub import get_token

SAMPLE_RATE = 16000  # whisperx.load_audio always resamples to 16kHz mono
MALE_FEMALE_F0_THRESHOLD_HZ = 165.0


def select_device() -> tuple[str, str, str, int]:
    """Auto-detects a usable CUDA GPU and returns (device, model_size, compute_type,
    batch_size). GPU gets the bigger/faster config; CPU falls back to a lighter one."""
    if torch.cuda.is_available():
        return "cuda", "medium", "float16", 16
    return "cpu", "small", "int8", 8


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


MIN_SAMPLE_DURATION_SECONDS = 4.0


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


def transcribe_to_vtt(
    input_path: str,
    output_path: str,
    hf_token: str | None,
    diarize: bool,
    min_speakers: int | None,
    max_speakers: int | None,
):
    input_file = Path(input_path)
    output_file = Path(output_path)

    if not input_file.exists():
        raise FileNotFoundError(f"Input file does not exist: {input_file}")

    output_file.parent.mkdir(parents=True, exist_ok=True)

    device, model_size, compute_type, batch_size = select_device()
    print(f"Using device: {device} (model={model_size}, compute_type={compute_type})")

    model = whisperx.load_model(model_size, device=device, compute_type=compute_type)
    audio = whisperx.load_audio(str(input_file))

    result = model.transcribe(audio, batch_size=batch_size)
    segments = result["segments"]

    speaker_labels: dict[str, str] = {}
    speaker_genders: dict[str, tuple] = {}

    if diarize:
        token = hf_token or get_token()
        if not token:
            raise ValueError(
                "Speaker diarization requires a HuggingFace token. "
                "Set RBVideoTranslator.HfToken in appsettings.json and re-run the install "
                "script, or pass --hf-token / set the HF_TOKEN environment variable."
            )

        # Word-level timestamps let diarization assign a speaker per WORD instead of only
        # per Whisper segment, which split_segments_by_speaker below needs to separate a
        # segment that spans more than one speaker. Not every language has an alignment
        # model available — fall back to segment-level speaker assignment (today's
        # behaviour) rather than failing the whole transcription.
        try:
            align_model, align_metadata = whisperx.load_align_model(
                language_code=result["language"], device=device
            )
            result = whisperx.align(segments, align_model, align_metadata, audio, device=device)
            segments = result["segments"]
        except ValueError as e:
            print(f"No word-alignment model for language '{result['language']}' ({e}); "
                  "speakers within a single Whisper segment cannot be split apart.")

        diarize_model = whisperx.diarize.DiarizationPipeline(use_auth_token=token, device=device)
        diarize_segments = diarize_model(audio, min_speakers=min_speakers, max_speakers=max_speakers)
        result = whisperx.assign_word_speakers(diarize_segments, result)
        segments = split_segments_by_speaker(result["segments"])

        speaker_genders = estimate_speaker_genders(audio, segments)

        # Human-friendly labels (Speaker1, Speaker2, ...) in order of first appearance.
        for segment in segments:
            speaker = segment.get("speaker")
            if speaker is not None and speaker not in speaker_labels:
                speaker_labels[speaker] = f"Speaker{len(speaker_labels) + 1}"

    sample_segments = find_speaker_sample_segment(segments) if diarize else {}

    with output_file.open("w", encoding="utf-8") as f:
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

    print(f"Created VTT: {output_file}")


def main():
    parser = argparse.ArgumentParser(
        description="Convert a WAV audio file to a VTT subtitle file using a local Whisper "
        "model, auto-detecting GPU vs. CPU, with optional speaker diarization "
        "(\"voice marks\") and gender estimation."
    )

    parser.add_argument("inputpath", help="Path to the input WAV file")
    parser.add_argument("outputpath", help="Path to the output VTT file")
    parser.add_argument(
        "--hf-token",
        default=os.environ.get("HF_TOKEN"),
        help="HuggingFace access token for pyannote diarization models (defaults to HF_TOKEN env var)",
    )
    parser.add_argument(
        "--no-voice-marks",
        action="store_true",
        help="Skip speaker diarization/voice marks and gender estimation, transcribe only",
    )
    parser.add_argument("--min-speakers", type=int, default=None)
    parser.add_argument("--max-speakers", type=int, default=None)

    args = parser.parse_args()

    transcribe_to_vtt(
        args.inputpath,
        args.outputpath,
        hf_token=args.hf_token,
        diarize=not args.no_voice_marks,
        min_speakers=args.min_speakers,
        max_speakers=args.max_speakers,
    )


if __name__ == "__main__":
    main()
