import { useState, type FormEvent } from 'react'
import { api } from './api'

export function AccountCard({ login, notify }: { login: string; notify: (text: string, error?: boolean) => void }) {
  const [newLogin, setNewLogin] = useState(login)
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [repeat, setRepeat] = useState('')

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (next !== repeat) return notify('Новые пароли не совпадают', true)
    try {
      await api.changeCredentials(current, newLogin, next)
      setCurrent('')
      setNext('')
      setRepeat('')
      notify('Вход изменён — на других устройствах нужно войти заново')
    } catch (err) {
      notify((err as Error).message, true)
    }
  }

  return (
    <section className="card">
      <h2>Вход в панель</h2>
      <p className="muted small">После смены все остальные устройства выйдут из панели. Пароль — не короче 8 символов.</p>
      <form className="row wrap" onSubmit={submit}>
        <label className="field">
          <span className="field-label">Логин</span>
          <input autoComplete="username" value={newLogin} onChange={(e) => setNewLogin(e.target.value)} required />
        </label>
        <label className="field">
          <span className="field-label">Текущий пароль</span>
          <input type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />
        </label>
        <label className="field">
          <span className="field-label">Новый пароль</span>
          <input type="password" autoComplete="new-password" minLength={8} value={next} onChange={(e) => setNext(e.target.value)} required />
        </label>
        <label className="field">
          <span className="field-label">Повторите</span>
          <input type="password" autoComplete="new-password" minLength={8} value={repeat} onChange={(e) => setRepeat(e.target.value)} required />
        </label>
        <button className="btn primary align-end">Сменить</button>
      </form>
    </section>
  )
}
