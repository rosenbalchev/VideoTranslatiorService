#!/usr/bin/env python3

# Required installation:
#   pip install whisperx librosa
#
# Speaker diarization uses gated pyannote models. Before first use:
#   1. Accept terms at https://huggingface.co/pyannote/speaker-diarization-3.1
#   2. Accept terms at https://huggingface.co/pyannote/segmentation-3.0
#   3. Create a read-scoped token at https://huggingface.co/settings/tokens
#   4. Put it in RBVideoTranslator.HfToken in appsettings.json and re-run the
#      install script (it caches the login), or pass --hf-token / set HF_TOKEN

import argparse
import os
from pathlib import Path

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

    model = whisperx.load_model("small", device="cpu", compute_type="int8")
    audio = whisperx.load_audio(str(input_file))

    result = model.transcribe(audio, batch_size=8)
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

        diarize_model = whisperx.diarize.DiarizationPipeline(use_auth_token=token, device="cpu")
        diarize_segments = diarize_model(audio, min_speakers=min_speakers, max_speakers=max_speakers)
        result = whisperx.assign_word_speakers(diarize_segments, result)
        segments = result["segments"]

        speaker_genders = estimate_speaker_genders(audio, segments)

        # Human-friendly labels (Speaker1, Speaker2, ...) in order of first appearance.
        for segment in segments:
            speaker = segment.get("speaker")
            if speaker is not None and speaker not in speaker_labels:
                speaker_labels[speaker] = f"Speaker{len(speaker_labels) + 1}"

    with output_file.open("w", encoding="utf-8") as f:
        f.write("WEBVTT\n\n")
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
        description="Convert a WAV audio file to a VTT subtitle file using a local Whisper model, "
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
