// Разбор ссылки маршрутизации Happ для предпросмотра в панели (сервер проверяет её сам).

export type RoutingInfo =
  | { mode: 'off' }
  | { mode: 'add' | 'onadd'; profile: Record<string, unknown> }

export function parseRouting(link: string): RoutingInfo | null {
  const m = /^happ:\/\/routing\/(?:(add|onadd)\/([A-Za-z0-9+/_=-]+)|(off))$/.exec(link.trim())
  if (!m) return null
  if (m[3]) return { mode: 'off' }
  try {
    const b64 = m[2].replace(/-/g, '+').replace(/_/g, '/')
    const bytes = Uint8Array.from(atob(b64.padEnd(b64.length + ((4 - (b64.length % 4)) % 4), '=')), (c) => c.charCodeAt(0))
    const profile = JSON.parse(new TextDecoder().decode(bytes))
    return profile && typeof profile === 'object' ? { mode: m[1] as 'add' | 'onadd', profile } : null
  } catch {
    return null
  }
}

/** Значение поля профиля без учёта регистра ключа (в примерах Happ встречается и Name, и name). */
export function field(profile: Record<string, unknown>, key: string): unknown {
  const k = Object.keys(profile).find((x) => x.toLowerCase() === key.toLowerCase())
  return k === undefined ? undefined : profile[k]
}

export function count(profile: Record<string, unknown>, ...keys: string[]): number {
  return keys.reduce((n, k) => n + (Array.isArray(field(profile, k)) ? (field(profile, k) as unknown[]).length : 0), 0)
}
