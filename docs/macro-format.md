# Macro format

A macro is one JSON document. The authoritative definition is section 6 of the project schema; this page
notes what the code implements and the small additions made for recording.

```json
{
  "schema": 1,
  "name": "Coin farm",
  "targetGame": "Roblox",
  "options": {
    "hotkeyStart": "F6",
    "hotkeyStop": "F7",
    "triggerMode": "toggle",
    "runAlongside": true,
    "repeat": { "mode": "count", "count": 1 },
    "humanize": { "enabled": false, "timingJitterPct": 0, "mousePx": 0 }
  },
  "blocks": [
    { "id": "b1", "type": "input.key", "enabled": true, "breakpoint": false,
      "params": { "key": "W", "action": "press", "holdMs": 1200 } }
  ]
}
```

## Key names

Letters `A`–`Z`, digits `0`–`9`, `F1`–`F24`, `NumPad0`–`NumPad9`, `Enter`, `Escape`, `Space`, `Tab`,
`Backspace`, arrows (`Up`, `Down`, `Left`, `Right`), `Home`, `End`, `PageUp`, `PageDown`, `Insert`, `Delete`,
`LeftShift`/`RightShift`, `LeftCtrl`/`RightCtrl`, `LeftAlt`/`RightAlt`, `LeftWin`/`RightWin`, punctuation
(`Semicolon`, `Equals`, `Comma`, `Minus`, `Period`, `Slash`, `Backtick`, `LeftBracket`, `Backslash`,
`RightBracket`, `Quote`) and a few more in `KeyCatalog.cs`. `Shift`, `Ctrl`, `Alt` and `Win` also work.

## Additions to the block catalog

These are optional params, so existing macros are unaffected:

| Block | Param | Why |
| --- | --- | --- |
| `input.key` | `holdMs` defaults to 30 when missing | Games often miss a key held for less than a frame |
| `input.click` | `action`: `press` (default), `down`, `up` | Recording "hold right mouse while pressing keys" needs separate down and up, like `input.key` |
| `input.click` | `holdMs` (default 20) | Replays how long the button was held |
| `input.click` | `x`/`y` may be omitted | Clicks wherever the cursor is |
| `input.scroll` | positive `amount` scrolls up | Matches Windows wheel direction |
| `window.waitFor` | `until`: `front` (default) or `open` | "Open" carries on as soon as the window exists, even behind others |
| `window.open` (new) | `path` (required, up to 260 characters, `%LOCALAPPDATA%` style variables allowed), `args`, `ifRunning`: `focus` (default) or `new`, `wait` (default true), `timeoutMs` | Starts a program, or brings it to the front if it's already running, then waits for its window |
| any block | `note` (text, up to 200 characters) | A short note shown under the block in the editor; ignored when running |

## Disabled blocks

A disabled block does nothing and keeps its index. A disabled If behaves as if its condition were true.
A Loop or Repeat until whose opener or End is disabled runs its body once.

## Implemented now

Every block in the palette runs:

- **Input**: key, key combo, hold key, click, mouse move, drag, scroll, type text.
- **Camera**: turn, zoom, center on a target.
- **Timing**: wait, random wait, wait for key, pause and resume another macro.
- **Detect**: wait for text (Windows OCR), wait for image, wait for pixel color, find and click, scan and turn.
- **Logic**: loop, repeat until, if, else, end, label, go to label, run macro, stop.
- **Window**: open window (start a program), focus, wait for window, resize, position.
- **Utility**: notify, screenshot (saved to Pictures\Night-loop), comment, description.

Conditions: `textVisible`, `imageVisible`, `pixelColor`, `windowFocused`, `processRunning`, `keyHeld`, `not`,
`all`, `any`. Regions are `[x, y, width, height]` inside the game window. Points with `relativeTo: "gameWindow"`
are inside the game window's client area, so they still line up when the window moves.

## Editing in the app

The Macros screen builds this document block by block:

- **Add block** shows the categories (Input, Camera, Timing, Detect, Logic, Window, Utility). Opening one lists
  its blocks; clicking a block adds it after the selected one. If, Loop and Repeat until get their End added too.
- Under the fields, buttons pick values off the screen: **Pick point**, **Pick color**, **Select region**,
  **Capture image**, and **Use current window size / position** for the window blocks.
- Each row has a breakpoint dot, an on/off box, its number, a colored tag and actions (duplicate, move up,
  move down, delete). The Delete key removes the selected block. Rows inside a Loop, If or Repeat until are
  indented.
- The panel on the right edits the selected block's params, with the limits from `MacroLimits`.
- **Play** starts at the selected block ("Play from 7"), or at the top when nothing is selected. Keys an
  earlier Key down block left held are pressed first. Play stops at breakpoints; press it again to continue.
  (Step is gone for now.)
- **Record** puts new blocks under the selected block, or at the end when the last block or nothing is
  selected. Keys and buttons that overlap stay overlapped: Ctrl+T records as Ctrl down, T, Ctrl up, with the
  real hold times. Blocks inside a held key are indented and show how long it was held.
- Window blocks pick their program with File Explorer (or from the programs running now) instead of typing a
  name. Picture blocks show the captured picture, and **Test on screen** looks once and says what it found.
- The sliders button sets the macro's start/stop key, trigger mode (toggle or hold), run alongside, repeat
  count and whether Record keeps plain mouse movement.
- New macros start with a starter sequence (Settings › Macros turns it off; the choice stays on this PC).
- Saved macros can be put in groups (name and color), searched, given their own start/stop key, and switched
  on and off from the list. The record key (F9 unless changed, next to Record), F6 and Ctrl+Alt+End are
  reserved for Record, Play and Stop all.
