// Типы и вызовы API. Пути относительные — панель живёт под префиксом nginx.

export type Subscription = { id: number; url: string; created: number }

export type Group = {
  name: string
  title: string
  top_n: number
  max_per_host: number
  token: string
  subscriptions: Subscription[]
}

export type Settings = {
  enabled: boolean
  panel_path: string
  interval_minutes: number
  exclude_countries: string[]
  happ_only: boolean
  routing_link: string
  routing_in_subscription: boolean
  latency_url: string
  trace_url: string
  site_urls: string[]
  site_timeout: number
  check_direct_ping: boolean
  check_proxy_ping: boolean
  check_sites: boolean
  check_speed: boolean
  speed_cap_mbps: number
  speed_bytes: number
  attempts: number
  timeout: number
  tcp_timeout: number
  batch_size: number
  workers: number
  speed_test_top: number
  speed_workers: number
  speed_timeout: number
  happ_links: Record<string, { url: string; link: string }>
}

export type State = { groups: Group[]; settings: Settings }

export type NodeReport = {
  rank: number
  name: string
  score: number
  host: string
  port: number
  latency_ms: number
  ok_ratio: number
  speed_mbps: number
  sites: string
  page_ms: number
  direct_ms: number
  exit_ip: string
  country: string
  uptime: string
  original_name: string
}

export type GroupStatus = Partial<{
  updated_ts: number
  duration_s: number
  total: number
  tcp_alive: number
  xray_valid: number
  working: number
  selected: number
  countries: Record<string, number>
  nodes: NodeReport[]
}>

export type Progress = Partial<{
  running: boolean
  group: string | null
  stage: string | null
  done: number
  total: number
  started: number
  last_run: number
  next_run: number
  queue: string[]
}>

export type Status = { progress: Progress; groups: Record<string, GroupStatus> }

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(path, {
    method,
    headers: method === 'GET' ? {} : { 'Content-Type': 'application/json', 'X-Panel': '1' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  const text = await res.text()
  const data = text ? JSON.parse(text) : {}
  if (res.status === 401 && path !== 'api/login') authEvents.dispatchEvent(new Event('logout'))
  if (!res.ok) throw new Error(data.error ?? `ошибка ${res.status}`)
  return data as T
}

/** Срабатывает, когда сессия истекла или вход сброшен — App показывает страницу входа. */
export const authEvents = new EventTarget()

export type Session = { authenticated: boolean; login: string | null }

export const api = {
  session: () => call<Session>('GET', 'api/session'),
  login: (login: string, password: string) => call<Session>('POST', 'api/login', { login, password }),
  logout: () => call<Session>('POST', 'api/logout'),
  changeCredentials: (current_password: string, login: string, new_password: string) =>
    call<Session>('POST', 'api/credentials', { current_password, login, new_password }),
  state: () => call<State>('GET', 'api/state'),
  status: () => call<Status>('GET', 'api/status'),
  updateSettings: (patch: Partial<Settings>) => call<State>('PATCH', 'api/settings', patch),
  addGroup: (name: string, title: string) => call<State>('POST', 'api/groups', { name, title }),
  updateGroup: (name: string, patch: Partial<Pick<Group, 'title' | 'top_n' | 'max_per_host'>>) =>
    call<State>('PATCH', `api/groups/${name}`, patch),
  deleteGroup: (name: string) => call<State>('DELETE', `api/groups/${name}`),
  addSubscriptions: (group: string, urls: string[]) =>
    call<State & { added: number }>('POST', `api/groups/${group}/subscriptions`, { urls }),
  deleteSubscription: (id: number) => call<State>('DELETE', `api/subscriptions/${id}`),
  run: (group = '*') => call<{ ok: boolean }>('POST', 'api/run', { group }),
  rotateToken: (group: string) => call<State>('POST', `api/groups/${group}/rotate-token`),
  happLink: (group: string) => call<State>('POST', `api/groups/${group}/happ-link`, { base: subsBase() }),
}

/** Адрес панели без хвоста — от него строятся ссылки на подписки. */
export function panelBase(): string {
  const path = location.pathname.endsWith('/') ? location.pathname : location.pathname.replace(/[^/]*$/, '')
  return location.origin + path.replace(/\/$/, '')
}

/** Подписки живут уровнем выше секретного адреса панели: …/checker/sub/…, — смена адреса панели их не ломает. */
export function subsBase(): string {
  return panelBase().replace(/\/[^/]+$/, '')
}

export function subUrl(token: string, group: string): string {
  return `${subsBase()}/sub/${token}/${group}`
}
