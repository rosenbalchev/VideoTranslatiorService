# How to Start

Step-by-step setup guide for RB.VideoTranslator on Windows.

> **Important:** Configure `appsettings.json` **before** running the install scripts — the scripts read the working folder path from that file.

---

## Step 1 — Configure appsettings.json

Open `RB.VideoTranslator.CLI\appsettings.json` (or the copy next to the exe after building) and fill in your values:

```json
{
  "RBVideoTranslator": {
    "WorkingFolderPath": "C:\\VideoTranslator",
    "VenvPath": "",
    "HfToken": "",
    "AzureSubscriptionKey": "<your-azure-key>",
    "AzureEndpointUrl": "https://<resource>.cognitiveservices.azure.com/",
    "AzureOpenAiEndpoint": "https://<resource>.services.ai.azure.com/",
    "AzureOpenAiDeployment": "gpt-4o-mini",
    "TranslationTargetLanguages": [ "Bulgarian" ],
    "FfmpegPath": "ffmpeg",
    "PythonPath": "python",
    "DemucsPath": "python",
    "OutputFolderPath": "",
    "UseFemaleVoice": false,
    "EnableVoiceMarks": true
  }
}
```

**Key fields to set now:**

| Field | Example | Notes |
|-------|---------|-------|
| `WorkingFolderPath` | `C:\\VideoTranslator` | The single root for all data. `input`, `processing`, `output` subfolders are created automatically. The CLI always runs in this directory. |
| `AzureSubscriptionKey` | `abc123...` | Key 1 from your Azure Cognitive Services resource. Used for both Speech TTS and OpenAI. |
| `AzureEndpointUrl` | `https://my-res.cognitiveservices.azure.com/` | Azure Speech endpoint. |
| `AzureOpenAiEndpoint` | `https://my-res.services.ai.azure.com/` | Azure AI Services root URL — do **not** append `/openai/v1`. |
| `TranslationTargetLanguages` | `["Bulgarian","German"]` | Languages to translate into; voices are selected automatically. |
| `EnableVoiceMarks` | `true` | Whether the Whisper step runs speaker diarization + gender estimation ("voice marks"). Requires `HfToken`. Set `false` to transcribe only. |

Leave `VenvPath` empty — the install script fills it in automatically.

---

## Step 2 — Install system dependencies

### .NET 10 SDK
Download and install from https://dotnet.microsoft.com/download

### Python 3.12
Download from https://www.python.org/downloads/  
During install, check **"Add Python to PATH"**.  
The install scripts require Python 3.12 specifically (`py -3.12`).

---

## Step 3 — Cloud & model access setup

### Speech TTS
1. In the Azure Portal, create an **Azure AI Services** or **Cognitive Services — Speech** resource.
2. Copy **Key 1** → `AzureSubscriptionKey` in `appsettings.json`.
3. Copy the **Endpoint** URL → `AzureEndpointUrl`.

### OpenAI translation
1. Create an **Azure AI Services** resource with a **GPT-4o-mini** deployment.
2. Copy the resource's root URL → `AzureOpenAiEndpoint` (no `/openai/v1` suffix).
3. The same subscription key is used — no separate `az login` required.

### HuggingFace (speaker diarization)

The Whisper step also detects **who is speaking** and estimates each speaker's gender by pitch, writing them as `NOTE Speaker2 (estimated: female)` comments above each VTT cue ("voice marks"). This uses gated pyannote models, so it needs a one-time HuggingFace token. Skip this if you don't need speaker labels — set `EnableVoiceMarks` to `false` in `appsettings.json` and transcription still works without it.

