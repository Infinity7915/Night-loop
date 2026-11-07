# Building

## Requirements

- Windows 10 1809 or later (Windows 11 is fine)
- .NET 8 SDK

## Commands

```
dotnet build NightLoop.sln
dotnet test NightLoop.sln
dotnet run --project src/NightLoop.App
```

The Contracts and Engine projects (and their tests) also build and test on Linux and macOS, because the
engine logic is tested with fake input. The WPF app builds anywhere with `EnableWindowsTargeting` but only
runs on Windows.

## Trying playback safely

Open Notepad, set the target to N/A, add a Type text block, click into Notepad and press F6. If anything goes wrong, Ctrl+Alt+End stops every macro and releases all keys.
