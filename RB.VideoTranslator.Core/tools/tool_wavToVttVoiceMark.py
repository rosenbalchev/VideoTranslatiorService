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

import torch
import whisperx
from huggingface_hub import get_token

from vtt_common import diarize_and_shape, write_vtt


def select_device() -> tuple[str, str, str, int]:
    """Auto-detects a usable CUDA GPU and returns (device, model_size, compute_type,
    batch_size). GPU gets the bigger/faster config; CPU falls back to a lighter one."""
    if torch.cuda.is_available():
        return "cuda", "medium", "float16", 16
    return "cpu", "small", "int8", 8


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
    sample_segments: dict = {}

    if diarize:
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
            language=result["language"],
            device=device,
            hf_token=token,
            min_speakers=min_speakers,
            max_speakers=max_speakers,
        )

    write_vtt(output_file, segments, diarize, speaker_labels, speaker_genders, sample_segments)
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
