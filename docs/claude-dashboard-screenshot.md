# Retaking the README screenshot

`docs/claude-dashboard-screenshot.png` is a capture of the real application, not a mockup. This
is how it is retaken when the UI moves — and it has already moved once, which is why this file
exists rather than the knowledge living in whoever took it.

## The rule that matters most

**Never screenshot the live dashboard.** This repository is public, and a real panel shows
session titles, prompts, answer text and full working-directory paths — the operator's actual
work. Every capture runs against an **isolated instance** with its own data folder, seeded with
the fictional workspaces below, which are the same ones the design documents use.

```powershell
$env:CLAUDE_DASHBOARD_HOME  = "<a temp folder>"   # its own database, port, settings and logs
$env:CLAUDE_DASHBOARD_TOKEN = "readme-shot"
```

`CLAUDE_DASHBOARD_HOME` also keys the single-instance gate, so the isolated copy runs happily
beside a real one and cannot touch its database or its port.

## Seeding

Sessions arrive the way they always do — over `POST /hook`, with `X-Dashboard-Token`. The port
is in `port.txt` under the data folder.

**The wire spellings have to be exact**, and this is the trap: an unrecognised value parses to
`Unknown` rather than failing, so a typo produces a panel with the wrong counts and no error
anywhere. The three that matter here are `permission_prompt`, `agent_needs_input` and
`rate_limit`.

| Row | Events |
|---|---|
| Question | `SessionStart` → `UserPromptSubmit` → `Notification` (`agent_needs_input`) |
| Permission | `SessionStart` → `UserPromptSubmit` → `Notification` (`permission_prompt`, `prompt` carries the tool) |
| Error | `SessionStart` → `UserPromptSubmit` → `StopFailure` (`rate_limit`) |
| Finished | `SessionStart` → `UserPromptSubmit` → `Stop` |
| Working | `SessionStart` → `UserPromptSubmit` → `PostToolBatch` |
| Quiet | `SessionStart` alone — a just-started session files under Acked |

Do **not** send `session_title`. With one the row renders `Title — prompt`; the screenshot shows
the prompt alone, as the mockups do.

## Time is part of the fixture

Two things in the panel are only true after real time passes, and neither can be set:

- **Row ages.** They come from the server clock at the moment the event arrives, and no hook
  field back-dates them. Seed, then wait — a capture taken immediately reads `22s` everywhere,
  which looks like a test harness rather than a working day.
- **Collapsed quiet groups.** A group folds to one line after 15 minutes of quiet
  (`MainViewModel.DefaultStaleAfter`), and that interval is not configurable. Seed the quiet
  sessions first and do something else for a quarter of an hour.

## Capturing

**Use `PrintWindow` with `PW_RENDERFULLCONTENT` (`0x2`), not `Graphics.CopyFromScreen`.**
Measured, not assumed: `CopyFromScreen` returned a blank white rectangle for this window on a
single-display machine at 150%, while the process was foreground and responding. `PrintWindow`
asks the window to render itself, so it also works when the window is not on top — which means
nothing has to be pinned over whatever the operator is doing.

**Set DPI awareness before touching any screen coordinate:**

```csharp
SetProcessDpiAwarenessContext(-4);   // PER_MONITOR_AWARE_V2
```

Without it Windows virtualises the coordinates for a DPI-unaware process and the capture
silently reads the wrong region. It does not error.

**Take it at 100% display scaling.** The caption's summary is laid out in `Display` text
formatting, which quantises glyph advances to whole *device* pixels — so its widths, and the
width at which counts drop out of it, move with the monitor's scale. `GetDpiForWindow` returning
96 is the check.

## Framing

The window is sized to its content before capture: wide enough that the caption's summary shows
every count it has, tall enough that the last group is not cut. Both are read off the capture and
adjusted, because both move with the seed data.

The README uses the **Grouped** view alone. `docs/Claude-Dashboard-ReadMe-Mockup.png` pairs
Grouped with Flat, which explains the sorting rule better but produces a wide image that a phone
shrinks to nothing. One panel reads at any width.
