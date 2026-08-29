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
- **Deliverables:** Kestrel minimal API bound to loopback fixed port; `POST /hook` (deserialize by `hook_event_name`, map event-specific fields per Impl §9.1 to an `InboundEvent`, enqueue, return `200` empty); `POST /show`; `GET /health`; `X-Dashboard-Token` check.
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

**T1.19 — Packaging (self-contained single-file)**
- **Goal:** a shippable exe that autostarts.
- **Depends:** T1.7, T1.18
- **Realizes:** Impl §10.2
- **Deliverables:** a `dotnet publish -c Release -r win-x64 --self-contained` single-file profile; the logon task points at the published exe.
- **Acceptance:** the published exe launches at logon via the task and runs headless-to-tray; no machine-wide runtime required. **Not MSIX.** **Verify the manifest reached the published executable** — a single-file publish generates its own apphost, and whether the source manifest is embedded in it is a packaging behaviour nobody here has observed. The failure would be silent and would look exactly like the source manifest being wrong, sending whoever debugs it to the wrong file.
- **Guardrails:** **After T1.19 there are two artefacts — the source and the published exe — and they can disagree.** Every task landing after this one carries "republish and repoint the logon task" in its acceptance. Without that, the executable the operator's logon task starts quietly drifts from the source, and the symptom is one nobody would connect to a change in task ordering.

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
- **Deliverables:** the three-attempt choice of §3.1 — `port.txt`, then a SHA-256-of-SID derivation, then a bounded walk; each walk step classifying the occupant through the `/health` identity; the bound port written to `port.txt` and carried into the hook URL; `DashboardSettings.DefaultPort`'s remark corrected, since its stated reason no longer holds.
- **Acceptance:** two data roots with different derived candidates both bind and both register their own URL; a fresh profile with no `port.txt` derives and binds; a `port.txt` naming a taken port falls through to the derivation; a stranger on the derived port causes a walk, not an exit; all three failing still starts the dashboard with the Error and the tooltip of §5.3; the registered URL carries the **bound** port, tested with a port that is neither the default nor the derived one. **Republish, and supplement the acceptance document** — §1 and §4 were measured against a single fixed port.
- **Guardrails:** **SHA-256, never `GetHashCode()`** — it is randomised per process, so the same user would derive a different port every launch and every in-process test would still pass (the T1.15 trap). Binding is the only question asked; build no registry of who owns which port. The walk is bounded. **Accepted residual, ruled by the operator:** allowlist entries accumulate, one per distinct URL ever registered, and nothing removes them.
- **Note:** the operator's own multi-user question settled two things worth carrying. The database and every other file are already per-user under `%LOCALAPPDATA%`, so **only the port is shared** and nothing in storage changes. And two users sharing one `CLAUDE_DASHBOARD_HOME` share one database, which our writer is not built for — document that as unsupported rather than leave it quietly half-working.

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

**Standing watchdog (never retired).** While anything is in flight, keep a fallback timer running: if no report, verdict, or idle notice has arrived within 30 minutes, ping the responsible peer for status and note it in a Progress Update. This timer runs for the entire run and is never dropped because the rhythm feels reliable — your only sense organ is incoming messages, so a peer that has gone quiet is indistinguishable from a peer that is working. The rhythm is never the protection; the timer is.

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
