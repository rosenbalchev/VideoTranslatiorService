#!/usr/bin/env python3

# Required installation for NVIDIA GPU:
# pip install faster-whisper
# pip install nvidia-cublas-cu12 nvidia-cudnn-cu12
#
# Also required:
# - NVIDIA GPU
# - NVIDIA driver
# - CUDA/cuDNN compatible with faster-whisper/CTranslate2

import argparse
from pathlib import Path
from faster_whisper import WhisperModel


def format_time(seconds: float) -> str:
    milliseconds = int((seconds % 1) * 1000)
    seconds = int(seconds)

    minutes, seconds = divmod(seconds, 60)
    hours, minutes = divmod(minutes, 60)

    return f"{hours:02}:{minutes:02}:{seconds:02},{milliseconds:03}"


def transcribe_to_srt(input_path: str, output_path: str):
    input_file = Path(input_path)
    output_file = Path(output_path)

    if not input_file.exists():
        raise FileNotFoundError(f"Input file does not exist: {input_file}")

    output_file.parent.mkdir(parents=True, exist_ok=True)

    model = WhisperModel(
        "medium",
        device="cuda",
        compute_type="float16"
    )

    segments, info = model.transcribe(
        str(input_file),
        word_timestamps=False
    )

    with output_file.open("w", encoding="utf-8") as f:
        for index, segment in enumerate(segments, start=1):
            start = format_time(segment.start)
            end = format_time(segment.end)
            text = segment.text.strip()

            f.write(f"{index}\n")
            f.write(f"{start} --> {end}\n")
            f.write(f"{text}\n\n")

    print(f"Created SRT: {output_file}")


def main():
    parser = argparse.ArgumentParser(
        description="Convert a WAV audio file to an SRT subtitle file using local Whisper on GPU."
    )

    parser.add_argument(
        "inputpath",
        help="Path to the input WAV file"
    )

    parser.add_argument(
        "outputpath",
        help="Path to the output SRT file"
    )

    args = parser.parse_args()

    transcribe_to_srt(args.inputpath, args.outputpath)


if __name__ == "__main__":
    main()