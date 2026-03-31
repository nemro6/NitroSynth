# NitroSynth

NitroSynth is a desktop sound-asset workstation for **NDS** Nitro sound data.
It focuses on practical inspection, audition, and conversion workflows for `.sdat` projects, with a real-time synth/mixer view and editing tools.

## Overview

NitroSynth loads an SDAT, parses core blocks (`SYMB`, `INFO`, `FAT`, `FILE`), and exposes each major asset family in dedicated tabs.
You can inspect structure, play sequences through an internal engine, monitor channel activity in real time, and export to common working formats.

This project currently prioritizes:

- fast iteration when analyzing NDS audio data
- reliable playback behavior for SSEQ event semantics
- in-memory editing workflows (without destructive overwrite of the original file)

## Key Features

### SDAT loading and navigation

- Open `.sdat` files from the app menu
- View SDAT metadata and block layout (`SYMB`, `INFO`, `FAT`, `FILE`)
- Browse assets in a tree view (SSEQ, SSAR, SBNK, SWAR, STRM, player/group metadata)
- Double-click entries in the tree to jump directly to the matching tab/item

### Real-time mixer and monitoring

- 16-channel mixer strips with per-channel controls and status
- Track output toggles, channel gate toggles, and quick solo-style right-click behavior
- Live channel activity feedback (audible/active state, channel slot mapping)
- Master L/R meter with clip indicators and peak line
- Live voice usage counters (PCM/PSG/Noise/Total)
- Master volume slider (top-right)

### Sequence workflow (SSEQ)

- List and select SSEQ entries from SDAT
- Inspect related playback metadata (bank, priorities, player)
- Real-time playback / pause / stop
- SSEQ decompile view (SMFT-style text)
- SMFT text compile-back with instruction-length safety checks
- Export selected sequence as:
  - `.sseq`
  - `.smft`
  - `.mid`
- Open selected SSEQ in HEX editor

### Sequence archive workflow (SSAR)

- List SSAR entries and sequence members
- Inspect per-sequence metadata (SBNK, volume, priorities, player)
- Open SSAR sequence sub-window with decompiled sequence text
- Play SSAR-contained sequences through the same playback engine
- Export SSAR as:
  - `.ssar`
  - `.mus`
- Export individual SSAR sequence as:
  - `.sseq`
  - `.smft`
  - `.mid`
- Open SSAR in HEX editor

### Instrument/bank workflow (SBNK)

- List instruments and instrument types
- Edit single-instrument parameters in-grid (type, wave refs, key, ADSR, pan)
- Support instrument structures including single instruments, drum sets, and key splits
- Launch dedicated editors for instrument subtypes (PCM/PSG/Noise/DrumSet/KeySplit)
- BNK text editor session support (in-memory save)
- Export selected bank as `.sbnk`
- Open selected bank in HEX editor

### Wave archive workflow (SWAR/SWAV)

- List SWAR archives and contained SWAV entries
- Inspect encoding/loop/rate/loop points/sample count/size
- Open SWAV editor by double-click
- Export selected SWAR as `.swar`
- Open selected SWAR in HEX editor

### Additional views

- `SeqPlayer` tab for player-level limits/heap/channel bitflags
- `STRM` tab for stream metadata inspection
- Piano keyboard display and MIDI note visualization

## Audio and MIDI Engine

NitroSynth includes an internal synth engine tuned for NDS-style behavior:

- PCM / PSG / Noise voice paths
- envelope and volume handling aligned with NDS-style tables/behavior
- per-channel controller application (volume/expression/pan/mod/pitch bend/portamento)
- channel and track masking at mix/output stages

### MIDI input

- MIDI input device list from system MIDI devices
- Auto-connect on device selection in Settings
- MIDI note/control/pitch handling mapped to mixer strips and engine state
- MIDI reset action for quick all-channel reset

### Audio output

- Select audio output device
- Mono/Stereo output toggle
- Buffer size options (default `48 ms`)
- Playback sample rate options including `32768 Hz` (native NDS rate)
  - default is `48000 Hz`

## Settings and Rendering

`SETTING` tab includes:

- Theme selection (`System`, `Light`, `Dark`)
- Audio output device
- MIDI input device
- Output mode (`Stereo` / `Mono`)
- Buffer size selection
- Playback sample rate selection
- Render FPS selection:
  - `24, 30, 60, 120, 144, 240, 280, 320, 360, 400, 480, 510, 540, 610, Unlimited`
- Meter decay controls (with slider + numeric text):
  - Master Meter Decay (`0-200 ms`, default `100 ms`)
  - Mixer Meter Decay (`0-200 ms`, default `100 ms`)

## Editing Model

NitroSynth currently uses an **in-memory override model** for edited asset bytes/text in active sessions.
This design is intentional to keep source SDAT data safe while iterating.

What this means in practice:

- HEX/BNK/MUS session saves are reflected in-app immediately
- exports use the current edited state when applicable
- direct full SDAT rewrite/save-back workflow is still limited

## Current Limitations

Some menu buttons are present but intentionally not fully implemented yet, depending on asset type/action (for example `REPLACE`, `SAVE`, and some tree context actions).

Planned/partial areas include:

- more complete write-back pipeline for all asset categories
- richer STRM editing workflow (current view is metadata-oriented)
- broader tree-action coverage (add/rename/replace/delete)

## Tech Stack

- .NET 8
- Avalonia UI (desktop)
- NAudio (audio output + MIDI input)

## Getting Started

### Prerequisites

- .NET SDK 8.0+

### Build

```bash
dotnet build
```

### Run

```bash
dotnet run --project src/NitroSynth.App
```

### Run tests

```bash
dotnet test tests/NitroSynth.Tests/NitroSynth.Tests.csproj
```

## Test Coverage Focus

The current test project includes coverage around:

- SSEQ event decoding boundaries and command variants
- SSEQ playback semantics (track timing, call/loop/if behaviors)
- SWAR/SWAV parsing and indexing behavior
- NDS-style volume/envelope behavior checks

## Repository Layout

- `src/NitroSynth.App` - main desktop application
- `src/NitroSynth.App/NDS` - NDS format parsers and sequence decoder/decompiler logic
- `src/NitroSynth.App/Audio` - synth/mixer/audio engine
- `tests/NitroSynth.Tests` - unit tests

## Notes

If you are validating audio timing or tone behavior against original NDS output, use `32768 Hz` playback sample rate first, then adjust buffer/FPS settings for your workstation profile.
