# How to Start

Step-by-step setup guide for RB.VideoTranslator on Windows, Linux, and macOS.

> **Important:** Configure `appsettings.json` **before** running the install scripts — the scripts read the working folder path from that file.

> **Cross-platform notes:** Steps that differ by OS show a Windows and a Linux/macOS variant. The install scripts (`scripts\install.bat` / `scripts/install.sh`) auto-detect NVIDIA/CUDA hardware and install the matching PyTorch build automatically — there's no separate CUDA/CPU script to choose.

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
The install scripts require Python 3.12 specifically.

**Windows:** Download from https://www.python.org/downloads/ and check **"Add Python to PATH"** during install (the script looks for it via `py -3.12`).

**macOS:** `brew install python@3.12` (the script looks for `python3.12` on `PATH`).

**Linux (Debian/Ubuntu 24.04+):** `sudo apt install python3.12 python3.12-venv`

**Ubuntu 22.04/20.04:** these releases don't ship Python 3.12 in the default repos (only 24.04+ does) — `apt install python3.12` fails with *"has no installation candidate"*. Add the [deadsnakes PPA](https://launchpad.net/~deadsnakes/+archive/ubuntu/ppa) first:
```bash
sudo apt update
sudo apt install software-properties-common
sudo add-apt-repository ppa:deadsnakes/ppa
sudo apt update
sudo apt install python3.12 python3.12-venv
```

### Homebrew (macOS only)
Install from https://brew.sh if not already present — the install script uses it to install ffmpeg.

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

With `appsettings.json` saved, run the install script for your OS **from the repository root**. It **auto-detects NVIDIA/CUDA hardware** (via `nvidia-smi`) and installs the matching PyTorch build — you don't need to pick CUDA vs. CPU yourself.

**Windows:**
```bat
scripts\install.bat
```

**Linux / macOS:**
```bash
chmod +x scripts/install.sh   # first run only
scripts/install.sh
```

To override the auto-detected hardware (e.g. right after installing or removing a GPU driver), pass `--cuda` or `--cpu`:

```bat
scripts\install.bat --cuda
scripts\install.bat --cpu
```

```bash
scripts/install.sh --cuda   # Linux only — macOS has no CUDA support
scripts/install.sh --cpu
```

With CUDA detected, it installs PyTorch 2.5.1 with CUDA 12.4, Demucs, and faster-whisper with CUDA runtime libs — Whisper and Demucs run **5–10× faster** with a GPU. Without it, the same steps run with CPU-only PyTorch; processing is slower — expect several minutes per minute of audio on a modern CPU.

> **Driver requirement (CUDA):** CUDA 12.4 needs Game Ready 550+ / Studio 555+ drivers (Linux: equivalent proprietary NVIDIA driver).
> Works on newer CUDA runtimes (12.6, 12.8) — CUDA is backward-compatible.
> PyTorch is pinned to 2.5.1 because 2.6+ requires `torchcodec`, which has no Windows build.

> macOS never installs a CUDA build — there's no NVIDIA/CUDA support on that platform. It always installs CPU PyTorch (from the default PyPI index, which carries the macOS/MPS wheels).

Package versions and pinning rationale are centralized in `scripts/dependencies.json` — both scripts read from it, so there is one place to update pins rather than two.

---

The script will:
1. Read `WorkingFolderPath` from `appsettings.json`
2. Create the `input`, `processing`, `output` subfolders there
3. Install ffmpeg (winget on Windows, Homebrew on macOS, apt on Linux)
4. Create a Python 3.12 venv at `<WorkingFolderPath>/rb.video.translator`
5. Install all Python packages (PyTorch, Demucs, WhisperX, librosa)
6. Cache the `HfToken` login for speaker diarization, if you set one in Step 3
7. Write `VenvPath` back into `appsettings.json` automatically

To remove the environment later, run `scripts\uninstall.bat` (Windows) or `scripts/uninstall.sh` (Linux/macOS).

---

## Step 5 — Verify the environment

**Windows:**
```bat
"C:\VideoTranslator\rb.video.translator\Scripts\activate"
```

**Linux / macOS:**
```bash
source "$HOME/VideoTranslator/rb.video.translator/bin/activate"
```

Then, on either platform:
```bash
python -c "import whisperx; print('WhisperX OK')"
python -c "import librosa; print('librosa OK')"
python -c "import demucs; print('Demucs OK')"
python -c "import torch; print('CUDA available:', torch.cuda.is_available())"
ffmpeg -version
```

`CUDA available: True` confirms GPU acceleration is active (CUDA option only — always `False` on macOS).

