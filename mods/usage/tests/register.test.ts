import { describe, expect, mock, test } from 'claude-code/testing'
import type { HttpInit, HttpResponse, On, SessionMeasureInput } from 'claude-code'

import { listeningFile } from '../hooks/listening-file'
import { report } from '../hooks/register'

const TOKEN = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO-_' // 43 characters, as the dashboard makes them
const SESSION = '3f6c1d1e-0000-4000-8000-000000000001'
const LISTENING = `52789\r\n${TOKEN}`

const MEASURE: SessionMeasureInput = {
  context: { window: 200000, tokens: 41200, percent: 21 },
  rateLimits: [
    { kind: 'five_hour', percentUsed: 17, resetsAt: '2026-10-08T18:10:00.000Z' },
    { kind: 'seven_day', percentUsed: 8.5, resetsAt: '2026-10-14T13:00:00.000Z' },
  ],
  cost: { usd: 0.42 },
  changed: ['rateLimits', 'cost'],
}

type Post = { url: string; init?: HttpInit }
type Dollar = Parameters<typeof report>[0]

const OK: HttpResponse = { status: 200, ok: true, headers: {}, text: '' }
const REFUSED: HttpResponse = { status: 401, ok: false, headers: {}, text: '' }
const never = <T>() => new Promise<T>(() => {})

/**
 * A `$` of plain functions, for calling `report` directly. Each call is counted as it is made,
 * so "nothing was sent" is known the moment `report` resolves.
 */
function fake(listening: string | Error, answer: () => Promise<HttpResponse> = async () => OK) {
  const posts: Post[] = []
  const reads: string[] = []
  const $ = {
    fs: {
      read: async (path: string) => {
        reads.push(path)
        if (listening instanceof Error) throw listening
        return listening
      },
    },
    session: { id: async () => SESSION },
    http: {
      fetch: (url: string, init?: HttpInit) => {
        posts.push({ url, init })
        return answer()
      },
    },
  } as unknown as Dollar
  return { $, posts, reads }
}

test('the token these tests use is the length the dashboard makes', () => {
  // Every case below leans on this: a 44-character "good" token would fail them for the wrong reason.
  expect(TOKEN.length).toBe(43)
})

describe('report, with a dashboard listening', () => {
  test('the figures go to its port, with its token', async () => {
    const { $, posts, reads } = fake(LISTENING)

    await report($, MEASURE)

    expect(reads).toEqual([listeningFile])
    expect(posts.length).toBe(1)
    expect(posts[0]?.url).toBe('http://127.0.0.1:52789/usage')
    expect(posts[0]?.init?.method).toBe('POST')
    expect(posts[0]?.init?.headers).toEqual({
      'Content-Type': 'application/json',
      'X-Dashboard-Token': TOKEN,
    })
    expect(JSON.parse(posts[0]?.init?.body ?? '')).toEqual({ sessionId: SESSION, ...MEASURE })
  })

  test('a line ending after the token is still the token', async () => {
    const { $, posts } = fake(`${LISTENING}\r\n`)

    await report($, MEASURE)

    expect(posts.length).toBe(1)
  })

  test('listening.txt is read again at every call', async () => {
    const { $, reads } = fake(LISTENING)

    await report($, MEASURE)
    await report($, MEASURE)

    expect(reads.length).toBe(2)
  })

  test('a dashboard that refuses the token throws nothing', async () => {
    const { $, posts } = fake(LISTENING, async () => REFUSED)

    await report($, MEASURE)

    expect(posts.length).toBe(1)
  })

  test('a dashboard that is gone throws nothing', async () => {
    const { $, posts } = fake(LISTENING, async () => {
      throw new Error('ECONNREFUSED')
    })

    await report($, MEASURE)

    expect(posts.length).toBe(1)
  })
})

describe('report, with no dashboard listening', () => {
  test('with no listening.txt nothing is opened, and nothing is thrown', async () => {
    const { $, posts, reads } = fake(new Error('ENOENT: no such file or directory'))

    await report($, MEASURE)

    expect(reads.length).toBe(1)
    expect(posts).toEqual([])
  })

  test('a session that cannot say its id sends nothing, and nothing is thrown', async () => {
    const { $, posts } = fake(LISTENING)
    $.session.id = async () => {
      throw new Error('no session bound')
    }

    await report($, MEASURE)

    expect(posts).toEqual([])
  })

  test('a post that cannot even be started throws nothing', async () => {
    const { $ } = fake(LISTENING)
    $.http.fetch = () => {
      throw new Error('refused by policy')
    }

    await report($, MEASURE)
  })
})

