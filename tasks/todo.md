# Claude Buddy — plan

Desktop pet for Windows: the Claude Code pixel mascot sits on top of the desktop 24/7,
animates according to what Claude Code is doing, and opens the Claude desktop app
(Code tab / the relevant session) when clicked.

## Findings that shaped the design

- Claude Code writes one live status file per session: `~/.claude/sessions/<pid>.json`
  with `status` = `busy` | `shell` | `idle` | `waiting` (+ `waitingFor`), plus `name`,
  `cwd`, `sessionId`, `hostSessionId`. Polling these needs **no hooks and no edits to
  the user's Claude settings**.
- There is no `error` status. A turn that ends in an API error (rate limit, overloaded,
  connection lost) leaves a synthetic assistant record with `"isApiErrorMessage":true`
  at the end of the transcript `~/.claude/projects/<enc-cwd>/<sessionId>.jsonl`.
- Desktop app is MSIX-packaged; the update-proof way to open it is the execution alias
  `%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe "claude://code/..."`.
- Shells spawned by the desktop app get AppData writes redirected into the package
  store, so the runtime is installed to `%USERPROFILE%\.claude-buddy` (not AppData)
  and the app registers its own autostart (HKCU Run) when it runs.
- Only toolchain needed: `csc.exe` from .NET Framework 4.8 (ships with Windows, C# 5).

## Light semantics

| Light  | Meaning                                                        |
|--------|----------------------------------------------------------------|
| Yellow | a session is waiting on you (permission prompt / question)     |
| Red    | a session's last turn died with an API error (last 30 min)     |
| Green  | a session is working                                           |
| Grey   | sessions open, all idle                                        |
| Off    | no sessions (buddy sleeps)                                     |

Priority when several sessions disagree: yellow > red > green > grey > off.

## Checklist

- [x] Research: status source, deep links, toolchain, container behaviour
- [x] `src/` — MiniJson, Sessions (scan + liveness + error probe), Sprite (poses + render),
      BuddyForm (layered window, drag, click, menu, tray), Launcher, Config, Program
- [x] `build.ps1` (csc) + icon generation (no install script: the exe self-registers autostart)
- [x] `--selftest` passes: 49 checks (state mapping, helper-fork filter, liveness, error probe, JSON)
- [x] `--render` sprite sheet looks right for every state
- [x] `--dump` against the real `~/.claude` matches reality (this session = working)
- [x] Live `--dev` run: buddy visible on screen, green while this session works
- [x] README.md: build guide + how it works
- [x] Click opens Claude on the right session (automated click test, user approved)
- [x] Menu, drag, position memory, size, tooltip, tray, on-top, exit (user tested: all pass)
- [x] Yellow shown from a `waiting` state file (copied real file with status changed)
- [x] Hide/show: menu item + global hotkey Ctrl+Alt+H (configurable), tray click restores
- [x] "Preview light" menu to see every state on demand
- [x] `install.ps1` / `uninstall.ps1`; installed to `%USERPROFILE%\.claude-buddy`, Desktop
      shortcut created, autostart + hotkey registered (per installed log)
- [x] User confirms round 2 tests (hide, hotkey, preview, shortcut, sign-in start)
- [x] Sprite redrawn from a pixel-grid measurement of the reference (8x6 body, 2x2 arms,
      square eyes); working animation rotates laptop / running / thinking
- [x] Mac: user chose Windows-only; README says so
- [x] Project moved to `Desktop\claude-buddy`
- [x] Yellow on a real permission prompt and red on real rate-limit errors: both in the
      installed buddy's log (2026-10-02)
- [x] Usage-limit countdown on the forehead (reset time parsed from the limit message),
      preview menu entry, 73 self-test checks, reinstalled
- [ ] Usage-limit countdown seen against a real limit
- [x] Independent multi-lens code review (third run completed): 18 findings confirmed,
      2 rejected, all 18 fixed, reinstalled. See Review below.
- [x] GitHub: user pushed it themselves, first commit `d0d81e9`, private repo
      https://github.com/sidhvik17/claude-buddy (note: the Desktop folder and the home
      folder are also git repos, so always `pwd` before `git add`)

## Review

Four reviewers (24/7 stability, Win32 UI, state logic, install/security), each finding
then attacked by a second agent. 18 survived, 2 were refuted. Reviewers said they held
back 5 more beyond the 5-per-lens cap; those were never reported.

Fixed (all 18):

| Area | What was wrong | Fix |
|------|----------------|-----|
| DPI (2 findings) | Resizing inside `WM_DPICHANGED` could ping-pong between monitors with different scaling, or leave the sprite past the screen edge | Resize after the notification (`SyncDpi`), never flip monitors while resizing, clamp into the work area |
| Limit / error | A slash command or task notification after an error cancelled it | Scan back to the last assistant record |
| Limit | A reply already streaming when the limit hit counted as "limit lifted" | Judge replies by when their request was sent |
| Liveness | Stale state file + pid reused by a system process showed as a live session | Unreadable start time is trusted only if the process is `claude`/`node` |
| Probe | One failed transcript read was cached | Record the file stamp only after a successful read |
| Click | Clicking to a waiting session silently acknowledged other sessions' errors | Acknowledge only when the lamp was red |
| Click | Double-click opened Claude twice | Ignore the second press |
| Menu | Menu left the invisible window holding keyboard focus | Give the foreground back on close |
| Menu | Once a second the sprite climbed over its own tray menu | Skip the on-top re-assert while the menu is open |
| Drag | Right-click or lost capture mid-drag dropped the position | Commit the drag; no menu mid-drag |
| Displays | After undock/re-dock the sprite never returned to its saved spot | Re-apply the saved position on display change |
| Log | A locked log file stopped logging once it passed 512 KB | Rotation failure no longer blocks the append |
| Uninstall | `-InstallDir` was deleted recursively, whatever it contained | Delete only the buddy's own files; remove the folder only if empty |
| Uninstall | Did half the job silently after a custom install | Find the install through the autostart entry; warn if not found |
| Install | A relative `-InstallDir` broke running-copy detection | Resolve to an absolute path first |
| CLI | A mistyped flag started a normal buddy and re-pointed autostart at the build folder | Unknown or incomplete arguments exit with code 2 |
| Docs | README said to edit a `hotkey=` line that did not exist on a fresh install | `config.ini` is written with defaults on first start |

Refuted as defects (both describe the code correctly, the consequence is harmless):
"the scan runs on the UI thread" (reads are cached and local; a stall needs the Claude
folder on a hung network drive, and only freezes the sprite) and "the limit is forgotten
while the limited session is briefly busy" (the countdown is hidden while working
anyway; it costs a pair of log lines per retry).

Not verifiable on this machine: the DPI fixes need two monitors with different scaling.