---

## Step 6 — Build the CLI

From the repository root:

```bash
dotnet build --configuration Release
```

The binary will be at:

**Windows:**
```
RB.VideoTranslator.CLI\bin\Release\net10.0\RB.VideoTranslator.CLI.exe
```
Copy `appsettings.json` next to the exe if you intend to run it from outside the source tree:
```bat
copy RB.VideoTranslator.CLI\appsettings.json RB.VideoTranslator.CLI\bin\Release\net10.0\
```

**Linux / macOS:**
```
RB.VideoTranslator.CLI/bin/Release/net10.0/RB.VideoTranslator.CLI.dll
```
Run it with `dotnet RB.VideoTranslator.CLI/bin/Release/net10.0/RB.VideoTranslator.CLI.dll`. Copy `appsettings.json` alongside it if running from outside the source tree:
```bash
cp RB.VideoTranslator.CLI/appsettings.json RB.VideoTranslator.CLI/bin/Release/net10.0/
```

Or run directly without a separate build step, on any platform:
```bash
dotnet run --project RB.VideoTranslator.CLI --configuration Release
```

---

## Step 7 — First run

Place video files (`.mp4`, `.mkv`, `.avi`, `.mov`, `.webm`) in `<WorkingFolderPath>/input`, then run:

**Windows:**
```bat
RB.VideoTranslator.CLI\bin\Release\net10.0\RB.VideoTranslator.CLI.exe
```

**Linux / macOS:**
```bash
dotnet RB.VideoTranslator.CLI/bin/Release/net10.0/RB.VideoTranslator.CLI.dll
```

No CLI arguments are needed when `appsettings.json` is fully configured.

### Passing values via CLI (optional overrides)

Any value from `appsettings.json` can be overridden on the command line:

```bat
:: Windows
RB.VideoTranslator.CLI.exe ^
  --work-folder    "C:\videos"             ^
  --azure-key      "<speech-key>"          ^
  --azure-endpoint "https://..."           ^
  --openai-endpoint "https://..."          ^
  --target-lang    "Bulgarian,German"
```

