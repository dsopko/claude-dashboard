# Task: branded caption (design option 2c) for the dashboard window

Replace the stock Windows title bar on `MainWindow` with the custom caption from design option **2c — Branded caption, the Word treatment**, and re-lay out the toolbar row beneath it to match. This is a visual change to the top of the window only. Session rows, the tray icon, and all behaviour stay as they are.

## Design source

Use the claude_design MCP (https://api.anthropic.com/v1/design/mcp, auth via /design-login) to import this project:
https://claude.ai/design/p/6df6df4b-1025-4f62-aaa0-a5fc79bd5a11?file=Claude+Dashboard+Window.dc.html

Focus on these files (the whole project is readable):
- `Claude Dashboard Window.dc.html`

Also read these files the selection imports:
- `_ds/classical-claude-97925898-7f68-4d74-b8f9-cc52ae8ac8ce/_ds_bundle.js`
- `_ds/classical-claude-97925898-7f68-4d74-b8f9-cc52ae8ac8ce/styles.css`
- `support.js`

The file shows several options. Implement **2c only**. Take colours, caption height, type sizes and spacing from `styles.css` and the 2c markup — not from a screenshot, and not from your own taste.

## What 2c is

- A taller caption that belongs to the app: tinted dark background (not flat grey) with a faint warm rule along its bottom edge.
- Left: the application icon, then the title "Claude Dashboard" in the design system's serif.
- Right: a summary slot ("11 sessions · 3 need you"), a `?` button, a divider, then minimize / maximize / close.
- Below it, the existing toolbar row: Grouped | Flat on the left, Select and Mute all on the right.

## How to build it

- WPF `WindowChrome` (`System.Windows.Shell`): `CaptionHeight` at the design's height, `UseAeroCaptionButtons="False"`, the caption drawn as ordinary XAML, and every interactive element in it marked `WindowChrome.IsHitTestVisibleInChrome="True"`. Caption buttons go through `SystemCommands` (`MinimizeWindow`, `MaximizeWindow`/`RestoreWindow`, `CloseWindow`).
- Do **not** use `WindowStyle="None"` with `AllowsTransparency="True"` — it drops the DWM shadow, Windows 11 rounded corners and snap animations. If those vanish, `GlassFrameThickness` is the knob.
- No new UI library (WPF-UI, ModernWpf, MahApps). `WindowChrome` is in the box.
- Close keeps its current meaning: it hides to the tray, exactly as the stock X did. Route it through the same path.
- The icon slot shows `Assets/app.ico` from the application-icon issue at 16–20 DIP, using the matching frame so it stays crisp. The glyph in the mockup's slot is a placeholder; don't draw a new one. If that issue hasn't landed yet, leave the slot empty.
- "N sessions · N need you" is exposed on the window's view model from the same counts the tray tooltip already computes. "Need you" is the count the tray calls need-you (permission / question). The count's colour is the existing attention brush for that state, not a new red.
- Colours and fonts become named resources in the existing resource dictionary; no hex literals in `MainWindow.xaml`. If the design's serif isn't installed on Windows, use the nearest system serif and say so in your report — don't download fonts.
- Toolbar row: restyle and re-lay out to the mockup's idle state. Everything it doesn't show (selection mode, its count and buttons) keeps working as it does today.

## Must still work

Drag by the caption; double-click to maximize/restore; right-click on the caption and Alt+Space open the system menu; resize from every edge; Win+arrow snapping; Alt+F4; taskbar and Alt-Tab still show the title and icon. When maximized, content must not run off the screen — `WindowChrome` windows overflow by the resize border, so fix it (a margin bound to `WindowState`, or `WM_GETMINMAXINFO`). Check at 100% and 150% scaling. The Snap Layouts flyout on hovering maximize needs `WM_NCHITTEST` to return `HTMAXBUTTON` for that button (plus `WM_NCLBUTTONUP` handling so the click still works); do it if it stays small, otherwise leave it out and say so. Give the three caption buttons `AutomationProperties.Name`.

## Out of scope

The tray icon and `TrayIcons.cs` — no changes. Session rows and everything below the toolbar. Any behaviour change. Any other option in the design file. New tests: nothing here is unit-testable in Core, and no UI-automation tests for this task.

## Process

Before changing code, reply with a short plan: the files you'll touch; the exact values you pulled from `styles.css` (caption height, colours, font family and sizes, spacing); what the summary slot shows in each state — the current toolbar's unread count is not in the mockup, so say where it goes; what the `?` button does if there is already a help/about action, or that you're omitting it because there isn't one; anything in 2c you can't match. Wait for my OK.

Then implement, run the existing tests, add a task line for this to `docs/claude-dashboard-execution-plan.md` so the plan and the build agree, and report with a checklist of the "must still work" items marking which you verified by hand.

## Done when

The window matches 2c at 100% and 150% scaling; every "must still work" item passes or is listed as skipped; existing tests pass; the tray icon, session rows and publish output are unchanged.
