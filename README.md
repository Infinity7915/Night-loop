# Night-loop

A Windows macro app for games. Record what you do with the keyboard and mouse, then replay it, or build
macros from blocks (keys, clicks, waits, loops, if/else and more).

Night-loop never touches game memory. It only sends ordinary input through Windows, like a keyboard and mouse would.

> **Status: early development.** Record and replay work, and macros sync to your account through the
> private server. Screen reading (text, image and pixel detection), settings screens and the full block
> editor are on the roadmap below.

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
- **Accounts**: sign up with an emailed code, sign in (optionally trusting a PC for 30 days), forgot
  password. One PC is signed in at a time; signing in elsewhere signs this PC out and stops its macros.
- **Sync**: macros live on your account (up to 15). Edits autosave as a draft within a couple of seconds;
  **Save** makes it the saved copy. Open them from any PC.
- **Import/Export** macros as JSON files; an imported file becomes a new macro on your account.

## Using it

Night-loop needs its server. Run [Night-loop-Server](https://github.com/Infinity7915/Night-loop-Server)
locally (its README has the two commands) or set `NIGHTLOOP_SERVER` / `serverUrl` in
`%LOCALAPPDATA%\NightLoop\settings.json` to your server's `https://…/v1` address.

1. Create an account or sign in. In development the server prints the 6-digit code in its console.
2. Pick a target game (or N/A for any window).
3. Press **Record** or **F9**, do your thing, press **F9** again. It's saved to your account as a draft.
4. Press **Play** or **F6** to replay. **F6** stops it, **Ctrl+Alt+End** stops everything.
5. Press **Save** to keep this version.

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
| `src/NightLoop.Contracts` | Shared with the server: macro JSON format, block catalog, limits, validator, error codes, API shapes |
| `src/NightLoop.Engine` | Runs and records macros. No UI code. Compiler, runner, input, window control, hotkeys, recording |
| `src/NightLoop.Client` | Talks to the server: API client, sign-in session, saved token, device id, draft autosave |
| `src/NightLoop.LocalData` | Per-PC files: target games list, settings.json |
| `src/NightLoop.App` | WPF desktop app |
| `tests/` | xUnit tests for Contracts, Engine (fake input) and Client (fake server) |

More in [docs/architecture.md](docs/architecture.md) and [docs/macro-format.md](docs/macro-format.md).

## Roadmap

| Milestone | Scope | State |
| --- | --- | --- |
| M0 Setup | Solution, CI | Done |
| M5 Engine core | Compiler, runner, SendInput, logic blocks, hotkeys, panic key | Done (vision-free parts) |
| M7 Recording | Hooks to blocks, target-game lock | Done (window blocks need testing in games) |
| M1 Accounts | Server: register, verify, sign in, sessions | Done (server repo) |
| M2 Client shell | Sign-in screens, DPAPI session | Done (Home screen still to come) |
| M3 Macros API | Server macro storage, drafts, 15-macro limit | Done (server repo); app syncs macros |
| M4 Editor UI | Block palette, inspector, saved list | |
| M6 Vision | Capture, OCR, image and pixel detection | |
| M8–M10 | Settings, updates (Velopack), deployment, 1.0 | |

## License

[MIT](LICENSE)
