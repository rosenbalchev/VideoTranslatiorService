# AI Video Translator Service

A .NET 10 CLI tool that ingests video files and runs them through a fully automated dubbing pipeline: subtitle extraction → GPT-4o-mini translation → Azure Neural TTS synthesis → Demucs voice removal → audio mix → final video mux with embedded subtitles and multiple audio tracks.

---

## Quick start

See **[how-to-start.md](how-to-start.md)** for full installation instructions, Python environment setup, and Azure configuration.

---

## Architecture

```
VideoTranslatiorService.slnx
├── VideoTranslatorService.Data/       ← EF Core entities, DbContext, repositories
├── VideoTranslatorService.BLL/        ← Business logic, pipeline services
├── VideoTranslatorService.CLI/        ← Console entry-point, DI wiring, CLI options
└── VideoTranslatorService.Tests/      ← xUnit unit tests (BLL + Data layers)
```

---

## Pipeline

```
Input folder
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
    ▼
[TranslatingSrt]        GPT-4o-mini — translate SRT to target language (50 entries/call)
    │  ↓ repeated for each target language
[SynthesisingAzureTts]  Azure Neural TTS — synthesise translated SRT → WAV
                         • Per-entry synthesis — one API call per subtitle entry
                         • <prosody rate> adjusts speed to fit each subtitle window
                         • Absolute-timestamp leading silence keeps sync across entries
                         • Retries up to 3 times on transient SDK timeouts
    │
[MixingAudio]           ffmpeg amix — blend no_vocals.flac + TTS WAV → mixed WAV
    │  ↑ end of per-language loop
    ▼
[AddingToVideo]         ffmpeg — mux silent MP4 + original audio + all language mixed tracks
                         + all translated SRTs as soft subtitles
                         Output → configurable output folder
    │
    ▼
Completed  ✓
```

---

## Services

### Data layer (`VideoTranslatorService.Data`)

| Type | Purpose |
|------|---------|
| `VideoJob` | Root entity — tracks all file paths and current pipeline state |
| `JobState` (enum) | Full state machine from `Queued` to `Completed` / `Failed` |
| `AppDbContext` | EF Core SQLite context |
| `IVideoJobRepository` / `VideoJobRepository` | CRUD + resumable-job query |

### BLL (`VideoTranslatorService.BLL`)

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

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | `net10.0` target |
| [ffmpeg](https://ffmpeg.org/download.html) | On `PATH` or via `--ffmpeg` |
| [Python 3.11](https://www.python.org/downloads/) | Required for Whisper and Demucs |
| Azure Cognitive Services (Speech) | TTS key + endpoint |
| Azure AI Services (OpenAI) | GPT-4o-mini deployment; uses `DefaultAzureCredential` — run `az login` first |

### Python environment

Use one of the provided scripts to create an isolated virtual environment with all dependencies:

| Script | When to use |
|--------|-------------|
| `scripts\install-cuda.bat` | NVIDIA GPU available — installs PyTorch with CUDA 12.1 (5–10× faster) |
| `scripts\install-cpu.bat`  | No GPU — installs CPU-only PyTorch |

Both scripts create a `bgtts-env` folder in the current directory. Pass `--venv bgtts-env` to the CLI to use it.

See [how-to-start.md](how-to-start.md) for detailed setup instructions.

---

## Usage

```bat
VideoTranslatorService.CLI.exe ^
  --input-folder      "C:\videos\input"               ^
  --processing-folder "C:\videos\processing"          ^
  --output-folder     "C:\videos\output"              ^
  --db                "C:\videos\videotranslator.db"  ^
  --azure-key         "<speech-subscription-key>"     ^
  --azure-endpoint    "https://<resource>.cognitiveservices.azure.com/" ^
  --openai-endpoint   "https://<resource>.services.ai.azure.com/"      ^
  --venv              bgtts-env                        ^
  --target-lang       "Bulgarian,German"
```

### All options

| Option | Default | Description |
|--------|---------|-------------|
| `--input-folder` | *(required)* | Folder scanned for new video files |
| `--processing-folder` | *(required)* | Working folder for in-progress jobs |
| `--output-folder` | `output` | Destination for finished dubbed videos |
| `--db` | `videotranslator.db` | SQLite database path |
| `--ffmpeg` | `ffmpeg` | Path to ffmpeg executable |
| `--python` | `python` | Path to Python executable (Whisper) |
| `--demucs` | `python` | Path to Python executable (Demucs) |
| `--venv` | — | Virtual environment root; overrides `--python` and `--demucs` |
| `--azure-key` | *(required)* | Azure Cognitive Services subscription key (Speech TTS) |
| `--azure-endpoint` | *(required)* | Azure Speech endpoint URL |
| `--openai-endpoint` | *(required)* | Azure AI Services root URL (no `/openai/v1` suffix) |
| `--openai-deployment` | `gpt-4o-mini` | Azure OpenAI deployment name |
| `--target-lang` | `Bulgarian` | Comma-separated target languages, e.g. `"Bulgarian,German"` |

> **Voices are selected automatically** per language. The mapping lives in `PipelineOrchestrator.VoiceMap` — add an entry there to support additional languages.

> **Authentication for OpenAI:** `DefaultAzureCredential` is used — run `az login` before the first run, or set `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_CLIENT_SECRET` for a service principal.

---

## Crash recovery

The orchestrator tracks which pipeline state is "stable" (fully committed to the database). If the process is killed mid-step, the job resets to its preceding stable state on the next run and retries cleanly. For multi-language jobs, each language's result is saved immediately after it completes — only the interrupted language is retried, not the whole batch.

---

## Testing

```bash
dotnet test VideoTranslatiorService.slnx
dotnet test VideoTranslatiorService.slnx --verbosity normal
```

### Test coverage (141 tests)

| Area | File | What is tested |
|------|------|----------------|
| `JobService` | `BLL/JobServiceTests.cs` | File move, job creation, state transitions, error paths |
| `MediaSeparatorService` | `BLL/MediaSeparatorServiceTests.cs` | Path building, ffmpeg args, state update, missing-output error |
| `SrtExtractorService` | `BLL/SrtExtractorServiceTests.cs` | Whisper invocation, output path, state transition |
| `SrtTranslatorService` | `BLL/SrtTranslatorServiceTests.cs` | GPT chunking (50/call), system prompt contains target language, output path, state transition |
| `SrtToAzureTtsService` | `BLL/SrtToPiperBgServiceTests.cs` | Per-entry SSML, `<prosody rate>` logic, silence padding, WAV concatenation, retry on failure, silence fallback |
| `VoiceRemoverService` | `BLL/VoiceRemoverServiceTests.cs` | Demucs args, output path detection, state transition |
| `AudioMixerService` | `BLL/AudioMixerServiceTests.cs` | ffmpeg amix args, language-specific output path, state transition, missing-output error |
| `VideoMuxerService` | `BLL/VideoMuxerServiceTests.cs` | Multi-stream ffmpeg args, apad filter, Original/language metadata, subtitle codec (MP4/MKV), output folder |
| `VideoJobRepository` | `Data/VideoJobRepositoryTests.cs` | CRUD, state filtering, `UpdatedAt` timestamp |

### Test dependencies

| Package | Role |
|---------|------|
| xUnit | Test framework |
| NSubstitute | Mocking (`IVideoJobRepository`, `IProcessRunner`, `IFileSystem`, `IAzureSpeechEngine`, `IAzureChatEngine`) |
| `Microsoft.EntityFrameworkCore.InMemory` | In-memory SQLite for repository tests |

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
