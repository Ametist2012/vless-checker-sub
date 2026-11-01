import { useCallback, useEffect, useState } from 'react'
import { api, authEvents, type Session, type State, type Status } from './api'
import { CountriesTab } from './CountriesTab'
import { LoginPage } from './LoginPage'
import { ResultsTab } from './ResultsTab'
import { SettingsTab } from './SettingsTab'
import { SubscriptionsTab } from './SubscriptionsTab'
import { timeAgo, timeIn } from './ui'

const TABS = [
  ['subs', 'Подписки'],
  ['results', 'Результаты'],
  ['countries', 'Страны'],
  ['settings', 'Настройки'],
] as const
type Tab = (typeof TABS)[number][0]

function initialTab(): Tab {
  const h = location.hash.slice(1)
  return TABS.some(([id]) => id === h) ? (h as Tab) : 'subs'
}

export default function App() {
  const [session, setSession] = useState<Session | null>(null)
  const [error, setError] = useState('')

  const check = useCallback(() => {
    api.session().then(setSession, (e: Error) => setError(e.message))
  }, [])

  useEffect(() => {
    check()
    const onLogout = () => setSession({ authenticated: false, login: null })
    authEvents.addEventListener('logout', onLogout)
    return () => authEvents.removeEventListener('logout', onLogout)
  }, [check])

  if (!session) return <div className="center muted">{error ? `Ошибка: ${error}` : ''}</div>
  if (!session.authenticated) return <LoginPage onLogin={check} />
  return (
    <Panel
      login={session.login ?? 'admin'}
      onLogout={() => api.logout().finally(() => setSession({ authenticated: false, login: null }))}
    />
  )
}

function Panel({ login, onLogout }: { login: string; onLogout: () => void }) {
  const [tab, setTab] = useState<Tab>(initialTab)

  // название сервиса показываем только после входа
  useEffect(() => {
    document.title = 'VLESS Checker'
    return () => {
      document.title = 'Вход'
    }
  }, [])
  const [state, setState] = useState<State | null>(null)
  const [status, setStatus] = useState<Status | null>(null)
  const [toast, setToast] = useState<{ text: string; error?: boolean } | null>(null)
  const [loadError, setLoadError] = useState('')

  const notify = useCallback((text: string, error = false) => {
    setToast({ text, error })
    setTimeout(() => setToast(null), error ? 6000 : 2500)
  }, [])

  /** Выполнить изменение, принять новое состояние и показать результат. */
  const apply = useCallback(
    async (p: Promise<State>, ok?: string) => {
      try {
        setState(await p)
        if (ok) notify(ok)
        api.status().then(setStatus, () => {})
        return true
      } catch (e) {
        notify((e as Error).message, true)
        return false
      }
    },
    [notify],
  )

  useEffect(() => {
    api.state().then(setState, (e: Error) => setLoadError(e.message))
  }, [])

  const running = !!status?.progress.running
  useEffect(() => {
    let alive = true
    const poll = () => api.status().then((s) => alive && setStatus(s), () => {})
    poll()
    const id = setInterval(poll, running ? 2000 : 10000)
    return () => {
      alive = false
      clearInterval(id)
    }
  }, [running])

  useEffect(() => {
    history.replaceState(null, '', `#${tab}`)
  }, [tab])

  useEffect(() => {
    const onHash = () => setTab(initialTab())
    addEventListener('hashchange', onHash)
    return () => removeEventListener('hashchange', onHash)
  }, [])

  if (!state)
    return <div className="center muted">{loadError ? `Не удалось загрузить панель: ${loadError}` : 'Загрузка…'}</div>

  const p = status?.progress ?? {}
  const pct = p.total ? Math.round(((p.done ?? 0) / p.total) * 100) : 0
  const groupTitle = state.groups.find((g) => g.name === p.group)?.title ?? p.group
  const enabled = state.settings.enabled

  const togglePower = () => {
    if (enabled) {
      if (!confirm('Выключить сервис?\n\nПроверки остановятся (идущая прервётся), подписки перестанут отдаваться всем пользователям. Панель останется доступной.'))
        return
      apply(api.updateSettings({ enabled: false }), 'Сервис выключен')
    } else apply(api.updateSettings({ enabled: true }), 'Сервис включён — запускаю проверку')
  }

  return (
    <div className="app">
      <header className={`top ${enabled ? '' : 'off'}`}>
        <div className="brand">
          <img src="./favicon.svg" alt="" width={28} height={28} />
          <div>
            <h1>VLESS Checker</h1>
            <span className="muted small">
              {!enabled ? (
                <span className="off-text">Сервис выключен: проверки остановлены, подписки не отдаются</span>
              ) : running ? (
                <>
                  Проверка: {groupTitle} — {p.stage}
                  {p.total ? ` ${p.done}/${p.total}` : ''}
                </>
              ) : (
                <>
                  Последняя проверка {timeAgo(p.last_run)} · следующая {timeIn(p.next_run)}
                </>
              )}
            </span>
          </div>
        </div>
        <div className="row top-actions">
          <button className={`btn ${enabled ? 'danger' : 'primary'}`} onClick={togglePower}>
            {enabled ? 'Выключить' : 'Включить сервис'}
          </button>
          <button className="btn" onClick={onLogout} title={`Выйти (${login})`}>
            Выйти
          </button>
          {enabled && (
            <button className="btn primary" disabled={running} onClick={() => apply(api.run().then(api.state), 'Проверка запущена')}>
              {running ? 'Идёт проверка…' : 'Проверить сейчас'}
            </button>
          )}
        </div>
        {running && enabled && (
          <div className="progress">
            <div style={{ width: `${pct}%` }} />
          </div>
        )}
      </header>

      <nav className="tabs">
        {TABS.map(([id, label]) => (
          <button key={id} className={tab === id ? 'active' : ''} onClick={() => setTab(id)}>
            {label}
          </button>
        ))}
      </nav>

      <main>
        {tab === 'subs' && <SubscriptionsTab state={state} status={status} apply={apply} />}
        {tab === 'results' && <ResultsTab state={state} status={status} />}
        {tab === 'countries' && <CountriesTab state={state} status={status} apply={apply} />}
        {tab === 'settings' && <SettingsTab state={state} apply={apply} login={login} notify={notify} />}
      </main>

      {toast && <div className={`toast ${toast.error ? 'error' : ''}`}>{toast.text}</div>}
    </div>
  )
}
