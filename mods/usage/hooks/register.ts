// Claude Dashboard - usage reporter.
//
// THE COPY IN THE DASHBOARD'S DATA FOLDER IS GENERATED. ClaudeDashboard.App writes it at every
// start from mods/usage/hooks/register.ts in its repository, so an edit to the copy is reverted.
//
// WHAT IT DOES. Claude Code raises session.measure when it measures the session: at the end of
// each turn of the main conversation, and when a plan limit moves a whole point. The hook below
// posts those figures to the dashboard when one is listening, and does nothing when none is. It
// only observes: it shows nothing, changes nothing, never throws, and hands the event on as it
// came.
//
// THE POST IS STARTED FROM A TIMER, AND THAT IS NOT A PREFERENCE. A $.http.fetch that starts
// while the hook runs holds the end of a `claude -p` run until the dashboard answers, awaited
// or not. Measured on Claude Code 2.1.294, 2026-10-08, against a server that accepts the
// request and never answers: the run ended after 32 s in place of 2 s. A timer's callback runs
// outside the event, and the same server then added nothing: the run ended in 2 to 3 s.
import type { EngineInterface, Register, SessionMeasureInput } from 'claude-code'

import { listeningFile } from './listening-file'

// The two lines of listening.txt, held to the rules post-status.cmd holds them to: a port from
// 1 to 65535 with no sign, space or leading zero, and a token of exactly 43 base64url characters.
const PORT = /^[1-9][0-9]{0,4}$/
const TOKEN = /^[A-Za-z0-9_-]{43}$/

export const register: Register = on => {
  on('session.measure', ($, e, next) => {
    $.clock.after(0, () => void report($, e))
    return next(e)
  })
}

/**
 * Sends one measurement to the dashboard, if one is listening. Resolves on every path.
 *
 * listening.txt is read at every call, never kept: the dashboard makes a new token at each
 * start, and a session must go on reporting through any number of dashboard restarts.
 */
export async function report(
  $: Pick<EngineInterface, 'fs' | 'http' | 'session'>,
  e: SessionMeasureInput,
): Promise<void> {
  try {
    // No listening.txt means no dashboard: the read rejects, and nothing is opened.
    const [port = '', token = ''] = (await $.fs.read(listeningFile)).split('\r\n')
    if (!PORT.test(port) || Number(port) > 65535 || !TOKEN.test(token)) return

    // The answer is never read. The dashboard answers 200 with an empty body, or 401.
    await $.http.fetch(`http://127.0.0.1:${port}/usage`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Dashboard-Token': token },
      body: JSON.stringify({ sessionId: await $.session.id(), ...e }),
    })
  } catch {
    // A dashboard that is closed, slow or gone is not this session's problem.
  }
}
