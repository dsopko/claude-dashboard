# Quiet scheduled jobs

A scheduled job that checks something and finds nothing to do can end its turn without the dashboard playing **finished**. This guide shows how to set one up.

## What a quiet tick is, and why the dashboard would otherwise beep

A Claude Code session can schedule a recurring job for itself: every 30 minutes, say, run a prompt that checks whether anything is overdue. Each time it fires, the session starts a turn, looks, and ends the turn. To the dashboard, a turn that ends is a turn that finished, so it plays the **finished** sound and marks the row unread. A watchdog that finds nothing therefore beeps every half hour for nothing.

A **quiet tick** is one of those turns that found nothing to do and says so. The dashboard plays no sound for it, sends no nudge, and puts the row back exactly as it was before the tick: the same state, the same answer, and the same place in its nudge schedule, as if the tick never happened.

The dashboard recognizes a tick by structure, not by what the prompt says. A turn is a tick when its prompt is **exactly** the prompt of one of the scheduled jobs the session listed at the end of its previous turn. Claude Code reports those jobs to the dashboard itself, so a prompt you type is never mistaken for one.

## The line to add

Add this line to the end of the scheduled job's prompt:

> If nothing is overdue and you took no action, reply with exactly WATCHDOG-QUIET with no punctuation, quotes or formatting, and nothing else.

When the agent's whole reply is `WATCHDOG-QUIET`, the dashboard treats the tick as quiet. Whitespace around it does not matter. Anything else, including the word followed by a full stop, the word in backticks or bold, or the word with any other text, is an ordinary reply and beeps as before.

The line spells this out for the agent, because the agent reads only the job's prompt, never this guide. Agents tend to end even a one-word reply with a full stop, and a word shown in backticks invites a reply in backticks, so the line names the word bare and asks for no punctuation, quotes or formatting.

## A complete example

A watchdog prompt for a session that directs other sessions, with the line in place:

```text
Check every task you have handed out. If a peer has sent no report, verdict or idle notice for 30 minutes, ping it for status and note it in a Progress Update. If anything needs the operator, resurface it now.
If nothing is overdue and you took no action, reply with exactly WATCHDOG-QUIET with no punctuation, quotes or formatting, and nothing else.
```

## It is opt-in, per job

Only a job whose prompt carries the line can be quiet, because only then does the agent know to reply with the sentinel. Jobs without it beep on every tick, exactly as before. Add the line to each job you want quiet.

## Jobs already running keep their old prompt

A scheduled job keeps the prompt it was created with. Editing your notes, or this guide, does not change a job that is already running. To pick up the line, **delete the job and create it again** with the new prompt.

## The line is harmless everywhere else

On an older dashboard, or with the dashboard not running, the line changes nothing you would notice: the agent replies `WATCHDOG-QUIET` and the dashboard beeps as it always did. You can add it before you upgrade.

## What still beeps

Everything that is not exactly the sentinel:

- **Any other reply**, including one that did real work and says so.
- **A Resurface**: the job found something the operator must see, such as "NEEDS YOU: the coder has been silent for 40 minutes". This is terminal text with no tool call, which is why the dashboard does not count tool calls to decide what is quiet.
- **A Progress Update**, or any report of action taken.
- **The sentinel with anything around it**, such as `WATCHDOG-QUIET.` or `` `WATCHDOG-QUIET` ``.
- **A job the dashboard does not recognize**: a one-off wake-up that is not in the session's list of scheduled jobs, or a prompt that has changed since the last turn listed it.

This is the safe direction. A missed sentinel costs one extra beep, which is how it was before. It can never silence an escalation, because an escalation is never the bare sentinel.

## How to check it is working

1. **Listen.** Leave the session idle across a tick. You should hear nothing, and the row should look the same afterwards as before, with the same answer under "Claude answered".
2. **Read the decisions record.** The dashboard records every decision in `%LocalAppData%\ClaudeDashboard\dashboard.db`. A quiet tick leaves a `StateMoved` row with the reason `QuietTick`, next to a `NoticeSuppressed` row with the reason `AlreadyAnnounced`:

   ```sql
   SELECT ts, session_id, kind, from_state, to_state, reason
   FROM decisions
   WHERE reason IN ('ScheduledPrompt', 'QuietTick', 'AlreadyAnnounced')
   ORDER BY id DESC LIMIT 20;
   ```

   `ScheduledPrompt` marks the tick starting. If you see it and never `QuietTick`, the job is being recognized but its reply is not exactly the sentinel. If you see neither, the job is not being recognized: check that it was recreated after you added the line.
3. **Or watch the log.** With `"logging": { "minimumLevel": "Debug" }` in `%LocalAppData%\ClaudeDashboard\settings.json`, the same decisions appear in the log as they happen.

The dashboard compares the reply as data and never records it: neither the reply nor the job's prompt appears in the decisions record or the log.
