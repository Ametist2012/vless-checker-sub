import { useState } from 'react'
import { api, type State } from './api'
import { count, field, parseRouting } from './routing'
import { CopyButton } from './ui'

type Props = { state: State; apply: (p: Promise<State>, ok?: string) => Promise<boolean> }

export function RoutingCard({ state, apply }: Props) {
  const s = state.settings
  const [draft, setDraft] = useState<string | null>(null)
  const value = draft ?? s.routing_link
  const info = value.trim() ? parseRouting(value) : null
  const dirty = draft !== null && draft.trim() !== s.routing_link

  const save = async (link: string) => {
    if (await apply(api.updateSettings({ routing_link: link.trim() }), link.trim() ? 'Маршрутизация сохранена' : 'Маршрутизация убрана'))
      setDraft(null)
  }

  return (
    <section className="card">
      <h2>Маршрутизация для Happ</h2>
      <p className="muted small">
        Одна на все группы. Вставьте ссылку <code>happ://routing/onadd/…</code> (её можно собрать на{' '}
        <a href="https://routing.happ.su" target="_blank" rel="noreferrer noopener">
          routing.happ.su
        </a>
        ). <b>onadd</b> — добавить и сразу включить, <b>add</b> — только добавить, <b>off</b> — выключить маршрутизацию.
      </p>

      <textarea
        className="mono"
        rows={3}
        placeholder="happ://routing/onadd/eyJOYW1lIjoi…"
        value={value}
        onChange={(e) => setDraft(e.target.value)}
      />

      {value.trim() && <RoutingPreview link={value} />}

      <div className="row wrap actions">
        <button className="btn primary" disabled={!dirty || (!!value.trim() && !info)} onClick={() => save(value)}>
          Сохранить
        </button>
        {s.routing_link && <CopyButton text={s.routing_link} label="Копировать ссылку" />}
        {s.routing_link && (
          <button className="btn danger" onClick={() => confirm('Убрать маршрутизацию из подписок?') && save('')}>
            Убрать
          </button>
        )}
      </div>

      <label className="switch">
        <input
          type="checkbox"
          checked={s.routing_in_subscription}
          onChange={(e) => apply(api.updateSettings({ routing_in_subscription: e.target.checked }), 'Сохранено')}
        />
        <span>Добавлять маршрутизацию в подписки</span>
      </label>
      <p className="muted small">
        Happ применит правила сам при добавлении или обновлении подписки — без ожидания проверки. Выключите, если хотите раздавать ссылку
        только вручную.
      </p>
    </section>
  )
}

function RoutingPreview({ link }: { link: string }) {
  const info = parseRouting(link)
  if (!info) return <p className="preview bad">Ссылка не распознана: нужен формат happ://routing/onadd/…, add/… или off.</p>
  if (info.mode === 'off') return <p className="preview">Ссылка выключает маршрутизацию в Happ.</p>
  const p = info.profile
  const global = String(field(p, 'GlobalProxy')) !== 'false'
  return (
    <div className="preview">
      <b>{String(field(p, 'Name') ?? 'Default')}</b> · {info.mode === 'onadd' ? 'добавить и включить' : 'только добавить'}
      <div className="muted small">
        Остальной трафик: {global ? 'через VPN' : 'напрямую'} · напрямую: {count(p, 'DirectSites', 'DirectIp')} · через VPN:{' '}
        {count(p, 'ProxySites', 'ProxyIp')} · блок: {count(p, 'BlockSites', 'BlockIp')} правил
      </div>
    </div>
  )
}
