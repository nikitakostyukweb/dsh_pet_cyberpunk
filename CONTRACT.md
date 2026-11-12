# EdgeRunners DSH port — shared contract

This package ports the Claude Code mod `Alexanderadon/edgerunners` to the DeepSeek Harness
desktop app. The original mod (MIT, (c) Alexanderadon) is cloned at `../_research/edgerunners`.
Art, the Windows pet engine (`pet.cs`/`pet.ps1`) and the call popup are reused unchanged (patched
where noted) with attribution kept in `LICENSE`.

Two halves talk through two interfaces:

```
DeepSeek Harness Host (index.js, Lead)
        │  calls director methods on events
        ▼
hooks/director.js  ──emit(state)──►  index.js writes assets/pet/state.json
        │                                  │
        │  reads nothing from disk         ▼
        │                          pet.cs reads state.json every 100 ms and draws
        └── uses hooks/mod-data.js
```

## 1. `assets/pet/state.json` — the pet engine's contract (frozen)

The engine (`assets/pet/pet.cs`, `ParseState`) reads exactly this shape. `Step` is
`[frame, ms]` or `[frame, ms, fx]` where `fx` is `"hop"` or `"shake"`:

```json
{
  "seq": "1700000000000-123456",     // new string whenever the scene changes; same seq = same scene
  "session": "session-id",           // id of the followed session; "demo" is always accepted
  "busy": false,                     // true between prompt submit and turn complete
  "hero": "kadr",                    // sprite folder name under assets/pet, or null for a hero without art
  "name": "Роман",
  "role": "оператор",               // optional, "" if none
  "timeline": [["nod_01", 160], ["nod_02", 110]],
  "loop":     [["idle_01", 4200], ["idle_02", 110]],
  "line": "Снимаю трюки Талии!",
  "alive": 1700000000000,            // ms epoch, the mod must refresh it at least every 15 s (engine leaves after ~60 s of silence)
  "roster": [ { "id": "roman", "sprite": "kadr", "name": "Роман", "role": "оператор", "about": "…", "stats": [["обаяние", 0.9]] } ],
  "pokes": [ [["laugh_01", 200], ["laugh_02", 110]] ],
  "pokeLines": ["…"],
  "pokeMad": ["…"],
  "enter": [["bow_01", 180], ["hello_01", 1040]],
  "costar": { "sprite": "talia", "name": "Талия", "stunts": [ [["run_01", 120]] ] },
  "chats": 2,                        // optional: number of open (live) main sessions, drawn in the monitor HUD
  "busyChats": 1,                    // optional: how many of them are working right now
  "spendRub": 20.7,                  // optional: money spent on tokens today (rubles, monitor HUD SPEND row)
  "spendBudgetRub": 100              // optional: daily budget for the SPEND gauge
}
```

`timeline` plays once from the current moment, then `loop` repeats. `enter` is played by the
engine itself when the hero was picked in its right-click menu (the mod only learns about that
through `assets/pet/choice.json`).

The engine is positioned in the bottom-right corner of the **screen working area** (never the Harness
window), is `WS_EX_TOPMOST` and stays visible while the app is minimized; it keeps running as long as
`alive` is refreshed. It also *writes* two diagnostic files and two the mod polls:

- `assets/pet/pet.beat` — `{"pid":<int>,"at":<ms>}`, refreshed every 5 s, deleted on graceful exit.
  The plugin relaunches the pet when this heartbeat is older than ~25 s (and an active session exists).
- `assets/pet/pet-crash.log` — written by the engine when a menu/item/draw handler throws; absence of
  this file means the window has not thrown. Use it to diagnose the "PowerShell error" class of bugs.

- `assets/pet/choice.json` — `{"hero":"wick","at":<ms>}` written when a hero is picked in the
  pet menu; `at` is a timestamp, process only values newer than the last seen one.
- `assets/pet/settings.json` — `{"lang":"ru"}` / `{"lang":"en"}`, booleans, hero size, `posX`/`posY`
  (saved window position), `mini` (mini mode: small 120×120 avatar instead of the hero, window 400×310),
  `monitor` (system-monitor HUD: CPU/RAM/GPU/DISK/NET/CHATS), `monX`/`monY` (the monitor panel is a
  separate always-on-top window in the same process; its own saved position, dragged independently).
  The director reads `lang`; the rest belongs to the engine (drag-and-drop, mini mode).

