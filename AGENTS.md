# AGENTS.md

Operational guide for automated agents (e.g., ChatGPT/Codex) to set up, build, test, and open Pull Requests safely for this C#/.NET repository.

## 0) Repository Purpose / Context

NitroSynth is a C#/.NET tool focused on Nintendo DS audio: SDAT (SBNK/SWAR/SWAV/SSEQ) editing and real-time MIDI workflows. The codebase uses the .NET SDK and the dotnet CLI.

## 1) Golden Rules

Never push directly to main. Work on a new branch and open a PR.

Prefer small, focused changes. Keep unrelated diffs out.

Run formatting and tests before committing.

All commands below are bash-first; Windows PowerShell fallbacks are provided.