```bash
# Linux / macOS
dotnet RB.VideoTranslator.CLI.dll \
  --work-folder    "/home/user/videos"     \
  --azure-key      "<speech-key>"          \
  --azure-endpoint "https://..."           \
  --openai-endpoint "https://..."          \
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
The orchestrator reads the last committed state from the SQLite database (`<WorkingFolderPath>/videotranslator.db`) and resumes from where it stopped. For multi-language jobs, already-completed languages are skipped — only the interrupted language is retried.

---

## Common issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| `WorkingFolderPath` missing error | Config not set | Edit `appsettings.json` and set `WorkingFolderPath` |
| `Python not found` | Python not on PATH | Re-install with "Add to PATH", or set `PythonPath` in `appsettings.json` |
| `ffmpeg not found` | ffmpeg not on PATH | Re-run install script, or set `FfmpegPath` in `appsettings.json` |
| `CUDA available: False` | Wrong PyTorch build or old driver | Re-run the install script with `--cuda` (`install.bat --cuda` / `install.sh --cuda`); update NVIDIA driver. Expected on macOS — no CUDA support there |
| Azure TTS timeout errors | Transient SDK issue | Automatically retried up to 3 times; silence is substituted if all fail |
| `No voice configured for language 'X'` | Language not in voice map | Add an entry to `PipelineOrchestrator.VoiceMap` |
| Job stuck after restart | Unexpected state in DB | Check `ErrorMessage` column in `videotranslator.db` |
| `Speaker diarization requires a HuggingFace token` | `HfToken` empty/not cached | Set `HfToken` in `appsettings.json` and re-run the install script, or set `EnableVoiceMarks` to `false` for plain transcription |
| `Could not download 'pyannote/segmentation-3.0' model` (or `speaker-diarization-3.1`), even though the token is valid | Only one of the two gated model pages was accepted — see the note in Step 3 | Visit and accept terms at **both** https://huggingface.co/pyannote/speaker-diarization-3.1 and https://huggingface.co/pyannote/segmentation-3.0 with the account that owns the token |
| `AssertionError: Torch not compiled with CUDA enabled` after a previously-working GPU install | An unpinned `pip install whisperx` (or its `pyannote-audio`/`nvidia-cudnn-cu12` dependencies) upgraded, silently replacing the CUDA-pinned PyTorch with a CPU-only build from PyPI | Re-run the install script with `--cuda` — `scripts/dependencies.json` pins `whisperx==3.4.2`, `pyannote-audio==3.4.0`, `speechbrain==1.0.3`, `nvidia-cudnn-cu12==8.9.7.29` for exactly this reason. If a venv is already broken this way, it's usually faster to delete it (`uninstall.bat` / `uninstall.sh`) and re-run the install script fresh than to fix packages one at a time |
| `Could not locate cudnn_ops_infer64_8.dll. Please make sure it is in your library path!` (Windows/CUDA) | cuDNN 9 got installed instead of the 8.x line ctranslate2 needs, or (if the DLL is actually present) the venv's `nvidia-*-cu12` package folders aren't on `PATH` for this process | `tool_wavToVttVoiceMark.py` adds those folders to `PATH` automatically at startup — if you still see this, re-run `install.bat --cuda` to restore the pinned `nvidia-cudnn-cu12==8.9.7.29` |
| HuggingFace login step crashes with `UnicodeEncodeError: 'charmap' codec can't encode characters` (Windows only) | The deprecated `huggingface-cli login` prints a warning with an emoji that the default Windows `cp1252` console can't render | Already fixed in `install.bat` (it uses `hf auth login` with `PYTHONUTF8=1`) — re-run the install script if you're on an older copy |
| `ImportError: libctranslate2-*.so.*: cannot enable executable stack as shared object requires: Invalid argument` (Linux only) | glibc 2.41+ (e.g. Ubuntu 24.10+ or other rolling-release distros) refuses to load ctranslate2's shared library, which is built requesting an executable stack | `install.sh` now clears the flag automatically with `patchelf --clear-execstack` after installing WhisperX. For an already-broken venv, run: `sudo apt install patchelf` then `find <VenvPath> -iname 'libctranslate2*.so*' -exec patchelf --clear-execstack {} \;` — no reinstall needed. [OpenNMT/CTranslate2#1849](https://github.com/OpenNMT/CTranslate2/issues/1849) |
| `ImportError: libcudnn.so.9: cannot open shared object file: No such file or directory` on `import torch` (Linux + CUDA only) | torch's CUDA build hard-links cuDNN 9 at import time on Linux (unlike Windows, which delay-loads it), but ctranslate2 `<4.5.0` (whisperx 3.4.2's default) needs cuDNN 8 — the two can't share one `nvidia-cudnn-cu12` install | `install.sh` handles this automatically: it skips the cuDNN-8 downgrade on Linux and instead force-upgrades `ctranslate2` to `4.5.0` (adds cuDNN 9 support) after WhisperX installs. If you hit this on an already-broken venv, activate it and run `pip install ctranslate2==4.5.0`. [whisperX#1158](https://github.com/m-bain/whisperX/issues/1158), [whisperX#954](https://github.com/m-bain/whisperX/issues/954) |
| `Unable to load any of {libcudnn_cnn.so.9...}` / `Invalid handle. Cannot load symbol cudnnCreateConvolutionDescriptor`, process exits with code 134 (Linux + CUDA only) | cuDNN 9 splits its backend into several `.so` files; `libcudnn.so.9` itself `dlopen()`s the one it needs *by filename* the first time a matching op runs (e.g. pyannote's VAD during diarization). That internal `dlopen()` only searches `LD_LIBRARY_PATH` — and glibc snapshots that variable once at process start, so the venv having the right files (`site-packages/nvidia/cudnn/lib/`) isn't enough if the *launching* process didn't set it | Fixed at the process-spawn layer: `DefaultProcessRunner` now sets `LD_LIBRARY_PATH` to the venv's `site-packages/nvidia/*/lib` directories before starting `python` on Linux (this can't be done from inside the `.py` scripts the way the Windows `PATH` fix is — see the comment on `BuildLinuxCudaLibraryPath` in `DefaultProcessRunner.cs`). Rebuild (`dotnet build`) to pick it up. [whisperX#1297](https://github.com/m-bain/whisperX/issues/1297) |
| `TypeError: hf_hub_download() got an unexpected keyword argument 'use_auth_token'` while loading the diarization pipeline | `huggingface_hub` wasn't pinned, so an unpinned install grabs the latest release. `huggingface_hub` 1.0.0 (Oct 2025) removed the deprecated `use_auth_token` kwarg entirely, but `pyannote-audio==3.4.0` still calls `hf_hub_download(..., use_auth_token=...)` internally | `dependencies.json` now pins `huggingface_hub==0.36.0` (the last 0.x release, which still supports the deprecated kwarg) in `common`. Re-run the install script, or on an already-broken venv: `pip install huggingface_hub==0.36.0`. [huggingface_hub#1094](https://github.com/huggingface/huggingface_hub/issues/1094) |
