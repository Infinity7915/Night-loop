# Architecture

Night-loop is a WPF desktop app plus (later) a private ASP.NET Core server. The full plan lives in the
project schema document; this page describes what is in this repository today.

## Projects

```
NightLoop.App  ──►  NightLoop.Engine  ──►  NightLoop.Contracts
      │                                         ▲
      └──────►  NightLoop.LocalData             │
                                    (server will reference this too)
```

- **Contracts** has no Windows code. It defines `MacroDoc`, the block catalog (`BlockTypes`), `KeyCatalog`
  (key names ↔ virtual keys), `Hotkey`, every limit (`MacroLimits`) and `MacroValidator`. The server will
  run the same validator, so the client and server always agree on what a valid macro is.
- **Engine** targets plain `net8.0` so its logic runs in tests on any OS. Classes that call Win32 are marked
  `[SupportedOSPlatform("windows10.0.17763")]` and use [CsWin32](https://github.com/microsoft/CsWin32)
  generated P/Invoke (`NativeMethods.txt`), so there are no hand-written signatures.
- **App** is WPF with CommunityToolkit.Mvvm. It's per-monitor DPI aware (`app.manifest`) so recorded and
  replayed pixel coordinates match on every monitor.

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

## Recording pipeline

```
WH_KEYBOARD_LL / WH_MOUSE_LL ──► InputRecorder ──► RawInputEvent list ──► RecordingBuilder ──► blocks
       (hook thread with its own message loop)                            (pure logic, unit-tested)
```

- Hook callbacks are `[UnmanagedCallersOnly]` static methods. They copy the event and return at once.
- Input that Night-loop injects carries a marker in `dwExtraInfo` and is never recorded.
- Clicks and keys aimed at Night-loop's own window (like the Record button) are skipped.
- The stop hotkey is swallowed, and modifier presses left at the end (for Ctrl+F9 style hotkeys) are dropped.

## Hotkeys

`HotkeyManager` calls `RegisterHotKey` on its own message-loop thread and runs callbacks on the thread pool.
A combo already owned by another program raises `HotkeyTakenException` ("F6 is already used by another program").

## Not built yet

Vision (text, image, pixel detection) raises a clear fault when a macro uses it. Accounts, server sync,
Velopack updates and the full editor follow the roadmap in the README.
