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
| any block | `note` (text, up to 200 characters) | A short note shown under the block in the editor; ignored when running |

## Disabled blocks

A disabled block does nothing and keeps its index. A disabled If behaves as if its condition were true.
A Loop or Repeat until whose opener or End is disabled runs its body once.

## Implemented now

Input (key, click, move, drag, scroll, type), camera.turn, wait, random wait, pause/resume another macro,
if/else, loop, repeat until, label, go to, stop, window resize/position/focus, comment, description,
notify, play sound, run macro. Conditions: `windowFocused`, `processRunning`, `not`, `all`, `any`.

Text, image and pixel detection (and the blocks that use them) fault with a clear message until the
Vision milestone.

## Editing in the app

The Macros screen builds this document block by block:

- **Add block** shows the categories (Input, Camera, Timing, Detect, Logic, Window, Utility). Opening one lists
  its blocks; clicking a block adds it after the selected one. If, Loop and Repeat until get their End added too.
  Blocks that need screen reading are marked "Coming soon" and fault if run.
- Each row has a breakpoint dot, an on/off box, its number, a colored tag and actions (duplicate, move up,
  move down, delete). The Delete key removes the selected block. Rows inside a Loop, If or Repeat until are
  indented.
- The panel on the right edits the selected block's params, with the limits from `MacroLimits`.
- **Step** starts paused and runs one block per press. **Run from** starts at the selected block. **Play**
  stops at breakpoints; press it again to continue.
- The gear sets the macro's start/stop key, trigger mode (toggle, hold, once) and run alongside.
- Saved macros can be put in groups (name and color), searched, given their own start/stop key, and switched
  on and off from the list. F9, F6 and Ctrl+Alt+End are reserved for Record, Play and Stop all.
