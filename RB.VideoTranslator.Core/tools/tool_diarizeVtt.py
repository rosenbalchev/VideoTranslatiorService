#!/usr/bin/env python3

# Speaker diarization + VTT writing only. The Whisper transcription step now runs natively in
# C# (RB.VideoTranslator.WhisperOnnx, invoked from WhisperOnnxTranscriberService.cs) — this
# script picks up from there: it takes the already-transcribed segments as JSON and does just
# the whisperx/pyannote/torch part that has no .NET equivalent (word alignment, diarization,
# gender estimation), reusing vtt_common.py's diarize_and_shape()/write_vtt() unchanged so the
# VTT format contract (WEBVTT + NOTE speaker headers) is identical to both
# tool_wavToVttVoiceMark.py's and the old tool_wavToVtt_onnx.py's output.

import argparse
import json

import librosa
from huggingface_hub import get_token

from vtt_common import SAMPLE_RATE, diarize_and_shape, write_vtt


def diarize_to_vtt(
    input_path: str,
    segments_json_path: str,
    output_path: str,
    language: str,
    hf_token: str | None,
    min_speakers: int | None,
    max_speakers: int | None,
):
    # utf-8-sig: VttExtractorService.cs writes this via .NET's Encoding.UTF8, which prepends a
    # BOM — plain "utf-8" chokes on it (json.decoder.JSONDecodeError: Unexpected UTF-8 BOM).
    with open(segments_json_path, encoding="utf-8-sig") as f:
        segments = json.load(f)

    audio, _ = librosa.load(input_path, sr=SAMPLE_RATE, mono=True)

    token = hf_token or get_token()
    if not token:
        raise ValueError(
            "Speaker diarization requires a HuggingFace token. "
            "Set RBVideoTranslator.HfToken in appsettings.json and re-run the install "
            "script, or pass --hf-token / set the HF_TOKEN environment variable."
        )

    segments, speaker_labels, speaker_genders, sample_segments = diarize_and_shape(
        audio=audio,
        segments=segments,
        language=language,
        device="cuda" if _torch_cuda_available() else "cpu",
        hf_token=token,
        min_speakers=min_speakers,
        max_speakers=max_speakers,
    )

    write_vtt(output_path, segments, True, speaker_labels, speaker_genders, sample_segments)
    print(f"Created VTT: {output_path}")


def _torch_cuda_available() -> bool:
    import torch

    return torch.cuda.is_available()


def main():
    parser = argparse.ArgumentParser(
        description="Diarize an already-transcribed audio file and write a VTT with speaker "
        "voice marks. Transcription itself is done elsewhere (natively in C#); this script "
        "only runs whisperx/pyannote alignment + diarization + gender estimation."
    )

    parser.add_argument("inputpath", help="Path to the input WAV file (16kHz mono)")
    parser.add_argument("segmentsjson", help="Path to a JSON file: a list of {start, end, text}")
    parser.add_argument("outputpath", help="Path to the output VTT file")
    parser.add_argument("--language", required=True, help="Source language as an ISO 639-1 code")
    parser.add_argument(
        "--hf-token",
        default=None,
        help="HuggingFace access token for pyannote diarization models (defaults to HF_TOKEN env var)",
    )
    parser.add_argument("--min-speakers", type=int, default=None)
    parser.add_argument("--max-speakers", type=int, default=None)

    args = parser.parse_args()

    diarize_to_vtt(
        args.inputpath,
        args.segmentsjson,
        args.outputpath,
        language=args.language,
        hf_token=args.hf_token,
        min_speakers=args.min_speakers,
        max_speakers=args.max_speakers,
    )


if __name__ == "__main__":
    main()
