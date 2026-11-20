# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

LexiVerse Video Captioner is a Windows WPF desktop application for offline video transcription and subtitle generation. It uses Whisper.net (local inference) and FFmpeg for audio extraction — no cloud API required.

## Tech Stack

- **Language**: C# / .NET 10.0
- **UI**: WPF (Windows Presentation Foundation), Windows-only
- **Transcription**: Whisper.net 1.9.0 (local speech-to-text)
- **Audio**: FFmpeg (auto-downloaded on first run)
- **Target**: `net10.0-windows10.0.26100.0`, `win-x64`

## Build & Run

```powershell
# Restore dependencies
dotnet restore -r win-x64

# Build (Release)
dotnet build -r win-x64 -c Release

# Run
dotnet run --project VideoSubtitleApp -r win-x64

# Publish (self-contained)
dotnet publish VideoSubtitleApp -r win-x64 -c Release
```

The `RuntimeIdentifier win-x64` is required — the project targets a Windows-specific SDK version.

There are no automated tests in this project.

## Architecture

### Data Flow

```
Load Video → FFmpeg (audio extraction → 16kHz mono WAV) → Whisper.net (transcription)
→ ObservableCollection<SubtitleEntry> → Real-time overlay synced to video position → SRT export
```

### Key Components

**`Views/MainWindow.xaml.cs`** (~580 lines) — Central orchestrator. Combines UI state, WPF `MediaElement` playback, and coordination between services. All business logic lives here (no separate ViewModel). A 200ms `DispatcherTimer` drives subtitle sync with video position.

**`Services/TranscriptionService.cs`** — Wraps Whisper.net. Downloads Whisper model files (Small/Medium/Large/Tiny/Base) on first run into `%LOCALAPPDATA%\LexiVerseVideoCaptioner\models\` and verifies each download against a pinned SHA-256 (`ModelSha256`). Accepts `IProgress<(string, double)>` and `CancellationToken`. Converts Whisper segments to `SubtitleEntry` objects.

**`Services/NpuWhisperRunner.cs`** — Optional NPU path (AMD Ryzen AI 300). Runs `npu\whisper-cli.exe` (AMD's `amd/whisper.cpp` fork built with `-DWHISPER_VITISAI=1`) as a subprocess instead of Whisper.net, parses its JSON output and stderr progress. The csproj copies the binaries from `whisperCpp\build\bin\Release\` (git-ignored, local build) into the output's `npu\` folder only if present; the "NPU" device option appears only then. Needs `ggml_<Model>-encoder-vitisai.rai` next to the model, downloaded from AMD's Hugging Face repos at a pinned revision and verified by SHA-256 (`RaiFiles`). Models without an AMD `.rai`: TinyEn, BaseEn, MediumEn, LargeV1, LargeV2.

**`Services/AudioExtractorService.cs`** — Shells out to FFmpeg CLI. Uses `ffmpeg.exe` from an absolute PATH entry if present (always launched by full path); otherwise downloads a pinned BtbN build into `%LOCALAPPDATA%\LexiVerseVideoCaptioner\tools\`, verifying the zip and exe SHA-256 (when bumping the version, update the URL and both hashes together). Converts video to 16kHz mono WAV in `%TEMP%\LexiVerseVideoCaptioner\`. Parses FFmpeg stderr for progress reporting. Supports cancellation via process termination.

**`Services/SrtExporter.cs`** — Exports `List<SubtitleEntry>` to SRT format with UTF-8 BOM.

**`Models/SubtitleEntry.cs`** — Data model: `Index`, `StartTime`, `EndTime` (TimeSpan), `Text`.

**`Converters/TimeSpanToStringConverter.cs`** — XAML `IValueConverter` for `MM:SS` display.

### UI Layout

Dark-themed 3-column layout: title bar | video player (3× width) | subtitle panel. The subtitle panel shows a searchable/editable list; a draggable overlay shows the current subtitle over the video. Language (Japanese/English) and model size are selectable at runtime.

### Runtime Artifacts

- FFmpeg binary: `%LOCALAPPDATA%\LexiVerseVideoCaptioner\tools\`
- Whisper models: `%LOCALAPPDATA%\LexiVerseVideoCaptioner\models\`
- Log file: `%LOCALAPPDATA%\LexiVerseVideoCaptioner\logs\app.log`
- Legacy `[AppDir]\tools\` and `[AppDir]\models\` (older versions) are migrated to the above on startup
- Temp WAV files: `%TEMP%\LexiVerseVideoCaptioner\` (cleaned on exit)
