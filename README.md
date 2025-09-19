# NitroSynth

NitroSynth is a desktop toolchain for working with Nintendo DS Nitro Sound Archive (`.sdat`) assets. The current prototype ships a
minimal Avalonia-based shell that can load an `.sdat` file from the menu bar and reports the selected file name and size.

## Getting started

### Prerequisites

* .NET SDK 8.0 or newer

### Build and run

```bash
dotnet build
# or run the desktop app directly
dotnet run --project src/NitroSynth.App
```
