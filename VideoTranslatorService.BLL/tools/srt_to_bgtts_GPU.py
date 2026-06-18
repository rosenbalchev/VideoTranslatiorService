#!/usr/bin/env python3

import argparse
import os
import re
import shutil
import subprocess
import tempfile
import wave
from pathlib import Path

from tts_v5.inference import synthesize


SRT_TIME_RE = re.compile(
    r"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})"
)


def srt_time_to_ms(hours: str, minutes: str, seconds: str, millis: str) -> int:
    return (
        int(hours) * 3600_000
        + int(minutes) * 60_000
        + int(seconds) * 1000
        + int(millis)
    )


def parse_srt(path: Path):
    content = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    blocks = re.split(r"\n\s*\n", content.strip())

    subtitles = []

    for block in blocks:
        lines = [line.strip() for line in block.split("\n") if line.strip()]
        if len(lines) < 2:
            continue

        time_line_index = None
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

        text_lines = lines[time_line_index + 1 :]
        text = " ".join(text_lines)

        # Remove simple HTML tags often found in subtitles.
        text = re.sub(r"<[^>]+>", "", text)

        # Remove subtitle positioning tags like {\an8}
        text = re.sub(r"\{\\.*?\}", "", text)

        text = text.strip()

        if text:
            subtitles.append(
                {
                    "start_ms": start_ms,
                    "end_ms": end_ms,
                    "text": text,
                }
            )

    return subtitles


def wav_duration_ms(path: Path) -> int:
    with wave.open(str(path), "rb") as wf:
        frames = wf.getnframes()
        rate = wf.getframerate()
        return int(frames / float(rate) * 1000)


def make_silence(path: Path, duration_ms: int, sample_rate: int = 22050):
    duration_ms = max(0, duration_ms)
    samples = int(sample_rate * duration_ms / 1000)

    with wave.open(str(path), "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(sample_rate)
        wf.writeframes(b"\x00\x00" * samples)


def run_ffmpeg_concat(wav_files, output_path: Path):
    if not shutil.which("ffmpeg"):
        raise RuntimeError(
            "ffmpeg is required for concatenation. Install it first, then try again."
        )

    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as f:
        concat_list = Path(f.name)
        for wav_file in wav_files:
            # ffmpeg concat needs POSIX-style escaped paths.
            f.write(f"file '{wav_file.as_posix()}'\n")

    try:
        subprocess.run(
            [
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
            ],
            check=True,
        )
    finally:
        concat_list.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(
        description="Synthesize Bulgarian speech from an SRT file using BG-TTS V5."
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
        default=Path("output_from_srt.wav"),
        help="Output WAV file",
    )

    parser.add_argument(
        "--checkpoint",
        default="checkpoint",
        help="Path to BG-TTS V5 checkpoint folder",
    )

    parser.add_argument(
        "--speaker",
        type=int,
        default=0,
        choices=[0, 1],
        help="Speaker ID: 0 = clear AI voice, 1 = audiobook female voice",
    )

    parser.add_argument(
        "--temperature",
        type=float,
        default=0.25,
        help="Sampling temperature",
    )

    parser.add_argument(
        "--top-k",
        type=int,
        default=50,
        help="Top-k sampling",
    )

    parser.add_argument(
        "--top-p",
        type=float,
        default=0.8,
        help="Top-p sampling",
    )

    parser.add_argument(
        "--sample-rate",
        type=int,
        default=22050,
        help="Silence sample rate. BG-TTS V5 uses 22kHz audio.",
    )

    parser.add_argument(
        "--keep-parts",
        action="store_true",
        help="Keep temporary generated subtitle WAV parts",
    )

    args = parser.parse_args()

    subtitles = parse_srt(args.srt)

    if not subtitles:
        raise RuntimeError("No subtitle entries found in the SRT file.")

    workdir = Path("bgtts_srt_parts")
    workdir.mkdir(exist_ok=True)

    pieces = []
    current_ms = 0

    print(f"Found {len(subtitles)} subtitle entries.")

    for index, item in enumerate(subtitles, start=1):
        start_ms = item["start_ms"]
        end_ms = item["end_ms"]
        text = item["text"]

        print(f"[{index}/{len(subtitles)}] {text}")

        if start_ms > current_ms:
            silence_path = workdir / f"{index:05d}_silence.wav"
            make_silence(silence_path, start_ms - current_ms, args.sample_rate)
            pieces.append(silence_path)
            current_ms = start_ms

        voice_path = workdir / f"{index:05d}_voice.wav"

        synthesize(
            checkpoint=args.checkpoint,
            text=text,
            output=str(voice_path),
            speaker_id=args.speaker,
            temperature=args.temperature,
            top_k=args.top_k,
            top_p=args.top_p,
        )

        pieces.append(voice_path)

        generated_duration = wav_duration_ms(voice_path)

        # Advance by actual generated audio length, not the subtitle duration.
        # This avoids cutting speech, but it may drift if generated audio is too long.
        current_ms = max(current_ms + generated_duration, end_ms)

    print("Concatenating audio...")
    run_ffmpeg_concat(pieces, args.output)

    print(f"Done: {args.output}")

    if not args.keep_parts:
        shutil.rmtree(workdir, ignore_errors=True)


if __name__ == "__main__":
    main()