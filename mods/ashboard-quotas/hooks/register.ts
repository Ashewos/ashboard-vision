import type { Register, SessionContextUsage, SessionRateLimit } from 'claude-code'

// Un fichier par session, lu par src/Trofeo.Modules.Agents/Claude/StatusLineFile.cs :
// <claudeHome>/ashboard-vision/statusline/<sessionId>.json, en camelCase. Le module de l'écran les lit sans savoir
// qui les a écrits. Rien n'est lu d'autre que les chiffres que le moteur a déjà ; aucune connexion réseau.

type Quota = { usedPercent: number; resetsAt: string | null }

export type Capture = {
  capturedAt: string
  sessionId: string
  modelId: string | null
  contextWindowSize: number | null
  contextUsedPercent: number | null
  fiveHour: Quota | null
  sevenDay: Quota | null
}

/** Un identifiant sûr comme nom de fichier : [A-Za-z0-9-], 1 à 64 caractères (comme StatusLineFile.IsValidSessionId). */
export const isValidSessionId = (id: string): boolean => /^[A-Za-z0-9-]{1,64}$/.test(id)

/** `<claudeHome>` : %CLAUDE_CONFIG_DIR% s'il est défini, sinon le dossier .claude du profil. */
export const claudeHome = (configDir: string | undefined, profile: string | undefined): string | null => {
  if (configDir) return configDir
  return profile ? `${profile}/.claude` : null
}

const quota = (limits: readonly SessionRateLimit[], kind: string): Quota | null => {
  const limit = limits.find(l => l.kind === kind)
  return limit ? { usedPercent: limit.percentUsed, resetsAt: limit.resetsAt ?? null } : null
}

/** La capture d'une mesure : `null` quand l'identifiant ne ferait pas un nom de fichier sûr. */
export const toCapture = (
  now: number,
  sessionId: string,
  model: string | null,
  context: SessionContextUsage,
  rateLimits: readonly SessionRateLimit[],
): Capture | null =>
  isValidSessionId(sessionId)
    ? {
        capturedAt: new Date(now).toISOString(),
        sessionId,
        modelId: model,
        contextWindowSize: context.window > 0 ? context.window : null,
        contextUsedPercent: context.percent ?? null,
        fiveHour: quota(rateLimits, 'five_hour'),
        sevenDay: quota(rateLimits, 'seven_day'),
      }
    : null

export const register: Register = on => {
  // Chaque mesure (après un tour, ou quand un quota bouge d'un point) réécrit la capture de la session. Une panne
  // d'écriture ne doit jamais gêner la session : elle est ignorée, la mesure suivante réessaiera.
  on('session.measure', async ($, e, next) => {
    try {
      const home = claudeHome(await $.env.get('CLAUDE_CONFIG_DIR'), (await $.env.get('USERPROFILE')) ?? (await $.env.get('HOME')))
      const model = await $.session.model().catch(() => null)
      const capture = toCapture(await $.clock.now(), await $.session.id(), model || null, e.context, e.rateLimits)
      if (home && capture) {
        await $.fs.write(`${home}/ashboard-vision/statusline/${capture.sessionId}.json`, JSON.stringify(capture))
      }
    } catch {
      // Disque plein, dossier protégé… : pas de capture cette fois-ci.
    }

    return next(e)
  })
}
