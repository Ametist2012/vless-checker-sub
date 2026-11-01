import { useState } from 'react'
import { api, subUrl, type Group, type State, type Status } from './api'
import { QrDialog } from './QrDialog'
import { CopyButton, NumberField, timeAgo } from './ui'

type Props = {
  state: State
  status: Status | null
  apply: (p: Promise<State>, ok?: string) => Promise<boolean>
}

export function SubscriptionsTab({ state, status, apply }: Props) {
  const [newName, setNewName] = useState('')
  const [newTitle, setNewTitle] = useState('')

  const addGroup = async () => {
    if (await apply(api.addGroup(newName.trim(), newTitle.trim()), 'Группа создана')) {
      setNewName('')
      setNewTitle('')
    }
  }

  return (
    <div className="stack">
      {state.groups.map((g) => (
        <GroupCard key={g.name} group={g} state={state} status={status} apply={apply} />
      ))}

      <section className="card">
        <h2>Новая группа</h2>
        <p className="muted">Отдельная подписка со своими источниками, например «для работы» или «мобильные».</p>
        <div className="row wrap">
          <input placeholder="имя в адресе: mobile" value={newName} onChange={(e) => setNewName(e.target.value.toLowerCase())} />
          <input placeholder="название: 📱 Мобильные" value={newTitle} onChange={(e) => setNewTitle(e.target.value)} />
          <button className="btn primary" disabled={!newName.trim()} onClick={addGroup}>
            Создать
          </button>
        </div>
      </section>
    </div>
  )
}

function GroupCard({ group: g, state, status, apply }: { group: Group } & Props) {
  const [urls, setUrls] = useState('')
  const [busy, setBusy] = useState(false)
  const [qr, setQr] = useState(false)
  const url = subUrl(g.token, g.name)
  const happ = state.settings.happ_links[g.name]
  const happStale = happ && happ.url !== url
  const gs = status?.groups[g.name]

  const addUrls = async () => {
    const list = urls.split(/\s+/).filter(Boolean)
    if (await apply(api.addSubscriptions(g.name, list), 'Источники добавлены')) setUrls('')
  }

  const makeHapp = async () => {
    setBusy(true)
    await apply(api.happLink(g.name), 'Ссылка для Happ готова')
    setBusy(false)
  }

  return (
    <section className="card">
      <div className="card-head">
        <div>
          <h2>{g.title}</h2>
          <span className="muted">
            {g.name} · проверено {timeAgo(gs?.updated_ts)}
            {gs?.selected !== undefined && ` · в подписке ${gs.selected} из ${gs.working} рабочих`}
          </span>
        </div>
        <div className="row">
          {state.settings.enabled && (
            <button className="btn" onClick={() => apply(api.run(g.name).then(api.state), 'Проверка запущена')}>
              Проверить
            </button>
          )}
          <button
            className="btn danger"
            onClick={() => confirm(`Удалить группу «${g.title}» со всеми источниками?`) && apply(api.deleteGroup(g.name), 'Группа удалена')}
          >
            Удалить
          </button>
        </div>
      </div>

      <div className="links">
        <div className="link-row">
          <span className="link-label">Happ</span>
          {happ && !happStale ? (
            <>
              <code className="link">{happ.link.slice(0, 48)}…</code>
              <CopyButton text={happ.link} />
            </>
          ) : (
            <span className="muted">{happStale ? 'адрес изменился — создайте заново' : 'ещё не создана'}</span>
          )}
          <button className="btn small" disabled={busy} onClick={makeHapp}>
            {busy ? 'Создаю…' : happ ? 'Пересоздать' : 'Создать ссылку для Happ'}
          </button>
          <button className="btn small" onClick={() => setQr(true)}>
            QR-код
          </button>
        </div>
        <div className="link-row">
          <span className="link-label">Прямая</span>
          <code className="link">{url}</code>
          <CopyButton text={url} />
        </div>
        <div className="link-foot">
          <span className="muted small">
            {state.settings.happ_only ? 'Включено «только Happ»: другие приложения и браузер получат отказ. ' : ''}
            Ссылка попала не тому человеку — смените её.
          </span>
          <button
            className="btn small danger"
            onClick={() =>
              confirm(
                `Сменить ссылку подписки «${g.title}»?\n\nСтарая ссылка (и Happ-ссылка) сразу перестанет работать — у всех, кто ею пользуется. Новую нужно будет раздать заново.`,
              ) && apply(api.rotateToken(g.name), 'Ссылка сменена — создайте новую ссылку для Happ')
            }
          >
            Сменить ссылку
          </button>
        </div>
      </div>

      <div className="row wrap">
        <label className="field title-field">
          <span className="field-label">Название (видно в Happ)</span>
          <input
            defaultValue={g.title}
            onBlur={(e) =>
              e.target.value.trim() && e.target.value !== g.title && apply(api.updateGroup(g.name, { title: e.target.value.trim() }), 'Сохранено')
            }
          />
        </label>
        <NumberField label="Конфигов в подписке" value={g.top_n} onSave={(v) => apply(api.updateGroup(g.name, { top_n: v }), 'Сохранено')} />
        <NumberField
          label="Максимум на сервер"
          value={g.max_per_host}
          hint="если не хватает — добирается сверх лимита"
          onSave={(v) => apply(api.updateGroup(g.name, { max_per_host: v }), 'Сохранено')}
        />
      </div>

      <h3>Источники ({g.subscriptions.length})</h3>
      <ul className="sources">
        {g.subscriptions.map((s) => (
          <li key={s.id}>
            <a href={s.url} target="_blank" rel="noreferrer noopener">
              {s.url}
            </a>
            <button className="icon-btn" title="Удалить" onClick={() => apply(api.deleteSubscription(s.id), 'Источник удалён')}>
              ✕
            </button>
          </li>
        ))}
        {g.subscriptions.length === 0 && <li className="muted">Источников нет — добавьте ссылку ниже.</li>}
      </ul>
      <div className="row wrap">
        <textarea
          rows={2}
          placeholder="https://… — можно несколько ссылок, по одной на строку"
          value={urls}
          onChange={(e) => setUrls(e.target.value)}
        />
        <button className="btn primary" disabled={!urls.trim()} onClick={addUrls}>
          Добавить
        </button>
      </div>
      {qr && (
        <QrDialog
          title={g.title}
          onClose={() => setQr(false)}
          codes={[
            ...(happ && !happStale
              ? [{ label: 'Happ (зашифрованная)', hint: 'Отсканируйте камерой телефона или в Happ: «+» → «Сканировать QR». Адрес подписки не виден.', text: happ.link }]
              : []),
            { label: 'Прямая ссылка', hint: 'Отсканируйте в Happ: «+» → «Сканировать QR». Адрес подписки виден тому, кто сканирует.', text: url },
          ]}
        />
      )}
    </section>
  )
}
