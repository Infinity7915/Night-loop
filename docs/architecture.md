# Architecture

Night-loop is a WPF desktop app plus a private ASP.NET Core server ([Night-loop-Server](https://github.com/Infinity7915/Night-loop-Server)). The full plan lives in the
project schema document; this page describes what is in this repository today.

## Projects

```
NightLoop.App ──► NightLoop.Engine.Windows ──► NightLoop.Engine ──► NightLoop.Contracts ◄── Night-loop-Server (git submodule)
     │                                                                     ▲
     ├──► NightLoop.Client ────────────────────────────────────────────────┘
     │          │
     ├──► NightLoop.LocalData ◄┘
     │
     └──► NightLoop.Updates ──► GitHub (Releases + announcements.md)
```

- **Contracts** has no Windows code. It defines `MacroDoc`, the block catalog (`BlockTypes`), `KeyCatalog`
  (key names ↔ virtual keys), `Hotkey`, every limit (`MacroLimits`), `MacroValidator`, the API's request and
  response records (`Api/ApiModels.cs`) and account rules (`AccountRules`). The server compiles this project
  through a git submodule, so both sides agree on what a valid macro or a login response is.
- **Client** talks to the server and has no UI code, so it's tested on any OS: `ApiClient` (typed calls,
  error codes → `ApiException`), `AccountSession` (saved token, sign-in/up, 60-second heartbeat, session
  replaced), `DraftAutosaver` (1.5 s debounce, If-Match versions, conflicts, offline retry),
  `DeviceIdentity` (install.id and hashed hardware fingerprint) and `DpapiSessionStore` (session.bin).
- **Engine** targets plain `net8.0` and has no Windows code: the compiler, runner, conditions, waits, picture
  matching and path playback. Input, screen capture, text reading and windows come in through `IInputSender`,
  `IScreenCapture`, `ITextReader` and `IWindowService`, so the PC app and the planned Android app share it and
  it runs in tests on any OS.
- **Engine.Windows** is the PC side of the engine: `Win32InputSender`, `Win32ScreenCapture`, `Win32WindowService`,
  `HotkeyManager`, `KeyState`. Classes that call Win32 are marked `[SupportedOSPlatform("windows10.0.17763")]` and
  use [CsWin32](https://github.com/microsoft/CsWin32) generated P/Invoke (`NativeMethods.txt`), so there are no
  hand-written signatures. It keeps the `NightLoop.Engine.*` namespaces.
- **Updates** has no UI code either: `UpdateService` (check → download → restart, one check at a time, failures
  keep the current version) over `VelopackUpdater`, and `AnnouncementFeed`, which reads `announcements.md` from
  the repo and caches it. Both talk only to GitHub. See [releasing.md](releasing.md).
- **App** is WPF with CommunityToolkit.Mvvm. `Program.Main` runs Velopack's hooks before WPF starts. Pages are
  Loading → Sign in → Home / Macros / Settings; `Controls/PageHost` cross-fades between them (off when Windows
  animation effects are off). `NightLoop.exe --smoke <folder>` draws every screen to a PNG for CI. It's per-monitor DPI aware (`app.manifest`) so picked and replayed pixel coordinates match on every
  monitor.

## Replay pipeline

```
MacroDoc ──► MacroValidator ──► MacroCompiler ──► CompiledMacro ──► MacroRun ──► IInputSender
                                (pairs If/Else/   (flat steps +      (background     (Win32InputSender:
                                 Loop/End, labels) jump targets)      task)           SendInput)
```

- `MacroCompiler` uses `MacroStructure` to pair openers with their `logic.end`. Disabled blocks compile to
  no-ops so indexes stay stable for the timeline highlight.
- `MacroRun` is one running macro. States: Idle, Running, Paused, AtBreakpoint, WaitingForWindow, Stopped,
  Faulted. Loop counters are keyed by the loop block, so `goto` in and out of loops is safe.
- Each run wraps the shared sender in a `TrackingInputSender` that remembers held keys and buttons and
  releases them on every exit path.
- `RunManager` owns all runs: run-alongside rules, the 8-at-once cap, a shared input lock so two macros
  can't interleave mid-combo, and stop-all for the panic key.
- Runaway guard: 10,000 steps with no wait or input faults with "Loop with no wait".
- `PreciseDelay` sleeps with `Task.Delay` and spins the last ~16 ms, because Windows timers tick every ~15 ms.

## No recording

Night-loop doesn't record keyboard or mouse input (removed in 0.10.0): macros are built from blocks only.
Mouse move and Drag blocks recorded by older versions can carry a `path` of points; the engine still plays
them, so saved macros keep working, but nothing creates new ones.

## Hotkeys

`HotkeyManager` calls `RegisterHotKey` on its own message-loop thread and runs callbacks on the thread pool.
A combo already owned by another program raises `HotkeyTakenException` ("F6 is already used by another program").

## Accounts and sync

```
Loading ──► GET /health, saved token? ──► GET /me ──► Macros page
   │                                  └─► Sign in ──► (code) ──► Macros page
   └─► offline: Retry
```

- The session token and "trust this PC" tokens live in `%LOCALAPPDATA%\NightLoop\session.bin`, encrypted
  with DPAPI for the current Windows user. `install.id` is a random GUID made on first run.
- One PC at a time: when another PC signs in, the next call (or the heartbeat within 60 s) gets
  `session_replaced`. The app stops every macro, releases held keys and returns to sign-in with
  "Signed in on another device".
- If the server can't be reached for 5 minutes, running macros stop until it answers again.
- A new macro reaches the account on its first edit (it takes one of the 15 slots as a draft). Later edits
  autosave the draft; **Save** makes it the saved copy. If another PC changed it meanwhile, the app asks
  whether to keep this version or load the server copy.
- The server URL comes from `serverUrl` in `settings.json` or the `NIGHTLOOP_SERVER` environment variable,
  and defaults to the public server, `https://api.night-loop.win/v1/`. For a local dev server set
  `NIGHTLOOP_SERVER=http://localhost:5080/v1/`.

## Not built yet

Vision (text, image, pixel detection) raises a clear fault when a macro uses it. The rest of Settings
(appearance, target games, username, email, password, 2FA; the API calls exist in `ApiClient`), groups in the
UI and the full editor follow the roadmap in the README.
