# Night-loop

A Windows macro app for games. Record what you do with the keyboard and mouse, then replay it, or build
macros from blocks (keys, clicks, waits, loops, if/else and more).

Night-loop never touches game memory. It only sends ordinary input through Windows, like a keyboard and mouse would.

> **Status: early development.** Record and replay work. Accounts, the private server, macro sync,
> screen reading (text, image and pixel detection) and the full block editor are on the roadmap below.

## What works today

- **Record** keyboard and mouse with low-level hooks. Quick presses become one block, overlapping keys
  (like Ctrl+C, or holding W while clicking) become separate down/up blocks so they replay exactly.
  Pauses over 50 ms become Wait blocks. Clicks inside the target game are saved relative to its window.
- **Replay** with `SendInput` using scan codes (many games ignore virtual-key events), eased mouse moves,
  drags, double clicks, scrolling and typing.
- **Safety**: every key and button a macro presses is released when it stops, pauses or fails.
  **Ctrl+Alt+End** stops every macro at once. A target lock waits instead of typing into another window.
- **Engine**: loops, repeat-until, if/else, labels and go-to, repeat count or forever, humanize
  (random timing and mouse jitter), breakpoints and single-step, several macros at once.
- **Import/Export** macros as JSON files (until account sync replaces this).

## Using it

1. Pick a target game (or N/A for any window).
2. Press **Record** or **F9**, do your thing, press **F9** again.
3. Press **Play** or **F6** to replay. **F6** stops it, **Ctrl+Alt+End** stops everything.

Night-loop runs with normal user rights. Windows won't let it send input to a game that runs as
administrator unless Night-loop runs as administrator too.

## Building

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows 10 1809 or later.

```
dotnet build NightLoop.sln
dotnet test NightLoop.sln
dotnet run --project src/NightLoop.App
```

See [docs/building.md](docs/building.md) for more.

## Repository layout

| Project | What it holds |
| --- | --- |
| `src/NightLoop.Contracts` | Shared with the server: macro JSON format, block catalog, limits, validator, error codes |
| `src/NightLoop.Engine` | Runs and records macros. No UI code. Compiler, runner, input, window control, hotkeys, recording |
| `src/NightLoop.LocalData` | Per-PC files: target games list |
| `src/NightLoop.App` | WPF desktop app |
| `tests/` | xUnit tests for Contracts and Engine (the engine is tested with fake input) |

More in [docs/architecture.md](docs/architecture.md) and [docs/macro-format.md](docs/macro-format.md).

## Roadmap

| Milestone | Scope | State |
| --- | --- | --- |
| M0 Setup | Solution, CI | Done |
| M5 Engine core | Compiler, runner, SendInput, logic blocks, hotkeys, panic key | Done (vision-free parts) |
| M7 Recording | Hooks to blocks, target-game lock | Done (window blocks need testing in games) |
| M1 Accounts | Server: register, verify, sign in, sessions | Next |
| M2 Client shell | Sign-in screens, Home, DPAPI session | |
| M3 Macros API | Server macro storage, drafts, 15-macro limit | |
| M4 Editor UI | Block palette, inspector, saved list | |
| M6 Vision | Capture, OCR, image and pixel detection | |
| M8–M10 | Settings, updates (Velopack), deployment, 1.0 | |

## License

[MIT](LICENSE)
