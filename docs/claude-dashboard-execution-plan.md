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
- **A test run is green only when all three hold** *(added 2026-10-05 from issue #12)*: `dotnet test` exits with code 0; its output has no line "The active test run was aborted"; and the `Total` is the count expected before the run (the last known count, plus the tests added, less the tests removed). `Passed!` with `Failed: 0` is not enough. If the test host crashes, `dotnet test` still prints `Passed!` for the tests that reported before the crash, and `Total` counts only those (measured 2026-10-05: a crash after 7 of 8 tests printed `Passed! … Total: 7`, and the exit code was 1). Every report of a run gives the exit code, the total and the expected total. **`build.ps1` applies the rule** (T1.80): before the run it takes the expected count from the test host's own list (`dotnet test --list-tests`, one line for each theory case, as `Total` counts them); after the run it checks the three things and prints one verdict line, `GREEN: exit 0, no abort, Total N, expected N, skipped S. Saved in <folder>` or `NOT GREEN: <each check that failed, with the numbers>; skipped S. Saved in <folder>`, and exits with 0 only for `GREEN`. A skipped test (`[Fact(Skip = …)]`, or a skipped theory row) is listed and counted in `Total`, so the line shows the skipped count and never fails on it (T1.80 review). The folder is `artifacts/test-runs/<date-time>-<configuration>/`, which git ignores: it holds the list (`list.txt`), the whole console output (`console.txt`, written as the run goes, so a crash leaves it) and the results file (`test-results.trx`). The script never deletes a saved run. `-ExpectedTotal` replaces the list's count, with the list kept as a check, for a suite whose list cannot count as `Total` does. A run by hand is read by the same three checks.

**Definition of Done (global):** builds clean; named tests green (by the rule above); acceptance criteria met; no cross-layer leakage (verified by the dependency rule); logs on the new paths.

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

**T1.52 — The log file follows the log level**
- **Goal:** `logging.minimumLevel` set to `Debug` puts Debug lines in the log file, as T1.37 intended and the code's own comments promise. Closes issue #68.
- **Depends:** T1.37 (the decisions record and the setting)
- **Realizes:** the operator's ruling of 2026-10-02: make the file follow the setting, rather than withdraw the promise.
- **Deliverables:**
  - `AppHost.CreateLogger` gives the file sink `logging.EffectiveMinimumLevel` in place of the fixed `LogEventLevel.Information`. The default stays Information, so nothing changes for anyone who has not set the key.
  - The comments that disagree are made true and consistent: `LoggingSettings.MinimumLevel`, `DecisionRecorder.Add` and `EventConsumer.Report`.
  - The documents that record the old floor: Impl §8.2 and §8.4, the event-flow guide §12 and §14, and `docs/quiet-scheduled-jobs.md` "How to check it is working".
- **Acceptance:** a test builds the logger through `AppHost.CreateLogger` with `minimumLevel` `Debug`, writes one Debug line, and reads it back from the file; a second test, with the default, finds the Debug line absent; a value above Information still removes Information lines from the file; no prompt, answer or title text appears in any Debug line that now reaches the file; both suite counts.
- **Guardrails:** the default level does not change. Debug lines are already written with identifiers only; verify that holds for every Debug call site before the floor is lifted.

**Ordering ruled 2026-09-02:** the packaging workstream — `PKG.1` → `PKG.2` → `PKG.3` → T1.33 → `PKG.4` in the [Packaging Execution Plan](claude-dashboard-packaging-execution-plan.md) — runs **ahead of T2.1**. Appendix A is unchanged; the packaging plan carries its own order.

### Milestone 1F — Observability, and small fixes

The work in GitHub milestone 3, "Observability 1": issues #3, #14, #67, #71, #72 and #73 (#68 was closed by T1.52). Each task is written here before it is dispatched. Code moves through a branch in the coder's own worktree and a pull request; nothing reaches `main` without the reviewer's APPROVE.

**Order, set 2026-10-03 (director):** T1.53 (#67) first, because it needs no ruling and touches nothing the others touch. Then #71, which turns the one notice into a short list; #72, #73 and #14 each add a source to that list, so they follow it. #3 is independent and waits for its ruling.

**T1.53 — An Error row says which error it was**
- **Goal:** a session in Error shows the kind of error (`rate_limit`, `overloaded`, and so on) beside its badge and in `/state`. Closes issue #67.
- **Depends:** T1.41 (the mapper's present shape), T1.46 (`/state`)
- **Realizes:** the wire is the authority where it disagrees with the documentation (hooks reference, Discrepancies). All 18 archived `StopFailure` events carry `error`; none carries `error_type` or `matcher` (event flow §13, measured 2026-09-29).
- **Deliverables:**
  - `HookPayload` binds `error`. `HookEventMapper` reads `error` first, then `error_type`, then `matcher`, then the empty string.
  - The row shows the raw value, so a kind that `StopFailureKinds` does not name still reaches the operator. `StopFailureKinds` gains no names unless the archive shows them.
  - The second effect in #67 is intended: a second `StopFailure` with a **different** kind on a session already in Error is no longer declined as a duplicate. It changes the row's detail. The same kind twice is still a duplicate.
  - Before the row shows the values, read the distinct values of `error` in a **copy** of the operator's `dashboard.db` (copy the file and its `-wal` to a scratch folder; never open the original). Report the distinct values and their counts. They are identifiers, not operator text.
  - Documents, in the same change: TS §II.2 and Appendix C (remove the row); Impl §3.5 (`errorKind`) and §9.1; the hooks reference, discrepancy 4 (it stays a discrepancy; the "known defect" note goes); Core and App §6.3 (the row "The error kind"). One row each in TS Appendix D and Impl Appendix C.
- **Acceptance:** a mapper test with the payload as the wire sends it (`"error": "rate_limit"`) gives `ErrorKind` `rate_limit`; a payload with only `error_type` still maps; a Registry test: Error with kind A, then a `StopFailure` with kind B, moves the detail to B, and a second B is a duplicate; a realized-window test shows the kind beside the badge with `BindingErrorWatch` clean; a `/state` test answers `"errorKind": "rate_limit"`; plant: revert the mapper to `error_type` first, and the wire-shape test fails; both suite counts.
- **Guardrails:** the error kind is an identifier, but `error_message` (if it ever arrives) is operator-adjacent text: it is not read, stored in a new field, shown or logged. No change to the state machine beyond the duplicate rule above.
- **Done 2026-10-03:** PR #75, merged as `e8fc89e`, `26da745`. The archive held `rate_limit` 14, `server_error` 3, `authentication_failed` 1; `server_error` was added to `StopFailureKinds`. Carried to T1.54: TS §II.2 says `error_type` is read "only when `error` is absent"; it is also read when `error` is not a string (Impl §9.1 is right).

**T1.54 — The window says when history is not recorded, and the notice row holds a list**
- **Goal:** when the dashboard cannot write `dashboard.db`, the window and the tray say so, and the store tries again each minute instead of giving up until a restart. The notice row becomes a short list, so that #72, #73 and #14 can each add a notice. For issue #71.
- **Depends:** T1.51 (the notice row and `HookNotice`), T1.37 (the decisions record that the store writes)
- **Realizes:** the operator's ruling in #71: **try again each minute**. TS §IV.7 ("the event log cannot be written") changes from "writes one warning and stops the log" to "says so on screen and tries again each minute".
- **Deliverables:**
  - **A list of notices.** The notice row shows each active notice on its own line, in a fixed order: the plugin and connection notices of T1.51 first, then "history not recorded". Each notice has its own window text, its own tray text and its own rule for when it clears. The plugin notices keep their present texts and rules exactly. Later tasks add: no sound device (#72), settings not read (#73), port taken (#14). Design the list so that each of those is one new source, not a change to the list.
  - **The tray tooltip** leads with the ingress fault, then each notice's tray text, joined by ` · ` as today, then the counts.
  - **The history notice.** Window: "History is not being recorded: the database could not be written. The dashboard tries again each minute." Tray: `history not recorded`. The tray colour does not change. It shows while the store's last write failed, and clears at the first write that succeeds.
  - **The retry.** After a failed write, the store attempts the next write no sooner than 60 seconds after the failure. The events that arrive in that minute are counted as lost, not queued. The time comes from an injected clock. **No new timer and no new thread:** the attempt rides on the next event, and the notice is read on the 15-second tick that already refreshes the tray. The value the tick reads is written on the archive thread, so it is published safely (`Volatile` or equivalent).
  - **The log:** one Warning when the store first fails (as today, with the "no further attempt" sentence replaced); no line for a failed retry; one Information line when it records again, with the count of lost events.
  - Documents, in the same change: TS §IV.7 (the row); TS §II.2, where the T1.53 nit says `error_type` is read "only when `error` is absent" (make it "absent or not a string", two places); Impl §5.2 (what leads the tooltip), §5.6.1 item 4 (the notice row is a list) and §8.3 (the store tries again); Design §9 (Notice). One row each in TS Appendix D, Impl Appendix C and Design §13.
- **Acceptance:**
  - With a scratch `CLAUDE_DASHBOARD_HOME` whose `dashboard.db` is a folder: one hook post shows the notice and the tray text; the session row still appears and the sound engine still plays (asserted through its path, not by reading state).
  - Under a fake clock: a failure, then a working file, then an event 59 seconds later is not written and an event 60 seconds later is; the notice clears on that write; the lost count in the log line is right.
  - Two notices at once (a plugin notice and the history notice) both show in the window, in order, and both lead the tooltip. Each clears by its own rule.
  - The present plugin notice tests pass unchanged, or each change to one is justified in the report.
  - A realized-window test with `BindingErrorWatch` clean.
  - Plants: (a) the store never retries, and the clock test fails; (b) the list shows only its first notice, and the two-notice test fails.
  - Both suite counts.
- **Guardrails:** degrade, never crash: a failing store never throws into the consumer. No payload, title or prompt in the log or a notice. `/state` does not change in this task. The retry never blocks the consumer thread for longer than a normal write.
- **Done 2026-10-03:** PR #77, merged as `a2dfaad`, `b3607c5`. `NoticeBoard(params INotice[])` in constructor order; `HistoryNotice` reads the store on the tick. The reviewer saw the real tick show the notice 14 s after a post on a live scratch dashboard. Carried to T1.55: a test for fail, recover, fail again; the Warning's stack on every episode; the documents say "one Warning" where the code writes one for each failure episode. Carried to #14's task: the ingress fault leads the tooltip by its own path, so a port notice must move it into the board, not add beside it.

**T1.55 — The window says when there is no sound device, and the record says when a sound was dropped**
- **Goal:** when Windows has no working sound output, the window and the tray say so, and the decisions record says that a sound was dropped instead of saying that it played. For issue #72.
- **Depends:** T1.54 (the notice list), T1.37 (the decisions record), T1.14 (the sound engine)
- **Realizes:** the operator's rulings of 2026-10-03 in #72: **a notice row and a tooltip line, with no mark on the tray icon** (the tray keeps five colours and no other marks, Design §9); **the record tells the truth:** the player reports a drop back, and the record gets a "dropped" row in place of "played".
- **Deliverables:**
  - **The player reports what it did.** `ISoundPlayer.Play` returns an outcome, a Core enum: queued, no output, or failed (a missing sound, or an unexpected exception). `NAudioSoundPlayer` returns it from the paths that already count `QueuedCount` and `DegradedCount`. Every other `ISoundPlayer` (fakes, the replay's player) returns queued unless a test sets otherwise.
  - **The record.** A queued sound records as today (`NoticePlayed`, `NudgePlayed`, `GroupNoticePlayed`). A dropped one records a new kind, `SoundDropped`, with reason `NoOutput` or `Failed` and the same identifier detail the played row would have had (`kind=… sound=…`, the rung for a nudge, the group for a group sound). Identifiers only.
  - **The sound rules do not change.** A dropped notice still counts as announced, and the nudge ladder advances as it would have. Nothing is replayed when a device returns: a stack of old sounds at that moment is noise. Say so in the comment and in Impl Part 7.
  - **The notice.** A third source on the board, after the history notice. Window: "No sound device. Notices and nudges are silent until Windows has an output device." Tray: `no sound device`. The tray colour does not change. It shows while the player has no output, read on the 15-second tick through an App interface, not the NAudio type. It clears at the next tick after a device returns. **It must not flash at start:** if the player binds its device after the window opens, the notice waits for the player's first attempt to finish. Find out, and report, how the player starts.
  - **The limit that stays:** a device that is listed, active and silent (volume at zero, a monitor with no speakers) cannot be told apart from one that works. Say so in TS §IV.7.
  - **From T1.54's review:**
    - A test for fail, then recover, then fail again: a second Warning and a second recovery line, with a new lost count.
    - The Warning carries the exception and its stack on the first failure of the process only. A later episode writes the Warning with the exception's type and message, without the stack. This bounds a flapping disk (a backup program locking the file) to one short line a minute.
    - The documents say "one Warning when it fails (not for a failed retry)" in place of "one Warning": TS §IV.7, Impl §8.3, event flow §11.
  - Documents, in the same change: TS §IV.7 (the row "The sound device fails"); Impl §5.2, §5.6.1, Part 7 and §8.3 (the new kind in the kinds table); Design §8 and §9; Core and App §3 where it lists the ports, if `ISoundPlayer` is described there. One row each in TS Appendix D, Impl Appendix C and Design §13.
- **Acceptance:**
  - With `FakeAudioEndpoints` and no default endpoint: the notice and the tray text show at the tick; given an endpoint, both clear at the next tick.
  - With no output, a notice that is due records `SoundDropped` with reason `NoOutput`, and no `NoticePlayed`. With output, `NoticePlayed` as today. The same for a nudge and a group sound.
  - The nudge schedule under a fake clock is the same with output and without.
  - No flash at start: a start whose player binds normally shows no sound notice at any tick.
  - The notice list shows the history notice and the sound notice together, in order.
  - A realized-window test with `BindingErrorWatch` clean.
  - Plants: (a) the engine records `NoticePlayed` whatever the outcome, and the record test fails; (b) the notice reads a stale value that never clears, and the clear test fails; (c) a dropped sound does not advance the ladder, and the schedule test fails.
  - Both suite counts. If the box has a sound device you can disable without touching the operator's settings, a hardware check; otherwise say it was not done. **Never change the operator's audio configuration.**
- **Guardrails:** never touch the operator's audio settings or default device. The player's state is set on the audio thread and read on the UI tick: publish it safely. Degrade, never crash: a player that throws still returns an outcome. `/state` does not change in this task.
- **Done 2026-10-03:** PR #82, merged as `2e8f739`, `821ea08`. The player binds its device in its constructor, before the window and the tray exist, so the notice cannot flash at start. No hardware check: it would change the operator's audio configuration. Open nit: the notice says "No sound device" also when a device is present and keeps failing.

**T1.56 — A settings file that cannot be read is kept aside, and the window says so**
- **Goal:** when the dashboard's own `settings.json` does not parse, the dashboard renames it, writes a new file with the defaults, and says so in the window and the tray. The operator's settings are never overwritten. For issue #73; it also removes the loss that issue #26 describes.
- **Depends:** T1.54 (the notice list), T1.51 (`HookNotice` and the start findings), T1.32 (an unreadable file registers no plugin)
- **Realizes:** the operator's ruling of 2026-10-03, recorded in the comment on #73. **It supersedes** "the dashboard runs on the defaults and leaves the file as it is" (TS §IV.7, Impl §8.2): that promise held only until the next save, which wrote the defaults over the file (#26). #26 stays "won't fix" as a separate change; the operator agreed that #26 closes as completed when #73 is completed.
- **Deliverables:**
  - **When.** The file exists, can be read, and does not parse: bad JSON, a wrong type, or a bare `null`.
  - **The backup.** The file is renamed (a move, not a copy) to `settings.error-<yyyyMMdd-HHmmss>.json` in the same folder, local time. The bytes do not change. If that name exists, add `-2`, `-3`.
  - **The new file.** A fresh `settings.json` with the defaults is written in its place. Later saves (the window's place, rosters, the Settings window) go to it, never to the backup.
  - **Who does it.** Only a start that is the first instance and will show the window, after the single-instance decision and before anything else reads the settings. A second instance that exits and the one-shot switches (`--install-hooks`, `--remove-hooks`, `--replay`) leave the file alone.
  - **This start knows the file was unreadable.** The hook check of this start must see the original outcome, not the fresh file: **this start registers no plugin** (T1.32's guard). Keep the first load's outcome as the authority for the whole start. A later `Load` that reads the fresh file must not turn this start into a normal one.
  - **When the file cannot be opened at all** (no permission, or another program holds it), or the rename fails: no backup and no new file; the dashboard runs on the defaults and **saves nothing for the rest of that run**. Put that guard where `Load` and `Save` meet (`SettingsStore`), so every save site obeys it: the quit-time window save, the roster save, the Settings window, the `installHooksAtStart` record. One log line when a save is refused.
  - **The notice.** A source on the board, after the history and sound notices. Window: "settings.json could not be read. It was renamed to `<backup name>`, and a new settings.json with the defaults was written in `<full folder path>`. Copy your settings back from the renamed file, then restart the dashboard. If you had removed the plugin with --remove-hooks, copy "installHooksAtStart": false back too, or the next start connects it again." In the cannot-open case: "settings.json in `<full folder path>` could not be opened, so the dashboard runs on its defaults and saves no settings until it is restarted." Tray, both cases: `settings not read · using defaults`. It stays until the next start. The parse error goes to the log only, never to the screen.
  - **The old opt-out notice.** `HookNotice.ShowOptOutUnknown` tells the operator to "fix or delete" the file. After a rename that advice is wrong, and the settings notice already says what this start did. Do not show it in the rename case. In the cannot-open case it stays as today. Report what you chose for its "clears when a session reports" rule and why.
  - **The log:** one Error line with the backup's full path (or the cannot-open reason) and the parse problem. No setting value is logged.
  - Documents, in the same change: TS §IV.7 (the row "The settings file cannot be read"); Impl §5.2, §5.6.1, §8.1 (the backup file in the data-folder table), §8.2 (the paragraph "A file that cannot be read never stops the start", and the known-defects line for #26), §9.4 (the start table's row for the dashboard's own settings); Design §9. One row each in TS Appendix D, Impl Appendix C and Design §13. Remove the T1.55 nit's cause only if you touch that text; otherwise leave it.
- **Acceptance:**
  - A start on a scratch data folder whose `settings.json` holds `{ "port": }`: the backup exists with the original bytes (hash), the new file holds the defaults, the notice and the tray text show, and no plugin is registered (a stand-in `claude`, or the existing seam). With the plugin enabled and with it absent.
  - The next start on that folder: no second backup, no notice, the file reads as `Loaded`.
  - A quit after the bad start saves the window's place into the new file; the backup is unchanged (hash).
  - The cannot-open case (the file held open with no sharing, in a test): no backup, defaults, the notice's second text, and every save site refuses (one test for each site, or one at the store with a test that each site goes through it).
  - A second instance and each one-shot switch leave a bad file byte for byte.
  - The board shows this notice with the others, in order. A realized-window test with `BindingErrorWatch` clean.
  - Plants: (a) the hook check reads the fresh file, and the no-plugin test fails; (b) the rename becomes a copy and a rewrite, and the hash test fails; (c) one save site skips the guard, and its test fails.
  - Both suite counts.
- **Guardrails:** the dashboard never writes Claude Code's settings. The backup is never deleted or rewritten by the dashboard. Tests use scratch folders only, never the operator's data folder. No setting value or parse text on screen; no setting value in the log.
- **Done 2026-10-03:** PR #83, merged as `01eef6e`, `f5886c6`. `PrepareForStart` runs in `Program.Main` right after the second-instance stand-down; the hook check reads the original outcome. #26 closed as completed with the operator's agreement. Issue #84 filed for an older race the suite met once (the store disposed mid-write). Carried to T1.57: review nits 1, 3, 4 and 5. Not carried: a crash between the two renames leaves the backup and `settings.json.new` with no notice at the next start; nothing is lost, and the window is microseconds.

**T1.57 — A port that is taken says what to do, and the port fault joins the notice list**
- **Goal:** when the dashboard cannot get a port, the log, the tray and the window each say what to do: free a port, or change the pin, then restart. The port fault becomes a source on the notice board instead of a separate tooltip path. For issue #14. Also the follow-ups from T1.56's review.
- **Depends:** T1.54 (the board), T1.56 (the settings notice), T1.21 (the port choice)
- **Realizes:** issue #14 and its recommended fix (comment of 2026-10-03), which is taken as written. The pinned-port log line is the model. **No retry and no poller:** the dashboard asks once, by binding (TS §I.2).
- **Deliverables, #14:**
  - **The tray lines** (`IngressStatus`): no pin, every port taken: `port <n> taken · free a port and restart`; a pin that is taken: `pinned port <n> taken · unpin it or free it, then restart`. Find every other `IngressStatus` that sets a fault (for example a bind that fails after the choice) and give each a remedy in the same short form. Report the list.
  - **The window notice** (the long form), as the **first** source on the board, before the plugin notices. No pin: "The dashboard cannot receive anything: every port it tried is in use (`<first>` to `<last>`). Free one of them, or pin a free port with "port" in `<full path of settings.json>`, then restart the dashboard. Claude Code's settings need no change: the hook finds the new port by itself." A pin: "The dashboard cannot receive anything: port `<n>` is pinned in settings.json and another program holds it. Free that port, or change or remove the "port" setting, then restart the dashboard."
  - **One path for the tooltip.** `TrayViewModel` today puts `_ingress.Fault` before the board's text. Move the ingress fault into the board and remove the separate term, so the tooltip shows it once, first. The tray colour rule for an ingress fault does not change.
  - **The unpinned log line** (`Program.ReportPortChoice`, the last branch) gains: "Free one of them, or pin a free port with "port" in settings.json, then restart the dashboard. Claude Code's hook settings need no change: the hook finds the new port by itself."
  - `StartupDecision.ExplainReportAndExit` already says what to do (read 2026-10-03); leave it.
- **Deliverables, from T1.56's review:**
  - **The Settings window** (`SettingsViewModel`): when the save is refused, the note reads "This choice is not remembered: the settings file could not be opened."
  - **A third settings text** for a file that opened but could not be kept aside (the rename failed): "settings.json in `<folder>` could not be read or kept aside, so the dashboard runs on its defaults and saves no settings until it is restarted." The cannot-open text stays for the cannot-open case.
  - **The doubled full stop** at the end of the settings Error line in `AppHost.ReportStartup`.
  - **Start with Windows follows the plugin rule.** A start whose settings were unreadable (kept aside, cannot open, or rename failed) does not reconcile the `Run` value: it leaves the value and Windows' `StartupApproved` mark as it found them, because the operator's choice was in the file it could not read (the same reason T1.32 registers no plugin). The kept-aside notice's copy-back sentence names both keys: "…copy "installHooksAtStart": false and "startWithWindows": false back too, if you had set them, or the next start turns them on again." (Director's ruling, 2026-10-03, extending the operator's #73 ruling.)
- Documents, in the same change: Impl §3.1 (the last paragraph: no free port), §5.2 (what leads the tooltip), §5.6.1 (the notice row's first source), §8.2 (the kept-aside start leaves the `Run` value), §10.1 (the start's reconcile is skipped after an unreadable file); TS §IV.7 (the row "No free port"). One row each in TS Appendix D and Impl Appendix C.
- **Acceptance:**
  - `PortSelection.Choose` with a probe that answers "taken" for every candidate (no real port is opened): the log line, the tray line and the window notice each hold "restart"; the window notice names the first and last port tried. The same for a taken pin.
  - The tooltip shows the port fault exactly once, first, and the counts after it; with a plugin notice as well, the port fault leads.
  - Every `IngressStatus` fault in the report's list has a test.
  - The Settings window shows the not-remembered note when the store refuses.
  - The rename-failed case shows the third text.
  - A kept-aside start, and a cannot-open start, do not call `Reconcile` (a seam or a guard), and leave a test `Run` value and mark as found. A normal start still reconciles.
  - A realized-window test with the port notice and one other, in order, `BindingErrorWatch` clean.
  - Plants: (a) the tooltip keeps the separate ingress term, and the exactly-once test fails; (b) an unreadable start reconciles, and its test fails.
  - Both suite counts.
- **Guardrails:** no test opens a real port for this; the probe is a function. Tests never touch the operator's real `Run` value or `StartupApproved` mark (T1.50's seam). No poller. No setting value on screen or in the log.
- **Review fix (2026-10-03, director):** the review found that a stranger on the port in `port.txt` makes a first instance start deaf (`StartupDecision.For` gives `StartWithoutIngress`) even when the walk found a free port; the window then calls the free port "in use", and a pin does not help, because the decision probes `port.txt`'s port before the pin is looked at. **That contradicts the specifications**, which are right: Impl §3.1 ("only the pin tries no other"), Impl §5.3 ("the port corroborates only"), and T1.21's acceptance ("a `port.txt` naming a taken port falls through to the derivation"). `StartWithoutIngress` dates from T1.15's single fixed port and was not updated by T1.21. **The fix is in the decision:** a first instance that holds the gate starts normally whatever holds the recorded port, unless it is a copy of this dashboard (which is still signalled), and `PortSelection.Choose` decides the port. Every message then tells the truth by construction. A test reproduces the review's live case (a stranger on `port.txt`'s port, no pin) and binds the walked port; a second with a pin on a free port binds the pin. The `AppHost` error line that calls the chosen port "held by another process" must be true in every remaining case. Also the review's nit: `StartupHookGuardTests.StatementAt` removes whitespace around `.` and before `(`.
- **Done 2026-10-03:** PR #85, merged as `0501a86`, `333ac12`, `dba2430` (one fix cycle). `StartWithoutIngress` is removed; a first instance starts normally unless a copy of itself holds the recorded port. The reviewer repeated both failing live runs: each bound a port and answered `/health`.

**T1.58 — A full queue sheds only events that change nothing**
- **Goal:** a permission prompt, a question, an error, a finish, a prompt, an Ack or any other event that can change what the board shows is never thrown away because the queue is full. Only events that repeat information are shed. The window says when events were shed. For issue #3.
- **Depends:** T1.54 (the board), T1.37 (the decisions record), T1.9 (the pipeline)
- **Realizes:** the operator's ruling of 2026-10-03 on #3: **shed only noise**; a test that fails if the queue cannot keep up; a notice on screen. It supersedes Impl Part 4's "drop-oldest" for the event channel.
- **Deliverables:**
  - **Noise, by kind, decided at ingress without reading the Registry:** `PostToolBatch`, and a `Notification` whose kind changes no state (`idle_prompt`, `agent_completed`; take the list from `SessionRegistry.TargetOf`, not by hand, or pin the two together with a test). Everything else, including every UI-published event (`Ack`, `SoundCommand`, `RostersChanged`), is never noise.
  - **The admission rule.** While the queue holds fewer than the capacity (1,024), every event is written. At or above it, a noise event is refused at the door (the newest is shed, not the oldest), and every other event is still written. Order is kept: nothing already queued is removed.
  - **A hard limit** for the case where state-changing events themselves flood (a fault, never seen): at 16,384 queued, the oldest event is dropped as today, so memory stays bounded. Degrade, never crash.
  - **The safe direction, documented:** a shed `PostToolBatch` that would have resumed a blocked session leaves the row red until the session's next event. A row that is too loud for a moment is the safe failure; a row that is silent while Claude waits is the one this task removes. Say so in Impl Part 4.
  - **The record and the log:** a shed event records `EventDropped` with reason `noise` and the event kind in `detail`; a hard-limit drop records reason `pipeline` as today. The log writes one Warning when shedding starts, with no line for each event, and one Information line when the queue is below the capacity again, with the count shed. Today's Warning for each dropped event would bury the log in the one situation where it matters.
  - **The notice**, a board source after the settings notice. While noise was shed in the last 5 minutes: window "The dashboard fell behind and skipped repeated tool events. Rows may lag until each session's next event." Tray: `fell behind`. It clears 5 minutes after the last shed, on the tick. If the hard limit dropped any event: window "The dashboard fell far behind and lost events. A row may be wrong until its session's next event; restart the dashboard to be sure." Tray: `events lost`. That one stays until the next start.
  - **The bound, asserted.** A test drives the real consumer on the expensive path, state-changing events that each apply, raise `SessionChanged`, run the sound policy and reach the projection, and fails if 1,024 of them take longer than a generous limit (choose it, at least 50 times the measured time on this box, and report both numbers). It fails if someone later puts blocking work on the consumer loop, which is how #3 becomes reachable. It must not be flaky: report 20 runs.
  - The archive channel (`EventArchive`, drop-oldest to the database writer) is not changed: a drop there loses history, not state. Say so in Impl Part 4.
  - Documents, in the same change: Impl Part 4 (the channel), §5.2, §5.6.1, §8.3 (`EventDropped`'s reasons); TS where the event channel or its loss is described (find it); the event flow's channel section and §11; Design §9. One row each in TS Appendix D, Impl Appendix C and Design §13.
- **Acceptance:**
  - #3's reproduction at capacity 2: a `UserPromptSubmit` applied, then a permission `Notification` and two `PostToolBatch` published unread: the permission prompt is applied (the transition log has `Working` to `NeedsPermission`, and the permission sound plays through the engine), and one `PostToolBatch` is shed. The session then ends in `Working`, because the batch that was written resumes a blocked session (TS §II.2); under drop-oldest the permission is dropped and `NeedsPermission` never happens. (Corrected 2026-10-03 at the coder's question: the first text asked for a final `NeedsPermission`, which contradicts §II.2.)
  - At capacity, a `Stop`, an `Ack` and a `SoundCommand` are each written; a `PostToolBatch` and an `idle_prompt` are each shed.
  - Order: the events written come out in the order they went in.
  - The hard limit drops the oldest and shows the second notice.
  - The noise notice shows after a shed and clears 5 minutes after the last one under a fake clock.
  - The noise list and `SessionRegistry.TargetOf` agree (a test that fails if one changes without the other).
  - One Warning and one Information line for a burst of 10,000 shed events.
  - The throughput test, with its 20 runs reported.
  - A realized-window test with `BindingErrorWatch` clean.
  - Plants: (a) drop-oldest restored, and the reproduction fails; (b) `Notification` of every kind treated as noise, and the at-capacity test fails; (c) one `Thread.Sleep(10)` added on the consumer path, and the throughput test fails.
  - Both suite counts.
- **Guardrails:** `/hook` still answers `200` empty for a shed event: shedding happens after the answer is decided and never changes it. The sink never blocks a request thread. One writer to the Registry. No payload, title or prompt in a log line, a record or a notice.
- **Done 2026-10-03:** PR #87, merged as `91b7b9e`, `9c7668a`, `7741bca` (one fix cycle, documents only). The noise list is pinned to `SessionRegistry.TargetOf`. The throughput test took about 35 ms unloaded and at most 119 ms with every core busy, against a 5,000 ms limit.

**Milestone 1F, first pass, closed 2026-10-03:** every open issue in GitHub milestone 3 has a merged change (T1.53 to T1.58). Not built from the observability review, and not in milestone 3: the hook self-test and "last hook received" (#74), a health block in `/state`, an Activity window, start and stop rows in the database, and indexes.

**Milestone 1F, second pass: GitHub milestone 4, "Observability 2".** Issues #74, #76, #78, #79, #80, #81 and #86, and #84 added at the operator's word. Each task is written here before it is dispatched, as in the first pass.

**Order, set 2026-10-03 (operator and director):** the operator's order on the milestone page (#78, #74, #80, #79, #81, #76, #86), with #84 added in front. **Why this order:** #84 first, because #78, #80 and #81 all change the history store, and its race makes about one suite run in four fail for no reason. #80 comes before #79, so that the conversion of the times does not also update indexes. #81 comes after #80, because it deletes by time and the times only compare correctly in UTC; it uses the time index of #79 and prunes the table of #78. #76 uses the refused-post count of #74. #86 is last: it shows its figures in the health block of #76, takes the hook round trip from #74 and writes its worst case to the `runs` row of #78. **#74 writes "last hook received" into a `health` object in `/state`, and #76 adds to that object**, so that no field moves (director).

**Rulings, operator, 2026-10-03:** #84 joins milestone 4, first. #78 is a table of its own, `runs`, not two new decision kinds. The Activity window is left for a later milestone: milestone 4 shows its figures in `/state` and the log. #81 stops the growth of the file and does not shrink it: no `VACUUM` and no `auto_vacuum`, because the space of deleted rows is used again for new rows.

**T1.59 — The history store can be closed while it writes**
- **Goal:** closing the history store while its writer thread is inside a write never throws, never leaves a half-written record, and never opens the file again afterwards. For issue #84.
- **Depends:** T1.54 (the store's retry, its `Available` flag and its present shape), T1.37 (one transaction for an event and its decisions)
- **Realizes:** degrade, never crash (Part 1). #84's first fix: **make the store safe on every path**, not only remove the one test's exposure. The product stops the archive writer before the container disposes the store, but a host disposed without a stop (a test, a failed start, a later caller) reaches the race, and the store is the place that can close it.
- **Deliverables:**
  - **The race, closed in `SqliteEventStore`.** Today `Dispose` (on the disposing thread) can dispose the connection while `Append` or `AppendDecisions` (on the writer's thread) is between `BeginTransaction` and `Commit`. The write then throws a `NullReferenceException` that no `catch` takes. The opposite order is possible too: `Append` passes its `_disposed` check, `Dispose` runs, and `Connect` opens a new connection that nothing ever closes.
  - **The fix to consider first:** one private lock object, held by `Append`, `AppendDecisions`, the count queries and `Dispose`. `Dispose` then waits for the current write, which takes milliseconds, and every write that starts after it sees `_disposed` and returns `false` without opening the file. This lock is in an App adapter, on the disk path. The Registry's "one writer, no locks" rule is not touched: the Registry is not here, and the consumer never calls the store. If a lighter guard closes both orders, use it and say why. A lock that the consumer thread can wait on is not acceptable.
  - **What a write after the close does:** it returns `false`, writes no log line and does not change `Available`. The store is going away, so the board has nothing to show.
  - **The test keeps its shape.** `TokenHandoverTests` keeps `await using` without `StopAsync` on the second host. With the store fixed, that path must be safe, so the test is now a check of it.
  - **Documents, in the same change:** Impl Part 4, at "The archive and the decision record": one sentence that the store may be closed during a write, that the close waits for the current write, and that a write after the close is dropped without a sound. One row in Impl Appendix C.
- **Acceptance:**
  - **A deterministic test of each order.** It must not depend on timing and must not loop until it gets lucky. Use a seam, for example a hook inside the transaction that the test can block on, to hold a write between `BeginTransaction` and `Commit` and then call `Dispose` from a second thread. (a) `Dispose` waits until the write commits, and the row is in the file. (b) `Dispose` first, then `Append`: it returns `false` and the file is not opened again; check with `File.Delete` succeeding at once, as T1.17 measured.
  - **No log line** and no change to `Available` for a write after the close.
  - **The flake is gone:** report 50 runs of `TokenHandoverTests` (Release), and both full suite counts.
  - **Plant:** remove the guard, and test (a) fails with the error that #84 recorded (or with a `false` where `true` was due). Report the failing output.
  - Build clean, 0 warnings.
- **Guardrails:** the consumer thread never waits on the store; only the archive writer's thread and the disposing thread take the guard. No payload in a log line or an exception message. No change to the retry of T1.54, the schema, the archive channel or the shutdown order. The store does not start to throw on any path where it returned `false` before.
- **Done 2026-10-03:** PR #88, merged as `85138a8`, `8e0d0a9` (no fix cycle). One private lock in `SqliteEventStore`; a read after the close throws `ObjectDisposedException` (director's ruling: the standard contract for a closed object, and no caller reads after the close). Seven plants, each caught. The `NullReferenceException` itself was not reproduced: the lock makes the commit and the close exclusive, which removes its window.

**T1.60 — The history database records each start and stop of the dashboard**
- **Goal:** `dashboard.db` says when the dashboard started and stopped, and which version it was. A crash or a kill shows as a start with no stop. `--replay` uses those rows to forget sessions at each start, as the live dashboard does. For issue #78.
- **Depends:** T1.59 (the store is safe to close), T1.37 (the decisions record and `--replay`), T1.17 (the store)
- **Realizes:** the operator's ruling of 2026-10-03 on #78: **a table of its own, `runs`**, not two new decision kinds. A run is not a decision: it has two times, and the stop may never come.
- **Deliverables:**
  - **The table**, created with the others in the store's schema, so an existing file gains it at the next start: `runs (id INTEGER PRIMARY KEY, started_at TEXT NOT NULL, stopped_at TEXT, version TEXT NOT NULL, port INTEGER, data_root TEXT NOT NULL)`, as #78 gives it.
  - **Times in UTC** from the first row: `ToUniversalTime().ToString("o")`, which ends in `Z`. The other two tables change to UTC in #80; this table never holds the old form.
  - **`version`** is the informational version, as `StartupVersion` gives it for the log's first line. **`port`** is the port that ingress bound; NULL when it could not bind. **`data_root`** is the data folder.
  - **The start row** is written once per process, after ingress has bound or failed, on the archive writer's thread (a record through the archive, or a call that the writer makes). It is never written on the UI thread or the consumer thread. A second instance that stands down writes no row. If the database cannot be written at that moment, the row is lost like any other record, counted, and not retried.
  - **The stop row:** a clean stop sets `stopped_at` on this process's row, after the archive's drain, before the store closes. A kill, a crash or a host disposed without a stop leaves it NULL.
  - **`--replay`** reads `runs`. When the next event to apply is at or after a run's `started_at`, replay first starts a new Registry and sound engine state, empty, as a live start does, and then continues. Compare times as parsed `DateTimeOffset` values, never as text: the `events` times are local text until #80. History before the first `runs` row replays as one run, as today, and the summary line says how many runs replay saw and whether older history had none. Replay never writes `runs`.
  - **No operator text** in this table: no title, no prompt, no path but the data folder.
  - **Documents, in the same change:** Impl §8.3 (the table; the note on `--replay`, where the 4,076 nudge rows came from one uninterrupted run), Part 8 (the `dashboard.db` row, if it lists the tables); event flow §9; the remarks on `ReplaySwitch`. TS where the database's contents are listed (find it). One row each in TS Appendix D and Impl Appendix C.
- **Acceptance:**
  - A host started and stopped twice against a scratch data folder leaves two `runs` rows, each with `started_at`, `stopped_at`, the version, the port and the data folder; both times end in `Z`.
  - A host disposed without `StopAsync` leaves its row with `stopped_at` NULL.
  - An existing database without the table gains it at the next start; its `events` and `decisions` rows are unchanged.
  - The start row is not written on the consumer thread (a test or a seam shows the thread, or the code path makes it plain to the reviewer).
  - Replay: events from two runs, with a session that went quiet in the first run. Its question nudge stops at the second run's start, and the same history without the `runs` rows still nudges (the old behaviour, which is what a pre-T1.60 database gets).
  - A time comparison across an offset change: a run started at `…T01:30:00Z`, with events in `+02:00` and `+01:00` text on either side, splits at the right event.
  - Plant: replay ignores `runs`, and the replay test fails. Plant: the stop row is written before the drain, and a test that queues a record at shutdown finds the order wrong (or explain why that cannot be observed and drop this plant).
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** tests use scratch data folders, never the operator's `dashboard.db`. Replay still never modifies `events`, and still refuses a database whose `decisions` table is not empty. No change to the `events` or `decisions` schema, the archive channel or the retry of T1.54. Never log a payload.
- **Correction (2026-10-03, at the coder's report):** the form `ToUniversalTime().ToString("o")` above gives `+00:00` on a `DateTimeOffset`, not `Z`. The code uses `UtcDateTime.ToString("o")`, which ends in `Z`, and that is the form for #80 too.
- **Addition (2026-10-03, director, before review):** replay also forgets every session at a run's `stopped_at` when it is not NULL, because a dashboard that is off sends no nudges. A run with no stop keeps its sessions until the next start. Without it, a 7.5-hour gap made 45 false nudges in the test.
- **Done 2026-10-03:** PR #89, merged as `7d017f1`, `888baa5`, `eb69fb5`, `ff2fa17` (no fix cycle; the addition was made before review). Accepted at the report: `HookToDatabaseTests` asserts that `events` is empty, since every start now writes its run row; `GrowthMeasurement` subtracts an empty file (16,384 bytes), because the constant is growth per day. A start whose Kestrel bind throws ends before the host runs and writes no row (Impl §8.3). The reviewer's live check: two killed starts, two rows with `stopped_at` NULL.

**T1.61 — The dashboard tests the path from Claude Code, and says when it last heard from it**
- **Goal:** at each start, and from a button in the Settings window, the dashboard runs its own hook script and checks that the message arrives. It shows a notice when the messages cannot arrive or are being refused, and the tooltip always says when the dashboard last heard from Claude Code. For issue #74.
- **Depends:** T1.54 (the notice board), T1.51 (the plugin notices), T1.46 (the token in `listening.txt`), T1.60 (merged; no shared code expected)
- **Realizes:** #74's three parts, and the operator's rulings of 2026-10-03: **a refused-message notice after 3 refusals in 10 minutes**, cleared 10 minutes after the last one; **"last heard" always, as the tooltip's last item**; and **plain words on screen**: the window and the tray say "messages from Claude Code", never "hook". The word "idle" is not used for this, because Idle is a session's state on the board. Director: #74 writes into a `health` object in `/state`, and #76 adds to it.
- **Deliverables:**
  - **The self-test.** The dashboard runs `DashboardPaths.HookScriptFile` as Claude Code does (`cmd.exe /c post-status.cmd`, the JSON on standard input), on a background thread, after `listening.txt` is written. The start never waits for it. The JSON carries a test event name of its own and a one-time value. Ingress recognises it before the mapper, notes the arrival and answers `200` with an empty body. It makes no session, no row, no sound, no `Ack`, no `SoundCommand` and no `RostersChanged`. `HookEventNames.Accepted` does not change. `post-status.cmd` is not edited.
  - **The result.** If it arrives within 3 seconds: one Information line with the round-trip time in milliseconds. If not: a notice. Window: "Messages from Claude Code cannot reach the dashboard: a test message did not arrive." followed by the cause where it can be known (the script is missing, `curl.exe` is not in `System32`, the script ran and nothing arrived). Tray: `messages cannot arrive`. It clears when a later self-test passes or when a real message from Claude Code is accepted. No colour change and no sound.
  - **The button.** **Test connection** in the Settings window runs the same test and shows the result beside the button, in the same words. The button cannot start a second test while one runs.
  - **Refused messages.** Count the `401` answers on `/hook`, readable by #76. While 3 or more were refused in the last 10 minutes, a notice. Window: "Messages from Claude Code are being refused: their token does not match this dashboard's." Tray: `messages refused`. It clears on the tick, 10 minutes after the last refusal. One refusal, as at a restart (event flow §11), never shows it. The per-post Warning stays as it is.
  - **The order on the board:** the self-test notice and then the refused notice come directly after `HookNotice`, so they come before the history, sound, settings and queue notices.
  - **Last heard.** One instant: when the last real message was accepted (not the self-test and not a refused post). It is written on the request thread and published safely (for example `Interlocked` on the ticks), and read on the 15-second tick. The tooltip's last item, always: "last heard from Claude Code just now" (under a minute), "… 2 min ago", "… 3 h ago", "… 2 d ago"; before the first: "not heard from Claude Code since start". **It is information, never an alarm** (Design §3): no colour, no sound, no notice.
  - **`/state`:** a `health` object with `lastHeardAt` (UTC, ISO 8601, or null) and `selfTest` (`passed`, `roundTripMs` or null, `at`). #76 adds the rest of the health block to this object later.
  - **The tooltip length.** Windows cuts a tray tooltip at about 127 characters. If the whole text is too long, the "last heard" item is left out whole first, never cut in the middle of a word. Find what `TrayTooltip` does today and say so.
  - **Documents, in the same change:** Impl §3.2 (the test event at ingress), §3.5 (the `health` object), §5.2 (the tooltip and its two notices), §9.4 (the self-test notice); event flow §11 and §12; Design, wherever the tooltip is described (find it). One row each in TS Appendix D, Impl Appendix C and Design §13.
- **Acceptance:**
  - A self-test against a test host with a scratch copy of the script and a scratch `listening.txt` arrives, logs the round trip, and shows no notice.
  - With the script replaced by one that does nothing, the notice shows with its cause. The button gives the same result. A real accepted message then clears the notice.
  - The test event leaves no row, no session in `/state`, no sound and no decision row. `/hook` answers `200` empty for it.
  - Refused: 2 refusals show nothing, and 3 within 10 minutes show the notice. Under a fake clock it clears 10 minutes after the last refusal.
  - Last heard: the tooltip says "not heard from Claude Code since start", then "just now" after a message, then "12 min ago" under a fake clock. A refused post and the self-test do not move it. No colour and no sound change at any gap.
  - `/state` answers `health.lastHeardAt` and `health.selfTest`.
  - The tooltip with every notice active stays within the Windows limit, and drops "last heard" whole.
  - A realized-window test with the two notices in order, and the Settings button, with `BindingErrorWatch` clean.
  - Plants: (a) the test event goes through the mapper, and the no-session test fails; (b) refused counted without the 10-minute window, and the clear test fails; (c) the self-test moves "last heard", and its test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** no edit to `post-status.cmd`. `/hook` answers `200` empty with no decision field, for the test event too. No payload, title, prompt or token in a log line, a notice or `/state`. Tests run a scratch copy of the script against a test host and a scratch data folder; they never run the operator's installed script, read the operator's `listening.txt`, or post to a real dashboard. No poller: "last heard" is read on the tick that already runs.
- **Addition (2026-10-03, at the coder's question):** the operator's comment on #74 (2026-10-03 21:18 UTC), which this block missed, is part of the task. Each refused post writes one decision row, kind `HookRefused` (the next free number), through `DecisionRecorder.External` from the Kestrel thread, as `EventDropped` does: `event_id` NULL, `session_id` NULL, reason and detail empty. A refused post is not trusted, so nothing from its body or headers reaches the row. The tooltip threshold is a named constant with its reason beside it. Impl §8.3 lists the kind.
- **Ruling (2026-10-04, operator, review cycle 1):** at most **one `HookRefused` row a second**, with the count of refusals since the last row in `detail`; the tick writes what a stopped flood leaves. The review measured one row for each refused post at about 100 MB a minute during a flood, with no real record lost. The memory that held every refusal of the last 10 minutes keeps at most three, and a test holds the one-time value of the self-test.
- **Done 2026-10-04:** PR #90, merged as `07bdbf0`, `6f54bde`, `fe36a70`, `39ce083` (one fix cycle). The review found that a flood of refused posts made memory grow without limit (the window kept every refusal), and that no test held the self-test's one-time value. Both were fixed with the operator's row ruling. Measured by the reviewer with a 5-second flood of about 33,000 wrong-token posts a second: 3 refusal times held (154,746 before), 5 rows (9 MB before), and 46 of 46 real prompts recorded. The self-test arrived in 147 ms in a live start. Carried to T1.62: two nits.

**T1.62 — Times in the history database are stored in UTC**
- **Goal:** every time in `dashboard.db` is UTC text, so a time range that crosses a clock change compares correctly as text. Existing rows are converted once, and the file records a schema version. For issue #80.
- **Depends:** T1.60 (the `runs` table, already in UTC), T1.59 (the store's lock), T1.54 (the store's retry)
- **Realizes:** the ruling in #80: **keep the times in UTC.** Display stays local. It is the first upgrade step the database has had; until now the schema step only creates what is absent.
- **Deliverables:**
  - **One form, everywhere:** `UtcDateTime.ToString("o", CultureInfo.InvariantCulture)`, which gives seven fractional digits and `Z`, for example `2026-10-02T12:03:11.1230000Z`. Not `ToUniversalTime().ToString("o")` on a `DateTimeOffset`, which gives `+00:00` (T1.60's correction). Text order equals time order only if every row has the same form, so new rows, converted rows and `runs` rows must all match. One helper writes it, and every writer of `ts`, `started_at` and `stopped_at` uses it.
  - **The conversion, once.** In the schema step, on the archive writer's thread, when `PRAGMA user_version` is 0: read each `ts` in `events` and `decisions`, parse it, write it back in the one form, and set `user_version` to 1, all in one transaction. The start never waits for it, because the store opens on the writer's thread. Records that arrive meanwhile wait in the archive channel.
  - **A time that will not parse** is left as it is and counted. The conversion does not fail because of one bad row.
  - **If the conversion fails** (disk full, file locked), the transaction rolls back, `user_version` stays 0, and the store follows T1.54's rule: the history notice, and another attempt a minute later.
  - **Say what was done:** one Information line, with the number of rows converted, the number left as they were, and the time it took. No `ts` value and no payload in it.
  - **Readers:** replay already parses each `ts` as a `DateTimeOffset`; confirm that it reads both forms, and find every other reader of `ts`. Display and log lines stay in local time.
  - **The documented queries:** the `$from` and `$to` of the query in Impl Part 4 are now UTC text. Say so, and show how to read a time as local in SQLite (`datetime(ts, 'localtime')`). The same applies to the query in event flow §12.
  - **Carried from T1.61's review:**
    - (a) Impl §3.2 and the `RefusedRowEvery` remarks say that no refusal goes unrecorded. The refusals since the last row are lost at a stop, so the text must say "while the dashboard runs", or a flush at the stop must make it true. Choose one and say which.
    - (b) The real-board order test also shows the history notice, so it holds the order of the two notices against history as well as against settings.
  - **Documents, in the same change:** Impl §8.3 and Part 4 (the note on `ts`, and the query); event flow §9 and §12; TS where the database's times are described (find it). One row each in Impl Appendix C and, if TS changes, TS Appendix D.
- **Acceptance:**
  - Two rows written one second apart across a simulated offset change (`+02:00` then `+01:00`) come back in the order they were written, by `ORDER BY ts`.
  - A database with old-form rows in both tables, opened by the store, has `user_version` 1 afterwards. Every `ts` is in the one form and names the same instant as before. The row count and the row ids are unchanged.
  - A second open does not convert again (the Information line is not repeated).
  - A row with a time that will not parse is left as it is, and counted.
  - A conversion that fails rolls back: the old rows are unchanged, `user_version` is 0, and the history notice shows.
  - New `events`, `decisions` and `runs` rows all have the one form (a test over all three).
  - Replay over a converted file gives the same decisions as over the same history in the old form.
  - **On a copy of the operator's database** (copy `dashboard.db` and its `-wal` to a scratch folder; never open the original): run the conversion. Report the rows converted, the rows left as they were, the time it took and the size before and after. They are counts, not operator text.
  - Plants: (a) the conversion skips `decisions`, and the both-tables test fails; (b) the new-row writer uses `ToUniversalTime().ToString("o")`, and the one-form test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** tests use scratch folders, and only a copy of the operator's database. The conversion never changes a payload, a row id or a row count. No `VACUUM` (the operator's #81 ruling). No `ts` value or payload in a log line. Replay opens its file through the store, so replay over a file that was never converted converts it first: the instants and the rows are unchanged, and only the form of the time changes. Say so where the documents state that replay never modifies `events` (T1.37).
- **Done 2026-10-04:** PR #91, merged as `0f93e45`, `0b16666` (no fix cycle). On a copy of the operator's database, the conversion took under a second for about 32,000 rows, and the file grew by 3.5 MB (no `VACUUM`). Carried item (a) was reworded ("while the dashboard runs"), not flushed: at a stop the consumer has already drained, so a decision written then has no scope to leave in. T1.60's offset test can no longer tell a text comparison from an instant comparison, because every time is in one UTC form; the parse is held by the conversion tests. Note: Windows' own `winsqlite3.dll` (3.51.1) answers NULL to `datetime(…, 'localtime')`; the SQLite that ships with the dashboard (3.53.3) does not. Carried to T1.63: `ReplaySwitch.cs:140` says "Nothing was written." after it converted an old file; "No decisions were written." is exact.

**T1.63 — The history database has indexes**
- **Goal:** a query by session, by time or by kind reads only the rows it needs, so the documented queries, and the readers to come (the health block, a later Activity window), stay fast as the file grows. For issue #79.
- **Depends:** T1.62 (the times are in one UTC form, so an index on `ts` orders them correctly; the conversion runs before the indexes exist on an old file)
- **Realizes:** #79 as written. The indexes go in the same schema step that creates the tables, so an existing file gains them at its next start.
- **Deliverables:**
  - **The six indexes**, each `CREATE INDEX IF NOT EXISTS`, in the store's schema step, on the archive writer's thread:
    - `ix_events_session_id ON events (session_id, id)`
    - `ix_events_ts ON events (ts)`
    - `ix_decisions_session_id ON decisions (session_id, id)`
    - `ix_decisions_event_id ON decisions (event_id)`
    - `ix_decisions_ts ON decisions (ts)`
    - `ix_decisions_kind_ts ON decisions (kind, ts)`, which serves "every sound played between 14:00 and 14:10".
  - **Order on an old file:** the T1.62 conversion first, then the indexes, so the conversion does not also update six indexes. Say how the code makes that order certain.
  - **The growth figure.** An index adds bytes to every insert. `GrowthMeasurement` re-measures a typical day; read its new figure. If it is over `TypicalBytesPerDay` (300,000), raise the constant to the measured figure with a margin, give the reason beside it, and change the "300 KB a day" and "100 MB a year" figures in the store's remarks and in the documents. #81 later replaces "a year" with "at most the retention window"; this task only makes the figures true.
  - **Documents, in the same change:** Impl §8.3 (the indexes, and the growth figure wherever it appears); the growth figure in TS §IV.6 and elsewhere if it is given (find each). One row in Impl Appendix C, and in TS Appendix D if TS changes.
  - **Carried from T1.62:** `ReplaySwitch.cs:140` says "No decisions were written." in place of "Nothing was written.", because replay converts an old file before it refuses it.
- **Acceptance:**
  - A new file has the six indexes (`sqlite_master`). A file made before this change gains them at the next open, with its rows unchanged.
  - `EXPLAIN QUERY PLAN` for the query in Impl Part 4 and the query in event flow §12, on a test file with enough rows to matter, uses the indexes and shows no `SCAN` of `events` or `decisions`. A test holds this, so that a later change to a query or an index that brings back a scan fails.
  - **On a copy of the operator's database** (copy `dashboard.db` and any `-wal` to a scratch folder; never open the original): the time of each of the two queries before and after, the time it took to create the indexes, and the size before and after. Counts and times only.
  - The new growth figure, and the constant if it changed.
  - Plant: remove `ix_decisions_kind_ts`, and the query-plan test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** no change to the columns of any table, to the T1.62 conversion or to `user_version`. No `VACUUM` (the operator's #81 ruling). Tests use scratch folders, and only a copy of the operator's database.
- **Done 2026-10-04:** PR #92, merged as `2c13dc3`, `e4824f1`, `dde5003` (one fix cycle, tests only). The review found that the plan test read the plans through Windows' `winsqlite3` instead of the product's SQLite, and held its own copies of the queries instead of the documented ones; it now plans the documented queries on a `Microsoft.Data.Sqlite` connection. Without `ix_decisions_kind_ts` the planner still finds a SEARCH (on `ix_decisions_ts`), so the test names the expected indexes as well as forbidding a SCAN. On a copy of the operator's database, the §12 query went from 12.8 ms to 0.07 ms and the Part 4 query from 1.55 ms to 0.37 ms; the indexes took 121 ms to build. Carried to T1.64: the growth figure (a synthetic day is 992 bytes under `TypicalBytesPerDay`, and it writes no decisions, while the real file is about 98 MB for 25,272 events), and issue #93, a test that reads the window too soon.

**T1.64 — The history database keeps 30 days, as a setting**
- **Goal:** `dashboard.db` holds at most the retention window, 30 days by default, and no longer grows for ever. The operator sets the window, and `0` keeps everything. For issue #81.
- **Depends:** T1.63 (the index on `ts`, which makes the delete cheap), T1.62 (times in one UTC form, so "older than" compares correctly), T1.60 (the `runs` table, which is pruned too)
- **Realizes:** #81's ruling, **default 30 days**, with room for at least a hundred years. And the operator's rulings of 2026-10-04: **no `VACUUM` and no `auto_vacuum`** (the file stops growing and does not shrink); **prune at start and once a day** while the dashboard runs, because it often runs for weeks. The operator will set `0` on their own machine before installing, so **settings must keep a key that a version does not know** (see below).
- **Deliverables:**
  - **The setting:** `history.retentionDays` in `settings.json`, a plain integer with no small ceiling (36,525 days fits). Default `30`. `0` means keep everything. A negative value means the default and is logged like the other repaired values (Impl §8.2). An absent value means the default.
  - **When:** once at each start, after the database opens, and then once every 24 hours while the dashboard runs. Always on the archive writer's thread, never on the consumer, UI or request threads. The start never waits for it. A schedule on the writer's own loop is acceptable, and so is the tick handing the writer a "prune now" request; no new thread and no new timer on the consumer.
  - **What is deleted, in one transaction:** `events` rows with `ts` older than the limit; the `decisions` rows that point at those events; `decisions` rows with no event (`event_id` NULL) that are older than the limit; and `runs` rows whose `started_at` is older than the limit and that are not this process's own run. The limit is `now − retentionDays`, written in the one UTC form of T1.62 and compared as text, which T1.62 made correct.
  - **Say what was done:** at each start, one Information line with the retention in effect ("keeps 30 days" or "keeps everything"), written **before** any prune. After a prune that deleted anything, one Information line with the counts for each table, the limit and the time taken. No payload and no row time.
  - **A failure** (disk full, file locked) rolls the transaction back and follows T1.54's rule (the history notice and the retry a minute later). It never deletes part of an event's record.
  - **Settings keep keys they do not know.** `SettingsStore.Save` writes `DashboardSettings` whole, so today a save drops any key that this version does not know, and a save happens at every quit (the window position), at a roster edit and at a settings change. Keep unknown keys (for example `[JsonExtensionData]` on the settings record, or a merge into the file's JSON) so that a save writes them back unchanged. A key that the operator adds for a newer version then survives. Test it.
  - **The growth figure, re-based** (carried from T1.63's report). `GrowthMeasurement` writes `events` rows only, with no decisions, and its typical day is 299,008 bytes, 992 under `TypicalBytesPerDay`. The operator's real file is about 98 MB for 25,272 events and 7,060 decisions, so real growth is far above that typical day. Make the measurement write a day's decisions too, at the ratio the copy shows. Set the constant to the new figure with a margin of at least 10 % and its reason. Then state the growth in the documents as "at most the retention window: about N MB for 30 days", with N from the copy's real rate (its bytes divided by the days its `ts` values span). Report both figures, the synthetic and the real.
  - **Carried: issue #93.** `HistoryNoticeAcceptanceTests.A_database_that_cannot_be_written_shows_the_notice_and_the_dashboard_goes_on` fails every time it runs alone: it reads `SessionProjection` after one dispatcher pump, before the consumer's post has run. Make it wait for the projection itself, as the other realized-window tests do. Report 10 runs of that test alone. The failed-prune test below touches the same notice.
  - **Documents, in the same change:** Impl §8.2 (the key and its repair), §8.3 (the prune, its schedule, its line), Appendix B (the proposal, which this settles); TS §IV.6 and Appendix C (the "retention not built" rows go); the growth figure wherever it appears. One row each in TS Appendix D and Impl Appendix C. Also the operator's guide to upgrading, if one exists, or the release notes: **the first start after this update deletes history older than the window, so set `history.retentionDays` to `0` first, with the dashboard quit, if you want to keep it.**
- **Acceptance:**
  - Events at 31 days and at 29 days of age, each with decisions, and `decisions` rows with no event at both ages: after the prune with the default, the 31-day rows are gone and the 29-day rows are kept, with their decisions.
  - With `0`, nothing is deleted. With a negative value, the default applies and one repaired-value line is logged.
  - `runs` older than the limit go, and the present run is kept even if it started long ago.
  - The second prune runs 24 hours after the first under a fake clock, and not before.
  - The prune runs on the writer's thread (a test or the code path shows it).
  - The retention line comes before any prune line, and neither holds a payload or a row time.
  - A failed prune (through a seam) deletes nothing and shows the history notice.
  - A `settings.json` with an unknown key keeps it, unchanged, after a save.
  - **On a copy of the operator's database** (copy `dashboard.db` and any `-wal` to a scratch folder; never open the original): a prune with 30 days. Report the rows deleted in each table, the time taken, the file size before and after (no `VACUUM`, so the size should not drop), the days the copy's history spans, and its real bytes per day.
  - Plants: (a) decisions with no event are not pruned, and the first test fails; (b) the 24-hour schedule removed, and the daily test fails; (c) `[JsonExtensionData]` (or the merge) removed, and the unknown-key test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** tests use scratch folders and only a copy of the operator's database. Never touch the operator's real `settings.json` or `dashboard.db`. No `VACUUM`. No delete outside the one transaction. No payload or row time in a log line.
- **Done 2026-10-04:** PR #94, merged as `26ca381`, `43ae56d` (no fix cycle). Through `SqliteEventStore.Prune` on a copy of the operator's database, a 30-day prune deleted 5,370 events and 2 decisions with no event in 76 ms, and the file size did not change. Its history spans 37.6 days at about 2.7 MB a day, so 30 days hold about 81 MB; the synthetic day is about 9 times smaller, and both figures are in the documents. T1.64 also found that the port's repaired-value sentence was built but never logged, and logs it. The full suite took about 3 minutes in the review against about 52 seconds before, and the code from before milestone 4 is just as slow under the same conditions. The time is in two `MainWindowTests` width sweeps while the console was locked, so no task caused it. Carried to T1.65: the port value in that sentence, and a test for a prune inside the retry minute.

**T1.65 — The dashboard shows its counts while it runs, and writes a summary each hour**
- **Goal:** what the dashboard counts (events applied, declined and dropped, posts refused, records not written, what the tick did) can be read at any moment in `/state`. A summary is written to the log and to the decisions table each hour, so the counts are seen even though the dashboard almost never quits cleanly. For issue #76.
- **Depends:** T1.61 (the `health` object in `/state`, and `HookHealth.RefusedCount`), T1.60 (the `runs` table: the version and the start), T1.58 (the event channel's shed and dropped counts), T1.54 (the store's state)
- **Realizes:** #76 as written, and the director's rulings of 2026-10-04, made while the operator was away (the operator delegated the remaining decisions of milestone 4):
  - **The Activity window is not built** (the operator's ruling of 2026-10-03). The counts are shown in `/state` and in the log.
  - **The hourly summary is written at the first tick after each full clock hour, in UTC**, and covers the time since the previous summary. The first one after a start is therefore partial and says so. Why: a reader looks for "the 14:00 row", and a gap between rows shows the hours when the dashboard was not running.
  - **`--replay` writes no hourly summary.** A summary describes a running process, and replay is not one.
  - **No summary row at a stop.** At a stop the consumer has already drained, so a decision written then has no scope to leave in (T1.62). The stop keeps its Information line, which now gives the same counts in the same form.
- **Deliverables:**
  - **The counts.** Since the start and for the present hour: applied, declined, uncorrelated, dropped (the event channel's shed and hard-limit drops, and the archive's drops, each named), refused (`HookHealth.RefusedCount`), records not written (the store's failed and lost counts), and the tick's work (ticks, sweeps, settles). Take each count from the place that already keeps it, and find any that is kept nowhere.
  - **A health snapshot**, built on the consumer thread at each tick and published the way `StateBoard` publishes its report (one immutable object, swapped in safely). A request thread reads the snapshot and never a live field, the Registry or the sound engine. It holds the version, the start time (UTC), the ingress state (the port, or that it cannot receive), the database state (writing, not writing, or not yet known), the sound output state, the mute and pause modes, the counts since the start and for the last full hour, and `countedAt` (UTC), so a reader knows the counts can be up to one tick old.
  - **`/state`'s `health` object** gains these fields beside `lastHeardAt` and `selfTest`, which stay as they are. Design the snapshot so that T1.66 can add its timings to it without moving a field.
  - **The hourly summary:**
    - one Information line with the counts for the hour, as identifiers and numbers;
    - one `decisions` row of a new kind, `HourlySummary` (the next free number after 42), with `event_id` and `session_id` NULL and the same counts in `detail` (`applied=212 declined=1840 dropped=0 …`), written on the consumer thread inside the tick, like every other decision.
    - No new timer and no new thread: the 15-second tick already runs.
  - **The stop line** in `EventConsumer` gives the same counts in the same form as the hourly line, since the start.
  - **Must-be-zero counts that move:** dropped and uncorrelated already warn, refused has its notice (T1.61), and a store failure has its notice and its recovery line (T1.54). Confirm each in the code, and add nothing that is already there.
  - **Carried from T1.64's review:**
    - (a) `SettingsStore.PortProblem`'s sentence, now logged by T1.64's repaired-value Warning, contains the raw `port` value (`The "port" setting is {port} , which…`). Director's ruling: drop the value, which also removes the stray " , ", because T1.56 set that no setting value is logged. Impl §8.2 says the log names the setting and the repair, not the value.
    - (b) No test holds that a prune inside the store's retry minute is skipped without counting a lost record (the reviewer's plant f passed). Add one.
  - **Documents, in the same change:** Impl §3.5 (the `health` fields), §8.3 (`HourlySummary`), §8.4 (the hourly line and the stop line); event flow, where the tick and the log are described; TS where the dashboard's self-reporting is described (find it). One row each in Impl Appendix C and, if TS changes, TS Appendix D.
- **Acceptance:**
  - Under a fake clock, a run that crosses two clock hours writes two hourly lines and two `HourlySummary` rows at the first tick after each hour. Their counts agree with each other and with `/state` at that moment. The first one is marked partial.
  - The hour's counts reset at each summary, and the counts since the start do not.
  - `/state` answers every new `health` field with the right types. A request never touches the Registry or the engine: a test, or the code path, shows that the endpoint reads only the published snapshot.
  - Replay over a history that spans hours writes no `HourlySummary` row.
  - No title, prompt, payload or path other than the data folder in the line, the row or `health`.
  - Plants: (a) the hour's counts are not reset, and the reset test fails; (b) the summary written at every tick, and the two-hours test fails; (c) `/state` reads a live consumer field, and the snapshot test fails (or explain why that plant cannot be observed and choose another).
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** one writer to the Registry; the request thread never waits on the consumer. Identifiers and numbers only. No change to the notices, the event channel or the store's retry. Tests use scratch folders.
- **Done 2026-10-04:** PR #95, merged as `d0c4f6c`, `2e8919a`, `ee3e214`, `b739716` (one fix cycle). The review found that before the first tick the new `health` fields were written as null while Impl §3.5 said "absent". Director's ruling: keep null and say so, because `lastHeardAt` and `selfTest` are null until known, so one rule holds for every `health` field. The fix cycle also closed three test gaps: the source of "not written", a half-hour time zone, and an honest comment. The review's probes: a tick three hours late writes one partial summary, not three, and a clock set back an hour merges into the next summary instead of doubling it.

**T1.66 — The dashboard measures the waits that would show a stall**
- **Goal:** when the one thread that applies events is stuck or late, the dashboard knows and says so, once, instead of looking like a quiet day. Seven timings are kept in memory and shown in `/state` and the hourly line. For issue #86.
- **Depends:** T1.65 (the health snapshot and the hourly line), T1.61 (the self-test's round trip)
- **Realizes:** #86's ruling: measure the seven timings, **keep them in memory and not in the database**, show them in the health block, log one summary line an hour and one at the stop, and warn once when a limit is crossed and once when the figure is back under it. Director's rulings of 2026-10-04, with the operator away:
  - **The `runs` row does not get a worst case.** #86 allows it ("at most"), but it needs a new column and an upgrade step for one number. The stop line in the log carries the worst cases of the run.
  - **The hourly line gives the figures for the hour, and `/state` gives them since the start.** This is the same split as T1.65's counts. A worst case since the start goes stale after a week, and the last hour stays readable.
  - **`System.Diagnostics.Metrics`:** use it only if it costs nothing; a hand-written type is acceptable (#86).
- **Deliverables:**
  - **The seven timings**, as #86's table gives them, with its limits:
    - queue wait: arrival to apply; warn above 1 s;
    - tick lateness: due to run; warn above 5 s;
    - apply time: one `SessionRegistry.Apply`; warn above 50 ms;
    - archive backlog: the archive channel's count at each hand-off; warn above 512;
    - UI hop: post to the dispatcher until the posted work runs; warn above 500 ms;
    - hook round trip: the self-test's round trip of T1.61; warn above 1 s;
    - start-up phases: a stopwatch around each phase of `Program.Main`, logged once at the end of the start, with no limit.
  - **One small type holds a figure:** count, total, worst and the limit. It is written on the thread that measures, and published in T1.65's snapshot, so a request reads a copy and never a live field. A figure measured on the request thread or the UI thread is handed over safely (for example `Interlocked`), never under a lock that the consumer waits on.
  - **Warnings:** one Warning when a figure crosses its limit, and one Information line when it is back under it. Never a line for each event. These two, the hourly line and the start-up line are the only lines that the measurement writes.
  - **Show them:** a `timings` object in `/state`'s `health`, with each figure since the start (count, average, worst, limit), null before the first tick like every other `health` field (T1.65). The hourly line, or a second line beside it, has the hour's figures. The stop line has the run's worst cases.
  - **Arrival time:** the event carries its arrival instant. If `InboundEvent` already has one that means arrival, use it; if not, add one that the request thread stamps, and say which.
  - **Documents, in the same change:** Impl §3.5 (`health.timings`), Part 4 (the tick and the hand-offs, the limits), §8.4 (the lines); event flow §7. One row in Impl Appendix C.
- **Acceptance:**
  - With the consumer held under a fake clock, an event waits past 1 s: the queue wait is in the snapshot, one Warning is logged, and then one all-clear when the waits are short again.
  - A late tick is measured as lateness, and warns once.
  - A thousand ordinary events write no timing line.
  - `/state` answers `health.timings` with the seven figures, and the hourly line has the hour's figures.
  - The start-up line lists the phases with their times, once.
  - The cost: T1.58's throughput test still passes within its limit, and report its time before and after.
  - Plants: (a) the warning written at every crossing event (no "once" state), and the one-warning test fails; (b) the queue wait measured from the apply instead of the arrival, and the wait test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** nothing new in the database. No line for each event. No new lock that the consumer can wait on. No payload in a line. The figures exist to catch a stall, not to measure speed (#86): no tuning of anything because of them in this task.
- **Done 2026-10-04:** PR #96, merged as `af1cc55`, `2d87f3a`, `6ca5380`, `b558f7a` (one fix cycle). The review found that `RosterStore` published `RostersChanged` with no timestamp: one roster edit recorded a queue wait of about 2,000 years, raised a false "stuck" Warning, and hid any real stall while the figure stayed over its limit. **Fix (director's ruling):** the store stamps the edit with the clock, and the queue wait skips an event with no arrival instant or one later than now, counted as `skipped`. The review also found that a figure that moves around its limit wrote a Warning and an all-clear each time (500 of each in 1,000 values). **Ruling:** the Warning comes once, and the all-clear comes only after a full minute with no value over the limit, checked on the tick. The T1.58 throughput test takes about 3 ms more per 1,024 events, against its 5,000 ms limit.

**Milestone 1F, second pass, closed 2026-10-04:** every issue in GitHub milestone 4 has a merged change: T1.59 (#84), T1.60 (#78), T1.61 (#74), T1.62 (#80), T1.63 (#79), T1.64 (#81 and #93), T1.65 (#76) and T1.66 (#86). `main` builds with 0 warnings, and both suites pass, 2096 of 2096. From T1.64 on, the director ruled alone, with the operator's delegation of 2026-10-04 ("make the best decisions you can without me"); each ruling is in its block. **Not built, and left for a later milestone:** the Activity window (the operator's ruling of 2026-10-03). **Before an installed copy takes T1.64:** the first start deletes history older than `history.retentionDays` (default 30). An operator who wants to keep it quits the dashboard, sets the key to `0`, then installs (README, Install).

**Milestone 1F, third pass: GitHub milestone 5, "Observability 3".** Issues #99, #102, #98 and #97 (#102 added at the operator's word on 2026-10-04, after T1.67 began). Each task is written here before it is dispatched, as in the first two passes. The operator may add an issue during the milestone. It goes in after the task in progress, unless the operator says that it is urgent.

**Order, set 2026-10-04 (operator):** #99 first, then #98, then #97. **Why:** #99 and #97 are separate pieces of work. #99 puts a sign on a row in the main window, and #97 is a new window; they share only the sound decisions that they listen to. #98 comes before #97, because the Activity window reads the session's name from the column that #98 adds. #102 goes after T1.67, at the operator's word: the order is #99, #102, #98, #97.

**Rulings, operator, 2026-10-04:**
- #99: the sign goes on the row of the session that set off the sound, also in a group, and never on the group heading.
- #98 stores the full path: `decisions` gains `session_title` and `cwd`, and `events` gains `session_title` beside its `cwd`.
- #97 shows the decisions of this start only. Its three "small additions" were built in milestone 4 (T1.61, and T1.54 to T1.56). Only "last heard from Claude Code" at the top of the Activity window is new.
- A session in a git worktree that shows the worktree's folder and not the project's is not in this milestone: issue #100, with no milestone.

**T1.67 — A speaker sign on the row that made a sound**
- **Goal:** for one minute after a sound plays, a small, still speaker sign shows on the row of the session that set it off. An operator who hears a sound can see which row made it. For issue #99.
- **Depends:** T1.55 (the record says if a sound played: `SoundOutcome`), T1.37 (the sound decisions and `IDecisionSink`), T1.44 (a group's settle and its `QuietSince`)
- **Realizes:** #99 as written, and the operator's ruling of 2026-10-04 above. Director's rulings:
  - **A group's own sound** (`GroupNotice`, `GroupNudge`) reaches the sink with an empty session. Its sign goes on the member whose finish settled the group: the member whose state entry instant is the group's `QuietSince` (T1.44). The group's reminders mark the same member. If no member matches (for example, it left the group), no row gets the sign.
  - **Only a sound that the player queued** sets the sign (`SoundPlayed`). A suppressed sound (muted, paused, already announced) and a dropped sound (no output, failed) set nothing.
  - **The minute** counts from the sound. The sign goes at the first refresh of the window at or after the minute. No new timer: report how late that can be.
  - **In memory only.** After a restart, no row has a sign until the next sound.
- **Deliverables:**
  - **The path from the sound to the row.** The sound engine runs on the consumer thread. Find the place that knows a sound was queued and for which session (the `IDecisionSink.SoundPlayed` call, or beside it). Hand the session, the sound and the instant to the UI thread through the existing dispatcher post: one post for each sound that played, never one for each event. **Core decides which session a sound marks**, with the group rule above, so a second interface gets the same answer (Core and App §4.3). The Registry is not written, and no lock is added.
  - **The row.** `SessionViewModel` gains the sign's state (shown or not) and its hover text. The row template shows the sign after the badge and its detail, before the age. It does not move: no animation, no fade, no storyboard. Hover: `played: finished, 20 s ago`, with the sound's plain name and the age in the row's own words (find the names that the documents and the Settings window use; never an enum name that is not a word). The screen-reader name is `sound played`.
  - **A narrow row.** When the row has too little width, the sign goes first, before the age. Find how the row gives up width today, and fit the sign into that rule.
  - **A reminder that plays again** shows the sign again and starts its minute again.
  - **Documents, in the same change:** Design §9 (the session row, and one sentence at the motion rule that the sign does not move); Impl §5.6.3 (a row in the table for the sign and its source), and where §5.6 describes the refresh; the event flow, where the sound plays; Core and App, for the rule that lands in Core. One row each in Design §13 and Impl Appendix C.
- **Acceptance:**
  - A notice that the player queued puts the sign on its session's row. Under a fake clock, the sign is there at 59 s and gone at the first refresh after 60 s.
  - A muted, a paused, an already-announced and a dropped (`NoOutput`) sound put no sign on any row.
  - A roster group settles: the sign is on the row of the member whose finish settled it, and the heading has none. A group reminder marks the same member.
  - A reminder that plays again at 50 s keeps the sign, and its minute starts again.
  - A realized-window test: the sign is in the row, between the badge and the age, and `BindingErrorWatch` is clean. No storyboard or animation targets the sign, with animations on or off.
  - A narrow realized row: the sign is hidden while the age still shows.
  - The hover text and the screen-reader name are as above. No title, prompt or path is in them, and a sign writes no log line.
  - Plants: (a) the sign also set on `SoundSuppressed`, and the muted test fails; (b) a group's sign put on no member, and the group test fails; (c) the minute not enforced, and the 60 s test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** no change to which sounds play or when. No new timer, no new thread, and no lock that the consumer can wait on. The Registry is not written. Nothing new in the database. The motion rule holds: red blinks, working breathes, nothing else moves.
- **Done 2026-10-04:** PR #101, merged as `ad65ce7`, `2006ab9`, `70818ca`, `9b7175b` (one fix cycle). The review found that a roster formed from sessions that had already finished played the group's sound and marked no row: the engine kept its own copy of each session's group, and a roster edit did not change it. **Fix (director's ruling):** the consumer names the member when the group settles, from the groups as they stand then (`RosterSettle.SettledBy`), and the engine keeps it with the group. **Ruling:** a member that ended last gets the sign; where its row is folded away (the flat view's Ended line, or a group's quiet line), no sign shows. **Accepted, and carried to T1.68 for the documents:** if the member that settled a group leaves the roster while the group stays settled, the group's reminder still marks that member. The sign shows for at least 60 s and less than 75 s. Not verified: a real sound device, Narrator, a real display at a fractional scale.

**T1.68 — The history follows Claude Code's `cleanupPeriodDays`**
- **Goal:** the dashboard deletes its history older than Claude Code's `cleanupPeriodDays`, so that text Claude Code has deleted does not stay in `dashboard.db`. `history.retentionDays` is no longer used. For issue #102.
- **Depends:** T1.64 (the prune, its schedule, its one transaction and its lines; settings keep keys that they do not know), T1.62 (times in one UTC form)
- **Realizes:** #102 as written, the operator's ruling of 2026-10-04. Its rule:
  - The file is read, and `cleanupPeriodDays` is a whole number of 1 or more: keep that many days.
  - The file is read, and the key is not there: 30 days, Claude Code's default.
  - The file cannot be read or parsed, or the value is not valid (`0`, a negative number, a fraction, text): delete nothing.
  - A value too large to count back from now: delete nothing.
  - The number is read again at each prune (at start and every 24 hours), so a change takes effect at the next prune, with no restart.
  - `history.retentionDays` is ignored and stays in the file, and one log line says so. Log only: nothing on screen.
- **Director's rulings:**
  - **Which file:** the `~/.claude/settings.json` that the dashboard already reads (Impl §9.3), found the same way. No other Claude Code settings file (project, local or managed): #102 names this one.
  - **Valid means a JSON number that is a whole number of 1 or more.** A string (`"30"`), a fraction, `true` or `null` is not valid, so nothing is deleted.
  - **A read that fails is not a history failure.** It shows no notice and does not start the store's retry minute (T1.54). The prune deletes nothing and says why in the log.
  - **The lines:** at start, one line with the rule in use, written before any prune: "History follows Claude Code's cleanupPeriodDays: keeps 30 days.", with "(Claude Code's default)" when the key is absent; or "Claude Code's settings could not be read: history is kept in full."; or "Claude Code's cleanupPeriodDays is not valid: history is kept in full." Never the raw value of a key that is not valid. A daily prune writes the rule line again only when the rule differs from the last one written, so a quiet day writes no line.
  - **The operator's own value is `99999`** (about 274 years). It counts back to 1752, so it is valid and keeps everything in practice. Test it, and test `99999999`, which cannot be counted back.
- **Deliverables:**
  - **The rule in Core:** from what was read (no file, not parsed, the key absent, the value) and the instant, give the limit or "delete nothing", with the cause. It is pure and tested alone.
  - **The read in App**, on the archive writer's thread at each prune, through a seam that tests can fake. A file read and a JSON parse only, never a write.
  - **The prune** of T1.64 takes its limit from the rule. Its schedule, its transaction and its counts line do not change.
  - **The setting goes.** `DashboardSettings` loses `history.retentionDays` and its repair. The key, if present, is kept by the unknown-keys rule of T1.64 and written back unchanged. One Information line at start when it is present: it is no longer used, and history follows Claude Code's `cleanupPeriodDays`.
  - **Carried from T1.67's review:** one sentence in Impl §5.6.3, "Which row": if the member that settled a group leaves the roster while the group stays settled, the group's reminder still marks that member.
  - **Documents, in the same change:** Impl §8.2 (the key goes; it is ignored and kept), §8.3 (the prune's rule and its lines; the first-start warning rewritten), §9.3 (a second key read from Claude Code's settings, still never written), Appendix B where it names retention; TS §IV.6 and wherever retention is described; the README (the history paragraph and the first-start warning). One row each in TS Appendix D and Impl Appendix C. The release that carries this task needs notes that say: a dashboard set to keep everything (`history.retentionDays: 0`) no longer is; to keep a long history, set `cleanupPeriodDays` high in Claude Code's settings. The director writes the release notes.
- **Acceptance:**
  - Rows in `events`, `decisions` and `runs` at 31 and at 29 days of age, as in T1.64. With no key in a readable file, the 31-day rows go and the 29-day rows stay.
  - With `cleanupPeriodDays: 10`, rows at 11 days go and rows at 9 days stay.
  - No file, a locked file, a file that is not JSON, and the values `0`, `-1`, `1.5`, `"30"`, `true` and `99999999`: nothing is deleted, and the start line says why. No notice shows.
  - With `99999`, nothing in the test data is deleted, and the line says "keeps 99999 days".
  - Under a fake clock, the value changes from 30 to 10 between two prunes: the second prune uses 10, with no restart.
  - `history.retentionDays: 0` in the dashboard's settings: it is ignored (the 31-day rows go, with no key in Claude Code's file), one line says so, and the key is still in the file after a save.
  - Claude Code's settings file is never written: its bytes and its last-write time are unchanged after the prunes.
  - No payload, no row time and no raw value that is not valid in a line.
  - Plants: (a) a read that fails treated as 30 days, and the unreadable test fails; (b) the value read once at start and kept, and the change test fails; (c) `history.retentionDays` still obeyed, and the ignored test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** never write Claude Code's settings. No `VACUUM`. No change to the prune's schedule, its transaction or its thread. Tests use scratch folders and a fake path for Claude Code's settings, never the operator's `~/.claude` or data folder.
- **Done 2026-10-04:** PR #103, merged as `ddecc4d`, `61ca068`, `20bcc5b`, `d05775e` (one fix cycle). The coder found seven test classes that started real hosts with the default Claude Code path; with this change, each would have read the operator's real `~/.claude/settings.json` at its first prune. The review found three more. **Fix (director's ruling):** a scratch path for each, and a module initializer that points `CLAUDE_CONFIG_DIR` at a scratch folder for the whole test process, held by a test. A detector counted 3 reads of the real file in a full run before the fix and 0 after. The reads changed nothing: the file's hash and last-write time were the same. **Ruling:** the cleanup read is strict JSON. A comment or a trailing comma means "not read", so nothing is deleted, because Claude Code may pause its own cleanup on such a file. The hook check stays lenient. **The coder's rulings, accepted:** an empty file is "not read"; `30.0` is valid; the key is case-sensitive, so a key in another case counts as absent (30 days, as Claude Code itself does); a value too large has its own line. Not verified: Claude Code's own handling of comments, an empty file and a key in another case. **The operator's `cleanupPeriodDays` is `99999`**, so an installed copy keeps everything. Seen once and not reproduced: `SqliteEventStoreTests.A_second_failure_after_a_recovery_warns_again_without_the_stack` failed at its write after a folder delete, likely a handle from another process on a new `%TEMP%` folder.

**T1.69 — The database stores the session's name, and the decisions store its path**
- **Goal:** each row that has a session id also has the session's name, as it was when the row was written. Each decision row also has the session's full path. A reader of `dashboard.db` sees which session a row is about without looking for its history. For issue #98.
- **Depends:** T1.62 (the schema version, `PRAGMA user_version`, and the upgrade at start), T1.63 (the indexes), T1.64 (the growth figure), T1.37 (the decisions record), T1.24 (no operator text in a decision row)
- **Realizes:** #98 as written, and the operator's ruling of 2026-10-04: `events` gains `session_title`, beside its `cwd`; `decisions` gains `session_title` and `cwd`, the full path. Director's rulings:
  - **The name is the Registry's**, as it is after the event is applied, on the consumer thread, where the event and its decisions are handed to the archive together. A rename event's own row holds the new name. An event with no title field holds the name the session already had. It is `Session.Title`, verbatim, not folded or cut. Null or empty stores NULL.
  - **The path is the Registry's** `Session.Cwd` for a decision. The `events.cwd` column does not change.
  - **A decision about a session that is not in the Registry** (an event that was declined, or a session not yet seen) takes the name and the path from its event, if it has one, or else NULL.
  - **A decision with no session** (a group's sound, an hourly summary) stores NULL in both.
  - **The upgrade** adds the columns with `ALTER TABLE … ADD COLUMN`, once, at start, and it is safe to run again: check the columns with `PRAGMA table_info`, and do not trust the version number alone. Raise `user_version` to 2. Old rows keep NULL and are not filled in afterwards.
- **Deliverables:**
  - **The schema:** the new columns, on a new database and on an existing one.
  - **The write:** `Decision` (or what the consumer hands the archive) carries the name and the path. The store writes them. The event row carries the name.
  - **The rule changes (T1.24):** a decision row holds ids and fixed words, and the session's name in `session_title` only. The guard test still holds `reason` and `detail` to ids and fixed words. Make it also hold that the name appears in no other column. The log rule does not change: no name in a log line.
  - **The growth figure:** re-measure `GrowthMeasurement` with a name and a path on each row, at sizes from a copy of the operator's database. If the new figure is above `TypicalBytesPerDay` less a margin of 10 %, raise the constant with its reason, and change the figure in the documents.
  - **On a copy of the operator's database** (copy `dashboard.db` and any `-wal` to a scratch folder; never open the original): run the upgrade. Report the time it takes, that the row counts are unchanged, and the file size before and after.
  - **Documents, in the same change:** Impl §8.3 (the tables, the upgrade, and the version), Impl §3.4 and the other places that state the T1.24 rule; TS where the decisions record and its rule on operator text are described; the event flow, where the archive writes; Core and App, if a rule lands in Core. One row each in TS Appendix D and Impl Appendix C.
- **Acceptance:**
  - A new database has the three columns. An existing database at version 1, with rows, gains them at start, keeps every row with NULL in the new columns, and is at version 2. A second start changes nothing.
  - Each new event row with a session holds that session's name, or NULL when it has none.
  - Each new decision row with a session holds the name and the full path, including a decision made by the clock (a reminder, "went quiet").
  - A renamed session: rows before the rename hold the old name, and rows from the rename on hold the new one.
  - A decision for a session not in the Registry takes the name and path from its event. A group's sound and an hourly summary store NULL in both.
  - The guard: no name, prompt or payload text in `reason` or `detail`, and the name in no column but `session_title`. No name in a log line.
  - The upgrade on a copy of the operator's database, with its figures.
  - Plants: (a) the name taken from the event and not from the Registry, and the clock-decision test fails; (b) the column check removed so that the upgrade runs twice, and the second-start test fails; (c) the name written into `detail`, and the guard test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** no new table. No change to the hook script or the plugin. No change to the indexes, the prune or its rule. No name in a log line. Tests use scratch folders and only a copy of the operator's database.
- **Done 2026-10-04:** PR #104, merged as `148e775`, `6e595a8` (no fix cycle). The upgrade runs at each open and adds a column only when `pragma_table_info` lacks it, in one transaction; a failure leaves the file at version 1 with none of the columns. On a copy of the operator's database it took 7 ms, with 26,401 events and 8,340 decisions unchanged and the file size unchanged. `TypicalBytesPerDay` rose from 340,000 to 350,000 (a typical day measured 315,392 bytes with a name and a path on each row). **The coder's rulings, accepted:** an empty path stores NULL; the column step runs at every open; a new file gets the columns by the same `ALTER` step; a decision from another thread takes the name from the Registry only. **Found by the review, older than this task (T1.54):** a `dashboard.db` that is read-only at start never recovers, even after it is made writable, because `Microsoft.Data.Sqlite` pools the read-only handle and each retry gets it back. Taken to the operator as a separate issue. Nits, not fixed: the column check is case-sensitive, but SQLite names are not; no test holds an empty path stored as NULL; `ForeignSqliteReader` calls a busy prepare a "shape" problem.

**T1.70 — The Activity window shows what the dashboard did, in plain words**
- **Goal:** a window named **Activity** lists what the dashboard did since it started, newest first, in plain words, with the session's name and project on each line. An operator who hears a sound can open it and see what made the sound. For issue #97.
- **Depends:** T1.69 (`Decision.SessionTitle` and `Decision.Cwd`, stamped on the consumer thread), T1.61 ("last heard from Claude Code"), T1.37 (the decisions that the consumer makes), T1.58 (the throughput test)
- **Realizes:** #97, without the speaker sign (moved to #99, T1.67), and the operator's rulings of 2026-10-04: **this start only**; the name is "Activity"; the three "small additions" were built in milestone 4, and only "last heard from Claude Code" at the top of this window is new. A click on a line and "Show activity" on a row are T1.71. **Changed while the task was in progress (the operator's ruling of 2026-10-04): the window never reads the database.** It is a log, in memory, of what the dashboard did since it started; #97's text said otherwise and was corrected. The rulings:
  - **The lines come from the consumer's decisions**, the same ones that it hands to the archive, kept in **one list in memory from the start**. On the consumer thread, a batch that holds a shown line becomes one dispatcher post (never one for each event), and the post adds its lines to the list on the UI thread. Nothing is read from `dashboard.db`, and the store's state does not change what the window shows.
  - **The window is created at start and is always there.** Opening it only shows it. Its list reads the one list directly and is virtualized: nothing is drawn while it is hidden, and nothing is built again when it opens. Lines are added while it is hidden.
  - **The newest 20,000 lines are kept.** The oldest line goes when a new one would pass the limit, and the bottom of the list then says that older lines are not kept. Why: a safety net for a dashboard that runs for weeks; a normal three weeks is a few thousand lines.
  - **What shows:** what happened to the operator's sessions and to sound. A starting table, which the coder may change with a reason:
    - shown: `SessionAdded`, `StateMoved`, `SilenceSwept`, `SessionEnded`, `AckApplied`, `NoticePlayed`, `NudgePlayed`, `GroupNoticePlayed`, `NoticeSuppressed`, `SoundDropped`, `MuteApplied`, `MuteExpired`;
    - not shown (the dashboard's own records, for a developer with SQL): `SessionRefreshed`, `EventDeclined`, `GroupRederived`, `AckDeclined`, `TaskTypeUnrecognised`, `EventDropped`, `ApplyFailed`, `HookRefused`, `HourlySummary`, `TrayLightChanged`, `WindowSurfaced`, `RosterEdited`.
- **Deliverables:**
  - **The window:** it opens from the tray menu and from the toolbar. One window, made at start: opening it again brings it to the front. It remembers its place and size like the main window. It moves nothing: the motion rule holds.
  - **A line:** the time (`14:32`; a day name before it when the line is not from today), `♪` for a sound that played, what occurred in plain words, the session's name, the project, and the detail.
    - **Plain words, never code names:** "working again", not `PostToolBatch`; "went quiet: no event for 10 minutes", not `silence`. A "no sound" line says why: muted, paused, the group owns the sound, announced before, or no sound device. Put the full table of words, kind by kind and reason by reason, in the documents. A test holds that every shown kind and reason has words, and that no enum name reaches the screen.
    - **The name:** the decision's `SessionTitle` (T1.69), the name as it was at that moment. A line with no name shows the first eight characters of the session id. A group's sound (no session) shows the group's name.
    - **The project:** the last folder of the decision's `Cwd`, by the rule that the main window uses (`RowVisuals.WorkspaceLabel`), so one project has one name in both windows. Hover gives the full path. A line with no path shows nothing there.
  - **Wide and narrow:** in a wide window, one line in columns. In a narrow window, two lines, like a row in the main window, with the second line small and grey. As the window gets narrower, the detail goes first, then the project. The time, the `♪`, what occurred and the name never go. Hover on a line holds everything.
  - **The top of the window:** "last heard from Claude Code …", the same text as the tooltip (T1.61).
  - **Screen reader:** each line reads as one sentence.
  - **Documents, in the same change:** Design (a new subsection for the Activity window, after §9, and one row in §13); Impl §5 (a new subsection: the window, its source, the limit, the table of words, the layout); TS where the windows are described, and Appendix C if it lists the window as not built; the event flow, where the consumer hands its decisions on; Core and App (what a second interface would need to show the same lines). One row each in TS Appendix D and Impl Appendix C.
- **Acceptance:**
  - A run with decisions of every kind: the window shows the shown kinds, newest first, and none of the others.
  - Lines are added while the window is hidden, and show when it opens, with nothing built again. The window never opens `dashboard.db` (a test or the code path shows it), and a store that cannot write changes nothing in the window.
  - One post for each batch with a shown line, and none for a batch without one. T1.58's throughput test still passes; report its time before and after.
  - At the 20,001st line, the oldest goes, the list holds 20,000, and the bottom says that older lines are not kept.
  - Every shown kind and reason has words; no enum name is on screen.
  - A line with no name shows the short id; a group's sound shows the group's name; the project is the same text as the main window's for the same path, and the hover has the full path.
  - Realized-window tests, with `BindingErrorWatch` clean: the wide form, the narrow form, and the order in which the detail and then the project go. A test shows that the list is virtualized (for example, 20,000 lines realize only the rows in view).
  - No title, prompt or path in a log line.
  - Plants: (a) the limit not enforced, and the 20,001st-line test fails; (b) a hidden kind let through, and the shown-kinds test fails; (c) the project dropped before the detail, and the narrow test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** nothing read from the database, no new table and no change to what is written. No change to the hook script or the plugin. No change to any rule about states, order or sound. No prompt or answer in the window. The consumer never waits on the UI thread. Tests use scratch folders, never the operator's data folder.
- **Done 2026-10-04:** PR #105, merged as `bed9872`, `27844ca`, `3795a46`, `f57d315` (one fix cycle). The review found that a line's time was fixed when it arrived, so a line from before midnight never showed its day name. **Fix:** the lines read their time again when the local date changes, on the 15 s tick (31 to 44 ms once a day for 20,000 lines). **Director's ruling:** within one record, a sound line (or a "no sound" line) goes above its cause, so that the line under a `♪` says what made the sound. **The cost, ruled in from the review:** at the limit, each new line cost 4 to 10 ms on the UI thread, hidden or shown, because removing the oldest line from a laid-out list costs a layout. The list now trims to 19,000 in one step when it passes 20,000, and it is collapsed while the window is hidden: about 1.15 ms a line at the limit. A trim keeps the reader's place and selection. The T1.58 throughput test takes about 4 ms more per 1,024 events. Not verified: a real start and quit with the place saved, Narrator, a real fractional scale. **For the operator, not built:** a suppressed group sound's line has no name and no project, because its decision carries no group key.

**T1.71 — From an Activity line to its row, and from a row to its lines**
- **Goal:** a click on a line in the Activity window shows that session's row in the main window. "Show activity" on a row lists only that session's lines in the Activity window. For issue #97, its last two items.
- **Depends:** T1.70 (the Activity window and its one list), T1.23 or wherever the main window's open row and its scroll are built, `WindowSurfacer` (bringing the main window to the front)
- **Realizes:** #97: "A click on a line shows that session in the main window" and "A choice on a row ('Show activity') lists only that session's lines. That is the story of the row." Director's rulings:
  - **A click on a line** brings the main window to the front, scrolls the session's row into view, and opens it. Opening it does what a click on the row does today and nothing more: no Ack, no mute, no event.
  - **In selection mode,** the click scrolls to the row and brings it into view, but does not open it and does not change the selection. Why: in that mode, a click on a row selects it (Design §9), and setting `IsExpanded` there toggles the selection.
  - **A row that is folded** (inside a collapsed group, the flat view's Ended line, or a group's quiet line): unfold what holds it, then scroll and open. Find how each fold opens today, and use that.
  - **A session that is no longer in the main window,** and a line with no session (a group's sound): the click brings the main window to the front and does nothing more. The line's hover says why: "This session is no longer in the window." For a group's sound, the click scrolls to the group's heading if the group is still there.
  - **"Show activity" is an action in the open row,** beside the session id. Why: the open row holds the row's actions (the Ack, the session id, and later "Open terminal"); no row has a right-click menu today; and an action in the open row works on a phone later, where a right click does not.
  - **The filtered Activity window** shows only that session's lines, from the same one list (a filter, not a copy). A bar at its top says "Only <name>" (the short id when it has no name) with **Show all**, which clears the filter. New lines for that session keep arriving. Lines with no session (a group's sound) are not shown in a session's filter. "Show activity" on another row changes the filter to that row.
  - **A keyboard works too:** Enter on a selected Activity line does what a click does.
- **Deliverables:**
  - The click, through a command on the line, never through a code-behind handler that a test cannot reach.
  - The main window's "show this session" path: unfold, scroll, open, with the selection-mode rule.
  - "Show activity" in the open row, the filter and its bar, and **Show all**.
  - **Documents, in the same change:** Design §9 (the open row's new action) and §9.1 (the click and the filter), one row in §13; Impl §5.6.4 (the open row), §5.7 (the click, the filter and its bar); Core and App (what a second interface needs). One row in Impl Appendix C.
- **Acceptance:**
  - A click on a line of a session whose row is in the main window: the main window comes to the front, the row is in view and open, and no Ack, mute or event follows (the Registry and the event channel see nothing).
  - The same click in selection mode: the row is in view, not open, and the selection is unchanged.
  - A row inside a collapsed group, and one in the flat view's Ended line: the click unfolds, scrolls and opens.
  - A line of a session that is gone, and a group's sound: the main window comes to the front and nothing else changes; the hover says why. A group's sound scrolls to the group heading when the group is there.
  - "Show activity" on an open row: the window shows only that session's lines, with the bar; a new line for that session arrives in the filtered list; a line for another session does not show; **Show all** brings every line back; "Show activity" on another row changes the filter.
  - Enter on a selected line does what a click does.
  - Realized-window tests, with `BindingErrorWatch` clean in both windows.
  - Plants: (a) the click sets `IsExpanded` in selection mode, and the selection test fails; (b) the filter made as a copy that does not take new lines, and the live-filter test fails; (c) the fold not opened, and the collapsed-group test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** a click in the Activity window never changes the Registry, the sound or the database. No right-click menu. No change to the main window's rules on order, motion or selection. No new timer. Tests use scratch folders.
- **Done 2026-10-04:** PR #106, merged as `77d531a`, `1777b89`, `d0bebaf`, `5782148` (one fix cycle). **#23 is fixed** as a side effect, by the coder's ruling and the review's measure: the unfold tests failed `BindingErrorWatch`, because a "+ 1 quiet" or Ended line that became a session row was replaced in place. `MainViewModel.Reconcile` now removes and inserts a row of another kind. The review measured 0 binding errors in five cases (from 99 to 473 each before), and the selection, the scroll position and an open row were kept. **The review found:** the newest Activity line was selected at open, because the list followed the collection view's current item (fixed). **Two layout costs, both real, both fixed:** with the window shown and not activated, a footer in an `Auto` row cost 4 to 9 ms a line at the limit (a fixed row of 28 DIP); in a window under 620 DIP wide, every line cost 6 to 17 ms while the layout triggers found the list with an `AncestorType` binding (now an inherited attached property). A new line now costs about 1 ms wide and 2 to 3 ms narrow at 18,000 lines. "Show activity" is an action in the open row. Not verified: a real mouse and keyboard, Narrator, a real fractional scale, and `Activate()` taking the foreground from another application.

**Milestone 1F, third pass, closed 2026-10-04:** every planned issue in GitHub milestone 5 has a merged change: T1.67 (#99), T1.68 (#102), T1.69 (#98), T1.70 and T1.71 (#97), and #23 with T1.71. `main` builds with 0 warnings, and both suites pass, 2242 of 2242. **Found during the milestone and taken to the operator, not built:** a `dashboard.db` that is read-only at start never recovers until a restart (T1.54, the pooled handle); forming a roster over finished sessions plays "finished" again for the group (T1.26); a suppressed group sound's Activity line has no name or project. **Before an installed copy takes T1.68:** `history.retentionDays` is no longer used, and the history follows Claude Code's `cleanupPeriodDays` (30 days when the key is absent). A copy set to keep everything no longer does, unless `cleanupPeriodDays` is set high in Claude Code's settings.

**Milestone 1F, third pass, opened again 2026-10-04:** the operator added two of the found defects to GitHub milestone 5 after its planned work closed: #107 (a roster made from finished sessions plays "finished" again) and #108 (a group's "no sound" line in the Activity window has no name). **Order:** T1.72 is #107, because the operator ruled on it first. T1.73 is kept for #108; its block is written when T1.72 is merged. The read-only `dashboard.db` defect is still not filed.

**T1.72 — A roster group's "finished" plays only when it has something new to announce**
- **Goal:** making a roster from sessions that have already finished, and already played their sound, plays no new sound. The group's "finished" plays only when at least one finished member was not announced yet. For issue #107.
- **Depends:** T1.26 (the roster group's own sound), T1.44 (a settle that comes back unchanged is not announced again), T1.67 (the consumer names the settling member from the groups as they stand, `RosterSettle.SettledBy`), T1.55 (a held-back or dropped sound still counts: the engine goes on as if it had played)
- **Realizes:** the operator's ruling of 2026-10-04 on #107: "Creating a roster of already finished sessions that have played a sound should not create a new sound. If you add a working session to a roster, then when that session ends the roster would play a sound." And: "create it like it is already acknowledged." Director's rulings, which map that ruling to the sound engine:
  - **The engine remembers, for each finished session, whether its finish was already announced.** It is one more fact on the engine's own record of the session, for the entry it holds (the state and the instant the session entered it).
    - Announced: the engine made the session's own "finished" notice for that entry. That is so when the notice played, when a mute or a pause held it back, and when the player dropped it (T1.55: the engine goes on as if the sound had played).
    - Not announced: the notice was given to the session's roster group (`GroupDone`), and the group has not announced yet.
    - When a group's settle is announced (played, held back by a mute or a pause, or dropped), every member that is Unread at that moment counts as announced.
    - A new entry (the session works again and finishes again) starts with the fact decided again. An entry that T1.44 restores keeps the fact it had.
  - **A group that settles is silent when every Unread member is already announced.** If one or more Unread members are not announced, the group plays "finished" once, as today. An Unread member that the engine holds no record of counts as not announced: when in doubt, the sound plays.
  - **The members come from the consumer**, read from the groups as they stand at the settle, the way `SettledBy` is (T1.67). Never from the engine's own copy of each session's group: a roster edit does not change that copy, and this defect exists because of such an edit.
  - **A silent settle is recorded** the way T1.44 records its own: `NoticeSuppressed`, with the reason `AlreadyAnnounced`, for the group's notice. The decisions record and the Activity window then say why nothing played. The group is held as settled, so a later unsettle and settle behave as today.
  - **A silent settle starts no reminder for the group.** Why: each session that announced its own finish keeps its own reminder (one soft reminder after `UnreadNudgeAfter`), and a roster edit does not touch it. Making a roster then adds no sound and removes none. Accepted limit: a roster renamed before its group's reminder plays loses that one reminder, because the reminder belongs to the old group's key.
  - **No speaker sign (T1.67) for a silent settle:** no sound played.
  - **What follows from the rule, and stays:** removing the working member from a roster whose other members finished inside it plays "finished" once. Their finish was never announced, because the roster held the sound. The operator can change this.
- **Deliverables:**
  - The fact on the engine's record, set and carried as above. The rule stays in Core, and the engine still holds no roster book.
  - `OnRosterGroupSettled` learns the group's Unread members from the consumer's settle pass (a Core helper beside `RosterSettle.SettledBy` is acceptable).
  - The silent settle: no sound, no reminder, the record above.
  - **Documents, in the same change:** TS §IV.5, where the group's sound is described; Impl Part 7 (the group's notice and its reminder), Impl §2.4 and §2.5 where the settle and the engine's events are listed, Impl §8.3 if it lists the reasons, Impl §5.7 if the Activity window's words for this line change; the event flow, where the settle pass runs; Core and App, for the rule in Core. One row each in TS Appendix D and Impl Appendix C.
- **Acceptance** (each through the real `EventConsumer`, `RosterStore`, `RosterGroupWatch` and engine, wired as `AppHost` wires them, with no settle called by hand, as in `SoundSignPipelineTests`):
  - Two sessions finish with no roster, and each plays "finished". A roster is made from them: no third sound, one `NoticeSuppressed` row with `AlreadyAnnounced` for the group, and no row gets a new speaker sign. Five minutes later the group plays no reminder, and each session's own reminder plays at its own time.
  - A roster of one finished (announced) session and one working session: when the working one finishes, the group plays "finished" once, and the group's reminder runs as today.
  - A roster that exists before its members finish plays one "finished" for the group (unchanged).
  - A roster renamed after its group announced: no sound.
  - A roster made inside the settle window of a member's own finish (under a fake clock): no second sound.
  - A finished session whose name changes to a roster member's name: no sound for the group.
  - A session whose own "finished" was held back by a mute, then put in a roster: no sound.
  - Removing the working member from a roster whose other members finished inside it: "finished" plays once.
  - The T1.44 tests pass unchanged: a quiet tick does not announce a settle again, and it does not lose the group's reminder.
  - Plants: (a) the fact not set when a session's own notice is made, and the made-from-finished test fails; (b) the group's settle no longer marks its members, and the rename test fails; (c) the members taken from the engine's own copy of the groups, and a test fails (choose the test that sees it, and print the members before the verdict leans on the plant).
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** no change to a session's own sounds, to the settle window, to the mis-mark rule, to the order of rows or to the tray light. The Registry is not written. No lock, no timer and no thread is added. Nothing new in the database: the reason `AlreadyAnnounced` exists. Tests use scratch folders and a scratch Claude Code path.
- **Done 2026-10-04:** PR #110, merged as `2aa5209`, `b201f8b` (no fix cycle). The engine's record of a session's entry holds whether the finish was announced (`Tracked.Announced`), and the consumer hands the settle the group's Unread members (`RosterSettle.UnreadMembers`). The review's probe through the real pipeline: making a roster from two finished sessions plays nothing and records `AlreadyAnnounced` for the group (the base played the extra "finished"); a member that works and finishes again makes the group play once, with its sign and its reminder; a quiet tick stays silent (on the base, the tick also brought back and played the extra settle's reminder). **The coder's rulings, accepted:** an empty or absent member list plays; only a settle that announces marks its members; a silent settle keeps `QuietSince` and `SettledBy`; the T1.67 test that held the third sound now holds that no row is marked. **Found by the review, older than this task and not changed:** a session that finishes inside a roster, and whose roster is deleted or which leaves it before the group settles, is never announced and gets no reminder. Taken to the operator. **The final check:** `main` at `b201f8b` builds with 0 warnings, and both suites pass, 2259 of 2259. The Debug half ran in a separate worktree, because a build in the shared checkout failed with `CS2001` for the generated `.g.cs` files while another process used the same `obj` folder. Not verified: a real roster made in the window with real sound.

**#109 added to GitHub milestone 5 on 2026-10-04, at the operator's word.** It is the read-only `dashboard.db` defect that T1.69's review found. **Order:** T1.74 is #109, and it runs before T1.73 (#108) at the operator's word ("start this issue"). T1.73 keeps its number, because the paragraph above kept it for #108. **The operator's rulings on #109, 2026-10-04:** connection pooling stays on ("don't abandon connection pool re-use"); a connection that failed is closed for real; and a new connection that is read-only gets a short second try.

**T1.74 — History recording comes back after the database could not be written**
- **Goal:** when the dashboard could not write to `dashboard.db` at the moment it opened it, the retry each minute writes again as soon as the cause is gone, with no restart. A program that reads the file for a moment while the dashboard opens it costs nothing. For issue #109.
- **Depends:** T1.54 (the history notice, the retry each minute, the lost count), T1.59 (the store's lock: a close waits for the current write), T1.64 (the prune, which opens the store too), T1.69 (the schema steps at open)
- **Realizes:** #109 as written, and the operator's rulings above. Why the retry fails today (measured with the product's library versions; the sources are in #109):
  - `Microsoft.Data.Sqlite` pools connections. A plain close returns the real connection to the pool, and the next open gets it back.
  - When SQLite cannot open the database for writing at that moment, it opens it read-only, with no error: the file is marked read-only, or another program reads the file and does not let others write. A connection keeps the mode it was opened with.
  - So each retry gets the same read-only connection back, and fails. The pool closes a connection only after it was not used for 2 to 8 minutes, and the store retries each minute while events arrive.
- **Director's rulings:**
  - **The hard close:** `SqliteConnection.ClearPool(connection)`, then `connection.Dispose()`, in one private method. The store uses it in each place where it gives up a connection after a failure: the `catch` in `Connect`, and `Unavailable`. Find any other such place.
  - **`Dispose` does not change.** It already releases the file with `SqliteConnection.ClearAllPools()` (T1.17). Why: a change there gains nothing for this defect.
  - **The read-only check:** right after `connection.Open()` in `Connect`, before the schema steps, ask SQLite with `sqlite3_db_readonly(handle, "main")` through `SQLitePCL.raw`, the library under `Microsoft.Data.Sqlite`. 1 means read-only.
  - **The short second try:** if the new connection is read-only, close it hard, wait, and open again. At most four more opens, after waits of 100, 200, 300 and 400 ms: 1.0 s in all (the operator's "a few times within about one second"). The coder may change the schedule with a reason, but not past 1.5 s in all, which is about the wait that SQLite itself makes for a file that is held. The waits go through a seam that tests replace, so no test sleeps for real. A blocking wait is acceptable here: it occurs only for a new connection that is read-only.
  - **If it is still read-only after the last open:** close it hard, and throw a `SqliteException` with SQLite's code 8 (`SQLITE_READONLY`) and a message that says that the database was opened read-only. The failure path of T1.54 then goes on as it does today: the notice, the retry each minute, the lost count, and one Warning at the first failure.
  - **If a later open makes a connection that can write:** go on, and write one Information line: the database was opened read-only, and could be written after N more opens. No line at a normal open.
  - **Where it runs:** on the archive writer's thread, under the store's lock, like every open. In the product, only the writer takes the lock while the dashboard runs; `--replay` uses the store in its own run; `Dispose` at quit can wait for the length of one second try. Confirm each in the code, and report the longest wait.
- **Deliverables:**
  - The hard-close method and its uses.
  - The read-only check and the short second try in `Connect`, with the seam for the waits.
  - **Documents, in the same change:** Impl §8.3, where the retry of T1.54 is described: a failed connection is closed for real and never used again; a new connection that is read-only is opened again a few times within a second; the reasons; the two ways the defect started. TS §IV.7, if it says how the retry works, in words that name no library. One row in Impl Appendix C, and one in TS Appendix D if TS changes.
- **Acceptance:**
  - **Read-only at start:** a database file marked read-only before the store's first write. The write fails after the second try, the store is unavailable, and one Warning says that the database was opened read-only. The file is made writable. Under a fake clock, the first write after the retry minute succeeds, the store is available, and the recovery line gives the lost count.
  - **Held for a moment:** a second handle that reads the file and lets others only read (`FileShare.Read`) is open at the store's first open, and is closed during the second try (from the wait seam). The write succeeds: no failed write, no notice, no lost record, and one Information line.
  - **Held through the whole second try:** the write fails and the notice shows. After the handle is closed, the first write after the retry minute succeeds.
  - **Closed for real:** after a failed write, the database file can be renamed at once. Windows refuses a rename while a handle is open.
  - The existing tests of T1.17 (the file is released at quit), T1.54 (a retry after a failure), T1.59 (a close during a write) and T1.64 (a failed prune) pass unchanged.
  - No title, prompt or payload in a line; a line names the data folder at most.
  - **Plants:** (a) the hard close replaced by a plain `Dispose`, and the read-only test fails; (b) the read-only check removed, and the held-for-a-moment test fails; (c) the second try with a plain `Dispose` between its opens, and the held-for-a-moment test fails, because the pool hands back the same read-only connection.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** pooling stays on (the operator's ruling). No change to the retry minute, the notice and its text, the lost count, the store's lock, the schema steps, the prune or `Dispose`. No new thread and no new timer. The consumer, the UI and the request threads never wait on the store. Tests use scratch folders, never the operator's data folder.
- **Done 2026-10-04:** PR #111, merged as `fade0f5`, `619db1f` (no fix cycle). `CloseForReal` (`ClearPool`, then `Dispose`) is used in `Connect`'s catch, after a failed open, between the opens of the second try and after its last one, and in `Unavailable`. `Dispose` does not change. The second try waits 100, 200, 300 and 400 ms, through a seam. **The review measured the real case:** a separate process ran `Get-FileHash` on the file in a loop while 300 stores each opened it and wrote once. 39 writes were lost on the base and none with this change; 30 opens met a read-only connection and wrote after one or two more opens. The T1.69 read-only probe now recovers at the minute. **The coder's rulings, accepted:** the new tests run alone, because each store's `Dispose` empties every pool in the process, and a test running beside them made a plain close look like a real one (the first plant runs passed for that reason; the product has one store); a failed open also closes for real; the exception's message names no path. **One failure, not found again:** the coder saw one failure in 60 runs of the Storage tests and did not save its output. The review's 60 saved runs were clean, and neither older flake appeared. **Found by the review, older than this task:** `RecordingSoundPlayer`, a test fake, keeps a plain list that a test reads while the consumer adds to it, so `RosterAnnouncedPipelineTests` (T1.72) failed once in five full runs with "Collection was modified". Taken to the operator. **Nit, not fixed:** no test holds the hard close in `Unavailable` (a plain `Dispose` there fails no test); a write that fails on an open connection, then a rename of the file, would hold it. Not verified: an installed dashboard with a real backup or indexing tool.

**#112 and #113 added to GitHub milestone 5 on 2026-10-04, at the operator's word** ("file two issues fix them next"). They are the two items that T1.74's review found. **Order:** T1.75 (#112 and #113, one task), then T1.73 (#108). The operator gave #108 to this director, after the fixes.

**T1.75 — A sound recorder that is safe across threads, and a test of the real close after a failed write**
- **Goal:** the suite stops failing at random because of a test helper, and a test holds the real close in `Unavailable`. Test code only. For issues #112 and #113.
- **Depends:** T1.74 (`CloseForReal`, and the store's tests that run alone), T1.72 (`RosterAnnouncedPipelineTests`), T1.54 (a write that fails on an open connection)
- **Realizes:** #112 and #113 as written. Director's rulings:
  - **One task and one pull request.** Both are small, both change test code only, and they touch different files.
  - **No product code changes.** If a product change looks necessary, send QUESTION first.
  - **#112:** `RecordingSoundPlayer` takes a lock in `Play` and `Clear`. Each reader (`Played`, `Last`, `Gains`, `PlayedOf`) takes a copy under the same lock and answers from it. The shapes that the members return do not change; `Gains` stays a plain list (see its remark). `Played` now returns a copy and not the live list: find any test that keeps `Played` and expects it to grow, and change that test.
  - **The same defect in other fakes:** search the test fakes for a plain collection that the consumer thread adds to while a test thread polls it. Fix each one the same way, and list each one in the report.
  - **#113:** the new test runs in the collection that runs alone (T1.74), because another class's `Dispose` empties every pool in the process and would make a plain close look like a real one. The test's own second connection uses `Pooling=False`, so that it holds no handle.
- **Deliverables:**
  - The lock in `RecordingSoundPlayer`, and in any fake of the same shape.
  - A test for each fake that is changed: one thread plays (or adds) many times while another reads every member in a loop.
  - The test of #113: a store opens a scratch database and writes once; a write then fails on the open connection (drop `events` through a second connection, as `SqliteEventStoreTests.cs:420` does); straight after the failed write, the database file can be renamed.
  - **Documents:** none, unless a document describes a changed fake.
- **Acceptance:**
  - The thread test of #112 passes. **Plant (a):** the lock removed, and the thread test fails. A race may not fail every time: make the test fail without the lock in at least 9 of 10 runs, and report the rate.
  - `RosterAnnouncedPipelineTests`, `SoundSignPipelineTests`, `EventConsumerTests` and `SoundCommandPipelineTests`: 50 runs, each saved to a file, with no failure.
  - The test of #113 passes. **Plant (b):** a plain `Dispose` in place of `CloseForReal` in `Unavailable`, and the test fails.
  - Both suite counts; build clean, 0 warnings. Three full runs, each saved to a file: name any failure.
- **Guardrails:** no product code. Tests use scratch folders, never the operator's data folder.
- **Done 2026-10-04:** PR #114, merged as `6107b42`, `159b0be` (no fix cycle). Only `RecordingSoundPlayer` needed the lock; the review confirmed that no other fake with a plain collection is added to on one thread and read on another. Plant (a), no lock: the thread test failed 10 of 10 runs. T1.74's plant d, a plain `Dispose` in `Unavailable`, now fails the new test, the only failure in the Storage tests (it failed none at T1.74). The four pipeline classes passed 50 runs (the coder) and 30 runs (the review). **Not worth an issue (the review):** `FakeClock` and `RecordingUiTick` hold a `DateTimeOffset` that one thread writes and another reads; each of its two fields is atomic on x64 and the shared clocks keep one offset, so a read sees the old value or the new one. **Seen in the review's full runs, older than this task:** #40 once (the foreign reader's prepare fails while a commit holds its exclusive lock; a busy timeout in `ForeignSqliteReader` would likely end it), and once a port race with no issue (`AppHostTests.FreePort()` releases a free port before the host binds it, and a test running beside it can take it). Taken to the operator.

**T1.73 — A group's "no sound" line says which group**
- **Goal:** in the Activity window, every line about a roster group's sound names the group, whether the sound played, was held back or was dropped, and a click on the line goes to the group's heading. For issue #108.
- **Depends:** T1.70 (the Activity window and its words), T1.71 (a click on a line; a group's sound goes to its heading), T1.72 (a silent settle records a held-back group sound, `AlreadyAnnounced`), T1.55 (a dropped sound), T1.37 (the decisions record)
- **Realizes:** #108 as written. Director's rulings:
  - **What is recorded:** `DecisionRecorder.SoundSuppressed` writes, for a group's sound (`GroupNotice`, `GroupNudge`), the same detail that `SoundDropped` writes for one: `kind=… sound=… group=… members=…`. A session's held-back sound keeps its detail as it is today (`kind=… sound=…`). Why: this adds no new kind of text to the database; a played and a dropped group sound already record the group in this form.
  - **The name on the line:** `ActivityWords` names the group from the detail for `NoticeSuppressed` and `SoundDropped` too, not only for `GroupNoticePlayed`, by the same rule (`GroupName`). A group line has no project, as a played group line has none today.
  - **The click:** a held-back or a dropped group line goes to the group's heading when the group is in the main window, as a played one does (T1.71). When it is not, the hover says "This group is not in the window.", as today.
  - **The case the operator sees most:** after T1.72, a roster made from sessions that were already announced records a held-back group sound (`AlreadyAnnounced`). Its line must now read as a "no sound" line with the group's name and the reason "announced before".
- **Deliverables:**
  - The group in the held-back record.
  - The group's name on held-back and dropped group lines, and the click to the heading.
  - **Documents, in the same change:** Impl §8.3 (the detail of `NoticeSuppressed` for a group's sound) and §5.7 (the name on a group line, and the click). One row in Impl Appendix C.
- **Acceptance:**
  - A group's sound held back for each reason (the group muted, all sound muted, monitoring paused, announced before) records `group=` and `members=` with the group's key and its members' ids. A session's held-back sound records the same detail as before.
  - Through the real pipeline: a roster made from two sessions that were already announced writes a held-back group sound that names the group, and the Activity line reads "no sound", with the group's name and "announced before".
  - The Activity window names the group on a held-back and on a dropped group line, as on a played one. A click on either goes to the group's heading in a realized main window, with `BindingErrorWatch` clean. When the group is not in the window, the hover says so.
  - The guard of T1.24 and T1.69 still passes: no title, prompt or payload in `reason` or `detail`.
  - **Plants:** (a) the group left out of the held-back record, and the record test fails; (b) the group named only for a played sound, as today, and the window test fails; (c) the click not wired for a held-back line, and the click test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** no change to which sounds play or when, to a session's lines, to the database's columns or to the Activity window's source. No title in a log line. Tests use scratch folders.
- **Done 2026-10-04:** PR #115, merged as `8f213a4`, `9c1a7a9` (no fix cycle). The click needed no code change: a line with no session already took its group from the detail (T1.71). The review's probe through the real pipeline: a group settles and plays, "mute all" goes through the channel, and six minutes later the group's reminder is held back; its row records the group, and its line reads "no sound", the group's name and "finished, all sound is muted". **The review on `group=`:** it holds the roster's own label ("Group" or "Group N" by default, or the operator's name for it), as played and dropped group sounds already did; this change does not widen it. **Nit, not fixed:** the general T1.24 guard test never makes a held-back group sound; this change's exact-format tests catch a title in `members=`. Five full runs in the review were clean: neither #40 nor the port race appeared. Not verified: a real roster in the window with real sound and a real mute.

**GitHub milestone 5 closed its nine issues on 2026-10-05**, and 0.0.22 was released from `a68231b`. **#118 added on 2026-10-05, at the operator's word:** the rule that kept the operator's text out of the log file goes.

**T1.76 — The log file may hold any text**
- **Goal:** the project no longer keeps names, titles, prompts or answers out of the log file. The rule leaves the documents, the tests that only hold it go, and the two types that hide text stop hiding it. For issue #118.
- **Depends:** T1.24 (the rule and its inventory), T1.52 (the Debug line for each decision), T1.69 (the name in its own column, and the guard that kept it out of the other fields), T1.48 (the token, which stays out of the log)
- **Realizes:** the operator's ruling of 2026-10-05: "log everything, don't censor anything. If someone sets a log flag and starts writing to disk, that's fine." The reason: `dashboard.db` stores the same text in plain form, in the same folder as the log file, and Claude Code keeps it on the same disk, so the rule protected nothing. Director's rulings:
  - **The token stays out of the log.** It is a credential, not the operator's text. `TokenHandoverTests.The_token_and_the_retired_value_appear_in_no_log_line` stays as it is.
  - **A test goes only if the rule is all that it holds.** A test that also holds a behaviour keeps that part: for example, a test that a roster edit writes one line keeps the count and loses the check that no member is named. Report each test removed and each test changed, with the reason, so that the review can check that no behaviour lost its test.
  - **`PayloadJson` and `OperatorText` stay as types** (removing them is a large change for no gain), and they stop hiding: `ToString` gives the text, and a structured log gives the text. Their other duties stay (JSON conversion, equality). Remove the remarks and tests that exist to keep them hidden.
  - **The Debug line of each decision** (`DecisionRecorder`) gains the session's name. No other line gains text in this task.
  - **At the default level, the log does not change.** No existing line prints a whole event or session today; confirm it, and report any line whose text changes because `ToString` changed.
  - **The database does not change.** `reason` and `detail` keep their present content; they are simply no longer guarded.
  - **CLAUDE.md is the director's to change, at the operator's word:** its working agreement on logging is changed in the same commit as this block. The coder does not edit CLAUDE.md.
- **Deliverables:**
  - The tests of the rule removed or reduced: `UnprotectedTextInventory`, and each test that checks that no log line or decision field holds a title, a name, a prompt, a body or a member (`SessionTitleLoggingTests`, `RosterLoggingTests`, `RosterEditWakeTests`, `SilenceLoggingTests`, `DeclineLoggingTests`, `DecisionRecordTests`, `SessionTitleRecordTests`, `SqliteEventStoreTests`, `PayloadJsonTests`, and any other that a search finds).
  - `PayloadJson` and `OperatorText` print their text.
  - The session's name in the Debug decision line.
  - Code comments that state the rule ("identifiers only", "never a title") are corrected where they are now false.
  - **Documents, in the same change:** Impl §3.4 (the bullet is rewritten: what may be logged, and that the token may not), §8.3 and §8.4 where they call a field or a line "identifiers only" because of the rule, §5.7 if it says so; TS §IV (line "never executed and never logged": "never executed" stays); the event flow and Core and App where they state the rule; `docs/quiet-scheduled-jobs.md` and the README if they do. One row each in TS Appendix D and Impl Appendix C. Section numbers stay.
- **Acceptance:**
  - A search of `docs/`, the README and the source for the rule's words ("never logged", "never log", "identifiers only", "operator text", "operator's words") finds no statement of the rule. Report the search and what is left, with the reason for each.
  - A log line that prints a `PayloadJson` or an `OperatorText` shows the text (a test).
  - At the Debug level, the decision line shows the session's name (a test).
  - At the default level, the same run writes the same lines before and after (a test, or a comparison of two logs of one scripted run; say which).
  - The token test passes unchanged.
  - The count of tests before and after, with the list of the tests removed.
  - **Plants:** (a) `PayloadJson.ToString` hides the text again, and its test fails; (b) the name left out of the Debug line, and its test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** no new line at the default level. No change to what the database stores, to `/state`, or to any state, order or sound rule. The token is never logged. Text is still data: stored and shown, never executed. Tests use scratch folders.
- **Done 2026-10-05:** PR #119, merged as `8dd11fc`, `6e54ae2`, `0991174`, `2ca0dc9` (one fix cycle, in the documents). 17 tests that held only the rule went, about 16 lost the rule's assertion and kept their behaviour, and four were added: 2264 tests, from 2277. `PayloadJson`, `OperatorText` and `ScheduledPrompts` print their text. The Debug line of a decision shows the session's name. **The default level did not change:** two logs of one scripted run, on the base and on the branch, held the same lines; and the review's search found no line that prints an event, a session or one of those types. **The review found:** three statements that still said that the raw body cannot be printed (Impl §2.1, the event flow, one comment), now corrected; and one Debug line that had lost its only test, now tested. **The token:** a new test holds it out of the log at the Debug level too; the review's plant, a Debug line that printed the header, failed that test and passed the default-level one. **The coder's rulings, accepted:** a `Text` property beside `Reveal()`; the name read when the decision is added; the checks that a refused post's body and a task's command are not stored stay, because they are not the log rule; the Phase 1 acceptance record is marked superseded, not rewritten. Not verified: a real run of the app at the Debug level. **For the operator:** #11 and #25 are no longer defects.

**#118 closed, and #11 and #25 closed as not planned, on 2026-10-05, at the operator's word. #120 added on 2026-10-05, at the operator's word** ("File the port problem and fix it").

**T1.77 — A test copy of the dashboard keeps the port that it chose**
- **Goal:** the tests that start a real copy of the dashboard stop failing at random because another test took their port first. Test code only. For issue #120.
- **Depends:** T1.46 (the `/state` tests that start a host), T1.57 (the port choice at start), T1.65 (`HealthStateHostTests`)
- **Realizes:** #120 as written. Director's rulings:
  - **Test code only.** If a product change looks necessary (for example, a way for a test to read the port that the host bound), send QUESTION first.
  - **The way to choose:** prefer a port that the host takes at the moment it listens (port 0, then read back the bound port). Where a test needs the number before the start, retry on a new port when the start fails because the port is taken. A helper that lets a port go and starts on it later is not acceptable, except where a test needs a port that nothing listens on (then say why).
  - **Each of the 19 callers of `FreePort()`** moves to the new way, or keeps it with a reason in the report.
- **Deliverables:** the new way to choose a port; the callers moved; `FreePort()` removed if no caller keeps it.
- **Acceptance:**
  - The tests that start a host pass in 50 runs side by side (the classes that call the helper, run together), each run saved to a file.
  - **Plant:** a test helper that takes the chosen port first, between the choice and the start, as another test would; the test that used the old way fails, and the new way passes. If such a plant cannot be built, say why and choose another that shows the race is closed.
  - Both suite counts; build clean, 0 warnings. Three full runs, each saved: name any failure.
- **Guardrails:** no product code. No change to the port rules of T1.57. Tests use scratch folders.
- **Done 2026-10-05:** PR #121, merged as `11757b7`, `5d630d2`, `df1f587` (one fix cycle). Port 0 was not possible for an `AppHost` host without a product change (a pin of 0 means "unset", and the host derives a port in the dashboard's own range), so `TestPorts` retries: it chooses a port, builds the host with it, starts it only if the host's own probe found the port free, and chooses again if the port was taken, up to five times. Hosts with their own Kestrel listen on port 0. `TestPorts.Unbound()` keeps the old way only where nothing listens on the port. **The review found:** a host that lost its port left its log file open, so a test could not clean up after a retry; fixed, with a test. 50 runs of the classes that start a host, side by side, passed. **Seen in the runs, older than this task, not reproduced:** `EventArchiveWriterTests.The_writer_says_that_it_ran_and_what_it_did` once, `ActivityLinkTests.A_click_on_a_line_brings_the_main_window_up_and_opens_its_row_in_view` once, and `PortSelectionTests.The_derivation_does_not_use_a_per_process_hash` once (its second assertion compares with a hash that is random in each process, and matches by chance about once in 1,000 runs). Taken to the operator.

**On 2026-10-05, at the operator's word:** #120 closed. The three failures above filed: #122 (the writer's start-up test) and #123 (the Activity click test) wait for the next failure with its whole output saved; #124 (the port test's chance match) is fixed now, with #40, added to milestone 5.

**T1.78 — Two tests that fail at random: a chance match, and the foreign reader**
- **Goal:** two known random failures stop. Test code only, unless the cause of #40 is in the product. For issues #124 and #40.
- **Depends:** T1.14 (the hook-to-row test), T1.21 (the derived port), T1.74 (the store's connection handling)
- **Realizes:** #124 and #40 as written. Director's rulings:
  - **#124:** remove the second check of `PortSelectionTests.The_derivation_does_not_use_a_per_process_hash`; keep the exact SHA-256 check.
  - **#40: find the cause before the fix, and show it.** `HookToDatabaseTests.A_posted_hook_becomes_a_row_carrying_the_body_it_arrived_with` fails about once in 30 full runs: the test's `ForeignSqliteReader` opens the file and cannot prepare its `SELECT`. The issue names three possible causes (the schema made late on the writer's thread; an unfinished hand-over at the store's close; the reader's open), and the T1.69 and T1.75 reviews named a fourth: the reader has no busy timeout, so its prepare fails while another connection holds the lock of a commit. Save every run's whole output, with SQLite's own message and code. Reproduce it (for example, many full runs, or a probe that holds a lock at the moment the reader prepares), and name the cause with that evidence.
  - **The fix follows the cause.** A test that owns the hand-over it depends on, or a busy timeout or short retry in the reader for a busy error only, are acceptable. A wider fixed wait is not.
  - **If the cause is in the product** (for example, the store's close does not leave the file readable at once), send QUESTION before a product change.
  - **Correct the record:** state the cause in a comment on #40, and correct T1.14's acceptance record (`phase1-acceptance.md`, §5's hook-to-row evidence) if it describes the mechanism that was at fault.
- **Deliverables:** the change to `PortSelectionTests`; the cause of #40 and its fix; the comment on #40.
- **Acceptance:**
  - #124: the port test has only the exact check.
  - #40: the cause is named, with the saved output or the probe that shows it. A test, or a plant, shows that the fix closes it: with the fix removed, the failure can be made to happen.
  - 50 runs of the Storage tests and the classes that start a host, run together, each saved: no failure. Three full runs, each saved: name any failure.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** test code only, unless the operator agrees to a product change. No wider fixed waits. Tests use scratch folders.
- **Done 2026-10-05:** PR #125, merged as `4032234`, `e9f8212`, `6b1aeeb` (no fix cycle). **The cause of #40:** a store's `Dispose` called `SqliteConnection.ClearAllPools()` (T1.17), which empties every pool in the process. In `Microsoft.Data.Sqlite` 10.0.0, `Clear` ends with `ReclaimLeakedConnections`, which treats a connection as leaked when it is active and has no owner; `Activate` marks it active before it sets the owner, with no lock against `Clear`. So another store's connection could be destroyed while it opened, and that store lost its tables or its rows (`ObjectDisposedException` on `SQLitePCL.sqlite3`, caught as a write failure). **The operator approved one product change (2026-10-05):** `Dispose` now closes its own connection for real (`CloseForReal`, its own pool only); the file is still released at quit. The race plant (stores that write while another thread makes and closes stores): writes lost in 7 of 9 runs before the change, none in 9 after; the review's own probe agreed (1 lost in 2,807 stores before, 0 in 2,611 after). The test helper `ForeignSqliteReader` now reports SQLite's own code and message; its missing-table test had failed on a syntax error, not on a missing table. #124: the chance check removed. **The review agrees** that #122's database sighting very likely had the same cause. Not verified: the test named in #40 did not itself fail in the runs; its neighbour and three other store tests did, with this cause.

**On 2026-10-05, at the operator's word:** #124, #40 and #122 closed (#122 with the likely cause of T1.78); #123 waits for its next failure; 0.0.24 released from `c553241`. **On 2026-10-06:** #9 rewritten with the operator's design for the log file.

**T1.79 — The log file says when an unknown value arrives, and traces each event at the Debug level**
- **Goal:** an event with a value that this build does not know is no longer received without a word in the log file. At the normal level the log file shows what is unusual; at the Debug level it traces every event with its details, close to what the database holds. For issue #9.
- **Depends:** T1.53 (the raw error kind), T1.52 (the log file follows `logging.minimumLevel`), T1.76 (the log file may hold any text), T1.37 (the decisions record)
- **Realizes:** #9 as rewritten on 2026-10-06, and the operator's design: "the database captures all the information; update the log to function more like the database". Director's rulings, from that design:
  - **The database is the full record; the log file is not a copy of it.** At the normal level, the log file holds what someone should notice, never routine events. At the Debug level, it holds every event.
  - **Normal level:** the first time a value parses to "unknown", one Information line names the event, the field and the value, and says that it changed nothing. One line for each field and value for the life of the process. The fields: the Notification type, the SessionStart source, the StopFailure error kind and the SessionEnd reason (the four lists in `Matchers.cs` with an `Unknown` value); find any other field read into a fixed list in the same way. A known value that is ignored on purpose (`idle_prompt`, `agent_completed`) writes nothing at the normal level.
  - **Debug level:** one line for each event that the consumer applies or declines: the event's name, the session, its type field where it has one, and the outcome (applied, or declined and the reason). The decision lines of T1.52 stay as they are.
  - **The database:** the `EventDeclined` row's `detail` gets the raw type (`type=<value>`) for an event that has a type field.
  - **The raw value** is written as Claude Code sent it (#118), cut to a sensible length, so a broken or hostile payload cannot write a huge line.
- **Deliverables:** the once-per-value Information line; the per-event Debug line; the type in the `EventDeclined` detail. **Documents, in the same change:** Impl §8.4 (the log file: the new Information line and the per-event Debug line), §8.3 (the `EventDeclined` detail), §9.1 (what happens to an unknown value); the hooks reference, where it says the dashboard knows four of twelve types. One row in Impl Appendix C.
- **Acceptance:**
  - Two notifications of the same unknown type write one Information line, not two; a second unknown type writes its own line. The same for an unknown start source, error kind and end reason.
  - An `idle_prompt` writes no Information line.
  - At the Debug level, each event writes one line with its name, session, type and outcome, applied or declined.
  - The `EventDeclined` row for an unknown notification has `type=<value>` in its detail.
  - At the normal level, a run with only known values writes the same lines as before (a test, or two logs of one scripted run).
  - A value of 10,000 characters is cut in the line.
  - **Plants:** (a) the "once" memory removed, and the two-arrivals test fails; (b) the type left out of the decline detail, and its test fails.
  - Both suite counts; build clean, 0 warnings.
- **Guardrails:** no change to what any event does to a session, to the sound or to the screen. No line for each event at the normal level. The token is never logged. Tests use scratch folders.
- **Done 2026-10-07:** PR #126, merged as `51e9b9a`, `636effe`, `16781b8` (one addition after the approval). `EventValues` and `UnknownValueLog`: one Information line per event, field and value for the life of the process, for five fields (the coder added the background task type on a Stop), with a cap of 100 values and then one line that no more are named; the value as sent, control characters and the Unicode separator and format categories escaped as `\uXXXX`, cut after 200 characters. The per-event Debug line replaces the old decline line; the `EventDeclined` detail holds `type=`. **The review's probe on a real host** at the default level: one line for an unknown type, none for a repeat or for `idle_prompt`; 150 types gave 100 lines and the cap line; a 10,000-character value gave a 332-character line. **Ruled in after the approval:** U+2028, U+2029 and U+202E reached the file raw, because `char.IsControl` does not cover their categories; now escaped, with a test for each. The runs under the green-run rule: exit 0, no abort line, Total 2286 (2268 + 18). Three runs made under the operator's high load hit #123 (ActivityLinkTests) and none after it. **For the operator, a later issue:** `BackgroundTaskReader` keeps a task only when its status is exactly "running" and skips any other status with no line. Not verified: a real Claude Code session sending a value unknown today.

**T1.80 — The build script says whether a test run is green**
- **Goal:** the green-run rule of Part 1 (added 2026-10-05 from issue #12) is applied by `build.ps1`, not by a person reading the output. One run ends with one verdict line, and the whole output is kept in a file. For issue #12 (its "still open" part: how a run is read). The operator's word: "Do issue #9 and then issue #12."
- **Depends:** T1.3 or wherever `build.ps1` was made; the Part 1 rule (84a718e)
- **Realizes:** #12 as rewritten on 2026-10-05: a crashed test host still prints `Passed!` with a smaller `Total`, and the exit code is 1. The script catches the exit code today; it does not check the total, it does not keep the output, and it says nothing a person can quote. Director's rulings:
  - **The script applies the rule itself.** After `dotnet test` it checks the three things: the exit code is 0; the output has no line "The active test run was aborted"; and `Total` equals the expected count. It then prints one line: `GREEN: exit 0, no abort, Total N, expected N` or `NOT GREEN: <which of the three failed, with the numbers>`, and exits with 0 only for GREEN.
  - **The expected count comes from the test host's own list** (`dotnet test --list-tests`, or the equivalent that counts theory cases the way `Total` does), taken before the run, so that no person types a number. If the list cannot count the way `Total` does, say so, and take the count from a `-ExpectedTotal` parameter instead, with the list as a check. Show on the present tree that the count and `Total` agree.
  - **The whole output is kept:** the console output to a file, and the results file (`--logger trx`) beside it, under a folder that is ignored by git (for example `artifacts/test-runs/<date-time>-<configuration>/`). The verdict line names the folder. A crash then leaves the evidence that #12 asks for.
  - **A plant proves it:** a throwaway test project that crashes its own test host after some tests (as the 2026-10-05 measurement did), run through the same checks, gives `NOT GREEN` with the abort line and the smaller total; the same project without the crash gives `GREEN`. The throwaway is not committed; the report gives both verdict lines. A second plant: `-ExpectedTotal` one off on the real suite gives `NOT GREEN`.
  - **The sessions report the verdict line.** Appendix B.1 (the coder's Status Report) and B.3 (the reviewer's verdict) say: run `build.ps1 -Configuration <c>` and give its verdict line and the folder, for each configuration. The Part 1 rule points to the script. A run by hand is still read by the same three checks.
  - **No product code**, and no change to the tests themselves.
- **Deliverables:** `build.ps1` with the checks, the saved output and the verdict line; the ignored folder; the Part 1 and Appendix B sentences; the README where it says how to build and test, if it does. One row in Impl Appendix C only if Impl describes the build; otherwise none.
- **Acceptance:**
  - On the present tree, `build.ps1` and `build.ps1 -Configuration Release` each end with `GREEN`, exit 0, and the folder holds the console output and the `.trx`.
  - The crash plant gives `NOT GREEN` with the abort line and `Total` below the expected count; the exit code is not 0.
  - `-ExpectedTotal` one off gives `NOT GREEN`, naming both numbers.
  - A run with one failing test (a throwaway change, not committed) gives `NOT GREEN` with the exit code.
  - The script is not run elevated and needs nothing installed beyond the SDK.
- **Guardrails:** no product code; no change to any test. The script never deletes a saved run. Never run elevated.

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
Runs: <build.ps1's verdict line for Debug, then for Release, each with its folder>
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
  - Tests: <pass | issue>; build.ps1's verdict line for each configuration
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

**Per task:** (1) read the named TS/Impl sections from the repo first; (2) implement **exactly** that task — no more, no less; (3) write the named tests and make them pass; run `build.ps1 -Configuration Debug` and `build.ps1 -Configuration Release`, and give each verdict line and its folder in the Status Report (Part 1: a run is green only by that line); (4) self-check against every acceptance criterion and working agreement; (5) if the spec leaves a small gap, choose reasonably, proceed, and record it under Assumptions — but if you hit a genuine blocker, an ambiguity you can't resolve, or a conflict between the specs, **send a `BLOCKED`/`QUESTION` Status Report instead of guessing**; (6) commit, then send your Status Report to `director`. Do not start another task on your own.

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
4. **Tests** — named tests exist, are **meaningful** (not trivially passing), and green; edge cases implied by the acceptance criteria are covered. Green means `GREEN` from `build.ps1 -Configuration <c>`, run by you for Debug and Release: give each verdict line and its folder in the Verdict (Part 1).
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
