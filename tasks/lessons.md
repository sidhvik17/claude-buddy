# Lessons

## 2026-10-02 — Multi-agent runs spend the user's quota

**What happened:** an eight-agent review workflow ran in the background twice. Both
times the session ended before it finished (the second time the user hit their usage
limit), so the quota was spent and no findings came back.

**Rule:** before launching a multi-agent workflow, say roughly what it costs and that
it produces nothing if the session ends first. When the user is close to a limit, do
the review inline or wait. Never relaunch a dead workflow without saying so.

## 2026-10-01 — Do not drive the user's live desktop without asking

**What happened:** to test the buddy's right-click menu I wrote a script that moved the
mouse cursor, posted clicks to the window and sent an Escape keystroke. The user
rejected it.

**Rule:** anything that moves the cursor, sends keystrokes, posts input to windows or
switches what the user's apps are showing needs an explicit yes first. Say what it will
do on their screen, then ask. Prefer tests that need no input injection (self-tests,
`--dump`, `--render`, `--state`), and for the parts that truly need a click, ask the
user to click it themselves and read the log afterwards.

## 2026-10-02 — Measure the reference before drawing it

**What happened:** I drew the mascot from a glance at the reference image and from
memory of the terminal art. The user said it did not resemble the original. Dumping
the image as a character grid showed the real proportions in one minute: 8 x 6 body,
2 x 2 arms, square eyes, 1-wide legs flush with the body edges. Mine was 2:1 and
thin-armed.

**Rule:** when asked to reproduce a picture, measure it first (pixel grid, bounding
boxes, unit size) and derive the geometry from the numbers. Eyeballing a thumbnail is
a guess, and the user sees the difference immediately.

## 2026-10-02 — Short instruction, short action

**What happened:** the user wrote "TRIAL RUN FOR 1". I answered with a 20-second
command that held the session busy and sampled state. The user rejected it. Twice
before, they also rejected commands that bundled extra steps around what they asked.

**Rule:** when an instruction is terse or ambiguous, do the smallest thing that matches
it and say in one line what was assumed. One command, one purpose: no bundled extras,
no deliberate waiting. If the user wants to test by hand, start the thing and hand over
a checklist instead of running a test for them.

## 2026-10-01 — Count before quoting a number

**What happened:** I reported "44 self-tests pass" from memory. The report file had 49.

**Rule:** read the number from the output before putting it in a status message.
