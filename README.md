# AI Video Translator Service

A .NET 10 CLI tool that ingests video files, separates audio/video tracks, and (in upcoming steps) translates speech via **Microsoft Azure AI Foundry**, then merges the translated audio back into the video.

---

## Architecture

```
VideoTranslatiorService.slnx
├── VideoTranslatorService.Data/       ← EF Core entities, DbContext, repositories
├── VideoTranslatorService.BLL/        ← Business logic, pipeline services
├── VideoTranslatorService.CLI/        ← Console entry-point, DI wiring, CLI options
└── VideoTranslatorService.Tests/      ← xUnit unit tests (BLL + Data layers)
```

### Data layer (`VideoTranslatorService.Data`)
| Type | Purpose |
|------|---------|
| `VideoJob` | Root entity — tracks file paths and current pipeline state |
| `JobState` (enum) | `Queued → SeparatingMedia → AudioExtracted → Translating → Merging → Completed / Failed` |
| `AppDbContext` | EF Core SQLite context |
| `IVideoJobRepository` / `VideoJobRepository` | CRUD for `VideoJob` |

### BLL (`VideoTranslatorService.BLL`)
| Service | Purpose |
|---------|---------|
| `IJobService` / `JobService` | Move file to processing folder, create job record, transition states |
| `IMediaSeparatorService` / `MediaSeparatorService` | Call **ffmpeg** to extract audio and produce a silent video copy |
| `IProcessRunner` / `DefaultProcessRunner` | Abstraction over `System.Diagnostics.Process` — enables unit testing without a real ffmpeg |

---

## Pipeline — current steps

```
Input folder  ──►  [CreateJob]  ──►  Processing folder (state: Queued)
                       │
                       ▼
                [SeparatingMedia]  ──►  ffmpeg extracts audio (_audio.wav)
                                   ──►  ffmpeg strips audio from video (_silent.mp4)
                       │
                       ▼
                [ExtractingSrt]  ──►  Whisper transcribes audio → .srt subtitle file
                       │
                       ▼
                [RemovingVoice]  ──►  Demucs separates vocals from music bed
                                  ──►  no_vocals.wav (music bed used downstream)
                       │
                       ▼
                [VoiceRemoved]  ──►  ready for SRT translation (upcoming)
```

---

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | `net10.0` target |
| [ffmpeg](https://ffmpeg.org/download.html) | Must be on `PATH` or pass `--ffmpeg <path>` |
| [Python 3.9+](https://www.python.org/downloads/) | Required for Whisper (SRT extraction) and Demucs |
| [Demucs](https://github.com/facebookresearch/demucs) | Voice/music separation — see installation below |
| SQLite | Bundled via `Microsoft.EntityFrameworkCore.Sqlite` — no separate install |

### Installing Demucs

Demucs is Meta's open-source audio source-separation model. It separates vocals from background music using a hybrid Transformer approach (v4 / `htdemucs`).

```bash
# Install via pip (requires Python 3.9+)
pip install demucs

# Verify the install
demucs --version
```

> **GPU acceleration (recommended for large files):** Install PyTorch with CUDA support first, then install Demucs on top:
> ```bash
> pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu121
> pip install demucs
> ```

The pipeline runs `demucs --two-stems=vocals` which produces two stems:
- `vocals.wav` — isolated voice track (discarded)
- `no_vocals.wav` — music/background bed (used as the base for the translated audio mix)

---

## Getting started

```bash
# Restore and build
dotnet build VideoTranslatiorService.slnx

# Run — scan input/ and stage work under processing/
dotnet run --project VideoTranslatorService.CLI -- \
    --input-folder "C:\videos\input" \
    --processing-folder "C:\videos\processing"

# Optional flags
#   --db path/to/videotranslator.db   (default: videotranslator.db in CWD)
#   --ffmpeg path/to/ffmpeg.exe       (default: ffmpeg on PATH)
#   --python path/to/python.exe       (default: python on PATH)
#   --demucs path/to/demucs.exe       (default: demucs on PATH)
```

---

## Testing

Unit tests live in **`VideoTranslatorService.Tests`** and cover the BLL and Data layers.

```bash
# Run all tests
dotnet test VideoTranslatiorService.slnx

# Run with detailed output
dotnet test VideoTranslatiorService.slnx --verbosity normal
```

### Test coverage

| Area | File | What is tested |
|------|------|----------------|
| `JobService` | `Tests/BLL/JobServiceTests.cs` | File move, job creation, state transitions, error paths |
| `MediaSeparatorService` | `Tests/BLL/MediaSeparatorServiceTests.cs` | Path building, ffmpeg invocation count, state update, error propagation |
| `VoiceRemoverService` | `Tests/BLL/VoiceRemoverServiceTests.cs` | Path building, demucs invocation, `--two-stems=vocals` flag, state update, error propagation |
| `VideoJobRepository` | `Tests/Data/VideoJobRepositoryTests.cs` | CRUD operations, state filtering, `UpdatedAt` timestamp |

### Test dependencies

| Package | Role |
|---------|------|
| xunit | Test framework |
| NSubstitute | Mocking (`IVideoJobRepository`, `IProcessRunner`) |
| `Microsoft.EntityFrameworkCore.InMemory` | In-memory SQLite substitute for repository tests |

> **TODO:** Integration tests (CLI end-to-end, real ffmpeg calls) are not yet implemented.

---

## CI

GitHub Actions runs on every push and pull request to `master`/`main`:

```
.github/workflows/ci.yml
  restore → build (Release) → test → upload test results (.trx)
```

---

## Database

A SQLite file is created automatically on first run (`videotranslator.db` by default). The schema is managed by EF Core's `EnsureCreated`.

```
VideoJobs
  Id                  GUID  PK
  OriginalFileName    TEXT
  InputFilePath       TEXT
  ProcessingFolderPath TEXT
  ProcessingVideoPath TEXT   (set after move)
  ExtractedAudioPath  TEXT   (set after step 1)
  SilentVideoPath     TEXT   (set after step 1)
  TranslatedAudioPath TEXT   (set after step 2 — future)
  OutputFilePath      TEXT   (set after step 3 — future)
  State               TEXT   (enum stored as string)
  ErrorMessage        TEXT
  CreatedAt           TEXT
  UpdatedAt           TEXT
```

---

## Upcoming pipeline steps

| Step | Description |
|------|-------------|
| **Step 4 — SRT translation** | Translate the subtitle file into the target language via Azure AI Foundry |
| **Step 5 — Voice synthesis** | Convert translated subtitles to speech (Text-to-Speech) |
| **Step 6 — Audio mix** | Blend synthesised voice with the music bed (`no_vocals.wav`) |
| **Step 7 — Final mux** | Mux the mixed audio back into the silent video using ffmpeg |
