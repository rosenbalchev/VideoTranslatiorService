# How to Start

Step-by-step setup guide for the AI Video Translator Service on Windows.

---

## 1. Install system dependencies

### .NET 10 SDK
Download and install from https://dotnet.microsoft.com/download

### ffmpeg
1. Download a Windows build from https://ffmpeg.org/download.html (the "Windows builds by BtbN" releases are convenient)
2. Extract to a permanent folder, e.g. `C:\tools\ffmpeg\bin`
3. Add that folder to your system `PATH`, **or** pass `--ffmpeg "C:\tools\ffmpeg\bin\ffmpeg.exe"` on every CLI run

### Python 3.11
Download from https://www.python.org/downloads/
- During install, check **"Add Python to PATH"**
- Python 3.9–3.12 all work; 3.11 is recommended for best compatibility with Whisper and Demucs

### Azure CLI
Required for authenticating with Azure OpenAI (translation step).
Download from https://learn.microsoft.com/en-us/cli/azure/install-azure-cli-windows

---

## 2. Set up the Python virtual environment

Whisper and Demucs are large ML libraries. A virtual environment keeps them isolated and makes it easy to pass the whole environment to the CLI with a single `--venv` flag.

### Option A — CUDA (NVIDIA GPU) — recommended

If you have an NVIDIA GPU, CUDA acceleration makes Whisper transcription and Demucs separation **5–10x faster**.

Run the CUDA install script from the repository root:

```bat
scripts\install-cuda.bat
```

This will:
1. Create a virtual environment named `bgtts-env` in the current directory
2. Install Whisper (`openai-whisper`)
3. Install PyTorch with CUDA 12.1 support
4. Install Demucs

> **Driver version:** the script targets CUDA 12.1 (`cu121`). If your NVIDIA driver is newer,
> check https://pytorch.org/get-started/locally/ for the matching `--index-url` and edit the
> script accordingly (e.g. `cu124` for CUDA 12.4).

---

### Option B — CPU only

For machines without an NVIDIA GPU:

```bat
scripts\install-cpu.bat
```

Same steps as above, but installs the CPU-only PyTorch build. Processing will be slower —
expect Demucs to take several minutes per minute of audio on a modern CPU.

---

## 3. Verify the environment

```bat
bgtts-env\Scripts\activate

python -c "import whisper; print('Whisper OK')"
python -c "import demucs; print('Demucs OK')"
python -c "import torch; print('CUDA available:', torch.cuda.is_available())"
```

`CUDA available: True` confirms GPU acceleration is active (CUDA option only).

---

## 4. Azure setup

### Speech TTS (required)
1. In the Azure Portal, create an **Azure AI Services** or **Cognitive Services — Speech** resource
2. Copy the **Key 1** value → `--azure-key`
3. Copy the **Endpoint** URL → `--azure-endpoint`  
   Example: `https://my-resource.cognitiveservices.azure.com/`

### OpenAI translation (required)
1. Create an **Azure AI Services** resource with a **GPT-4o-mini** deployment
2. Copy the resource's root URL → `--openai-endpoint`  
   Example: `https://my-resource.services.ai.azure.com/`
3. Log in with the Azure CLI — the app uses `DefaultAzureCredential`, so no API key is needed:
   ```bat
   az login
   ```
   Your logged-in identity is used automatically. For unattended/server runs, set the
   `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_CLIENT_SECRET` environment variables
   for a service principal instead.

---

## 5. Build the CLI

From the repository root:

```bat
dotnet build --configuration Release
```

The binary will be at:
```
VideoTranslatorService.CLI\bin\Release\net10.0\VideoTranslatorService.CLI.exe
```

Or run directly without a separate build step:

```bat
dotnet run --project VideoTranslatorService.CLI --configuration Release -- [options]
```

---

## 6. First run

Place video files (`.mp4`, `.mkv`, `.avi`, `.mov`, `.webm`) in your input folder, then run:

```bat
VideoTranslatorService.CLI\bin\Release\net10.0\VideoTranslatorService.CLI.exe ^
  --input-folder      "C:\videos\input"               ^
  --processing-folder "C:\videos\processing"          ^
  --output-folder     "C:\videos\output"              ^
  --db                "C:\videos\videotranslator.db"  ^
  --azure-key         "<speech-subscription-key>"     ^
  --azure-endpoint    "https://<resource>.cognitiveservices.azure.com/" ^
  --openai-endpoint   "https://<resource>.services.ai.azure.com/"      ^
  --venv              bgtts-env                        ^
  --target-lang       "Bulgarian"
```

### Multiple target languages

Separate languages with a comma. The voice for each language is selected automatically:

```bat
  --target-lang "Bulgarian,German"
```

Supported languages and their Azure Neural voices are listed in `PipelineOrchestrator.VoiceMap`
inside `VideoTranslatorService.BLL/Services/PipelineOrchestrator.cs`.

---

## 7. Crash recovery

If the process is interrupted (power loss, Ctrl+C, crash), just run the same command again.
The orchestrator reads the last committed state from the SQLite database and resumes from
where it stopped. For multi-language jobs, already-completed languages are skipped automatically —
only the interrupted language is retried.

---

## 8. Common issues

| Symptom | Cause | Fix |
|---------|-------|-----|
| `Python not found` | Python not on PATH | Re-install with "Add to PATH", or use `--venv` |
| `ffmpeg not found` | ffmpeg not on PATH | Add to PATH or use `--ffmpeg` |
| `CUDA available: False` | Wrong PyTorch build or driver too old | Re-run `install-cuda.bat`; update NVIDIA driver |
| Azure TTS timeout errors | Transient SDK issue | Automatically retried up to 3 times; silence is substituted if all fail |
| `No voice configured for language 'X'` | Language not in voice map | Add an entry to `PipelineOrchestrator.VoiceMap` |
| Job stuck after restart | State in DB doesn't match a resumable state | Check `ErrorMessage` column in the DB |