1. Create a free account at https://huggingface.co/join (or log in if you have one).
2. Accept the model terms on **both** gated model pages individually (click **"Agree and access repository"** on each):
   - https://huggingface.co/pyannote/speaker-diarization-3.1
   - https://huggingface.co/pyannote/segmentation-3.0

   > **Both are required, separately.** Accepting only one is not enough — diarization loads `segmentation-3.0` as an internal dependency of `speaker-diarization-3.1`, so a token that works fine for one will still fail on the other. The failure is also misleading: it prints *"Could not download '...' model. It might be because the model is private or gated..."* even when your token is completely valid — that message fires for *any* download failure, not just missing access. If you see it, re-check both URLs above rather than assuming the token itself is wrong.

3. Go to https://huggingface.co/settings/tokens → **New token** → give it any name → role **Read** is enough → **Generate**.
4. Copy the generated token (starts with `hf_...`) into `HfToken` in `appsettings.json`.
5. Run (or re-run) the install script from Step 4 below — its last step calls `hf auth login` with this token and caches it to your Windows user profile (`%USERPROFILE%\.cache\huggingface\token`), so it is never needed again at runtime (no environment variable to set, and it's shared across any venv for this Windows account).

---

## Step 4 — Run the install script

With `appsettings.json` saved, run the script that matches your hardware **from the repository root**:

### Option A — CUDA (NVIDIA GPU) — recommended

```bat
scripts\install-cuda.bat
```

Installs PyTorch 2.5.1 with CUDA 12.4, Demucs, and faster-whisper with CUDA runtime libs.  
Whisper and Demucs run **5–10× faster** with a GPU.

> **Driver requirement:** CUDA 12.4 needs Game Ready 550+ / Studio 555+ drivers.  
> Works on newer CUDA runtimes (12.6, 12.8) — CUDA is backward-compatible.  
> PyTorch is pinned to 2.5.1 because 2.6+ requires `torchcodec`, which has no Windows build.

### Option B — CPU only

```bat
scripts\install-cpu.bat
```

Same steps without CUDA. Processing is slower — expect several minutes per minute of audio on a modern CPU.

---

Both scripts will:
1. Read `WorkingFolderPath` from `appsettings.json`
2. Create the `input`, `processing`, `output` subfolders there
3. Install ffmpeg via winget
4. Create a Python 3.12 venv at `<WorkingFolderPath>\rb.video.translator`
5. Install all Python packages (PyTorch, Demucs, WhisperX, librosa)
6. Cache the `HfToken` login for speaker diarization, if you set one in Step 3
7. Write `VenvPath` back into `appsettings.json` automatically

---

## Step 5 — Verify the environment

```bat
"C:\VideoTranslator\rb.video.translator\Scripts\activate"
python -c "import whisperx; print('WhisperX OK')"
python -c "import librosa; print('librosa OK')"
python -c "import demucs; print('Demucs OK')"
python -c "import torch; print('CUDA available:', torch.cuda.is_available())"
ffmpeg -version
```

`CUDA available: True` confirms GPU acceleration is active (CUDA option only).

---

## Step 6 — Build the CLI

From the repository root:

```bat
dotnet build --configuration Release
```

The binary will be at:
```
RB.VideoTranslator.CLI\bin\Release\net10.0\RB.VideoTranslator.CLI.exe
```

Copy `appsettings.json` next to the exe if you intend to run it from outside the source tree:
```bat
copy RB.VideoTranslator.CLI\appsettings.json RB.VideoTranslator.CLI\bin\Release\net10.0\
```

Or run directly without a separate build step:
```bat
dotnet run --project RB.VideoTranslator.CLI --configuration Release
```

---

## Step 7 — First run

Place video files (`.mp4`, `.mkv`, `.avi`, `.mov`, `.webm`) in `<WorkingFolderPath>\input`, then run:

```bat
RB.VideoTranslator.CLI\bin\Release\net10.0\RB.VideoTranslator.CLI.exe
```

No CLI arguments are needed when `appsettings.json` is fully configured.

### Passing values via CLI (optional overrides)

Any value from `appsettings.json` can be overridden on the command line:

```bat
RB.VideoTranslator.CLI.exe ^
  --work-folder    "C:\videos"             ^
  --azure-key      "<speech-key>"          ^
  --azure-endpoint "https://..."           ^
  --openai-endpoint "https://..."          ^
  --target-lang    "Bulgarian,German"
```

### Multiple target languages

Set them in `appsettings.json`:
```json
"TranslationTargetLanguages": [ "Bulgarian", "German", "French" ]
```
Or override on the CLI: `--target-lang "Bulgarian,German,French"`.

Supported languages and their Azure Neural voices are listed in
`RB.VideoTranslator.Core\Services\PipelineOrchestrator.cs` (`VoiceMap`).

---

## Step 8 — Crash recovery

If the process is interrupted (power loss, Ctrl+C, crash), just run it again.  
The orchestrator reads the last committed state from the SQLite database (`<WorkingFolderPath>\videotranslator.db`) and resumes from where it stopped. For multi-language jobs, already-completed languages are skipped — only the interrupted language is retried.

---

## Common issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| `WorkingFolderPath` missing error | Config not set | Edit `appsettings.json` and set `WorkingFolderPath` |
| `Python not found` | Python not on PATH | Re-install with "Add to PATH", or set `PythonPath` in `appsettings.json` |
| `ffmpeg not found` | ffmpeg not on PATH | Re-run install script, or set `FfmpegPath` in `appsettings.json` |
| `CUDA available: False` | Wrong PyTorch build or old driver | Re-run `install-cuda.bat`; update NVIDIA driver |
| Azure TTS timeout errors | Transient SDK issue | Automatically retried up to 3 times; silence is substituted if all fail |
| `No voice configured for language 'X'` | Language not in voice map | Add an entry to `PipelineOrchestrator.VoiceMap` |
| Job stuck after restart | Unexpected state in DB | Check `ErrorMessage` column in `videotranslator.db` |
| `Speaker diarization requires a HuggingFace token` | `HfToken` empty/not cached | Set `HfToken` in `appsettings.json` and re-run the install script, or set `EnableVoiceMarks` to `false` for plain transcription |
| `Could not download 'pyannote/segmentation-3.0' model` (or `speaker-diarization-3.1`), even though the token is valid | Only one of the two gated model pages was accepted — see the note in Step 3 | Visit and accept terms at **both** https://huggingface.co/pyannote/speaker-diarization-3.1 and https://huggingface.co/pyannote/segmentation-3.0 with the account that owns the token |
| `AssertionError: Torch not compiled with CUDA enabled` after a previously-working GPU install | An unpinned `pip install whisperx` (or its `pyannote-audio`/`nvidia-cudnn-cu12` dependencies) upgraded, silently replacing the CUDA-pinned PyTorch with a CPU-only build from PyPI | Re-run `install-cuda.bat` — it now pins `whisperx==3.4.2`, `pyannote-audio==3.4.0`, `speechbrain==1.0.3`, `nvidia-cudnn-cu12==8.9.7.29` for exactly this reason. If a venv is already broken this way, it's usually faster to delete it and re-run the install script fresh than to fix packages one at a time |
| `Could not locate cudnn_ops_infer64_8.dll. Please make sure it is in your library path!` | cuDNN 9 got installed instead of the 8.x line ctranslate2 needs, or (if the DLL is actually present) the venv's `nvidia-*-cu12` package folders aren't on `PATH` for this process | `tool_wavToVttVoiceMark.py` adds those folders to `PATH` automatically at startup — if you still see this, re-run `install-cuda.bat` to restore the pinned `nvidia-cudnn-cu12==8.9.7.29` |
| HuggingFace login step crashes with `UnicodeEncodeError: 'charmap' codec can't encode characters` | The deprecated `huggingface-cli login` prints a warning with an emoji that the default Windows `cp1252` console can't render | Already fixed in the install scripts (they use `hf auth login` with `PYTHONUTF8=1`) — re-run the install script if you're on an older copy |
