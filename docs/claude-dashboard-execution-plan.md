# Claude Dashboard — Execution Plan

**Draft v0.1 · 2026-08-22 · for the director agent**

**Director - coder - reviewer orchestration starting at Apendix B**

## Part 0 — How to use this plan

This plan is written to be consumed by a **director agent** that turns it into prompts for a **coder agent**. It is not itself the architecture — the architecture lives in two companion documents, and this plan points into them rather than repeating them:

- **Technical Specification** (`claude-dashboard-spec.md`, "TS") — the *why*, technology-agnostic. Numbered **Parts I–V**.
- **Implementation Specification** (`claude-dashboard-impl-spec.md`, "Impl") — the *how*, C#/.NET 10/WPF.
- **Design Document** (`claude-dashboard-design.md`, "Design") — the product shape: the attention model, view modes, sound design, and **§9 Main window anatomy**, which is the authority on row structure and the motion discipline ("red blinks; working breathes; nothing else moves"). Numbered **§1–§12**.

> **Correction (2026-08-24, found at T1.11 drafting).** T1.10, T1.11 and T1.13 previously
> cited "TS §5, §7", "TS §5–§9" and "TS §9". Those sections do not exist — the TS is
> numbered in Roman parts, I–V. The intended target in every case was the **Design
> Document**, whose §5–§9 carry exactly the content those tasks need. Left uncorrected,
> a coder would have followed the reference into the TS, found Part V (a
> mechanism-to-phase map), and built the UI from the mockups alone — without the row
> anatomy or the motion rule. Note also that the Design Document was **not listed among
> the authoritative documents in `CLAUDE.md`**, so it would not have been read at all.
>
> **Swept 2026-08-24.** Every `TS §<roman>` reference in this plan resolves to a real
> section. The broken ones were exactly those written with **Arabic** numerals, and all
> five are now corrected to `Design §…`: T1.1 (§4), T1.10 (§5, §7), T1.11 (§5–§9),
> T1.12 (§4), T1.13 (§9). **Rule of thumb for future edits: the TS is cited `TS §I.2`
> style, the Design Document `Design §4` style. An Arabic numeral after "TS" is a bug.**

**The director's loop, per task:**

1. Take the next task whose dependencies are all `Done`.
2. Build one coder prompt from it using the skeleton in Part 5, pasting in the task's spec references and the global working agreements (Part 1).
3. Hand it to the coder; require the task's **acceptance criteria** to be met and its named tests to pass before marking it `Done`.
4. Do not batch multiple tasks into one prompt, and do not advance past a task whose acceptance criteria are unmet. One task ≈ one coder prompt ≈ one commit/PR.

**Scope of this draft:** Phase 1 is specified at task level (Part 3) because it is what gets built now. Phases 2–7 are task *outlines* (Part 4) — enough to sequence and plan, to be expanded into full task blocks when their phase is reached.

---

## Part 1 — Global working agreements (inject into every coder prompt)

These hold for every task. The director should include them (or a link to them) in every prompt so the coder never has to re-derive them.

**Architecture & dependencies**
- Three projects (Impl §1.2). `ClaudeDashboard.Core` has **no** WPF, Win32, COM, or ASP.NET references. `ClaudeDashboard.App` may reference Core; **nothing references App**. `ClaudeDashboard.Remote` (later) references Core only.
- All OS-specific behavior lives in App **behind port interfaces** declared for Core (Impl §1.3). The coder implements adapters, never calls Win32/UIA from Core.

**Domain invariants**
- State transitions are **idempotent** and **timestamp-guarded**: re-applying an event is a no-op; an event older than the session's last-applied stamp is dropped (TS §I.2, §IV.1).
- The `SessionRegistry` is mutated by **exactly one thread** (the event consumer); therefore **no locks** inside it (Impl §4).
- Ingress hooks are **pure observers**: `/hook` returns `200` empty and never a decision field, so the dashboard can never block or alter a Claude turn (Impl §3.3).
- **All hook text is data** — prompt/answer strings are stored and rendered, never executed or interpreted (Impl §3.4).

**Safety & degradation**
- OS adapters (UIA, WinEvent, virtual desktop) **degrade, never crash**: a failure downgrades a feature to a coarser fallback (TS §IV.7). Wrap them; surface faults to logs, not to the process.
- The app runs at **normal integrity, never elevated** (Impl §6.5).
- **No secrets in committed files** — the ingress token comes from an environment variable (Impl §3.4, §9.2).

**Engineering standards**
- Nullable reference types enabled; warnings-as-errors on Core at minimum; analyzers on.
- `async` end to end on I/O paths; never block the WPF Dispatcher thread.
- Every Core behavior (state machine, attention ordering, grouping, sound policy) ships with **xUnit tests**. Adapters get contract tests against fakes where feasible; genuinely OS-bound behavior is verified by a documented manual smoke test.
- **Where a third-party library does work on its own threads, green is not evidence — launch it.** *(Standing exception, added 2026-08-24 from T1.13.)* The manual bar elsewhere is "what cannot be observed in-process without ending or hanging the run, not what is merely awkward". This is a third thing: a region in-process testing **cannot observe at all**. H.NotifyIcon converts its icon on a thread-pool continuation, so three separate startup crashes — each killing the process before a window appeared — left **818 tests green**, because the throw never reached the test thread and the icon simply never appeared. Not a forgotten test; no test could have been written. Any task adopting a component that does work on threads it owns must actually run the app, and say so.
  - Two corollaries from the same incident, both cheaper than the debugging they replace. **Read the library's own metadata before the second attempt, not the third** — the three crashes were not three bugs but *one wrong belief surviving two corrections*, because each fix changed the input while keeping the belief. And **a live run only exercises the paths it happens to take**: the icon cache was safe against reuse-after-dispose, but the running app went grey → blue → amber → red and never back, so the cycling case was proved by a written probe, not by use.
- Small, single-purpose commits, one per task.

**Definition of Done (global):** builds clean; named tests green; acceptance criteria met; no cross-layer leakage (verified by the dependency rule); logs on the new paths.

---

## Part 2 — Milestones

| Phase | Outcome | Exit criteria |
|---|---|---|
| **1 — See clearly** | Resident tray app shows every live session's state, banded and grouped, with sound and manual/auto ack — no Windows integration. | Real Claude Code sessions across ~15 terminals light up the dashboard and tray correctly; notices/nudges fire; ack tiers 1–2 work; survives logon restart. |
| **2 — Go there** | Click a row → its terminal tab comes forward. | Navigation resolves the correct tab via content-matching for the common case; degrades to window-level otherwise. |
| **3 — It notices** | Looking at a terminal acknowledges it; on-screen sessions don't beep. | Focus inference acks at window (then tab) granularity; suppression works. |
| **4 — Task lens** | Grouping by virtual desktop, with desktop names. | Sessions group by desktop; degrades to cwd grouping if VD breaks. |
| **5 — Memory** | Searchable history + wait-time stats; warm restart. | 30-day event history queryable; restart rebuilds recent state. |
| **6 — Polish** | Settings UI, sound editor, themes, task/hook repair. | Settings editable in-app; setup repairable. |
| **7 — Anywhere** | Authenticated phone read/ack surface. | Remote consumer reads state and acks over an authenticated channel. |

---

## Part 3 — Phase 1 tasks (detailed)

Grouped into four sub-milestones. Each task lists Goal · Depends · Realizes · Deliverables · Acceptance · Guardrails. Tests named in Acceptance are required.

### Milestone 1A — Core (portable domain, no host)

**T1.0 — Solution & project scaffolding**
- **Goal:** create the four-project solution with correct TFMs, references, and analyzer/nullable settings.
- **Depends:** —
- **Realizes:** Impl §1.1–1.2
- **Deliverables:** solution; `Core` (`net10.0`), `App` (`net10.0-windows`, WPF), `Remote` stub (`net10.0`), `Tests` (`net10.0`, xUnit); reference wiring; nullable + analyzers; a build script.
- **Acceptance:** solution builds; `dotnet test` runs an empty suite; an architecture test (or documented reference check) fails if Core gains a WPF/Win32/ASP.NET reference or anything references App.
- **Guardrails:** dependency rule (Part 1).

**T1.1 — Core domain types**
- **Goal:** the immutable domain vocabulary.
- **Depends:** T1.0
- **Realizes:** Design §4; Impl §2.1
- **Deliverables:** `SessionId`, `Exchange`, `SessionState` enum, `Session`, `Group`, and the `InboundEvent` record hierarchy (one variant per consumed event, Impl §9.1).
- **Acceptance:** tests for construction, value-equality, and the `InboundEvent` variants carrying the fields from Impl §9.1.
- **Guardrails:** Core-only; no behavior yet.

**T1.2 — SessionRegistry & state machine**
- **Goal:** apply events to sessions per the state machine.
- **Depends:** T1.1
- **Realizes:** TS §IV.1; Impl §2.2
- **Deliverables:** `SessionRegistry` with `Apply(InboundEvent)`; the full transition table; a change-notification event.
- **Acceptance:** tests for every transition (Working→Unread on Stop; →NeedsPermission/NeedsQuestion on the Notification variants; →Error on StopFailure; →Ended on SessionEnd; →Working + **auto-ack** of prior Unread/Needs-You on UserPromptSubmit; manual/synthetic Ack→Acked); **idempotency** (same event twice = one effect); **stale-drop** (older-timestamp event ignored).
- **Guardrails:** single-writer assumption (no locks); `IClock` for time.

**T1.3 — Attention engine**
- **Goal:** band and order sessions for display.
- **Depends:** T1.2
- **Realizes:** TS §IV.2; Impl §2.3
- **Deliverables:** pure `Order(sessions) → banded ordered list`.
- **Acceptance:** tests proving Needs-You **oldest-first**, Unread **newest-first**, then Working/Quiet/Ended; band precedence; and the grouped case ordering within groups with groups sorted by most-urgent member.
- **Guardrails:** pure/deterministic; no side effects.

**T1.4 — Group resolver**
- **Goal:** derive groups from `cwd`.
- **Depends:** T1.2
- **Realizes:** TS §IV.3; Impl §2.1
- **Deliverables:** grouping by `Cwd`; worst-member-state and most-recent-activity per group; re-derivation when a session's `cwd` changes.
- **Acceptance:** tests for grouping, worst-state roll-up, recency, and re-grouping on cwd change.
- **Guardrails:** Phase 1 key is `cwd` only (desktop grouping is Phase 4).

**T1.5 — Sound-policy engine**
- **Goal:** decide when notices and nudges fire.
- **Depends:** T1.2
- **Realizes:** TS §IV.5; Impl §2.4
- **Deliverables:** engine emitting `PlayNotice`/`PlayNudge(gain)` intents against `ISoundPlayer`, driven by Registry state and `IClock`; per-session/group mute honored.
- **Acceptance:** tests for notice-on-entry; nudge after T₁ with **widening** intervals (2→5→10 min); **cancel on Acked**; Unread at most one soft nudge; mute suppresses.
- **Guardrails:** emits intents only; never touches audio APIs.

**T1.6 — Port interfaces**
- **Goal:** declare the host seam.
- **Depends:** T1.1
- **Realizes:** Impl §1.3
- **Deliverables:** `IClock`, `ISoundPlayer`, `IEventSink`, and the (as-yet-unimplemented) `ITerminalLocator`, `IFocusSource`, `ITerminalNavigator`, `IVirtualDesktopService`.
- **Acceptance:** compiles; Core references none of their implementations; fakes exist in Tests for the ones Phase 1 uses (`IClock`, `ISoundPlayer`, `IEventSink`).
- **Guardrails:** interfaces only.

### Milestone 1B — Host, ingress, pipeline

**T1.7 — Generic Host bootstrap**
- **Goal:** the app process skeleton.
- **Depends:** T1.0
- **Realizes:** Impl §3.1, §5.1, §10.1
- **Deliverables:** .NET Generic Host with DI; config loaded from `%LOCALAPPDATA%\ClaudeDashboard\settings.json`; Serilog rolling logs; global exception handlers (`AppDomain`, `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException`); `ShutdownMode.OnExplicitShutdown`.
- **Acceptance:** app starts headless and logs; a thrown test exception is caught and logged, not fatal; settings round-trip.
- **Guardrails:** no business logic here.

