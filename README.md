# RB.VideoTranslator

A .NET 10 CLI tool and reusable BLL library that ingests video files and runs them through a fully automated dubbing pipeline: subtitle extraction → GPT-4o-mini translation → Azure Neural TTS synthesis → Demucs voice removal → audio mix → final video mux with embedded subtitles and multiple audio tracks.

---

## Setup overview

> **Do these steps in order — the install scripts read from `appsettings.json`, so configure it first.**

1. [Edit `appsettings.json`](#1-configure-appssettingsjson) — set your working folder and Azure credentials
2. [Run an install script](#2-run-the-install-script) — installs ffmpeg, Python packages, creates the venv in your working folder
3. [Build and run](#3-build-and-run)

See **[how-to-start.md](how-to-start.md)** for the full step-by-step guide including system dependencies.

---

## Architecture

```
RB.VideoTranslator.slnx
├── RB.VideoTranslator.Data/    ← EF Core entities, DbContext, repositories
├── RB.VideoTranslator.BLL/     ← Business logic, pipeline services, DI extension (NuGet packable)
├── RB.VideoTranslator.CLI/     ← Console entry-point, CLI options, appsettings.json support
└── RB.VideoTranslator.Tests/   ← xUnit unit tests (BLL + Data layers)
```

### NuGet package

`RB.VideoTranslator.BLL` is published as a NuGet package for use in other hosts (e.g. a background service or web API):

```csharp
services.AddRBVideoTranslator(configuration, o =>
{
    o.AzureSubscriptionKey = "...";
    // override individual values programmatically
});
```

Pack locally:
```bat
dotnet pack RB.VideoTranslator.BLL\RB.VideoTranslator.BLL.csproj --output nupkg
```

---

## 1. Configure appsettings.json

`appsettings.json` lives next to `RB.VideoTranslator.CLI.exe` (or inside `RB.VideoTranslator.CLI\` in source). Fill in your values **before** running the install scripts.

```json
{
  "RBVideoTranslator": {
    "WorkingFolderPath": "C:\\VideoTranslator",
    "VenvPath": "",
    "FfmpegPath": "ffmpeg",
    "PythonPath": "python",
    "DemucsPath": "python",
    "AzureSubscriptionKey": "<your-key>",
    "AzureEndpointUrl": "https://<resource>.cognitiveservices.azure.com/",
    "AzureOpenAiEndpoint": "https://<resource>.services.ai.azure.com/",
    "AzureOpenAiDeployment": "gpt-4o-mini",
    "TranslationTargetLanguages": [ "Bulgarian" ],
    "OutputFolderPath": "",
    "UseFemaleVoice": false
  }
}
```

| Field | Required | Notes |
|-------|----------|-------|
| `WorkingFolderPath` | **yes** | Root folder; `input`, `processing`, `output` subfolders are created automatically. The CLI always runs with this as its working directory. |
| `VenvPath` | auto | Filled in automatically by the install script (`<WorkingFolderPath>\rb.video.translator`). Leave empty before first run. |
| `AzureSubscriptionKey` | **yes** | Azure Cognitive Services key — used for both Speech TTS and OpenAI. |
| `AzureEndpointUrl` | **yes** | Azure Speech endpoint URL. |
| `AzureOpenAiEndpoint` | **yes** | Azure AI Services root URL (no `/openai/v1` suffix). |
| `TranslationTargetLanguages` | | Comma-separated languages. Voices are selected automatically. |
| `OutputFolderPath` | | Overrides the default `<WorkingFolderPath>\output`. |

All values can be overridden at run-time with CLI arguments (see [All options](#all-options)).

---

## 2. Run the install script

From the repository root, choose the script that matches your hardware:

| Script | When to use |
|--------|-------------|
| `scripts\install-cuda.bat` | NVIDIA GPU — installs PyTorch 2.5.1 with CUDA 12.4 (5–10× faster) |
| `scripts\install-cpu.bat`  | No GPU — installs CPU-only PyTorch 2.5.1 |

The script will:
1. Read `WorkingFolderPath` from `appsettings.json`
2. Create the `input`, `processing`, `output` subfolders
3. Install ffmpeg via winget
4. Create a Python 3.12 virtual environment at `<WorkingFolderPath>\rb.video.translator`
5. Install PyTorch, Demucs, faster-whisper (and CUDA runtime libs if CUDA)
6. Write `VenvPath` back into `appsettings.json` automatically

After this step `appsettings.json` will have `VenvPath` filled and no further CLI flags are needed.

---

## 3. Build and run

```bat
dotnet build --configuration Release
```

Place video files (`.mp4`, `.mkv`, `.avi`, `.mov`, `.webm`) in `<WorkingFolderPath>\input`, then run:

```bat
RB.VideoTranslator.CLI\bin\Release\net10.0\RB.VideoTranslator.CLI.exe
```

No CLI arguments are required when `appsettings.json` is fully configured.

---

## Pipeline

```
WorkingFolderPath\input
    │
    ▼
[SeparatingMedia]       ffmpeg — extract audio WAV + produce silent MP4
    │
    ▼
[ExtractingSrt]         Whisper — transcribe audio → SRT subtitle file
    │
    ▼
[RemovingVoice]         Demucs htdemucs — separate vocals from music bed → no_vocals.flac
    │
    ▼  ┌─────────────────── repeated for each target language ───────────────────┐
[TranslatingSrt]        GPT-4o-mini — translate SRT (50 entries/call)            │
    │  │                                                                          │
[SynthesisingAzureTts]  Azure Neural TTS — synthesise translated SRT → WAV       │
                         • Per-entry synthesis, one API call per subtitle entry   │
                         • <prosody rate> adjusts speed to fit each window        │
                         • Absolute-timestamp leading silence keeps sync          │
                         • Retries up to 3 times on transient SDK timeouts        │
    │  │                                                                          │
[MixingAudio]           ffmpeg amix — blend no_vocals + TTS WAV                  │
    │  └───────────────────────────────────────────────────────────────────────── ┘
    ▼
[AddingToVideo]         ffmpeg — mux silent MP4 + original + all language tracks
                         + all translated SRTs as soft subtitles → OutputFolderPath
    │
    ▼
Completed  ✓
```

---

## Services

### Data layer (`RB.VideoTranslator.Data`)

| Type | Purpose |
|------|---------|
| `VideoJob` | Root entity — tracks all file paths and current pipeline state |
| `JobState` (enum) | Full state machine from `Queued` to `Completed` / `Failed` |
| `AppDbContext` | EF Core SQLite context |
| `IVideoJobRepository` / `VideoJobRepository` | CRUD + resumable-job query |

### BLL (`RB.VideoTranslator.BLL`)

| Service | Purpose |
|---------|---------|
| `IJobService` / `JobService` | Move file to processing folder, create job record, transition states |
| `IMediaSeparatorService` / `MediaSeparatorService` | ffmpeg — extract audio + produce silent video |
| `ISrtExtractorService` / `SrtExtractorService` | Whisper — transcribe audio to SRT |
| `IVoiceRemoverService` / `VoiceRemoverService` | Demucs — separate vocals from music bed |
| `ISrtTranslatorService` / `SrtTranslatorService` | GPT-4o-mini — translate SRT to target language |
| `ISrtToAzureTtsService` / `SrtToAzureTtsService` | Azure TTS — synthesise WAV from translated SRT |
| `IAudioMixerService` / `AudioMixerService` | ffmpeg amix — blend no_vocals + TTS audio |
| `IVideoMuxerService` / `VideoMuxerService` | ffmpeg — mux video + all audio tracks + embedded subtitles |
| `IPipelineOrchestrator` / `PipelineOrchestrator` | Drives the state machine; resets interrupted jobs on restart |
| `IAzureSpeechEngine` / `AzureSpeechEngine` | Azure Speech SDK wrapper (injectable for testing) |
| `IAzureChatEngine` / `AzureChatEngine` | Azure OpenAI `ChatClient` wrapper (injectable for testing) |
| `IProcessRunner` / `DefaultProcessRunner` | `System.Diagnostics.Process` abstraction |
| `IFileSystem` / `PhysicalFileSystem` | File I/O abstraction |

---

## All options

Every option can come from `appsettings.json` (preferred) or be overridden on the CLI. Nothing is required on the command line when the config file is complete.

| CLI option | appsettings.json key | Default | Description |
|------------|----------------------|---------|-------------|
| `--work-folder` | `WorkingFolderPath` | *(required)* | Root folder for all pipeline data |
| `--azure-key` | `AzureSubscriptionKey` | *(required)* | Azure Cognitive Services key |
| `--azure-endpoint` | `AzureEndpointUrl` | *(required)* | Azure Speech endpoint URL |
| `--openai-endpoint` | `AzureOpenAiEndpoint` | *(required)* | Azure AI Services root URL |
| `--venv` | `VenvPath` | auto | Python venv path (auto-set by install script) |
| `--ffmpeg` | `FfmpegPath` | `ffmpeg` | ffmpeg executable |
| `--python` | `PythonPath` | `python` | Python executable (Whisper) |
| `--demucs` | `DemucsPath` | `python` | Python executable (Demucs) |
| `--openai-deployment` | `AzureOpenAiDeployment` | `gpt-4o-mini` | Azure OpenAI deployment name |
| `--target-lang` | `TranslationTargetLanguages` | `Bulgarian` | Comma-separated target languages |
| `--female` | `UseFemaleVoice` | `false` | Use female Azure TTS voice |

> **Voices are selected automatically** per language. The mapping lives in `PipelineOrchestrator.VoiceMap` — add an entry there to support additional languages.

---

## Crash recovery

The orchestrator tracks which pipeline state is "stable" (fully committed to the database). If the process is killed mid-step, the job resets to its preceding stable state on the next run and retries cleanly. For multi-language jobs, each language's result is saved immediately after it completes — only the interrupted language is retried, not the whole batch.

---

## Testing

```bash
dotnet test RB.VideoTranslator.slnx
dotnet test RB.VideoTranslator.slnx --verbosity normal
```

### Test coverage (141 tests)

| Area | File | What is tested |
|------|------|----------------|
| `JobService` | `BLL/JobServiceTests.cs` | File move, job creation, state transitions, error paths |
| `MediaSeparatorService` | `BLL/MediaSeparatorServiceTests.cs` | Path building, ffmpeg args, state update, missing-output error |
| `SrtExtractorService` | `BLL/SrtExtractorServiceTests.cs` | Whisper invocation, output path, state transition |
| `SrtTranslatorService` | `BLL/SrtTranslatorServiceTests.cs` | GPT chunking (50/call), system prompt contains target language, output path, state transition |
| `SrtToAzureTtsService` | `BLL/SrtToAzureTtsServiceTests.cs` | Per-entry SSML, `<prosody rate>` logic, silence padding, WAV concatenation, retry on failure, silence fallback |
| `VoiceRemoverService` | `BLL/VoiceRemoverServiceTests.cs` | Demucs args, output path detection, state transition |
| `AudioMixerService` | `BLL/AudioMixerServiceTests.cs` | ffmpeg amix args, language-specific output path, state transition, missing-output error |
| `VideoMuxerService` | `BLL/VideoMuxerServiceTests.cs` | Multi-stream ffmpeg args, apad filter, Original/language metadata, subtitle codec (MP4/MKV), output folder |
| `VideoJobRepository` | `Data/VideoJobRepositoryTests.cs` | CRUD, state filtering, `UpdatedAt` timestamp |

---

## Database schema

```
VideoJobs
  Id                    GUID   PK
  OriginalFileName      TEXT
  InputFilePath         TEXT
  ProcessingFolderPath  TEXT
  ProcessingVideoPath   TEXT   after SeparatingMedia
  ExtractedAudioPath    TEXT   after SeparatingMedia
  SilentVideoPath       TEXT   after SeparatingMedia
  SrtFilePath           TEXT   after ExtractingSrt
  VoiceRemovedAudioPath TEXT   after RemovingVoice
  TranslatedSrtFilePath TEXT   after TranslatingSrt  (last language processed)
  AzureTtsAudioPath     TEXT   after SynthesisingAzureTts  (last language processed)
  MixedAudioPath        TEXT   after MixingAudio  (last language processed)
  LanguageResultsJson   TEXT   JSON array of {Language, MixedAudioPath, TranslatedSrtFilePath}
  OutputFilePath        TEXT   after AddingToVideo
  State                 TEXT   enum stored as string
  ErrorMessage          TEXT
  CreatedAt             TEXT
  UpdatedAt             TEXT
```

---

## CI

GitHub Actions runs on every push and pull request to `master`/`main`:

```
.github/workflows/ci.yml
  restore → build (Release) → test → upload test results (.trx)
```
