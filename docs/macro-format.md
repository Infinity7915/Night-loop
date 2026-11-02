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

## Disabled blocks

A disabled block does nothing and keeps its index. A disabled If behaves as if its condition were true.
A Loop or Repeat until whose opener or End is disabled runs its body once.

## Implemented now

Input (key, click, move, drag, scroll, type), camera.turn, wait, random wait, pause/resume another macro,
if/else, loop, repeat until, label, go to, stop, window resize/position/focus, comment, description,
notify, play sound, run macro. Conditions: `windowFocused`, `processRunning`, `not`, `all`, `any`.

Text, image and pixel detection (and the blocks that use them) fault with a clear message until the
Vision milestone.
