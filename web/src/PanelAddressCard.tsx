import { useState } from 'react'
import { api, subsBase, type State } from './api'
import { CopyButton } from './ui'

type Props = { state: State; notify: (text: string, error?: boolean) => void }

function randomPath(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(18))
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export function PanelAddressCard({ state, notify }: Props) {
  const current = state.settings.panel_path
  const [value, setValue] = useState(current)
  const valid = /^[A-Za-z0-9_-]{4,64}$/.test(value) && value !== 'sub' && value !== 'api'
  const url = (path: string) => `${subsBase()}/${path}/`

  const save = async () => {
    if (!confirm(`Перенести панель на новый адрес?\n\n${url(value)}\n\nСтарый адрес сразу перестанет открываться. Сохраните новый в закладки. Подписки не изменятся.`))
      return
    try {
      await api.updateSettings({ panel_path: value })
      location.href = url(value) + '#settings'
    } catch (e) {
      notify((e as Error).message, true)
    }
  }

  return (
    <section className="card">
      <h2>Адрес панели</h2>
      <p className="muted small">
        Секретная часть адреса. Без неё по адресу сервера видна только пустая страница с ошибкой 404. Ссылки на подписки от неё не
        зависят и после смены продолжат работать.
      </p>
      <div className="link-row">
        <code className="link">{url(current)}</code>
        <CopyButton text={url(current)} />
      </div>
      <div className="row wrap actions">
        <label className="field title-field">
          <span className="field-label">Секрет (4–64 символа: латиница, цифры, - и _)</span>
          <input className="mono-input" value={value} onChange={(e) => setValue(e.target.value.trim())} />
        </label>
        <button className="btn align-end" onClick={() => setValue(randomPath())}>
          Сгенерировать
        </button>
        <button className="btn primary align-end" disabled={!valid || value === current} onClick={save}>
          Сменить адрес
        </button>
      </div>
      {value.length > 0 && value.length < 12 && <p className="muted small">Короткий секрет проще угадать — лучше 16+ символов.</p>}
    </section>
  )
}
