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

**`Services/TranscriptionService.cs`** — Wraps Whisper.net. Downloads Whisper model files (Small/Medium/Large/Tiny/Base) on first run into `[AppDir]\models\`. Accepts `IProgress<(string, double)>` and `CancellationToken`. Converts Whisper segments to `SubtitleEntry` objects.

**`Services/AudioExtractorService.cs`** — Shells out to FFmpeg CLI. Auto-downloads FFmpeg from BtbN GitHub Releases into `[AppDir]\tools\` on first run. Converts video to 16kHz mono WAV in `%TEMP%\VideoSubtitleApp\`. Parses FFmpeg stderr for progress reporting. Supports cancellation via process termination.

**`Services/SrtExporter.cs`** — Exports `List<SubtitleEntry>` to SRT format with UTF-8 BOM.

**`Models/SubtitleEntry.cs`** — Data model: `Index`, `StartTime`, `EndTime` (TimeSpan), `Text`.

**`Converters/TimeSpanToStringConverter.cs`** — XAML `IValueConverter` for `MM:SS` display.

### UI Layout

Dark-themed 3-column layout: title bar | video player (3× width) | subtitle panel. The subtitle panel shows a searchable/editable list; a draggable overlay shows the current subtitle over the video. Language (Japanese/English) and model size are selectable at runtime.

### Runtime Artifacts

- FFmpeg binary: `[AppDir]\tools\`
- Whisper models: `[AppDir]\models\`
- Temp WAV files: `%TEMP%\VideoSubtitleApp\` (cleaned on exit)
