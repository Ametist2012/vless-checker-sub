import { useState, type FormEvent } from 'react'
import { api } from './api'

export function LoginPage({ onLogin }: { onLogin: () => void }) {
  const [login, setLogin] = useState('')
  const [password, setPassword] = useState('')
  const [show, setShow] = useState(false)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError('')
    try {
      await api.login(login, password)
      onLogin()
    } catch (err) {
      setError((err as Error).message)
      setPassword('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-wrap">
      <form className="login card" onSubmit={submit}>
        <h1>Вход</h1>

        <label className="field wide">
          <span className="field-label">Логин</span>
          <input name="username" autoComplete="username" autoFocus value={login} onChange={(e) => setLogin(e.target.value)} required />
        </label>
        <label className="field wide">
          <span className="field-label">Пароль</span>
          <div className="password">
            <input
              name="password"
              type={show ? 'text' : 'password'}
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
            />
            <button type="button" className="icon-btn" onClick={() => setShow(!show)} title={show ? 'Скрыть' : 'Показать'}>
              {show ? '🙈' : '👁'}
            </button>
          </div>
        </label>

        {error && <p className="login-error">{error}</p>}

        <button className="btn primary wide" disabled={busy || !login.trim() || !password}>
          {busy ? 'Вход…' : 'Войти'}
        </button>
      </form>
    </div>
  )
}