**T1.8 — Ingress endpoint + payload mapping**
- **Goal:** receive hooks and normalize them.
- **Depends:** T1.7, T1.1
- **Realizes:** Impl §3.2, §9.1
- **Deliverables:** Kestrel minimal API bound to loopback fixed port; `POST /hook` (deserialize by `hook_event_name`, map event-specific fields per Impl §9.1 to an `InboundEvent`, enqueue, return `200` empty); `POST /show`; `GET /health`; `X-Dashboard-Token` check. **Superseded in part by T1.21** — the port is chosen per user rather than fixed (issue #5). Left as written because it records what T1.2 delivered.
- **Acceptance:** tests mapping **real sample payloads** for each consumed event (using the exact field names — `prompt`, `last_assistant_message`, the `Notification`/`StopFailure` matcher values, `source`, `cwd`) to the right `InboundEvent`; `/hook` returns `200` empty; bad/missing token rejected; binds loopback only.
- **Guardrails:** no Registry work on the request thread; pure-observer (no decision fields).

**T1.9 — Event pipeline (Channel + consumer + Dispatcher marshalling)**
- **Goal:** the one crossing point.
- **Depends:** T1.8, T1.2
- **Realizes:** Impl §4
- **Deliverables:** `Channel<InboundEvent>`; `EventConsumer : BackgroundService` (single reader) applying to the Registry; marshalling of Registry change-notifications onto `Application.Current.Dispatcher` into an `ObservableCollection`.
- **Acceptance:** concurrency test — many concurrent producers, single consumer, timestamp order preserved, no corruption; marshalling smoke test; a burst of simultaneous events doesn't stall the producers.
- **Guardrails:** exactly one consumer; no locks in the Registry.

### Milestone 1C — UI, tray, ack, sound

**T1.10 — ViewModels**
- **Goal:** expose the banded model to WPF.
- **Depends:** T1.9, T1.3, T1.4
- **Realizes:** Impl §5.5; Design §5, §7
- **Deliverables:** `SessionViewModel`, `GroupViewModel`, `MainViewModel` (banded `ObservableCollection`, grouped/flat toggle, counts strip) via CommunityToolkit.Mvvm.
- **Acceptance:** tests/harness showing the VM reflects Registry changes and orders via the attention engine; grouped/flat toggle re-projects the same data.
- **Guardrails:** no OS calls; ordering comes from Core.

**T1.11 — Main window & rows**
- **Goal:** the dashboard UI matching the mockups.
- **Depends:** T1.10
- **Realizes:** Design §5–§9; mockups
- **Deliverables:** main window; session row (status LED, monospace prompt snippet, state+age line, ack affordance on Unread); grouped & flat views with labeled bands; **expanded row** showing the You-asked/Claude-answered exchange; collapse rules (stale group → one line; acked → "+ k quiet" footer; **Unread always full row**).
- **Acceptance:** renders from the live Registry; structure matches the mockups; **motion only** red-blink and working-breathe; collapse rules behave.
- **Guardrails:** presentation only; honor reduced-motion.

**T1.12 — Ack tiers 1–2**
- **Goal:** mark results seen.
- **Depends:** T1.11, T1.9
- **Realizes:** Design §4 (Acknowledgment — the three tiers); TS §I.3 (all ack sources travel one path); TS §IV.1
- **Deliverables:** auto-ack (already in the state machine via next `UserPromptSubmit`) verified end-to-end; **manual ack** via the row/expanded button emitting a synthetic ack `InboundEvent` into the Channel.
- **Acceptance:** manual ack → Acked and the row greys/collapses; a new prompt auto-acks a prior Unread; both travel the same pipeline.
- **Guardrails:** ack is an event through the Channel, not a direct Registry poke from the UI.

**T1.12b — No silent collaborators** *(added 2026-08-24; scheduled ahead of T1.13)*
- **Goal:** make a missing registration fail loudly at startup instead of quietly degrading the app.
- **Depends:** T1.12
- **Realizes:** the "single writer" working agreement (Part 1) — structurally, rather than by convention.
- **Why it jumps the queue:** five instances of one class have now been found by hand (T1.6 unowned tick, T1.11 `UiTick` registration, T1.11a `Flush`, T1.11 collapsed-row restatement, T1.12 ack publisher). The cause is not a forgotten test: **in each case the collaborator is optional and its absence is silent.** Deleting `AddSingleton<IAckPublisher, AckPublisher>()` left 746 tests green with every Ack button in the shipped app permanently disabled — Microsoft DI honours a constructor default for an unregistered service rather than throwing. Worse: `SessionRegistry.guard` and `SoundPolicyEngine.guard` are both optional, so deleting that one registration leaves the Registry running with **no single-writer guard** while every test that constructs its own keeps passing. Doing this before T1.13 also spares retrofitting the tray and the Core mute surface it adds.
- **Deliverables:** a guard test asserting that **for every type the container resolves, no constructor parameter that is itself a registered service may have a default value** (scanned via `IServiceProviderIsService`, so it holds for types added later and needs no list); the four current violations made required — `EventConsumer.uiTick`, `SessionRegistry.guard`, `SoundPolicyEngine.guard`, `UnhandledExceptionPolicy.clock`; **measure first** whether the container already publishes its `ServiceDescriptors` (a type registered behind an interface is container-constructed but invisible to `IsService`) and add the one-line seam only if it does not.
- **Acceptance:** deleting any one of the four registrations reddens a test; reintroducing a default on any of the four reddens the guard; **no allowlist** — `SessionViewModel`'s optional `motion`/`ack` are excluded by the property itself, because rows are never resolved from a container.
- **Guardrails:** no exemption list. If a genuine exemption ever appears, that is a stop-and-ask, not a suppression — the discriminating work belongs to the property, not to a hand-maintained set.

**T1.13 — Tray status light**
- **Goal:** the always-on status glyph.
- **Depends:** T1.9
- **Realizes:** Design §9; Impl §5.2, §5.1
- **Deliverables:** H.NotifyIcon tray icon; worst-state roll-up **Red (`NeedsPermission`) > Amber (`Error` or `NeedsQuestion`) > Green (Unread) > Blue (Working) > Grey (quiet)**, mirroring TS §IV.3 — see the correction in Impl §5.2; **tooltip carries counts**; **static** (no animation); left-click toggles the window; right-click menu (Open · Mute all / 30 min · Pause monitoring · Settings · Quit) — **Mute all keeps the glyph truthful, Pause monitoring greys it out "off duty" and toggles to Resume; see Impl §5.2**; window close → hide to tray.
- **Acceptance:** icon color tracks the worst current state; **the colour is derived from `AttentionOrder.Rank`, not from a second `SessionState` → colour table** — pinned by a test that enumerates `SessionState` (so a state added later fails rather than defaulting to Grey) and asserts the mapping is a **monotone coarsening of `Rank`**; the mixed case — one `Error` plus one `NeedsQuestion` — shows **Amber**; tooltip breaks out the Needs-You kinds; close hides; Quit exits; menu items wired (Settings may be a stub until Phase 6).
- **Guardrails:** color carries state, not digits; no elevation; **`RowVisuals.AccentOf` is not the tray palette** and must not be reused for it (Impl §5.2).

**T1.13a — The intermittent tick test** *(added 2026-08-24; scheduled ahead of T1.14)*
- **Goal:** find out whether `UiTickTests.The_tick_is_posted_rather_than_run_on_the_consumer_thread` is a racy test or a real race in `UiTick`, and fix whichever it is.
- **Depends:** T1.11 (whose test it is), T1.13
- **Why it jumps the queue:** it failed once on a clean tree at 1908deb and did not reproduce in 5 isolated and 8 full-suite runs. **Every verdict in this build rests on "N green"**, so one intermittent test puts an asterisk on all of them and will eventually redden an unrelated run and cost a phantom investigation — the same currency as the contaminated measurement and the `ZzSeamProbe` scare. Cheaper to chase now than to have it surface inside T1.14's evidence.
- **Deliverables:** the **failure message first** — loop the suite with output captured rather than reasoning about the test; then the diagnosis, then the fix. **Which it is decides everything**: a racy assertion is a test defect, but a real posting race in `UiTick` is a product defect in the one loop that drives ages, the collapse rule, and now the tray tooltip.
- **Acceptance:** the cause is named on evidence, not hypothesis; the fix is demonstrated against a reproduction rather than against the absence of one; and if it is a test defect, the assertion is replaced with one that is not racy by construction — **not** widened, retried, or given a tolerance.
- **Guardrails:** do not delete or weaken the property being asserted. A negative about a queue ("delivered, but not yet run") measured against a producer free-running at 25ms is racy whatever this failure was; that is the structural observation to design against.

**T1.14 — Sound adapter (NAudio)**
- **Goal:** play notices and nudges with volume.
- **Depends:** T1.5, T1.7
- **Realizes:** TS §IV.5; Impl §7
- **Deliverables:** `ISoundPlayer` over NAudio — per-sound gain (notice vs nudge), fade-in for nudges, a mixer to coalesce bursts; sound files in the app dir with user-override under the config dir. **`MasterVolume` goes in Core's `SoundPolicyOptions`, folded into the gain the engine passes — the adapter implements neither mute nor volume policy (see the clarification in Impl Part 7); mute already lives in `SoundPolicyEngine` as of T1.13.** Also **delete `SilentSoundPlayer.cs`** rather than register over it.
- **Acceptance:** notice and nudge play at different gains from the **same** file; a burst coalesces rather than stacking; mute still means **no `Play` call at all**; the resolved `ISoundPlayer` **is** the NAudio adapter (one `Assert.IsType` against the container — its failure mode is silence, which is indistinguishable from a quiet afternoon and from a working mute); a missing file, an absent output device, and an undecodable file each degrade to silence plus a log line, never a throw, each paired with a positive control; and **the app is actually run and heard** — NAudio plays on threads it owns, so green is not evidence (Part 1).
- **Guardrails:** driven by the policy engine's intents.

**T1.15 — Single instance**
- **Goal:** one resident process.
- **Depends:** T1.8
- **Realizes:** Impl §5.3
- **Deliverables:** named `Mutex` at startup + the loopback port bind as interlock; a second instance `POST /show` to the first, then exits.
- **Acceptance:** launching a second copy surfaces the first's window and the second exits; no port/mutex leak on clean exit.
- **Guardrails:** reuse ingress for the signal (no separate IPC).

**T1.16 — DPI + pin-to-all-desktops + placement**
- **Goal:** correct rendering and always-present window.
- **Depends:** T1.11, T1.6
- **Realizes:** Impl §5.4, §6.3 (documented tier only)
- **Deliverables:** a **minimal** `IVirtualDesktopService` adapter exposing just `PinToAllDesktops` (`GetDesktop` returns null until Phase 4, which is the documented "fall back to cwd grouping" signal); pin the window to all desktops; restore last position, else open on the focused monitor; always-on-top toggle (default **off**).
- **Acceptance:** window stays crisp when dragged between differently-scaled monitors; appears on every virtual desktop; position restores, and a vanished monitor falls back to the focused one; with pinning forced to fail the app starts, logs once, and behaves normally on one desktop. **Republish and repoint the logon task** — see T1.19.
- **Guardrails:** adapter degrades to `false` if pinning is unavailable; full grouping is Phase 4.
- **Two stale premises corrected 2026-08-26, both verified against the tree and against Microsoft's reference.** Per-Monitor v2 is **already declared** in `app.manifest` and has been since T1.0, so this task **confirms** it and proves the window renders correctly across two scale factors; it does not add a line that exists. And pinning is an **undocumented**-tier call — see the correction in Impl §6.3. `IVirtualDesktopManager` has three methods and none of them pins.

### Milestone 1D — Persistence, integration, packaging

**T1.17 — SQLite event log**
- **Goal:** durably record events.
- **Depends:** T1.9
- **Realizes:** Impl §8
- **Deliverables:** `dashboard.db` (Microsoft.Data.Sqlite); append-only `events(id, session_id, ts, event_type, payload_json, cwd)`; write every `InboundEvent`.
- **Acceptance:** events persist across runs; write path is off the UI thread; **no pruning yet** (retention is Phase 5).
- **Guardrails:** write-only in Phase 1 (no read-back required); don't block the consumer on disk.

**T1.18 — First-run setup (the integration milestone)**
- **Goal:** make Claude Code feed the dashboard.
- **Depends:** T1.8
- **Realizes:** Impl §9.2–9.3, §10.2
- **Deliverables:** register the **logon scheduled task** (restart-on-failure; normal integrity); write `port.txt`; ensure `CLAUDE_DASHBOARD_TOKEN` exists (generate + set at **User** scope if absent); and **register the hook handlers when the process starts, remove them when it quits** — Impl §9.3 as amended, which is where the merge now lives. The URL carries the **bound** port, not the compiled-in default.
- **Acceptance:** on a clean profile, running setup then starting a Claude Code session causes real events to reach `/hook` and drive the dashboard; existing user hooks in `settings.json` are preserved across **add, remove, and add-then-crash**, asserted by their command strings and not by a count; starting twice adds no duplicate handler; **with the dashboard shut down, a new Claude Code session submitting a prompt produces no hook error** (GitHub issue #4); a write that loses a race to another writer leaves a valid file; the registered URL is tested with a **non-default** port; and the residual after a hard kill is written down rather than claimed closed.
- **Guardrails:** parse-merge-write settings (never overwrite the file); back up to a plain copy at a stated path, restorable by hand without the dashboard; write atomically; identify our handlers by URL, never by an added key; token via env var only. **`DashboardPaths.SettingsFile` is the dashboard's own file, not Claude Code's** — Claude's path must not hang off that class.
- **Note:** the acceptance above only ever tests sessions started *after* the dashboard. Whether Claude Code re-reads `settings.json` while running decides what this feature achieves for sessions already open; determine it, do not assume it.

**T1.19 — Packaging (self-contained, a directory of files — retitled by PKG.2)**
- **Goal:** a shippable exe that autostarts.
- **Depends:** T1.7, T1.18
- **Realizes:** Impl §10.2
- **Deliverables:** `build\package.ps1 -Version <semver>`, which runs `dotnet publish -c Release -r win-x64 --self-contained` to a **directory of files** under `artifacts\publish`; the logon task points at the published exe. *(This block was titled "self-contained single-file" and asked for a single-file profile until PKG.2. The single-file half is superseded by Packaging Design D2 — Velopack diffs releases at the file level, single-file is not a shape it packages, and the exe's neighbours live inside Velopack's managed install directory, which no user browses. The old reason — nobody should copy "the exe" without its halves — is answered by that same directory.)*
- **Deliverables (T1.27, [issue #17](https://github.com/dsopko/claude-dashboard/issues/17)):** `src/ClaudeDashboard.App/Assets/app.ico` — the application icon at 256, 48, 32 and 16 px, named by `<ApplicationIcon>` and compiled into the executable, so the publish output gains no file. Regenerable from `docs/Claude Dashboard Icon-selection.png` by the `magick` commands recorded beside that property. **Not the tray glyph** — `Ui/TrayIcons.cs` is a status light and stays separate.
- **Deliverables (T1.28, [issue #29](https://github.com/dsopko/claude-dashboard/issues/29)):** `Setup/HookScript.cs` — `post-status.cmd`, written to the data folder at every start and compared byte for byte, so a fix reaches an install that already exists. `Configuration/ListeningFile.cs` — `listening.txt`, whose absence is how the script knows to do nothing. `Hosting/IngressAnnouncement.cs` — replaces `HookLifecycle`; writes both port files after a successful bind and withdraws `listening.txt` at four exits. `Setup/HookInstaller.cs` and `Setup/HookSwitches.cs` — `--install-hooks` and `--remove-hooks`, the only things that write Claude Code's settings, plus the read-only start check. **`port.txt` is unchanged in content, meaning, lifetime and callers**, and a test fails if a withdrawal deletes it. The exec form is verified against Claude Code 2.1.251; command hook mechanics are in `docs/claude-code-hooks-reference.md`. **Revised in part by T1.32** — the two switches are no longer the only things that write Claude Code's settings; a start puts back a handler that has gone missing, and the read-only start check became the thing that decides ([issue #39](https://github.com/dsopko/claude-dashboard/issues/39)).
- **Deliverables (T1.30, [issue #28](https://github.com/dsopko/claude-dashboard/issues/28)):** `SessionState.Interrupted` — grey, badged `INTERRUPTED`, in the Quiet band, ranked below `Working` and above `Acked`, and still. `Core/SilenceWatch.cs` — the ten-minute threshold, injectable, with no `settings.json` key because the log is the calibration path. `SessionRegistry.SweepSilent(now, threshold)`, run on the consumer's existing 15-second tick, de-escalating from `Working` and from nothing else. **`Session.LastHeardAt`** — a new field, because `LastActivity` does not advance on an event the Registry declines and a timeout built on it would have greyed out every long turn. Any event restores the state; a `PostToolBatch` does so explicitly, which recovers the long-tool-call false positive. The reference records that **no hook fires on an interrupt** (re-verified 2026-08-31) and that `MessageDisplay` is rejected because T1.28 made every event spawn two processes.
- **Acceptance:** the published exe launches at logon via the task and runs headless-to-tray; no machine-wide runtime required. **Not MSIX.** **Verify the manifest reached the published executable** — the concern as written was the single-file *bundle*, which no longer exists (PKG.2): the apphost carries the manifest either way, so the check is now the ordinary one — the published `ClaudeDashboard.App.exe`'s embedded manifest matches `app.manifest` — and its failure mode is the generic apphost one, not a bundling behaviour nobody had observed.
- **Guardrails:** **After T1.19 there are two artefacts — the source and the installed app — and they can disagree.** Every task landing after this one carries the republish in its acceptance: run `build\package.ps1 -Version x.y.z` and install the Setup it produces (PKG.3). Without that, the executable the operator actually runs quietly drifts from the source, and the symptom is one nobody would connect to the change that caused it. *(This read "republish and repoint the logon task" until PKG.3. The repoint half is gone because the premise moved: Velopack installs to a stable `current\` path that survives every update — D3 — and no logon task exists to repoint until Step 2 wires one against that path. The republish half is what survives, reshaped from a bare `dotnet publish` to the script-and-Setup pair, because a publish nobody installs updates nothing the operator runs.)*

**T1.20 — Phase 1 end-to-end acceptance**
- **Goal:** prove the slice under real load.
- **Depends:** T1.11–T1.19
- **Realizes:** Phase 1 exit criteria (Part 2)
- **Deliverables:** a documented E2E run.
- **Acceptance:** across ~15 real Claude Code terminals: states and bands are correct; the tray light rolls up correctly; notices/nudges fire and coalesce; manual + auto ack behave; the app survives a logon restart and a forced crash (relaunches via the task).
- **Guardrails:** this task gates the phase.

### Milestone 1E — After the gate

Tasks landing after T1.20. Each one puts the acceptance document out of date in a stated way, so each carries a supplement to it and the republish that T1.19's guardrail requires.

**T1.21 — Per-user ingress port**
- **Goal:** every signed-in user gets a dashboard that can hear.
- **Depends:** T1.15, T1.18, T1.19
- **Realizes:** Impl §3.1 as amended 2026-08-26; §5.3; §9.3. Closes [issue #5](https://github.com/dsopko/claude-dashboard/issues/5)
- **Deliverables:** the three-attempt choice of §3.1 — `port.txt`, then a SHA-256-of-SID derivation, then a bounded walk; each walk step classifying the occupant through the `/health` identity; the bound port written to `port.txt` and carried into the hook URL; `DashboardSettings.DefaultPort`'s remark corrected, since its stated reason no longer holds. **Superseded in part by T1.28** — there is no hook URL now, and the bound port is carried into `listening.txt` instead (issue #29). That applies to the acceptance line below as well: what two data roots register today is one command handler each, naming their own `post-status.cmd`. Both lines are left as written because they record what T1.21 delivered and was judged on.
- **Acceptance:** two data roots with different derived candidates both bind and both register their own URL; a fresh profile with no `port.txt` derives and binds; a `port.txt` naming a taken port falls through to the derivation; a stranger on the derived port causes a walk, not an exit; all three failing still starts the dashboard with the Error and the tooltip of §5.3; the registered URL carries the **bound** port, tested with a port that is neither the default nor the derived one. **Republish, and supplement the acceptance document** — §1 and §4 were measured against a single fixed port.
- **Guardrails:** **SHA-256, never `GetHashCode()`** — it is randomised per process, so the same user would derive a different port every launch and every in-process test would still pass (the T1.15 trap). Binding is the only question asked; build no registry of who owns which port. The walk is bounded. **Accepted residual, ruled by the operator:** allowlist entries accumulate, one per distinct URL ever registered, and nothing removes them. **That guardrail is retired by T1.28** — a command hook is not on `allowedHttpHookUrls`, so nothing accumulates, and `--remove-hooks` clears what earlier builds left (issue #29, Impl §9.3). Unlike the two lines above, this one was a standing instruction rather than a record, so it is withdrawn rather than merely marked.
- **Note:** the operator's own multi-user question settled two things worth carrying. The database and every other file are already per-user under `%LOCALAPPDATA%`, so **only the port is shared** and nothing in storage changes. And two users sharing one `CLAUDE_DASHBOARD_HOME` share one database, which our writer is not built for — document that as unsupported rather than leave it quietly half-working.

**T1.29 — Branded caption (design option 2c)**
- **Goal:** the window's top belongs to the app rather than to the OS.
- **Depends:** T1.11, T1.27
- **Realizes:** Design Document §9 as amended by design option **2c — Branded caption, the Word treatment** (`Claude Dashboard Window.dc.html`, over the `classical-claude` system's `styles.css`). No Impl section covers a custom caption; this is the first.
- **Deliverables:** `Ui/MainWindow.xaml` — `WindowChrome` at the design's 48 DIP caption with `UseAeroCaptionButtons="False"`, the caption drawn as ordinary XAML (icon, serif title, summary, divider, help slot, three window buttons), and the toolbar row re-laid out to 2c's idle state. `Ui/CaptionChrome.cs` — the maximized inset and the `HTMAXBUTTON` answer that earns Snap Layouts. `Ui/FittingStrip.cs` — the panel that shortens the summary's words while that is enough and drops whole counts from the right once it is not, plus the `Labels` and `HideAtTier` attached properties that give each word its per-tier form. `Ui/RowTemplates.xaml` — the caption's brushes, fonts and button styles as named resources. `MainViewModel.SessionCount` / `SessionsWord`. **The counts strip moved into the caption rather than being copied there**; the toolbar row keeps everything 2c does not draw, selection mode included.
- **Acceptance:** drag, double-click, right-click and Alt+Space system menu, resize from every edge and corner, Win+arrow snapping, Alt+F4, and the taskbar and Alt-Tab title and icon all still behave; **close still hides to the tray**, through the same `OnClosing` the stock X reached; a maximized window's content stays inside the work area at 100% **and 150%**; `WM_NCHITTEST` answers `HTMAXBUTTON` over the maximize button and the click through it still toggles. **`FittingStrip` ships with tests** — the tier ladder, the shorten-before-drop rule, the prefix rule, tier 0's long form, and the widths this plan and the caption's remarks quote — measured on an STA thread with no window and no UI automation. **The fixture must set every text property the caption sets: the `UiFont` family, `TextOptions.TextFormattingMode="Display"`, and `Typography.NumeralAlignment="Tabular"` on the four numbers.** All three move the tier boundary, and a fixture one property short does not fail — it describes a different strip and reproduces its own figures on demand.
- **And the fourth input is the display scale, which cannot be owned from inside a test (T1.31, [issue #33](https://github.com/dsopko/claude-dashboard/issues/33)).** Display formatting quantizes glyph advances to whole *device* pixels, so every absolute width is a function of the monitor: `"11"` is 12 at 100% and 13.333 at 150%. `VisualTreeHelper.SetRootDpi` does not fix it — measured, it moves what `GetDpi` reports but not what the text stack measures against once any window has been realized, and it latches to the first scale set on the thread. **So the rules are asserted against widths the strip is asked for, never against widths written down**, and the recorded 100% ladder is quoted by exactly one test which verifies it where that scale still holds and checks the ladder's shape everywhere else. A fixture quoting absolute pixels either owns every input to them or does not quote them. Everything the caption does that only Win32 or a real screen can answer is verified by hand instead, and listed as such in the hand-off.
- **Correction to the acceptance line above, recorded rather than silently replaced:** it read *"No new tests: nothing here is expressible in Core, and the existing suite must pass unchanged."* That was carried over from the task brief, which was written for a visual change to a title bar and before the responsive strip existed. It named the wrong reason — `FittingStrip` is in `App`, not `Core`, and needs no UI automation — and it named it in the one place a reason must not be wrong, since an Acceptance line is the criterion the task is graded against and that one asserted an impossibility that was not one.
- **Guardrails:** **never `WindowStyle="None"` with `AllowsTransparency="True"`** — it makes the window layered and costs the DWM shadow, the Windows 11 rounded corners and the snap animations, none of which can be drawn back. No new UI library; `WindowChrome` is in the box. No hex literal in `MainWindow.xaml`. **Every value read off the design, not chosen** — the alphas, the 46 px buttons and the 48 px bar are all the mockup's.
- **Note:** two things 2c could not give. Its serif is **Cormorant Garamond**, which is a Google font and not a Windows one, so the title falls back to **Constantia**; and the design is drawn at 820 px where the window opens at 520, so the summary slot cannot hold all four counts at once. `FittingStrip` is the concession — at 520 the caption reads exactly as 2c draws it, and the unread and working counts appear as the window widens. **Whether that is the right trade is the design's call, not this task's**; the alternative is a wider default window.

**T1.32 — Install the hook at start when it is missing**
- **Goal:** a user who has never opened a terminal receives events.
- **Depends:** T1.28
- **Realizes:** Impl §10.2's standing requirement, which nothing satisfied. **Amends Impl §9.3**, whose sentence "a running dashboard reads that file and never writes it" this task makes false. Closes [issue #39](https://github.com/dsopko/claude-dashboard/issues/39)
- **Deliverables:** the start path in `Program.cs` acts on the `HookPresence` it already reads, calling `HookInstaller.Install()` when the handler is incomplete, the settings were readable, and the operator has not opted out. `DashboardSettings.InstallHooksAtStart` — the opt-out, ours and not Claude Code's, default `true`, JSON key `installHooksAtStart`, cleared by `--remove-hooks` and set by `--install-hooks`. **`HookInstaller` itself gains no logic** — `Install()` already writes the script first and merges second, and `Register` matches on script path, so a repeat call adds nothing. The two Impl sentences are rewritten in the same commit as the behaviour that falsifies them.
- **Acceptance:** an absent handler installs; a partial handler tops up; **a complete handler leaves the settings file byte for byte unchanged**, which is the assertion, not the reported outcome; an unreadable file and a malformed one — the duplicate key included — each warn and write nothing; the opt-out installs nothing whatever the presence; `--remove-hooks` then a start leaves the handler removed; `--install-hooks` restores handler and flag together; a hand-formatted file keeps its comments across a start that installs nothing; the log line names the events and the script path and no part of the file. **Republish and repoint the logon task** (T1.19's guardrail).
- **Guardrails:** **never write a file you could not read** — a settings file rewritten from a partial parse costs the operator everything in it, and that is a worse failure than the one this task fixes. **Nothing is written on quit**; the part of issue #29 that mattered is that the handler outlives the process. **Top up on partial rather than only installing at zero**, or a build that adds an event never reaches an install that already exists. Never log the file's contents (T1.24).
- **Note:** the cost is a real one and is accepted rather than solved. `SettingsFileWriter` renders from `JsonNode`, which carries neither comments nor formatting, so the install that repairs a missing handler also flattens a hand-formatted file. Guardrail three keeps the bill to the starts that actually repair something, which for most installs is one.

**T1.33 — No Claude Code, no hook install**
- **Goal:** the dashboard never creates `~/.claude` on a machine that has never had Claude Code.
- **Depends:** T1.32
- **Realizes:** the packaging gate's item 7 (Packaging Execution Plan, PKG.4). Follow-up to T1.32, [issue #39](https://github.com/dsopko/claude-dashboard/issues/39). Closes [issue #42](https://github.com/dsopko/claude-dashboard/issues/42)
- **Deliverables:** `StartupHookInstall.Wanted` refuses when the directory holding Claude Code's settings file does not exist — the one reliable sign that Claude Code is not installed — and `Run` logs one line saying so. An absent **file** inside a present directory still installs: that is a fresh Claude Code user, and it is the case T1.32 exists for. `HookInstaller` gains no logic. The truth table gains the row; §5k gains the line.
- **Acceptance:** no `~/.claude` directory → nothing written anywhere under it, one log line naming the reason; `~/.claude` present with no `settings.json` → installs as before; every other T1.32 row unchanged. The `--install-hooks` switch is **not** gated — an operator who runs it by hand is asking, and the directory is created for them as today. Both suite counts.
- **Guardrails:** the check is the directory, not a search for a Claude Code executable — the app never goes looking for other software. Never log the path's contents (T1.24).

**T1.34 — Ack all**
- **Goal:** one click clears everything that is waiting on the operator.
- **Depends:** T1.11, T1.29
- **Realizes:** Design Document §9's toolbar row, extended. Closes [issue #43](https://github.com/dsopko/claude-dashboard/issues/43), which is the authority for look and placement.
- **Deliverables:** an *Ack all* button, rightmost of the toolbar's right cluster — **[Select] [Mute all] [Ack all]** — visible in selection mode too. `MainViewModel` gains a flag that is true when any session satisfies `Acknowledgment.Applies`, in either view, and a command that publishes one `IAckPublisher.Acknowledge` per eligible session through the publisher every row already uses. **No new event type, no Registry change.** A style based on `HeaderButtonStyle` whose one trigger, on that flag, gives the checked-segment look (`RaisedBrush`, `InkBrush`); unlit is the plain header look, not the dimmed disabled look.
- **Acceptance:** the flag follows session state in both views, a collapsed group included; the click publishes exactly the eligible set once each and nothing when the flag is false; the flag is false again once the Registry has applied the acks; the lit and unlit looks are asserted against the styles' own brushes, not literals; tooltip *Acknowledge every session that is waiting on you.*; both suite counts.
- **Guardrails:** publish only for sessions eligible at the click — the channel is bounded and drops its oldest when full (Impl §4), so a declined ack is how a real event is lost; state the capacity in the remark and what happens when the eligible count exceeds it. Lit and enabled are the same predicate.

**T1.35 — Selection you can see, and a Group these that says it is ready**
- **Goal:** grouping two sessions is something the operator can watch themselves do.
- **Depends:** T1.26, T1.34
- **Realizes:** Design Document §9's selection mode, made visible; and a new §9 rule for lit action chips. Closes [issue #44](https://github.com/dsopko/claude-dashboard/issues/44) and [issue #45](https://github.com/dsopko/claude-dashboard/issues/45), which are the authority.
- **Deliverables:** a selected row's mark, bound to `IsSelected` and to nothing else — a check in the LED slot and a background distinguishable from the focus shade `RowToggleStyle` paints on `IsKeyboardFocused`; a dimmed look and a tooltip on a row that cannot be selected because it has no title; *Group these* lit at two or more through the style *Ack all* uses (T1.34), extracted to a shared style rather than restated a third time; and **the lit rule written into Design §9**: an action chip is lit when it is enabled and is the primary action of the current state — *Ack all* when something waits, *Group these* when selection can complete — never *Select*, *Cancel* or *Mute all*; a segmented toggle keeps the raised look for its current segment.
- **Acceptance:** two rows selected carry the mark at once whichever has focus, and a test that moves focus keeps the first row's mark; the mark survives expansion, a state change and a `Refresh` that rebuilds the row, and leaves with the mode; an untitled row in selection mode is dimmed and carries the tooltip, a titled one is not; *Group these* is at the plain header look below two and lit at two or more, following `SelectedCount` as rows are ticked and unticked and resetting on leaving the mode; every look asserted against the resource dictionary's brushes by instance; Design §9 carries the rule; both suite counts.
- **Guardrails:** the mark is not the focus shade and not the hover shade — three states must be tellable apart on one row. The selection state itself is not changed: `IsSelected`, `CanSelect` and `IsSelecting` already do the right thing (issue #44's finding), and the task is display only. Never log a title.

**T1.36 — An orchestration is acknowledged once**
- **Goal:** a roster group chimes once and is acknowledged once.
- **Depends:** T1.25, T1.26, T1.34
- **Realizes:** Design Document's acknowledgment tiers, extended to the roster group as the unit. Closes [issue #47](https://github.com/dsopko/claude-dashboard/issues/47), which is the authority.
- **Deliverables:** an Ack on the **roster** group header (`GroupKeyKind.Roster` only — never a working-directory group), shown when the group's derived state satisfies `Acknowledgment.Applies`, whose click publishes one `IAckPublisher.Acknowledge` per member eligible at the click — *Ack all*'s pattern scoped to the group. Member rows inside a roster group carry no Ack, on the row or expanded; the same session in Flat view or in a cwd group keeps its own. *Ack all* still counts roster members. Tier 1 auto-ack unchanged. The Design Document records that a roster group is acknowledged as one. **No new event type, no Registry change, no Core change.**
- **Acceptance:** a roster header with one eligible member shows the Ack and with none does not; a cwd header never does; the click publishes exactly the eligible members once each and nothing when none is eligible; a member row inside a roster has no Ack collapsed or expanded while the same session in Flat or in a cwd group has one; after the acks apply the header's Ack is gone and the group reads Acked; *Ack all* still includes roster members; looks asserted against the resource dictionary; both suite counts.
- **Guardrails:** the accepted trade-off is written where the header's command lives — a blocked member (permission, question, error) can only be cleared by hand at the group, and that is chosen. Publish eligible members only; the channel is bounded (T1.34's remark). Never log a title.

**T1.37 — The decisions record, and a replay that rebuilds it from history**
- **Goal:** a sound heard at 13:18 can be traced to its cause by reading a table.
- **Depends:** T1.17, T1.30, T1.36
- **Realizes:** Impl §4's archive, extended from an inbox to a journal. Closes [issue #48](https://github.com/dsopko/claude-dashboard/issues/48), which is the authority for the row kinds and the shape.
- **Deliverables:** a `decisions` table in `dashboard.db` beside `events`, one row per decision the dashboard makes or deliberately does not make — every kind in #48's list — with `event_id` for the causing event and `NULL` for a tick. Written by the archive thread: the consumer hands the archive **one record, event plus decisions, after** the Registry and sound engine have decided, and the archive inserts both in one transaction. **`--replay`:** a switch that opens an existing `dashboard.db`, runs its `events` through the real `SessionRegistry` and `SoundPolicyEngine` with a clock driven by the timestamps and ticks synthesised every 15 s, and writes the decisions rows for the whole history; it never touches `events`. The same rows to the Serilog file at Debug, and a `logging.minimumLevel` key in the dashboard's own settings.
- **Acceptance:** every decision kind has a test asserting its row and its `event_id`; a plant that throws between the two inserts leaves neither; a declined event, an `Apply` that throws, and a tick each produce their rows; **the live recorder and `--replay` produce identical rows for the same event sequence** — one fixture, two paths, asserted equal; `--replay` over the operator's real database completes and its sweep rows match the six the log already carries; no `reason` or `detail` can carry operator text — the inventory guard covers the new type; the consumer thread still never blocks on the archive; both suite counts.
- **Guardrails:** **never a title, prompt, payload, or message body** in a decision row — identifiers and enums only (T1.24). The archive stays the file's one writer. Moving the hand-off after `Apply` must keep two properties T1.17 placed it before `Apply` for: a declined event is still recorded, now as a decline; an `Apply` that throws still archives the event, via `try/finally`. Replay cannot recover what was never archived — mutes, roster edits, the bound device — and says so in its output rather than guessing. **Ruled 2026-09-25:** the sound engine's suppression reasons and nudge ladder never cross its port, so `Core/Ports` gains `IDecisionSink` beside `ISoundPlayer` (null sink by default) and `SoundPolicyEngine.cs` gains sink calls at its existing decision points and remarks — every added line one or the other, no branch or threshold or chime changed; the reviewer diffs it as such. "Row removal scheduled / removed" is deferred until something removes a session.
- **Note:** ruled 2026-09-25 to land **before** the sound and threshold changes ([#49](https://github.com/dsopko/claude-dashboard/issues/49), the quiet-prompt rule), so those are made against a record rather than an inference. The replay is what makes the record retroactive.

**T1.38 — A title-bar icon of its own**
- **Goal:** the "C" in the window's drawn caption reads as a "C" at the size it is drawn.
- **Depends:** T1.29
- **Realizes:** Design option 2c's caption icon slot. Closes [issue #50](https://github.com/dsopko/claude-dashboard/issues/50) as narrowed by the operator 2026-09-26: **only the title-bar icon changes**; the exe's `ApplicationIcon` — taskbar, Alt-Tab, Start menu — stays byte-identical.
- **Deliverables:** a caption asset with 20 px and 30 px frames (100% and 150%), made by the recipe the operator's chosen reference was made by — the glyph lightly thickened (`Disk:6` dilation of source and mask before the 130% enlargement), the dot erased and redrawn flat at its drawn radius at its own centre, each size reduced separately with Mitchell and `-sigmoidal-contrast 4x40%`. The caption's `Image` reads that asset so that at each scale the matching frame is drawn pixel for pixel. The recipe's commands go in the csproj beside the existing ones, and the csproj's remark against a second caption copy is corrected, not deleted.
- **Acceptance:** the committed 20 px and 30 px frames are pixel-identical to the operator's reference images (`magick compare -metric AE` reports 0); `app.ico` is unchanged byte for byte; in a realized window at 100% the caption draws the 20 px frame unscaled; `IconAssetTests` still pass and gain a test that the caption reads its own asset and the exe keeps `app.ico`; both suite counts.
- **Guardrails:** no change to `ApplicationIcon`, the tray glyph, or the Velopack `--icon`. The reference images, not a description of them, are the authority.

**T1.39 — Numbers before no numbers: a numbers-only caption tier**
- **Goal:** narrowing the window loses words before it loses a single count.
- **Depends:** T1.29, T1.31, T1.38
- **Realizes:** T1.29's own rule, "words are cheaper to lose than numbers, so the words go first", which the built ladder stops following after tier 1. Closes [issue #53](https://github.com/dsopko/claude-dashboard/issues/53), which is the authority.
- **Deliverables:** a tier 2 in the caption strip that shows numbers only (`11 · 3 · 5 · 8`), placed before any count is dropped. The `need`, `unread` and `working` words hide at tier 2 through `ui:FittingStrip.HideAtTier="2"`. The colours name each count. A tooltip on the strip carries the full text of the non-zero counts, and it follows them as they change. The XAML remark's tier table gains a row for the new tier.
- **Acceptance:** `FittingStripTests` covers tier 2 against widths measured in the same run. Every word is gone before any count is dropped. The prefix rule still holds, so no separator dangles. The tooltip is asserted against the view model's counts. Both suite counts.
- **Guardrails:** no pixel figure is asserted as a literal (#33). Nothing in `FittingStrip.cs`'s selection logic changes unless a test shows it has to. T1.38's icon and its realized-window test are untouched.

**T1.40 — "You asked" says how long ago, and the work keeps one clock**
- **Goal:** the row says how long the current piece of work has been going, and flips between states do not restart it.
- **Depends:** T1.23, T1.24
- **Realizes:** Closes [issue #51](https://github.com/dsopko/claude-dashboard/issues/51), which is the authority, with the operator's rulings recorded on it. Lands before #52, which shows this anchor on its Waiting row.
- **Deliverables:** "You asked [time] · [time ago]" in the expanded row, the time ago in the row's existing relative form, ticking on the panel's 15-second refresh. One anchor for the work: the prompt that began it — an operator's prompt or a cross-session message; a `<task-notification>` wake-up is a continuation and moves nothing. The collapsed row's time reads from the same anchor while the session is working, so it no longer restarts on every state transition. Finished states keep today's behaviour.
- **Acceptance:** the relative time ticks; a session flipped across states repeatedly under a fake clock keeps one elapsed time from the original prompt; a `<task-notification>` prompt changes neither "You asked" nor the elapsed time; an operator's prompt and a cross-session message each start a new ask; both suite counts.
- **Guardrails:** no Waiting state here — that is #52. A `<task-notification>` is recognised by its prefix only, never by reading the rest of the prompt. Never log the prompt.

**T1.41 — Waiting: a session paused on its own background work is not finished**
- **Goal:** the operator hears "finished" once, when the work is finished, and never while an agent waits on a background command or subagent.
- **Depends:** T1.30, T1.37, T1.40
- **Realizes:** Closes [issue #52](https://github.com/dsopko/claude-dashboard/issues/52), which is the authority, with the operator's rulings in its comments. Documents `background_tasks` and `session_crons` in the hook reference.
- **Deliverables:** `SessionState.Waiting`. A `Stop` whose `background_tasks` lists a running entry of type `shell` or `subagent` moves the session to Waiting instead of Unread, with no sound and no nudge. The allow-list is exactly those two types: `monitor` and any unseen type fall back to today's behaviour, and the decisions record logs the unseen type. The wake-up prompt moves Waiting to Working. A `PostToolBatch` while Waiting leaves the session Waiting. A permission `Notification` outranks Waiting. The silence sweep stays Working-only. Waiting keeps a roster group unfinished and counts as working for the tray light. The collapsed row reads "Waiting [elapsed from the ask] · [task description] · [project]". The expanded row reads "Claude said so far" and gains a "Waiting on" block, one line per task: description, kind, age. Waiting joins T1.40's ask-anchored clock. **#52 owns the general rule that a prompt the operator did not type moves the session to Working without tier-1 auto-acknowledgment.**
- **Acceptance:** each "must" in #52 has a test; in #52's event-by-event run there is no "finished" chime at step 4 and exactly one at step 8; a replay of the archive on a copy counts the Stops that now decide Waiting and the false "finished" notices removed; a task's command is never stored, shown or logged; both suite counts.
- **Guardrails:** every `SessionState` switch has a `_ =>` arm, so the compiler will not list the sites. Find every one by search and report the list (T1.30's lesson). Only the type field decides; description text never decides state. Registry invariants stand.

**T1.42 — The strip keeps a count it has room for**
- **Goal:** the caption never drops a count while any tier would fit it.
- **Depends:** T1.39
- **Realizes:** T1.29's and T1.39's ladder rule. Closes [issue #55](https://github.com/dsopko/claude-dashboard/issues/55), which is the authority.
- **Deliverables:** a reproduction first, then the fix. On 0.0.15 the operator saw a grey 3 and a green 2 with room to spare, while one session was Working. The fix names which rule dropped the count.
- **Acceptance:** a test reproduces it in the window's own layout, with counts total 3, unread 2, working 1, at a width where all three fit in some tier, and fails before the fix. The strip always picks the richest tier that fits, and a count is dropped only when no tier fits it. Both are asserted against widths the strip is asked for, never written down (T1.31). Both suite counts.
- **Guardrails:** state the cause before changing code. T1.39's ladder, tooltip and tests stay green.

**T1.43 — Counts on their own row when even numbers do not fit**
- **Goal:** a narrow window still shows every count, on a line of its own.
- **Depends:** T1.42
- **Realizes:** [issue #54](https://github.com/dsopko/claude-dashboard/issues/54), which is the authority, with the operator's rulings of 2026-09-27.
- **Deliverables:** a thin row between the caption and the toolbar. It is used when the caption slot is narrower than the numbers-only tier. All the counts move there together, and the caption's slot is left empty. The ladder starts again from full words on the row's own width. #53's tooltip rule holds on either line.
- **Acceptance:** #54's list, and in particular a 1 px window sweep with no width that shows the counts in both places or in neither, and no oscillation. Both suite counts.
- **Guardrails:** the caption stays at 48, and its drag, snap and system menu are unchanged. The choice is a pure function of the window's width.

**T1.44 — A watchdog tick that finds nothing makes no sound**
- **Goal:** a scheduled job that checks and finds nothing does not beep.
- **Depends:** T1.37, T1.41
- **Realizes:** the quiet-prompt rule, built on T1.41's machine-prompt rule. Closes [issue #56](https://github.com/dsopko/claude-dashboard/issues/56); the issue body and its comment are the authority.
- **Deliverables:**
  - **A tick is identified by structure.** It is a `UserPromptSubmit` whose prompt exactly equals a cron listed in `session_crons` on that session's previous `Stop`. No keyword matching.
  - **The quiet rule.** A tick whose `Stop` reply, trimmed, is exactly `WATCHDOG-QUIET` plays no finished sound and no nudge. The row reverts to its pre-tick state, answer and `EnteredAt`, including Waiting. Anything else beeps and displays as today.
  - **Decisions rows** record a suppression with its reason.
  - **Documentation:**
    - `docs/quiet-scheduled-jobs.md`, the user guide the operator's comment specifies;
    - a *Quiet scheduled jobs* section in the README that links to it;
    - the sentinel convention in the hook reference;
    - Appendix B's watchdog instruction gains the opt-in line, word for word the guide's example line.
- **Acceptance:** #56's list, including the typed-prompt and unlisted-cron negatives, and a replay of a copy of the archive that reports zero silenced ticks today. Also the shape of `last_assistant_message` confirmed from real Stop payloads, the nudge ladder not reset by a quiet tick, and both suite counts.
- **Guardrails:**
  - The failure mode is always a beep, never a silenced escalation.
  - The reply is compared as data and never logged.
  - The operator's own settings are untouched. The dashboard changes no cron.

**T1.45 — Upgrade Velopack**
- **Goal:** the packager and its runtime are current before the update client (install path, Step 3) is built on them.
- **Depends:** PKG.1, PKG.3
- **Realizes:** Packaging Design D1 and D5. `vpk` reported 1.2.158 available against our pinned 1.2.0.
- **Deliverables:** the `Velopack` PackageReference and the `vpk` tool in `.config/dotnet-tools.json` raised together to the newest stable release that restores against `net10.0-windows`. The version-match test already enforces that the two agree. Any source or `build\package.ps1` change the upgrade requires, found from Velopack's own release notes rather than by trial.
- **Acceptance:** .NET 10 support confirmed from the package, not assumed; `build\package.ps1 -Version x.y.z` produces the six artefacts with the same names; `VelopackApp.Build().Run()` is still the first statement of `Main`, and its guard passes; the portable zip runs with roots redirected; **a Setup built with the new version upgrades an install made by the old one in place**, keeping the data folder, in a throwaway install root and never on the operator's machine; both suite counts.
- **Guardrails:** stable releases only, no preview. The two versions move together or not at all. If the newest stable release changes the update-feed format or the `vpk pack` flags in a way that breaks an existing install's upgrade, stop and report.

**T1.46 — The state endpoint**
- **Goal:** the assembled program can be asked what it currently believes, so a correct dashboard and a wrong one stop answering identically from outside. Closes issue #10.
- **Depends:** T1.37 (the decisions record, which covers history where this covers the present)
- **Realizes:** Impl §3.2's endpoint set, widened by one. The acceptance gap named in `docs/claude-dashboard-phase1-acceptance.md` §5 criterion 4 — nudge firing unobservable outside the process — closes with it.
- **Deliverables:**
  - `GET /state`, loopback-bound, returning one entry per session the Registry holds: id, state, band, workspace group, `EnteredAt`, `LastActivity`, `LastHeardAt`, `ErrorKind`, the title, the waiting tasks with their kind and description, and the session's next nudge time; plus the band counts and the tray roll-up.
  - **A token is required, not optional.** With no `CLAUDE_DASHBOARD_TOKEN` configured, `/state` answers `404` and is not served. `IngressToken.Accepts` passes everything when no token is set, and this is the first endpoint that *emits* rather than ingests, so it must not inherit that default. `/hook`, `/show` and `/health` are unchanged.
  - **A snapshot the request thread owns.** The Registry has one writer and no locks, and `SessionRegistry.Sessions` is a live view that throws when enumerated mid-apply — the T1.2 review hit exactly that. Copy `SessionProjection`: subscribe to `SessionChanged`, take the immutable `Session` out of the event arguments on the consumer thread, and read `SoundPolicyEngine.NextNudgeAt` there too.
  - **Prose wrapped, not inventoried.** The title and the task descriptions are in `UnprotectedTextInventory.CarriesOperatorText`. They reach the response through a wrapper modelled on `PayloadJson` — no public string property, a redacting `ToString()`, a `Reveal()`, and a `JsonConverter` that writes the revealed value. The inventory shrinks or stays level; it does not grow.
  - Counts derived by calling `AttentionOrder.BandOf`, never by a second copy of the band rule.
  - The endpoint and its token requirement documented in the README and the hook reference.
- **Acceptance:** `/state` against a running dashboard reports the states the window shows, for at least one session in each of Needs-You, Working and Quiet; a next-nudge time that arrives before the nudge fires; `404` with no token configured and `401` with the wrong one; counts equal to the window's; no prompt or answer text anywhere in the body; the response object rendered through a real Serilog pipeline reveals no title and no task description; both suite counts.
- **Guardrails:**
  - Read-only. No request may change a session, an acknowledgment or a setting.
  - The request thread never touches `SessionRegistry.Sessions`.
  - `/health` stays unauthenticated, and `Health_answers_without_a_token` stays green.
  - No prompt and no answer text, ever. A background task's `command` is not stored and does not appear (T1.41).

**T1.47 — The finish clock, and the reserved button hidden**
- **Goal:** a finished row keeps telling the operator when the work finished, through an acknowledgment and through the session's end; and the row shows no control that does nothing. Closes issues #59 and #60.
- **Depends:** T1.40 (the ask clock), T1.41 (Waiting joins the ask)
- **Realizes:** the operator's ruling of 2026-09-29: the time that matters is when the session finished, not when it was acknowledged or closed.
- **Deliverables:**
  - `SessionViewModel.Age` reads a third anchor. Working and Waiting read the ask, `Exchange.StartedAt`, as today. **Unread, Acked and Ended read the finish, `Exchange.AnsweredAt`.** When `AnsweredAt` is null — a session closed or acknowledged in the middle of a turn — they read `Session.EnteredAt`, as today. NeedsPermission, NeedsQuestion, Error and Interrupted keep time in state.
  - The remark on `Age` rewritten to match, including the table of states and clocks.
  - The **Open terminal · PHASE 2** button in `RowTemplates.xaml` hidden. The markup stays in place for Phase 2 navigation, with a comment saying why it is hidden.
- **Acceptance:** an acknowledged row reads the same age after the click as before it; an Unread row that ends reads the same age after `SessionEnd` as before it; a session that ends in the middle of a turn counts from its end; an Unread row reads the same age it reads today; the sort order, the nudge ladder and the roster settle are unchanged; no visible control in the expanded row says "PHASE 2"; the expanded row lays out correctly without the button at 100% and at a fractional scale; both suite counts.
- **Guardrails:**
  - Only the display reads the new anchor. The sort order, the nudge ladder and the roster settle keep reading `Session.EnteredAt` (the line T1.40 drew).
  - The short session id in the expanded row stays.
  - Hide the button; do not delete it.
- **Amended 2026-09-29, after the first review (operator's rulings):**
  - **An acknowledgment or a close never restarts the clock.** Acked and Ended keep the moment that mattered in the state they came from: from Unread, the finish; from NeedsPermission, NeedsQuestion or Error, when the session became blocked; from Interrupted, when it went silent. Only a session acknowledged or closed while Working or Waiting counts from the acknowledgment or the close. The rule is transitive: Unread, then Acked, then Ended still reads the finish.
  - **An Interrupted row counts from `Session.LastHeardAt`**, the last event heard, not from the sweep that moved it. The sweep's own threshold (#49) is unchanged.
  - **The header's Mute all is wired** to the tray's Mute all / Unmute all command, with its label following the muted state. The stale T1.13 tooltip goes.
  - **The mute and pause labels change at the click (ruled 2026-09-30, after the second review).** The command still travels the Channel and the label still reads the engine's real state, so nothing optimistic is shown. What was missing is a refresh: after the consumer applies a `SoundCommand`, it drives the UI tick at once, so the tray and the header re-read the mode within milliseconds instead of at the next 15-second tick. The reviewer measured the old delay live at 7 to 11 s. It covers Mute all, Unmute all, Mute for 30 minutes, Pause and Resume.

**T1.48 — The token travels with the port**
- **Goal:** a running Claude Code session keeps reporting through any number of dashboard restarts, and the ingress token protects every user without setup. Closes issue #57.
- **Depends:** T1.28 (the command hook and `listening.txt`), T1.46 (`/state`), T1.47 (merged first; one checkout)
- **Realizes:** the design the operator chose on 2026-09-30, recorded in issue #57. It replaces Impl §3.4's optional environment-variable token.
- **Deliverables:**
  - **A new token at every start**, 32 random bytes as base64url (43 characters), held in memory for the life of the process. `IngressToken` reads it from there and never from the environment, so a token is always configured.
  - **`listening.txt` holds two lines, the port then the token**, written temp-then-rename as today and deleted on quit. `set /p` reads only the first line, so the port logic is unchanged.
  - **The hook script reads the token from the file at every event**, validates it — exactly 43 characters, each from `A–Z a–z 0–9 - _` — and always sends the header. On a missing or invalid token it sends nothing and exits 0. The environment branch goes.
  - **The second-launch `/show` reads the running dashboard's token from `listening.txt`**, not from the environment.
  - **`CLAUDE_DASHBOARD_TOKEN` is retired.** When set, it is ignored, with one Information line at start. `DashboardTokenSetup.Ensure` and its user-scope write are removed.
  - **`/state`'s "404 when no token is configured" branch is removed**, because a token always exists.
  - **The script is written before `listening.txt` is announced**, so an old script never meets a dashboard that requires the token.
  - Docs: README, the hook reference, Impl §3.4, §9 and §10.2, the screenshot guide, and the `HookRegistration` remark on environment inheritance. The "restart every session after setting a token" warning goes.
- **Acceptance:** a hook run with an environment from before a dashboard restart keeps reporting after it; two starts produce two different tokens, and the old one is refused; `listening.txt` holds the port and the token and is deleted on quit; the script sends nothing and prints nothing, and exits 0, when the token line is missing, short, long, or holds any character outside base64url, including a quote, `&`, `%` and `!`; `CLAUDE_DASHBOARD_TOKEN` is ignored with one log line; a second launch still surfaces the running window; `/state` refuses a request without the current token; the token appears in no log line and on no screen; the cost per hook is measured before and after; both suite counts.
- **Guardrails:**
  - **`findstr` character ranges are not ASCII ranges** (`[a-z]` matches some capitals and accented letters). The script's check must not rely on them.
  - The script still prints nothing on every path and always exits 0. Its output reaches Claude's context on two events.
  - `/health` stays unauthenticated.
  - The token is never logged, displayed or committed.
  - A hook refused once during a restart, when it read the old file just before the swap, is accepted.

**T1.49 — The hook as a Claude Code plugin**
- **Goal:** the dashboard stops being a second writer of `~/.claude/settings.json`. Claude Code registers the hook itself. Closes issue #30.
- **Depends:** T1.28 (the command hook), T1.32 and T1.33 (the start-time install and its bounds), T1.48 (the script and `listening.txt`, which do not change)
- **Realizes:** Impl §9.4. The design and the measurements behind it are in issue #30.
- **Deliverables:**
  - **The plugin's three files in the data folder**, `%LocalAppData%\ClaudeDashboard\plugin`, rewritten when they differ. `hooks.json` holds the same handler as the settings entry, on the same events, naming `post-status.cmd` by absolute path.
  - **Registration through the `claude` program**: `claude plugin marketplace add <folder>`, then `claude plugin install claude-dashboard@claude-dashboard`, with input closed.
  - **A start with no handler of the dashboard's registers the plugin**, under every bound T1.32 and T1.33 set. A start that finds the plugin enabled asks nothing and writes nothing.
  - **A start never moves an existing settings handler.** `--install-hooks` does: it registers the plugin, takes the old entries out, and tells the operator to restart the open sessions.
  - **`--remove-hooks` removes both routes.**
  - **The settings file is the fallback** when `claude.exe` is not found, when Claude Code refuses, or when another data folder holds the plugin name.
  - Docs: README, Impl §9.4 and §10.2.
- **Acceptance:** with no hook, a start leaves a hand-formatted `settings.json` byte for byte and asks Claude Code for the plugin; a second start asks nothing; an existing settings handler is left alone and a partial one is topped up where it is; the opt-out, the unreadable file and the machine without Claude Code each ask nothing; both switches work through `Main` against redirected roots with the real `claude` program; a guard holds that `Program.cs` hands the plugin route to both calls; both suite counts.
- **Guardrails:**
  - A dashboard that receives nothing is the worse failure. No path may end with no hook because the plugin could not be registered.
  - The plugin folder is never the install folder: Claude Code loads the plugin in place, and a hook whose folder is gone stops in silence.
  - An open session does not see a new plugin. Nothing automatic may trade a working settings handler for one.
  - The dashboard reads `enabledPlugins` and `extraKnownMarketplaces` and writes neither.
- **Ruled 2026-10-01 (operator):**
  - **The PR's five decisions stand:** a start never moves an existing settings entry to the plugin; the restart notice for `--install-hooks` is text only; a data folder whose plugin name is held by another data folder stays on the settings file; the settings file is the fallback when `claude` is missing or refuses; a `claude.cmd` shim is treated as not found.
  - **R1 — a plugin the operator turned off stays off.** When Claude Code has the dashboard's plugin registered and set to `false` in `enabledPlugins` (as `claude plugin disable` leaves it), a start does **not** run `claude plugin install` (which turns it back on — measured on 2.1.286), does **not** enable it, and does **not** write the settings handler round it. It shows the operator instead: a notice in the window, and the tray tooltip leading with `plugin off · not receiving hooks`. The notice gives the command `claude plugin enable claude-dashboard@claude-dashboard` (run before it was written), says to restart open sessions (measured: a session open across the enable reported nothing; one started after reported), and says it clears at the next start. A complete settings handler beside a disabled plugin still carries every event, and then there is nothing to show. `--install-hooks` is an explicit request and turns the plugin back on, saying so.
- **Review fixes (2026-10-01):**
  - **A run of `claude` is bounded from start to its last byte.** A child that `claude` starts can hold the output pipe open after `claude` exits; the run now waits for the output only within the same budget, then stops the whole tree through a job object and returns with the exit code.
  - **A `claude` that records the plugin and then fails is registered.** After a failed run, the installer reads Claude Code's settings again; if the plugin is now enabled, no settings handler goes in beside it.
  - **A relative `PATH` entry is not searched** for `claude.exe`.

**T1.50 — Start with Windows, and the Settings window**
- **Goal:** the dashboard is running whenever the operator is signed in, unless the operator turns that off. Closes issue #36.
- **Depends:** PKG.3 (the installed `current\` path), T1.13 (the tray menu)
- **Realizes:** the operator's rulings of 2026-10-01, recorded in issue #36. **They supersede Impl §10.1's choice of Task Scheduler:** the `Run` key appears where users look for startup programs (Windows Settings › Apps › Startup, Task Manager's Startup tab), and the restart after a crash is given up. It brings forward the first piece of T6.1, the Settings window.
- **Deliverables:**
  - **"Settings…" in the tray menu opens a Settings window** with one checkbox, *Start Claude Dashboard when Windows starts*. A change takes effect at once; there is no Save button.
  - **`startWithWindows` in the dashboard's `settings.json`, default `true`.** Each start reconciles the `Run` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` with it, per the table in issue #36.
  - **The value** is named `Claude Dashboard`, and holds the quoted full path to `current\ClaudeDashboard.App.exe`, never the root stub `Claude Dashboard.exe`.
  - **Only an installed copy registers.** A portable copy, or a build from the repository, never writes the value; its checkbox is disabled, with a line saying why.
  - **Windows' own off switch is respected.** When `HKCU\…\Explorer\StartupApproved\Run` marks the value disabled, a start leaves the mark alone and the checkbox shows unticked, with a line saying that Windows has it off. Ticking the checkbox clears the mark.
  - **Only the checkbox turns it off.** Closing the dashboard, or ending it in Task Manager, changes nothing.
  - **Velopack's uninstall hook removes the value**, fast, and never failing the uninstall.
  - `LogonTask.cs` and its tests are removed. Impl §10.1 and §10.2 step 1 are amended to record the ruling.
- **Acceptance:** issue #36's list. In particular: a logoff and logon start the dashboard to the tray; the entry shows in Windows Settings › Apps › Startup and in Task Manager's Startup tab; every row of the reconcile table; the Windows-disabled mark is left alone and shown; uninstall removes the value; a registry failure logs one Warning and never blocks the start; both suite counts.
- **Guardrails:**
  - **Tests never touch the operator's real `Run` value or `StartupApproved` mark.** They use a registry seam, or a unique value name deleted in a `finally`.
  - No administrator rights. No change to the hook, the plugin or ingress.
  - Live checks run against a scratch install, never the operator's.

**T1.51 — The plugin is the only route**
- **Goal:** the dashboard never writes Claude Code's settings file, and tells the operator on screen whenever it is not connected to Claude Code. Closes issue #65, and leaves issue #8 with nothing to describe: the writer whose error it reports is gone.
- **Depends:** T1.49 (the plugin), T1.48 (the script, which does not change)
- **Realizes:** the operator's rulings of 2026-10-01, given after T1.49 shipped. **They supersede T1.49's fallback, its migration, and its rule that an existing settings handler is topped up.** Impl §9.3 and §9.4.
- **Deliverables:**
  - **No write of `~/.claude/settings.json`, anywhere.** `SettingsFileWriter`, the merge, the removal of old entries and the legacy HTTP removal are deleted, with their tests. The file is read.
  - **A start registers the plugin, or shows a notice.** The notice is a row in the window and the first line of the tray tooltip. It covers: no Claude Code install detected; Claude Code's settings unreadable; the plugin turned off; the plugin removed with `--remove-hooks`; the dashboard's own settings unreadable; another data folder holding the plugin name; `claude.exe` not found; Claude Code refusing.
  - **Where the plugin cannot be registered, the notice gives the two commands to run by hand.** There is no other route.
  - **An old hook in Claude Code's settings is warned about and left alone.** The plugin is not registered while it is there, because both together post every event twice. The notice says how to remove it: ask Claude, or use `/hooks`.
  - **A notice that says nothing is reporting clears when a session reports.** The old-hook notice does not: events arrive through the old hook.
  - **`--install-hooks` registers the plugin and `--remove-hooks` removes it.** Neither does anything else.
  - `HookInstaller` becomes `HookCheck`, and `HookRegistration` becomes `HookHandlers`: each now reads, and the names say so.
  - Docs: README, Impl §9.2 to §9.4 and §10.2.
- **Acceptance:** every start outcome leaves a hand-formatted settings file byte for byte and shows its notice; the order of the findings is a table test; with an old hook nothing is asked of Claude Code, and the next start after it is gone registers the plugin; a session added to the projection clears a "nothing is reporting" notice and not the old-hook one; both switches work through `Main` with the real `claude` program against redirected roots; a source guard holds that only the reading type names the file and that it holds no writing call; both suite counts.
- **Guardrails:**
  - Nothing out of Claude Code's settings is logged or shown.
  - A turned-off plugin stays off at a start (T1.49, R1).
  - No notice tells the operator to edit a settings file by hand.
  - A `claude.cmd` shim stays "not found"; such a machine gets the notice with the two commands.

**Ordering ruled 2026-09-02:** the packaging workstream — `PKG.1` → `PKG.2` → `PKG.3` → T1.33 → `PKG.4` in the [Packaging Execution Plan](claude-dashboard-packaging-execution-plan.md) — runs **ahead of T2.1**. Appendix A is unchanged; the packaging plan carries its own order.

---

## Part 4 — Phases 2–7 task outlines

Expand each into full task blocks (Part 3 format) when the phase is reached.

**Phase 2 — Go there** *(TS §III.2, §III.7, §III.8; Impl §6.1, §6.4)*
- T2.1 `ITerminalLocator` via FlaUI: enumerate Windows Terminal windows/tabs, read pane text (UIA Text pattern), match against Registry exchange text; ambiguity → unresolved. Acceptance: finds the right tab for the common case; degrades to window-level.
- T2.2 `ITerminalNavigator`: `wt.exe -w <window> focus-tab -t <index>` when resolved, else `SetForegroundWindow` + UIA invoke. Acceptance: brings the correct tab forward; click-initiated so foreground isn't blocked.
- T2.3 Wire the expanded-row "Open terminal" action to the navigator. Acceptance: clicking navigates; failures degrade, don't throw.
- T2.4 Per-terminal locate strategy (WT vs classic console). Acceptance: classic-console path uses process-tree location.

**Phase 3 — It notices** *(TS §III.5; Impl §6.2)*
- T3.1 `IFocusSource`: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` on a message-pumped thread; raise window-focus events.
- T3.2 Focus→session via `ITerminalLocator.IdentifyForegroundTab`; dwell threshold → synthetic ack into the Channel.
- T3.3 Tab-level focus via UIA selection events (refinement over window-level).
- T3.4 On-screen notice suppression: mute the notice for the session currently focused.

**Phase 4 — Task lens** *(TS §III.9; Impl §6.3)*
- T4.1 Extend `IVirtualDesktopService` to read a window's desktop id (documented tier).
- T4.2 Swap the group key to virtual desktop; desktop names as labels; degrade to cwd grouping if VD breaks.
- T4.3 Harden the undocumented-tier calls behind the adapter; version-pin.

**Phase 5 — Memory** *(Impl §8)*
- T5.1 30-day rolling prune of `events`.
- T5.2 History search over past exchanges.
- T5.3 Wait-time stats (how long agents sit blocked).
- T5.4 Warm-restart: rebuild recent Registry state from `dashboard.db`.

**Phase 6 — Polish**
- T6.1 Settings UI (thresholds, sounds, mutes, view, always-on-top, port).
- T6.2 Sound editor.
- T6.3 Themes.
- T6.4 Task/hook repair from within the app.

**Phase 7 — Anywhere** *(TS §I.4, §11; Impl §1.2)*
- T7.1 `ClaudeDashboard.Remote` (ASP.NET Core + SignalR) as a second Core consumer.
- T7.2 Authenticated channel; remote **read** of states.
- T7.3 Remote **ack**.

---

## Part 5 — Per-task prompt skeleton (for the director)

Fill the placeholders from the task block and Part 1, then hand to the coder:

```
You are implementing ONE task for the Claude Dashboard (C# / .NET 10 / WPF).

Read first (authoritative — do not contradict):
  • Technical Spec sections: <task "Realizes" TS refs>
  • Implementation Spec sections: <task "Realizes" Impl refs>

Global rules (must hold):
  <paste Part 1 working agreements, or the relevant subset>

Task <ID> — <name>
Goal: <task goal>
Build: <task deliverables>
Constraints: <task guardrails>

Done when (all required):
  <task acceptance criteria, as a checklist>
Write the tests named above and make them pass.

Do NOT:
  • touch anything outside this task's deliverables
  • add NuGet dependencies beyond those in Impl Appendix A for this layer
  • violate the dependency rule (Core has no WPF/Win32/ASP.NET; nothing references App)

Deliver: the code + tests, plus a one-line summary of what changed and any assumption you made.
```

**Director guidance:** keep tasks in dependency order (Part 3 milestones 1A→1D); never merge two tasks into one prompt; if the coder's output misses an acceptance criterion, send it back with the specific unmet criterion rather than proceeding.

---

## Appendix A — Phase 1 dependency order

```
T1.0
 ├─ T1.1 ─ T1.2 ─┬─ T1.3 ─┐
 │               ├─ T1.4 ─┤
 │               └─ T1.5 ─┤
 ├─ T1.6         (Core)   │
 └─ T1.7 ─ T1.8 ─ T1.9 ───┼─ T1.10 ─ T1.11 ─ T1.12
                          │            └─ T1.16
                          ├─ T1.13
                          ├─ T1.14 (needs T1.5)
                          ├─ T1.15
                          └─ T1.17
 T1.8 ─ T1.18 ─ T1.19
 (all) ─ T1.20   ← phase gate
```

---

## Appendix B — Agent role prompts (over cross-session messaging)

These operationalize the workflow over **Claude Code cross-session messaging**: three independent sessions you start yourself in separate terminals — named `director`, `coder`, and `reviewer` — that message each other with the `SendMessage` and `ListAgents` tools. Setup and launch are in **Appendix C**. All three share the four project documents (mockups, TS, Impl, this plan); the Handoff Contract (B.0) defines the message payloads. Paste each role block as that session's instructions (Appendix C shows how to attach it) and set `<DOCS_DIR>`.

> **Because cross-session messaging is very new, each role prompt names the tools explicitly.** Don't assume the model reaches for `SendMessage`/`ListAgents` on its own — the prompts below tell it exactly when to send, to whom, and what.

### B.0 — Handoff contract

The agents coordinate by sending each other plain-text messages with `SendMessage`, addressed by session name (`director`, `coder`, `reviewer`); `ListAgents` (or the `/list-agents` command) shows who's reachable. A message carries **only text — never files or conversation history** — so all code and artifacts move through the **git repository**, and these messages carry just the coordination text below. The **Task/Fix Prompt**, **Status Report**, **Review Request**, and **Verdict** travel over `SendMessage`. The **Resurface** and **Progress Update** are *not* messages — they are the director speaking in its own terminal, where you're watching.

**Reference, don't re-quote.** Anything that lives in the repo travels as a reference — a task ID, a spec §, a commit ref — never as a pasted block. Only content that exists nowhere else (a coder's assumptions, a reviewer's findings) is carried verbatim, and condensed at that. This keeps messages small, keeps the plan as the single source of truth, and gives the channel's loop guard — which drops a message that looks like one it has already passed — nothing to false-positive on.

**Director → `coder` — Task Prompt** (via `SendMessage`) — the Part 5 skeleton, placeholders filled from the task block. Because the message is text-only, it points the coder at the task by ID and spec refs; the coder reads the docs from the repo itself.

**Director → `coder` — Fix Prompt** (via `SendMessage`) — the same task, with an added `Fix these (from review):` list quoting the reviewer's required changes verbatim.

**Coder → `director` — Status Report** (via `SendMessage`)
```
STATUS REPORT
Task: <ID> — <name>
Status: DONE | BLOCKED | QUESTION
Summary: <what was built, 1–3 sentences>
Commit/Files: <commit ref + changed files, so the director and reviewer can find it in the repo>
Tests: <named tests> → <n passed / n failed>
Assumptions: <any assumption made because the spec left a gap>
Deviations: <anything done differently from the task block, and why> | none
Problem: <only if BLOCKED/QUESTION — the exact blocker or question>
```

**Director → `reviewer` — Review Request** (via `SendMessage`)
```
REVIEW REQUEST
Task: <ID> — <name>   (read the task block — acceptance, guardrails, spec refs — from Execution Plan Part 3 in the repo)
Change: <commit ref + files to review — the reviewer reads the actual diff from the repo>
Coder notes: <condensed: assumptions, deviations, test results — only what exists nowhere but the Status Report>
```

**Reviewer → `director` — Verdict** (via `SendMessage`)
```
VERDICT
Task: <ID> — <name>
Verdict: APPROVE | CHANGES_REQUESTED | ESCALATE
Findings:
  - Plan adherence: <pass | issue>
  - Spec compliance: <pass | issue, with TS/Impl §>
  - Working agreements: <pass | issue>
  - Tests: <pass | issue>
  - Code quality: <pass | notes>
Required changes: <numbered, specific, each tied to a criterion or spec § — only if CHANGES_REQUESTED>
Escalate because: <the spec conflict / ambiguity / cross-task design concern — only if ESCALATE>
```

**Director → You — Resurface** (spoken in the director's own terminal, not a message)
```
NEEDS YOU
Where: Task <ID> — <name>
What happened: <the blocker, escalation, repeated failure, phase gate, or spec conflict>
Options: <the choices, if it's a decision>
Recommendation: <the director's suggested course>
```

**Director → its own terminal — Progress Update** (the always-visible heartbeat, one line per exchange)
```
[Dashboard ▸ <ID>] <who> <what> → <next action>
```

### B.1 — Coder  · session name `coder`

You are the **Coder** on the Claude Dashboard project. You implement **one task at a time**, exactly to spec, with tests, and report back to the director. You do not pick your own tasks and you do not start work beyond the task you were handed.

**Messaging.** You receive tasks as incoming cross-session messages from the session named `director`. You report back by sending a message to `director` with `SendMessage` (Claude Code exposes `ListAgents` to find it and `SendMessage` to deliver). A message is **text only** — it can't carry files — so you do the work as **commits in the git repo**, and your Status Report names the commit and changed files so the director and reviewer can find them. You never message the reviewer; the director routes review. **A dropped send must not end the story:** if your Status Report send is refused or dropped — the channel tells you when it drops one — wait briefly and resend **once**; if that fails too, record the outcome in your own transcript and go idle. Never loop resends: the director's idle subscription and watchdog will find you.

**The project.** Claude Dashboard is a Windows tray app that shows a developer, at a glance, which of their many concurrent Claude Code sessions need attention. Its world is event-sourced from Claude Code hooks. Full context is in four documents in `<DOCS_DIR>`, which are **authoritative — never contradict them**: `claude-dashboard-spec.md` (Technical Spec, "TS"), `claude-dashboard-impl-spec.md` (Implementation Spec, "Impl"), `claude-dashboard-execution-plan.md` (this plan), `claude-dashboard-design.md` (Design — the product shape; §9 is the authority on row anatomy and the motion rule), `claude-code-hooks-reference.md` (all 31 Claude Code hook events, transcribed from source — the authority on what a hook fires on and what fields it carries; check its Discrepancies section before trusting a field name), `claude-dashboard-mockups.html` (UI reference — **visuals only, never ordering: its flat view is drawn in the superseded pre-ruling order; see the correction in that file and TS §IV.2/§IV.3**).

**Tech stack.** C# on .NET 10 (LTS), WPF. Three projects: `ClaudeDashboard.Core` (portable domain, **no** WPF/Win32/ASP.NET), `ClaudeDashboard.App` (WPF host + ingress + Windows integration), `ClaudeDashboard.Remote` (later), `ClaudeDashboard.Tests` (xUnit). Add no dependencies beyond Impl Appendix A for your layer without flagging it.

**Working agreements** (Part 1): Core free of WPF/Win32/ASP.NET and nothing references App; transitions idempotent + timestamp-guarded; single-writer Registry, no locks; ingress hooks are pure observers (`200` empty, no decision); hook text is data, never executed; OS adapters degrade, never crash; never elevated; no secrets committed; every Core behavior has xUnit tests.

**Per task:** (1) read the named TS/Impl sections from the repo first; (2) implement **exactly** that task — no more, no less; (3) write the named tests and make them pass; run the build and tests; (4) self-check against every acceptance criterion and working agreement; (5) if the spec leaves a small gap, choose reasonably, proceed, and record it under Assumptions — but if you hit a genuine blocker, an ambiguity you can't resolve, or a conflict between the specs, **send a `BLOCKED`/`QUESTION` Status Report instead of guessing**; (6) commit, then send your Status Report to `director`. Do not start another task on your own.

### B.2 — Director  · session name `director`

You are the **Director**. You own the Execution Plan and drive it to completion by messaging the coder and reviewer and deciding what happens next. **You never write product code yourself — you orchestrate.** You run in your own terminal, which the human watches. Your inputs are the four documents in `<DOCS_DIR>`, especially Part 3 (tasks), Appendix A (dependency order), and Part 5 (the prompt skeleton).

**Messaging.** The coder and reviewer are separate Claude Code sessions named `coder` and `reviewer`. Reach them with `ListAgents` (confirm both are reachable before you start) and `SendMessage` (address by name). Messages are **text only** — code lives in the git repo, so when you hand off or review, refer to the coder's **commit and files**, not message attachments. **Subscription is part of dispatch, not a habit:** every `SendMessage` that hands work to a peer — the coder *and* the reviewer, no exceptions — carries `notify_when_idle`, so you're pinged the moment that peer finishes. A dispatch without a subscription is an error, not a style choice. A subscription also expires after 12 hours, so it is the wake-up, never the safety net — the standing watchdog below is the safety net.

**Your loop:**
1. **Select** the next task whose dependencies are all `Done`, in the Appendix A order. If none remain in the phase, or the next item is a **phase gate** (e.g. T1.20), Resurface to the human.
2. **`SendMessage` a Task Prompt to `coder`** (Part 5 skeleton from the task block) and subscribe to its idle.
3. **Receive the coder's Status Report.** `DONE` → emit a Progress Update, then go to review. `BLOCKED`/`QUESTION` → if the answer is unambiguous in the specs, `SendMessage` the answer and continue; otherwise Resurface.
4. **`SendMessage` a Review Request to `reviewer`** (task ID, commit/files, condensed coder notes — the reviewer reads the task block from the plan) and subscribe to its idle.
5. **Receive the Verdict.** `APPROVE` → mark the task `Done`, emit a Progress Update, return to step 1. `CHANGES_REQUESTED` → `SendMessage` a Fix Prompt to `coder` with the required changes; re-review. **Cap at 2 fix cycles** per task; if it still fails, Resurface. `ESCALATE` → Resurface.
6. **Also Resurface** at phase gates, on a spec ambiguity or conflict you notice, or anything needing a human decision.

**Standing watchdog (never retired).** While anything is in flight, keep a fallback timer running: if no report, verdict, or idle notice has arrived within 30 minutes, ping the responsible peer for status and note it in a Progress Update. This timer runs for the entire run and is never dropped because the rhythm feels reliable — your only sense organ is incoming messages, so a peer that has gone quiet is indistinguishable from a peer that is working. The rhythm is never the protection; the timer is. When the timer is a scheduled job in your own session, end its prompt with this line, so a tick that finds nothing makes no sound on the dashboard ([Quiet scheduled jobs](quiet-scheduled-jobs.md)): If nothing is overdue and you took no action, reply with exactly WATCHDOG-QUIET with no punctuation, quotes or formatting, and nothing else.

**Visibility (required).** After **every** exchange — each coder report, each verdict, each decision — print a one-line **Progress Update in your own terminal**, even when you auto-continue, so the human can watch without being interrupted. Auto-continue on `APPROVE` and on trivially spec-answerable coder questions; **Resurface** (pause, address the human in your terminal) on blockers, escalations, a task that fails review twice, phase gates, and spec conflicts. Never mark a task `Done` without an `APPROVE`; never skip review; one task at a time; respect dependency order.

### B.3 — Reviewer  · session name `reviewer`

You are the **Reviewer**. You perform a **combined review**: code quality *and* adherence to the Execution Plan *and* satisfaction of the Specification. You do not write the code — you judge it and return a verdict.

**Messaging.** You receive Review Requests as incoming cross-session messages from the session named `director`. The request carries a **task ID and a commit + files** — read the task block (acceptance criteria, guardrails, spec refs) from Execution Plan Part 3 in the repo yourself, and read the actual change **from the git repo** (the diff and the tests), since the message itself carries only text. Send your Verdict back to `director` with `SendMessage`. You report to the director, not the coder. **A dropped send must not end the story:** if your Verdict send is refused or dropped — the channel tells you when it drops one — wait briefly and resend **once**; if that fails too, record the verdict in your own transcript and go idle. Never loop resends: the director's idle subscription and watchdog will find you.

**Review these five dimensions:**
1. **Plan adherence** — did it implement *exactly* this task (no scope creep, no skipped deliverables)? Dependencies respected? **Each acceptance criterion** met and covered by a test?
2. **Spec compliance** — does it satisfy the referenced TS/Impl sections, and contradict none? Cite the section for any issue.
3. **Working agreements** (Part 1) — Core free of WPF/Win32/ASP.NET and nothing references App; transitions idempotent + timestamp-guarded; single-writer Registry, no locks; ingress pure-observer (`200` empty, no decision); hook text treated as data; OS adapters degrade rather than crash; not elevated; no secrets committed.
4. **Tests** — named tests exist, are **meaningful** (not trivially passing), and green; edge cases implied by the acceptance criteria are covered.
5. **Code quality** — correctness, clarity, error handling, async correctness (no blocking the WPF Dispatcher), no obvious races or bugs, sensible naming.

**Verdict.** Send the Verdict format (B.0). `APPROVE` only when every acceptance criterion and working agreement is satisfied. Use `CHANGES_REQUESTED` with **specific, actionable** items, each tied to a criterion or spec section, separating must-fix from nits. Use `ESCALATE` — rather than approving — when you find a spec/plan conflict, a genuine ambiguity, or a cross-task design concern the current task can't resolve; that belongs to the human via the director.

---

## Appendix C — Cross-session messaging: setup & launch runbook

The operator's guide to turning the three role prompts into a running pipeline. Cross-session messaging is Claude Code's native peer-to-peer messaging; nothing to install once the requirements are met.

### C.1 Requirements

- **Version:** Claude Code **v2.1.234 or later on native Windows** (v2.1.224+ on macOS/Linux/WSL 2). Check `claude --version`.
- **Provider:** first-party Anthropic. **Not** available on Amazon Bedrock, Claude Platform on AWS, Google Cloud's Agent Platform, or Microsoft Foundry.
- **Feature flag not disabled:** ensure none of `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`, `DISABLE_TELEMETRY`, `DO_NOT_TRACK`, or `DISABLE_GROWTHBOOK` is set in your shell, settings, or managed settings — any of them turns the feature off.

### C.2 Verify it's on

- Run `/list-agents` (alias `/peers`) in a session. If the command **isn't recognized**, the session lacks the feature — recheck the version. If it **lists this session's own name** (and, once others are running, the reachable sessions), messaging is on.
- `/status` shows a `Peer address` row when messaging is active.

### C.3 Delivery setting

- Set `crossSessionInbound` to **`accept`** so peer messages deliver without an approval dialog — via `/config` → **"Messages from your other sessions"**, or in `settings.json`. Mind permission modes: a session running in bypass-permissions mode holds incoming messages for approval by default, which would stall the pipeline.

### C.4 Launch the three sessions (each in its own Windows Terminal tab)

Name each session so they can address each other by name:

```
Tab 1:  claude --name director
Tab 2:  claude --name coder
Tab 3:  claude --name reviewer
```

Attach each session's role (Appendix B) as its instructions — any of:
- `--append-system-prompt "<role block>"` on the launch command, or
- a subagent/role definition file the session loads, or
- simplest: paste the role block as the session's **first message**.

Keep the names unique; if a name is already taken, Claude Code appends a variant, so check `/list-agents` and rename with `/rename` if needed.

### C.5 Kick off

In the **`director`** tab:

```
Confirm you can reach `coder` and `reviewer` with /list-agents, then begin the
Execution Plan at T1.0. Follow your role instructions: dispatch one task at a
time, route completed work to the reviewer, and surface to me here when a
decision is mine.
```

The director then messages `coder`, watches for its report (via `notify_when_idle`), routes to `reviewer`, and drives the loop.

### C.6 Watch

You watch all three tabs directly. The `director` tab prints a Progress Update after each exchange and pauses (Resurface) only when a decision is yours — so the director tab alone tells you where things stand, and you can drop into the coder or reviewer tab whenever you want the detail.

### C.7 If a message doesn't arrive

`/list-agents` recognized but nothing landed → check, in order: no `SendMessage`/`ListAgents` **deny rule** in permissions; the receiver's `crossSessionInbound` isn't `hold`/`refuse`; the **target name** is right (watch for collisions/variants in `/list-agents`).

### C.8 On a skill for this

There's **no skill, and none is needed**: `SendMessage`/`ListAgents` are native tools, on automatically when C.1 is met. The newness risk — the model not reaching for them — is handled by the role prompts naming the tools directly (B.0–B.3), and optionally a `CLAUDE.md` note in the repo. A skill would add discoverability, not capability.

---

## Appendix D — Compaction messages (one per role)

> **Restored 2026-08-26.** These are the operator's, written on the night of 25 August and lost
> the same night: they were added to a copy of this file that predated Appendix C, and that copy
> overwrote the committed plan at `00:00:00`. The director restored the plan from git — which
> recovered the runbook and destroyed these. They survived only because a copy of the overwritten
> file was kept first. Numbered **D** rather than **C** because Appendix C now holds the launch
> runbook; the text is otherwise as written, less one stray character.
>
> The failure is worth naming where it happened: the director diffed the overwritten file, saw
> 152 committed lines removed, concluded "reversion", and never asked what the file **added**.
> Checking one direction of a diff is the same defect as a test that can only fail one way.

### D.1 Director

```
Preserve the orchestration state that lives only in this session and can't be rebuilt from the repo:
- The task ledger: every task and its status — APPROVED/Done, in-flight (and with which session), or blocked (and on what).
- My position in Execution Plan Appendix A's dependency order, and what's next.
- Every ruling or decision I've made this run and its rationale (task-order changes, spec ratifications, any doc amendments committed), plus every decision still pending with the human.
- Any pre-drafted next Coder prompt in my scratchpad, with its {{placeholders}}.
- Standing procedures adopted mid-run (pre-drafting the next prompt; this compaction step; anything else the human added).
- Commit refs of work landed so far, and that the peers are named coder and reviewer.
Drop the verbatim SendMessage payloads and the Progress Update lines already emitted.
My authoritative role is CLAUDE.md and Appendix B.2 — re-read them rather than relying on this summary for it.
```

### D.2 Coder

```
Preserve the build state that lives only in this session:
- The current task ID with its acceptance criteria and guardrails, and how far along I am.
- Every commit I've landed (refs + files) and what each did.
- Every decision I made with downstream impact, and WHY — e.g. path/casing normalization, a type that's now an identity rather than a display string, any entry point I added that the task block didn't name.
- Assumptions I recorded in Status Reports, and anything in flight or not yet committed.
- Any open question I've posed and am awaiting a ruling on.
Drop exploration chatter, compile errors already resolved, and approaches already superseded.
My authoritative role and working agreements are CLAUDE.md and Appendix B.1 — re-read them rather than relying on this summary for them.
```

### D.3 Reviewer

```
Preserve the review state that lives only in this session:
- The tasks I've reviewed and the verdict I issued for each.
- Any CHANGES_REQUESTED items still outstanding — sent back and not yet re-reviewed and cleared.
- Recurring issues or patterns I've been flagging, so my standard stays consistent across tasks.
- Any ESCALATE I raised and its status.
Drop the line-by-line diff-reading notes for tasks I've already returned a verdict on.
My authoritative role and the five review dimensions are CLAUDE.md and Appendix B.3 — re-read them rather than relying on this summary for them.
```