## 2. `hooks/director.js` — API the Lead's `index.js` drives

Pure ESM JavaScript, no Node built-ins, no I/O, no timers of its own. Every visual decision is
made here; `index.js` only translates Harness events into calls and performs side effects
(writing `state.json`, launching the pet, reading `choice.json`/`settings.json`) through the
callbacks it passes in.

```js
import { createDirector, DEFAULT_HERO, heroList, heroReport } from './hooks/director.js'

const director = createDirector({
  now: () => Date.now(),                 // ms epoch
  random: () => Math.random(),           // optional, for tests
  log: (msg) => {},                      // optional diagnostics
  emit: (state) => { /* full state.json object, already JSON-ready */ },
})

director.setSession('session-id')        // followed session (string). Called before the events below.
director.setLang('ru')                   // 'ru' | 'en'
director.start()                         // session start: pick stored/default hero, play the greeting
director.promptSubmit({ text, machine }) // a human prompt (machine=false) or a task-notification (machine=true)
director.turnStart()                     // a turn started without a prompt.submit (continuation)
director.toolCall({ name, input })       // tool is about to run; input is the raw tool arguments object
director.toolResult({ name, input, isError, background }) // background=true when it started a background job
director.turnComplete({ answer, durationMs, reason })     // reason: 'answer' | 'aborted' | 'error'
director.tick()                          // call every 1000 ms: alive pulse, rest poses, fidgets, deferred work scene
director.chooseHero(id)                  // returns { ok, name, text } — text is the chat reply
director.report()                        // returns the /report text for the followed session
director.attention(text)                 // ask-the-human line: question/confirmation; raises the hero
                                         // (point gesture) when he was resting, then emits the state
director.current()                       // last emitted state object (for tests)
```

`emit(state)` must be called on every change and at least every 15 s. It receives the complete
`state.json` object from section 1 (minus `alive`, which the director fills with `now()`).

Mapping Harness tools to the mod's vocabulary happens inside the director: `read`→Read,
`write`/`edit`/`apply_patch`→Write/Edit, `grep`/`glob`→Grep/Glob, `pwsh`/`bash`→Bash,
`web_search`→WebSearch, `web_fetch`→WebFetch, `subagent`/`subagent_fork`/`send_message`→Agent,
unknown→Bash-like "film". The original `hooks/deeds.ts` already knows how to phrase a deed from
that vocabulary; keep its rules.

## 3. Where things live

Plugin config (`cordis.patch.yml`): `launchPet` (default true), `activeSessionQuietSeconds` (default 30 —
how long the active session may be silent before the pet follows another session that is working) and
`completionCallAfterSeconds` (default 15 — backup "done" call threshold when the director did not ring).
Call cards come from `director.lastCall` and from a backup on `turn/end`; `tools/pre-execute`,
`approval/request` and `user-questions/request` are registered prepended so their decisions are seen.

| Path | Owner | Contents |
|---|---|---|
| `index.js`, `package.json`, `cordis.patch.yml`, `locale/`, `icon.svg`, `README.md` | Lead | Host plugin wiring, commands, install |
| `hooks/**` | director-dev | `mod-data.js` (bundled data from the original `hooks/*.ts`) and `director.js` |
| `assets/**` | pet-engine-dev | copied art, patched `pet.cs`, `pet.ps1`, `popup/`, `sounds/`; the popup takes `-Kind call|question|approval` (green/yellow/red, single ACCEPT button) |
| `_research/edgerunners/**` | read-only | the original mod, do not edit |

## 4. Verification expected

- `node --experimental-strip-types --no-warnings _research/edgerunners/tools/pet-demo.mjs check`
  must report zero missing frames: every frame a director timeline can name exists on disk.
- The director must be testable head-less by a script that simulates a session (prompt → tools →
  turn complete → idle) and prints/validates the emitted `state.json`s, including that idle
  reaches `sit`/`lie`/`sleep` after the configured quiet time and that a hero change from
  `choice.json` is applied exactly once.