describe('report, with a listening.txt the dashboard did not write', () => {
  const bad: Record<string, string> = {
    'an empty file': '',
    'a port alone': '52789',
    'port zero': `0\r\n${TOKEN}`,
    'a port above 65535': `65536\r\n${TOKEN}`,
    'a port of six digits': `527890\r\n${TOKEN}`,
    'a port with a leading zero': `05278\r\n${TOKEN}`,
    'a port with a sign': `+52789\r\n${TOKEN}`,
    'a port with a space': `52789 \r\n${TOKEN}`,
    'a port that is not a number': `5278x\r\n${TOKEN}`,
    'a host in place of a port': `evil.example:80\r\n${TOKEN}`,
    'a token of 42 characters': `52789\r\n${TOKEN.slice(1)}`,
    'a token of 44 characters': `52789\r\n${TOKEN}A`,
    'a token with a character outside the set': `52789\r\n${TOKEN.slice(1)}+`,
    'a token with a line break inside': `52789\r\n${TOKEN.slice(0, 20)}\n${TOKEN.slice(21)}`,
    'LF line endings': `52789\n${TOKEN}`,
  }

  for (const [what, listening] of Object.entries(bad)) {
    test(`${what}: nothing is sent`, async () => {
      const { $, posts } = fake(listening)

      await report($, MEASURE)

      expect(posts).toEqual([])
    })
  }
})

/**
 * The same, through the engine: the mod loaded as it ships, and the test's hooks standing where
 * the clock, listening.txt, the dashboard, the session and the engine's own measure would be.
 */
function world(on: On, listening: string | undefined, answer: () => Promise<HttpResponse> = async () => OK) {
  const clock = mock.clock(on)
  const measured: SessionMeasureInput[] = []
  const reads: string[] = []
  const posts: Post[] = []

  on('fs.read', ($, e) => {
    reads.push(e.path)
    return listening === undefined ? { deny: 'ENOENT: no such file' } : { value: listening }
  })
  on('session.id', () => ({ value: SESSION }))
  on('http.fetch', async ($, e) => {
    posts.push({ url: e.url, init: e.init })
    return { value: await answer() }
  })
  on('session.measure', ($, e) => {
    measured.push(e)
    return { changed: e.changed }
  })

  return { clock, measured, reads, posts }
}

describe('register', () => {
  test('a measurement reaches the dashboard, and the event goes on as it came', async ($, on) => {
    const { clock, measured, posts } = world(on, LISTENING)

    const result = await $.session.measure(MEASURE)
    await clock.settle()

    expect(posts.length).toBe(1)
    expect(posts[0]?.url).toBe('http://127.0.0.1:52789/usage')
    expect(JSON.parse(posts[0]?.init?.body ?? '')).toEqual({ sessionId: SESSION, ...MEASURE })
    // It only observes: the engine's own measure got the event as it was raised, and answered.
    expect(measured).toEqual([MEASURE])
    expect(result).toEqual({ changed: ['rateLimits', 'cost'] })
  })

  test('nothing is read or sent inside the event: the post starts from a timer', async ($, on) => {
    const { clock, reads, posts } = world(on, LISTENING)

    await $.session.measure(MEASURE)

    // The event has been answered and the timer has not run: the mocked clock has not moved.
    expect(reads).toEqual([])
    expect(posts).toEqual([])

    await clock.settle()

    // Through the engine a relative path arrives made absolute, so only the count is held here.
    // The direct test above holds the path itself.
    expect(reads.length).toBe(1)
    expect(posts.length).toBe(1)
  })

  test('a dashboard that never answers does not hold the event', async ($, on) => {
    const { clock, measured, posts } = world(on, LISTENING, never)

    const result = await $.session.measure(MEASURE)
    await clock.settle()

    expect(posts.length).toBe(1)
    expect(measured).toEqual([MEASURE])
    expect(result).toEqual({ changed: ['rateLimits', 'cost'] })
  })

  test('with no dashboard the event still goes on as it came', async ($, on) => {
    const { clock, measured, reads, posts } = world(on, undefined)

    const result = await $.session.measure(MEASURE)
    await clock.settle()

    expect(reads.length).toBe(1)
    expect(posts).toEqual([])
    expect(measured).toEqual([MEASURE])
    expect(result).toEqual({ changed: ['rateLimits', 'cost'] })
  })
})
