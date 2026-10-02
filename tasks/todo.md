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
- [ ] Independent multi-lens code review: started twice, both runs died when the session
      ended (usage limit). Not rerun automatically because it spends a lot of quota.
- [ ] GitHub: user pushes it themselves from the step list, run inside `Desktop\claude-buddy`
      (the Desktop folder itself is an old, empty git repo: do not run `git add` there)

## Review

(filled in at the end)
