#!/usr/bin/env python3

# Required installation for NVIDIA GPU:
#   pip install whisperx librosa
#   pip install nvidia-cublas-cu12 nvidia-cudnn-cu12
#
# Also required:
# - NVIDIA GPU
# - NVIDIA driver
# - CUDA/cuDNN compatible with faster-whisper/CTranslate2
#
# Speaker diarization uses gated pyannote models. Before first use:
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
import whisperx
from huggingface_hub import HfFolder

SAMPLE_RATE = 16000  # whisperx.load_audio always resamples to 16kHz mono
MALE_FEMALE_F0_THRESHOLD_HZ = 165.0


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


def find_longest_segment_per_speaker(segments: list) -> dict:
    """For each speaker, the segment with the longest speaking duration (end - start)."""
    longest: dict[str, dict] = {}
    for segment in segments:
        speaker = segment.get("speaker")
        if speaker is None:
            continue
        duration = segment["end"] - segment["start"]
        current = longest.get(speaker)
        if current is None or duration > current["duration"]:
            longest[speaker] = {"duration": duration, "start": segment["start"], "end": segment["end"]}
    return longest


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

    model = whisperx.load_model("medium", device="cuda", compute_type="float16")
    audio = whisperx.load_audio(str(input_file))

    result = model.transcribe(audio, batch_size=16)
    segments = result["segments"]

    speaker_labels: dict[str, str] = {}
    speaker_genders: dict[str, tuple] = {}

    if diarize:
        token = hf_token or HfFolder.get_token()
        if not token:
            raise ValueError(
                "Speaker diarization requires a HuggingFace token. "
                "Set RBVideoTranslator.HfToken in appsettings.json and re-run the install "
                "script, or pass --hf-token / set the HF_TOKEN environment variable."
            )

        diarize_model = whisperx.diarize.DiarizationPipeline(use_auth_token=token, device="cuda")
        diarize_segments = diarize_model(audio, min_speakers=min_speakers, max_speakers=max_speakers)
        result = whisperx.assign_word_speakers(diarize_segments, result)
        segments = result["segments"]

        speaker_genders = estimate_speaker_genders(audio, segments)

        # Human-friendly labels (Speaker1, Speaker2, ...) in order of first appearance.
        for segment in segments:
            speaker = segment.get("speaker")
            if speaker is not None and speaker not in speaker_labels:
                speaker_labels[speaker] = f"Speaker{len(speaker_labels) + 1}"

    longest_segments = find_longest_segment_per_speaker(segments) if diarize else {}

    with output_file.open("w", encoding="utf-8") as f:
        f.write("WEBVTT\n\n")

        if diarize and speaker_labels:
            noun = "speaker" if len(speaker_labels) == 1 else "speakers"
            f.write("NOTE\n")
            f.write(f"The conversation contains {len(speaker_labels)} {noun}.\n")
            for speaker, label in speaker_labels.items():
                gender, pitch_hz = speaker_genders.get(speaker, ("unknown", None))
                longest = longest_segments.get(speaker)
                start_short = format_time_short(longest["start"]) if longest else "00:00:00"
                end_short = format_time_short(longest["end"]) if longest else "00:00:00"
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
        description="Convert a WAV audio file to a VTT subtitle file using local Whisper on GPU, "
        "with optional speaker diarization and gender estimation."
    )

    parser.add_argument("inputpath", help="Path to the input WAV file")
    parser.add_argument("outputpath", help="Path to the output VTT file")
    parser.add_argument(
        "--hf-token",
        default=os.environ.get("HF_TOKEN"),
        help="HuggingFace access token for pyannote diarization models (defaults to HF_TOKEN env var)",
    )
    parser.add_argument(
        "--no-diarize",
        action="store_true",
        help="Skip speaker diarization and gender estimation, transcribe only",
    )
    parser.add_argument("--min-speakers", type=int, default=None)
    parser.add_argument("--max-speakers", type=int, default=None)

    args = parser.parse_args()

    transcribe_to_vtt(
        args.inputpath,
        args.outputpath,
        hf_token=args.hf_token,
        diarize=not args.no_diarize,
        min_speakers=args.min_speakers,
        max_speakers=args.max_speakers,
    )


if __name__ == "__main__":
    main()
