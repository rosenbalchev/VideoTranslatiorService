#!/usr/bin/env python3
"""Exports the Whisper model used for transcription to ONNX.

Separate, optional step run AFTER install.bat/install.sh, not part of them — this is a
multi-GB one-time download and an experimental transcription path, not a hard requirement
for the pipeline to work. See scripts/dependencies.json "onnx" section for package/version
notes and why each pin exists.

Usage:
    <venv>/bin/python scripts/export_onnx_models.py <appsettings.json> <dependencies.json>

Output: <WorkingFolderPath>/onnx-models/<model-name>/, loaded directly by the native C#
transcriber (RB.VideoTranslator.WhisperOnnx / WhisperOnnxTranscriberService.cs) — nothing
Python-side runs inference against this model anymore, so no onnxruntime execution-provider
package needs installing here; the plain 'onnxruntime' package pulled in by exportPackages
(needed by optimum during export) is left in place afterward, unused but harmless.
"""

import json
import subprocess
import sys
from pathlib import Path


def pip_install(*packages: str) -> None:
    subprocess.run([sys.executable, "-m", "pip", "install", "--quiet", *packages], check=True)


def pip_uninstall(*packages: str) -> None:
    subprocess.run([sys.executable, "-m", "pip", "uninstall", "-y", "--quiet", *packages], check=True)


def main() -> None:
    if len(sys.argv) != 3:
        print("Usage: export_onnx_models.py <appsettings.json> <dependencies.json>", file=sys.stderr)
        sys.exit(2)

    appsettings_path = Path(sys.argv[1])
    deps_path = Path(sys.argv[2])

    appsettings = json.loads(appsettings_path.read_text(encoding="utf-8-sig"))
    work_folder = appsettings["RBVideoTranslator"]["WorkingFolderPath"]

    deps = json.loads(deps_path.read_text(encoding="utf-8-sig"))["onnx"]
    whisper_model = deps["whisperModel"]
    output_dir = Path(work_folder) / "onnx-models" / whisper_model.split("/")[-1]

    print("[1/2] Installing export tooling...")
    # onnxruntime-directml/-gpu register the same 'onnxruntime' import name as plain onnxruntime
    # but are separate pip distributions — clean up any leftover from an older version of this
    # script (which used to install one of those afterward) before the plain one goes in.
    pip_uninstall("onnxruntime", "onnxruntime-directml", "onnxruntime-gpu")
    print(f"       Installing: {', '.join(deps['exportPackages'])}")
    pip_install(*deps["exportPackages"])

    print(f"[2/2] Exporting {whisper_model} to ONNX (this can take a few minutes)...")
    from optimum.onnxruntime import ORTModelForSpeechSeq2Seq
    from transformers import WhisperProcessor

    model = ORTModelForSpeechSeq2Seq.from_pretrained(whisper_model, export=True)
    output_dir.mkdir(parents=True, exist_ok=True)
    model.save_pretrained(str(output_dir))

    # save_pretrained() above only writes the ONNX graphs + config, not the tokenizer/
    # feature-extractor — without this the exported folder can't be loaded standalone.
    processor = WhisperProcessor.from_pretrained(whisper_model)
    processor.save_pretrained(str(output_dir))

    print(f"       Exported to: {output_dir}")

    print()
    print("Done. Enable it by setting RBVideoTranslator.UseOnnxTranscription to true in")
    print("appsettings.json (diarization still runs via whisperx/torch either way).")


if __name__ == "__main__":
    main()
