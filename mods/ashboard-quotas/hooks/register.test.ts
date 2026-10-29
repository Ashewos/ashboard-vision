import type { On, SessionMeasureInput } from 'claude-code'
import { expect, test } from 'claude-code/testing'

const NOW = Date.UTC(2026, 9, 25, 15, 7, 36)

const MEASURE: SessionMeasureInput = {
  context: { tokens: 50_000, window: 1_000_000, percent: 5 },
  rateLimits: [
    { kind: 'five_hour', percentUsed: 40, resetsAt: '2026-10-25T19:00:00.000Z' },
    { kind: 'seven_day', percentUsed: 61.5, resetsAt: '2026-10-28T12:00:00.000Z' },
  ],
  changed: ['context', 'rateLimits'],
}

/** Le moteur rend les chemins avec les séparateurs du système : on compare avec des « / ». */
const slashes = (path: string) => path.replaceAll('\\', '/')

/** Les hooks posés ici tiennent la place du moteur ; rend la liste des écritures demandées. */
const engine = (on: On, env: Record<string, string | undefined>, sessionId = 'abc-123') => {
  const writes: { path: string; text: string }[] = []
  on('env.get', (_, e) => ({ value: env[e.name] }))
  on('session.id', () => ({ value: sessionId }))
  on('session.model', () => ({ value: 'claude-opus-5-5' }))
  on('clock.now', () => ({ value: NOW }))
  on('fs.write', (_, e) => {
    writes.push({ path: e.path, text: e.text })
    return { value: undefined }
  })
  on('session.measure', (_, e) => ({ changed: e.changed }))
  return writes
}

test('une mesure écrit la capture au format attendu par Ashboard Vision', async ($, on) => {
  const writes = engine(on, { USERPROFILE: 'C:/Users/alex' })

  await $.session.measure(MEASURE)

  expect(writes.length).toBe(1)
  expect(slashes(writes[0]!.path)).toBe('C:/Users/alex/.claude/ashboard-vision/statusline/abc-123.json')
  expect(JSON.parse(writes[0]!.text)).toEqual({
    capturedAt: '2026-10-25T15:07:36.000Z',
    sessionId: 'abc-123',
    modelId: 'claude-opus-5-5',
    contextWindowSize: 1_000_000,
    contextUsedPercent: 5,
    fiveHour: { usedPercent: 40, resetsAt: '2026-10-25T19:00:00.000Z' },
    sevenDay: { usedPercent: 61.5, resetsAt: '2026-10-28T12:00:00.000Z' },
  })
})

test('CLAUDE_CONFIG_DIR passe avant le profil', async ($, on) => {
  const writes = engine(on, { CLAUDE_CONFIG_DIR: 'D:/claude', USERPROFILE: 'C:/Users/alex' })

  await $.session.measure(MEASURE)

  expect(slashes(writes[0]!.path)).toBe('D:/claude/ashboard-vision/statusline/abc-123.json')
})

test('sans quotas ni remplissage connus, les champs valent null', async ($, on) => {
  const writes = engine(on, { USERPROFILE: 'C:/Users/alex' })

  await $.session.measure({ context: { window: 200_000 }, rateLimits: [], changed: ['context'] })

  const capture = JSON.parse(writes[0]!.text)
  expect(capture.contextUsedPercent).toBe(null)
  expect(capture.fiveHour).toBe(null)
  expect(capture.sevenDay).toBe(null)
})

test("un identifiant qui ne ferait pas un nom de fichier sûr n'écrit rien", async ($, on) => {
  const writes = engine(on, { USERPROFILE: 'C:/Users/alex' }, '../../evil')

  await $.session.measure(MEASURE)

  expect(writes.length).toBe(0)
})

test("une écriture refusée ne gêne pas la mesure", async ($, on) => {
  on('env.get', (_, e) => ({ value: e.name === 'USERPROFILE' ? 'C:/Users/alex' : undefined }))
  on('session.id', () => ({ value: 'abc-123' }))
  on('session.model', () => ({ value: 'claude-opus-5-5' }))
  on('clock.now', () => ({ value: NOW }))
  on('fs.write', () => {
    throw new Error('EACCES')
  })
  on('session.measure', (_, e) => ({ changed: e.changed }))

  const result = await $.session.measure(MEASURE)

  expect(result.changed).toEqual(['context', 'rateLimits'])
})
