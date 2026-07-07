#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""
srt_to_piper_bg.py
==================

Generate Bulgarian speech from an .srt subtitle file using Piper TTS.

Tested target setup:
- Windows
- Python 3.11
- Piper installed with:
    pip install piper-tts
- Bulgarian Piper voice:
    voices/bg_BG-dimitar-medium.onnx
    voices/bg_BG-dimitar-medium.onnx.json
- ffmpeg installed and available in PATH:
    winget install Gyan.FFmpeg

Important for Bulgarian on Windows:
- Do NOT pipe Cyrillic text with: echo ... | python -m piper
- This script writes each subtitle line to a temporary UTF-8 text file and passes
  that file to Piper. This avoids Windows CMD encoding problems.

Example usage:
    python srt_to_piper_bg.py input.srt -o output.wav --model voices\bg_BG-dimitar-medium.onnx

Optional speed control:
    python srt_to_piper_bg.py input.srt -o output.wav --model voices\bg_BG-dimitar-medium.onnx --length-scale 0.95

Notes:
- The script preserves subtitle start times by inserting silence.
- It does not cut speech if a generated line is longer than the subtitle slot.
- Use --fit-to-slot to try simple per-line speed fitting with ffmpeg atempo.
"""

from __future__ import annotations

import argparse
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import wave
from pathlib import Path
from typing import Iterable, List, Optional


SRT_TIME_RE = re.compile(
    r"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})"
)


def srt_time_to_ms(hours: str, minutes: str, seconds: str, millis: str) -> int:
    return (
        int(hours) * 3_600_000
        + int(minutes) * 60_000
        + int(seconds) * 1000
        + int(millis)
    )


def clean_subtitle_text(text: str) -> str:
    """Remove common subtitle markup while preserving Bulgarian text."""
    text = re.sub(r"<[^>]+>", "", text)          # HTML tags
    text = re.sub(r"\{\\.*?\}", "", text)        # ASS-style tags like {\an8}
    text = text.replace("&nbsp;", " ")
    text = text.replace("&amp;", "&")
    text = re.sub(r"\s+", " ", text)
    return text.strip()


def parse_srt(path: Path) -> List[dict]:
    content = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    blocks = re.split(r"\n\s*\n", content.strip())

    subtitles: List[dict] = []

    for block in blocks:
        lines = [line.strip() for line in block.split("\n") if line.strip()]
        if len(lines) < 2:
            continue

        time_line_index: Optional[int] = None
        for i, line in enumerate(lines):
            if "-->" in line:
                time_line_index = i
                break

        if time_line_index is None:
            continue

        match = SRT_TIME_RE.search(lines[time_line_index])
        if not match:
            continue

        start_ms = srt_time_to_ms(*match.groups()[:4])
        end_ms = srt_time_to_ms(*match.groups()[4:])
        text = clean_subtitle_text(" ".join(lines[time_line_index + 1 :]))

        if text:
            subtitles.append(
                {
                    "start_ms": start_ms,
                    "end_ms": end_ms,
                    "duration_ms": max(0, end_ms - start_ms),
                    "text": text,
                }
            )

    return subtitles


def wav_duration_ms(path: Path) -> int:
    with wave.open(str(path), "rb") as wf:
        frames = wf.getnframes()
        rate = wf.getframerate()
        if rate <= 0:
            return 0
        return int(frames / float(rate) * 1000)


def wav_sample_rate(path: Path) -> int:
    with wave.open(str(path), "rb") as wf:
        return wf.getframerate()


def make_silence(path: Path, duration_ms: int, sample_rate: int = 22050) -> None:
    duration_ms = max(0, int(duration_ms))
    samples = int(sample_rate * duration_ms / 1000)

    with wave.open(str(path), "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(sample_rate)
        wf.writeframes(b"\x00\x00" * samples)


def ensure_ffmpeg() -> None:
    if not shutil.which("ffmpeg"):
        raise RuntimeError(
            "ffmpeg was not found in PATH. Install it with: winget install Gyan.FFmpeg"
        )


def run_command(cmd: List[str], *, env: Optional[dict] = None) -> None:
    result = subprocess.run(
        cmd,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
    )

    if result.returncode != 0:
        print("Command failed:", " ".join(cmd), file=sys.stderr)
        if result.stdout:
            print("\nSTDOUT:\n", result.stdout, file=sys.stderr)
        if result.stderr:
            print("\nSTDERR:\n", result.stderr, file=sys.stderr)
        raise RuntimeError(f"Command failed with exit code {result.returncode}")


def synthesize_with_piper(
    *,
    text: str,
    model: Path,
    output_wav: Path,
    temp_text_file: Path,
    piper_command: str,
    length_scale: float,
) -> None:
    """
    Write text to UTF-8 file and invoke Piper.

    We avoid stdin piping because Windows CMD often corrupts Cyrillic text.
    """
    temp_text_file.write_text(text + "\n", encoding="utf-8")

    cmd = [
        sys.executable,
        "-m",
        "piper",
        "--model",
        str(model),
        "--input-file",
        str(temp_text_file),
        "--output-file",
        str(output_wav),
        "--length-scale",
        str(length_scale),
    ]

    if piper_command and piper_command.lower() != "python-module":
        cmd = [
            piper_command,
            "--model",
            str(model),
            "--input-file",
            str(temp_text_file),
            "--output-file",
            str(output_wav),
            "--length-scale",
            str(length_scale),
        ]

    env = os.environ.copy()
    env["PYTHONUTF8"] = "1"

    run_command(cmd, env=env)

    if not output_wav.exists() or output_wav.stat().st_size == 0:
        raise RuntimeError(f"Piper did not create a valid WAV file: {output_wav}")


def ffmpeg_atempo_chain(speed_factor: float) -> str:
    """
    Build ffmpeg atempo filter chain.

    atempo accepts factors from 0.5 to 100 in modern ffmpeg, but chaining gives
    better compatibility. speed_factor > 1 speeds up audio.
    """
    if speed_factor <= 0:
        raise ValueError("speed_factor must be positive")

    factors = []

    remaining = speed_factor
    while remaining > 2.0:
        factors.append(2.0)
        remaining /= 2.0

    while remaining < 0.5:
        factors.append(0.5)
        remaining /= 0.5

    factors.append(remaining)

    return ",".join(f"atempo={f:.6f}" for f in factors)


def fit_wav_to_duration(
    *,
    input_wav: Path,
    output_wav: Path,
    target_ms: int,
) -> bool:
    """
    Speed-adjust generated WAV to fit within target_ms.

    Returns True if adjusted, False if skipped.
    """
    ensure_ffmpeg()

    source_ms = wav_duration_ms(input_wav)
    if source_ms <= 0 or target_ms <= 0:
        return False

    # If speech already fits, do not slow it down. We only speed up long lines.
    if source_ms <= target_ms:
        shutil.copyfile(input_wav, output_wav)
        return False

    speed_factor = source_ms / target_ms
    filter_chain = ffmpeg_atempo_chain(speed_factor)

    cmd = [
        "ffmpeg",
        "-y",
        "-i",
        str(input_wav),
        "-filter:a",
        filter_chain,
        str(output_wav),
    ]

    run_command(cmd)
    return True


def ffmpeg_concat(wav_files: Iterable[Path], output_path: Path) -> None:
    ensure_ffmpeg()

    with tempfile.NamedTemporaryFile(
        "w", suffix=".txt", delete=False, encoding="utf-8"
    ) as f:
        concat_list = Path(f.name)
        for wav_file in wav_files:
            # ffmpeg concat works best with forward slashes and quoted file paths.
            safe_path = wav_file.resolve().as_posix().replace("'", "'\\''")
            f.write(f"file '{safe_path}'\n")

    try:
        cmd = [
            "ffmpeg",
            "-y",
            "-f",
            "concat",
            "-safe",
            "0",
            "-i",
            str(concat_list),
            "-c",
            "copy",
            str(output_path),
        ]
        run_command(cmd)
    finally:
        concat_list.unlink(missing_ok=True)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate Bulgarian speech from SRT using Piper TTS."
    )

    parser.add_argument(
        "srt",
        type=Path,
        help="Input .srt subtitle file",
    )

    parser.add_argument(
        "-o",
        "--output",
        type=Path,
        default=Path("piper_srt_output.wav"),
        help="Output WAV file",
    )

    parser.add_argument(
        "--model",
        type=Path,
        default=Path("voices/bg_BG-dimitar-medium.onnx"),
        help="Path to Piper Bulgarian ONNX voice model",
    )

    parser.add_argument(
        "--length-scale",
        type=float,
        default=1.0,
        help="Piper length scale. Lower=faster, higher=slower. Default: 1.0",
    )

    parser.add_argument(
        "--sample-rate",
        type=int,
        default=22050,
        help="Sample rate for inserted silence. Default: 22050",
    )

    parser.add_argument(
        "--fit-to-slot",
        action="store_true",
        help="Speed up generated lines that exceed their subtitle duration",
    )

    parser.add_argument(
        "--piper-command",
        default="python-module",
        help=(
            "How to run Piper. Default uses current Python: python -m piper. "
            "Set to 'piper' if you want to call a piper executable."
        ),
    )

    parser.add_argument(
        "--keep-parts",
        action="store_true",
        help="Keep temporary per-subtitle WAV files",
    )

    parser.add_argument(
        "--workdir",
        type=Path,
        default=Path("piper_srt_parts"),
        help="Temporary working directory",
    )

    args = parser.parse_args()

    if not args.srt.exists():
        print(f"Input SRT not found: {args.srt}", file=sys.stderr)
        return 2

    if not args.model.exists():
        print(f"Piper model not found: {args.model}", file=sys.stderr)
        print("Expected files:", file=sys.stderr)
        print("  voices/bg_BG-dimitar-medium.onnx", file=sys.stderr)
        print("  voices/bg_BG-dimitar-medium.onnx.json", file=sys.stderr)
        return 2

    config_path = Path(str(args.model) + ".json")
    if not config_path.exists():
        print(f"Warning: model config not found: {config_path}", file=sys.stderr)
        print("Piper usually needs the .onnx.json file next to the .onnx model.", file=sys.stderr)

    subtitles = parse_srt(args.srt)
    if not subtitles:
        print("No valid subtitle entries found.", file=sys.stderr)
        return 2

    args.workdir.mkdir(parents=True, exist_ok=True)

    pieces: List[Path] = []
    current_ms = 0

    print(f"Loaded {len(subtitles)} subtitle entries.")
    print(f"Using model: {args.model}")
    print(f"Output: {args.output}")
    print()

    try:
        for index, item in enumerate(subtitles, start=1):
            start_ms = int(item["start_ms"])
            end_ms = int(item["end_ms"])
            duration_ms = int(item["duration_ms"])
            text = str(item["text"])

            print(f"[{index}/{len(subtitles)}] {start_ms}–{end_ms} ms: {text}", flush=True)

            if start_ms > current_ms:
                silence_path = args.workdir / f"{index:05d}_silence.wav"
                make_silence(silence_path, start_ms - current_ms, args.sample_rate)
                pieces.append(silence_path)
                current_ms = start_ms

            txt_path = args.workdir / f"{index:05d}.txt"
            voice_raw_path = args.workdir / f"{index:05d}_voice_raw.wav"
            voice_final_path = args.workdir / f"{index:05d}_voice.wav"

            synthesize_with_piper(
                text=text,
                model=args.model,
                output_wav=voice_raw_path,
                temp_text_file=txt_path,
                piper_command=args.piper_command,
                length_scale=args.length_scale,
            )

            if args.fit_to_slot:
                adjusted = fit_wav_to_duration(
                    input_wav=voice_raw_path,
                    output_wav=voice_final_path,
                    target_ms=duration_ms,
                )
                if adjusted:
                    print("  adjusted speed to fit subtitle slot", flush=True)
            else:
                shutil.copyfile(voice_raw_path, voice_final_path)

            pieces.append(voice_final_path)

            generated_ms = wav_duration_ms(voice_final_path)

            # Preserve start times as much as possible. If generated speech is longer
            # than the slot, do not cut it; allow the timeline to drift forward.
            current_ms = max(current_ms + generated_ms, end_ms)

        print()
        print("Concatenating WAV parts with ffmpeg...", flush=True)
        ffmpeg_concat(pieces, args.output)

        print(f"Done: {args.output}")
        return 0

    finally:
        if not args.keep_parts:
            shutil.rmtree(args.workdir, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
