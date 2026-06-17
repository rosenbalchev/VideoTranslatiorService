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
                [SeparatingMedia]  ──►  ffmpeg extracts audio (.aac)
                                   ──►  ffmpeg strips audio from video (_silent.mp4)
                       │
                       ▼
                [AudioExtracted]  ──►  ready for translation (upcoming)
```

---

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | `net10.0` target |
| [ffmpeg](https://ffmpeg.org/download.html) | Must be on `PATH` or pass `--ffmpeg <path>` |
| SQLite | Bundled via `Microsoft.EntityFrameworkCore.Sqlite` — no separate install |

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
| **Step 2 — Translation** | Send extracted audio to Azure AI Foundry Speech-to-Text → translate → Text-to-Speech |
| **Step 3 — Merge** | Mux translated audio back with silent video using ffmpeg |
| **Step 4 — Output** | Move finished file to output folder, mark job `Completed` |
