# RB.VideoTranslator

A .NET 10 CLI tool and reusable Core library that ingests video files and runs them through a fully automated dubbing pipeline: subtitle extraction → GPT-4o-mini translation → Azure Neural TTS synthesis → Demucs voice removal → audio mix → final video mux with embedded subtitles and multiple audio tracks.

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
├── RB.VideoTranslator.Domain/  ← Enums, consts, interfaces, DB entities (DBOs), public models (NuGet packable)
├── RB.VideoTranslator.Data/    ← EF Core DbContext, repository implementations
├── RB.VideoTranslator.Core/    ← Business logic, pipeline services, DI extension (NuGet packable)
├── RB.VideoTranslator.CLI/     ← Console entry-point, CLI options, appsettings.json support
└── RB.VideoTranslator.Tests/   ← xUnit unit tests (Core + Data layers)
```

### NuGet package

`RB.VideoTranslator.Core` (with its `RB.VideoTranslator.Domain` dependency) is published as a NuGet package for use in other hosts (e.g. a background service or web API):

```csharp
services.AddRBVideoTranslator(configuration, o =>
{
    o.AzureSubscriptionKey = "...";
    // override individual values programmatically
});
```

Pack locally:
```bat
dotnet pack RB.VideoTranslator.Domain\RB.VideoTranslator.Domain.csproj --output nupkg
dotnet pack RB.VideoTranslator.Core\RB.VideoTranslator.Core.csproj --output nupkg
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
    "HfToken": "",
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
| `HfToken` | | HuggingFace token for speaker diarization + gender-estimate comments in the VTT. See [HuggingFace token setup](how-to-start.md#huggingface-speaker-diarization). Leave empty to skip diarization (plain transcription still works). |
| `AzureSubscriptionKey` | **yes** | Azure Cognitive Services key — used for both Speech TTS and OpenAI. |
| `AzureEndpointUrl` | **yes** | Azure Speech endpoint URL. |
| `AzureOpenAiEndpoint` | **yes** | Azure AI Services root URL (no `/openai/v1` suffix). |
| `TranslationTargetLanguages` | | Comma-separated languages. Voices are selected automatically. |
| `OutputFolderPath` | | Overrides the default `<WorkingFolderPath>\output`. |

All values can be overridden at run-time with CLI arguments (see [All options](#all-options)).

---

## 2. Run the install script

From the repository root, run the script for your OS. Both scripts **auto-detect NVIDIA/CUDA hardware** (`nvidia-smi`) and install the matching PyTorch build — no need to choose manually. Pass `--cuda` or `--cpu` to force a build regardless of what's detected (e.g. right after installing/removing a GPU driver).

| Platform | Script |
|----------|--------|
| Windows | `scripts\install.bat` |
| Linux / macOS | `scripts/install.sh` |

```bat
:: Windows
scripts\install.bat            REM auto-detect
scripts\install.bat --cuda     REM force CUDA build
scripts\install.bat --cpu      REM force CPU build
```

```bash
# Linux / macOS
scripts/install.sh             # auto-detect
scripts/install.sh --cuda      # force CUDA build (Linux only — macOS has no CUDA)
scripts/install.sh --cpu       # force CPU build
```

Package versions and pinning rationale live in one place: `scripts/dependencies.json`. Both scripts read from it, so there's a single source of truth instead of duplicated pins.

The script will:
1. Read `WorkingFolderPath` from `appsettings.json`
2. Create the `input`, `processing`, `output` subfolders
3. Install ffmpeg (winget on Windows, Homebrew on macOS, apt on Linux)
4. Create a Python 3.12 virtual environment at `<WorkingFolderPath>/rb.video.translator`
5. Install PyTorch, Demucs, WhisperX + librosa (and CUDA runtime libs if CUDA)
6. Cache the `HfToken` login for speaker diarization, if set
7. Write `VenvPath` back into `appsettings.json` automatically

After this step `appsettings.json` will have `VenvPath` filled and no further CLI flags are needed.

To remove the environment later: `scripts\uninstall.bat` (Windows) or `scripts/uninstall.sh` (Linux/macOS).

---

## 3. Build and run

```bash
dotnet build --configuration Release
```

Place video files (`.mp4`, `.mkv`, `.avi`, `.mov`, `.webm`) in `<WorkingFolderPath>/input`, then run:

```bat
:: Windows
RB.VideoTranslator.CLI\bin\Release\net10.0\RB.VideoTranslator.CLI.exe
```

```bash
# Linux / macOS
dotnet RB.VideoTranslator.CLI/bin/Release/net10.0/RB.VideoTranslator.CLI.dll
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
[ExtractingVtt]         WhisperX — transcribe audio → WebVTT subtitle file
                         • Speaker diarization + pitch-based gender estimate,
                           written as `NOTE` comments above each cue (needs `HfToken`)
    │
    ▼
[RemovingVoice]         Demucs htdemucs — separate vocals from music bed → no_vocals.flac
    │
    ▼  ┌─────────────────── repeated for each target language ───────────────────┐
[TranslatingVtt]        GPT-4o-mini — translate VTT (50 entries/call)            │
    │  │                                                                          │
[SynthesisingAzureTts]  Azure Neural TTS — synthesise translated VTT → WAV       │
                         • Per-entry synthesis, one API call per subtitle entry   │
                         • <prosody rate> adjusts speed to fit each window        │
                         • Absolute-timestamp leading silence keeps sync          │
                         • Retries up to 3 times on transient SDK timeouts        │
    │  │                                                                          │
[MixingAudio]           ffmpeg amix — blend no_vocals + TTS WAV                  │
    │  └───────────────────────────────────────────────────────────────────────── ┘
    ▼
[AddingToVideo]         ffmpeg — mux silent MP4 + original + all language tracks
                         + all translated VTTs as soft subtitles → OutputFolderPath
    │
    ▼
Completed  ✓
```

---

## Services

### Domain (`RB.VideoTranslator.Domain`)

| Folder | Contents |
|--------|----------|
| `Enums` | `JobState` — full state machine from `Queued` to `Completed` / `Failed` |
| `Consts` | `PipelineOptionsDefaults` — `appsettings.json` section name |
| `Dbo` | `VideoJob` — root entity, tracks all file paths and current pipeline state. `VoicePaceStat` — per-voice Azure TTS speaking-pace stats, learned across sessions |
| `Models` | `PipelineOptions`, `LanguageResult` — externally visible pipeline configuration/results |
| `Exceptions` | `StepNotImplementedException` |
| `Interfaces` | All service contracts (`IJobService`, `IMediaSeparatorService`, `IVttExtractorService`, `IVoiceRemoverService`, `IVttTranslatorService`, `IVttToAzureTtsService`, `IAudioMixerService`, `IVideoMuxerService`, `IPipelineOrchestrator`, `IAzureSpeechEngine`, `IAzureChatEngine`, `IProcessRunner`, `IFileSystem`, `IVideoJobRepository`, `IVoicePaceRepository`, `IPipelineRunner`) |

### Data layer (`RB.VideoTranslator.Data`)

| Type | Purpose |
|------|---------|
| `AppDbContext` | EF Core SQLite context |
| `VideoJobRepository` | Implements `IVideoJobRepository` — CRUD + resumable-job query |
| `VoicePaceRepository` | Implements `IVoicePaceRepository` — accumulates/reads per-voice speaking-pace stats (`VoicePaceStats` table) |

### Core (`RB.VideoTranslator.Core`)

| Service | Purpose |
|---------|---------|
| `JobService` | Move file to processing folder, create job record, transition states |
| `MediaSeparatorService` | ffmpeg — extract audio + produce silent video |
| `VttExtractorService` | WhisperX — transcribe audio to VTT, with speaker diarization + gender-estimate `NOTE` comments |
| `VoiceRemoverService` | Demucs — separate vocals from music bed |
| `VttTranslatorService` | GPT-4o-mini — translate VTT to target language |
| `VttToAzureTtsService` | Azure TTS — synthesise WAV from translated VTT. Learns each voice's real chars/sec speaking pace from observed synthesis results (persisted via `IVoicePaceRepository`), so later entries — and later jobs — start closer to the correct prosody rate instead of relying on overrun retries every time |
| `AudioMixerService` | ffmpeg amix — blend no_vocals + TTS audio |
| `VideoMuxerService` | ffmpeg — mux video + all audio tracks + embedded subtitles |
| `PipelineOrchestrator` | Drives the state machine; resets interrupted jobs on restart |
| `AzureSpeechEngine` | Azure Speech SDK wrapper (injectable for testing) |
| `AzureChatEngine` | Azure OpenAI `ChatClient` wrapper (injectable for testing) |
| `DefaultProcessRunner` | `System.Diagnostics.Process` abstraction |
| `PhysicalFileSystem` | File I/O abstraction |

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
| *(none)* | `HfToken` | *(empty)* | HuggingFace token for speaker diarization; install-time only, cached into the venv's HF login — see [setup guide](how-to-start.md#huggingface-speaker-diarization) |
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

### Test coverage (266 tests)

| Area | File | What is tested |
|------|------|----------------|
| `JobService` | `Core/JobServiceTests.cs` | File move, job creation, state transitions, error paths |
| `MediaSeparatorService` | `Core/MediaSeparatorServiceTests.cs` | Path building, ffmpeg args, state update, missing-output error |
| `VttExtractorService` | `Core/VttExtractorServiceTests.cs` | Whisper invocation, output path, state transition |
| `VttTranslatorService` | `Core/VttTranslatorServiceTests.cs` | GPT chunking (50/call), system prompt contains target language, output path, state transition |
| `VttToAzureTtsService` | `Core/VttToAzureTtsServiceTests.cs` | Per-entry SSML, `<prosody rate>` logic, overrun-retry, silence padding, WAV concatenation, retry on failure, silence fallback, per-voice pace learning + blending, persisted-history seeding |
| `VoiceRemoverService` | `Core/VoiceRemoverServiceTests.cs` | Demucs args, output path detection, state transition |
| `AudioMixerService` | `Core/AudioMixerServiceTests.cs` | ffmpeg amix args, language-specific output path, state transition, missing-output error |
| `VideoMuxerService` | `Core/VideoMuxerServiceTests.cs` | Multi-stream ffmpeg args, apad filter, Original/language metadata, subtitle codec (MP4/MKV), output folder |
| `DefaultProcessRunner` | `Core/DefaultProcessRunnerTests.cs` | Linux CUDA library path resolution for subprocess `LD_LIBRARY_PATH` |
| `VideoJobRepository` | `Data/VideoJobRepositoryTests.cs` | CRUD, state filtering, `UpdatedAt` timestamp |
| `VoicePaceRepository` | `Data/VoicePaceRepositoryTests.cs` | Sample accumulation, per-voice tracking, invalid-sample rejection |

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
  VttFilePath           TEXT   after ExtractingVtt
  VoiceRemovedAudioPath TEXT   after RemovingVoice
  TranslatedVttFilePath TEXT   after TranslatingVtt  (last language processed)
  AzureTtsAudioPath     TEXT   after SynthesisingAzureTts  (last language processed)
  MixedAudioPath        TEXT   after MixingAudio  (last language processed)
  LanguageResultsJson   TEXT   JSON array of {Language, MixedAudioPath, TranslatedVttFilePath}
  OutputFilePath        TEXT   after AddingToVideo
  State                 TEXT   enum stored as string
  ErrorMessage          TEXT
  CreatedAt             TEXT
  UpdatedAt             TEXT

VoicePaceStats
  Voice           TEXT     PK — Azure voice name, e.g. "bg-BG-BorislavNeural"
  TotalChars      REAL     accumulated across every recorded sample
  TotalNaturalMs  REAL     accumulated natural-pace (rate=100%) duration; TotalChars / (TotalNaturalMs/1000) = learned chars/sec
  SampleCount     INTEGER
  LastRateUsed    REAL     prosody rate (%) used for the most recent sample — diagnostic only
  UpdatedAt       TEXT
```

---

## CI

GitHub Actions runs on every push and pull request to `master`/`main`:

```
.github/workflows/ci.yml
  restore → build (Release) → test → upload test results (.trx)
```
