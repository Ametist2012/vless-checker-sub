import { useState } from 'react'
import { api, type State } from './api'
import { NumberField } from './ui'

type Props = { state: State; apply: (p: Promise<State>, ok?: string) => Promise<boolean> }

export function SitesCard({ state, apply }: Props) {
  const s = state.settings
  const [draft, setDraft] = useState<string | null>(null)
  const text = draft ?? s.site_urls.join('\n')
  const urls = text.split(/\s+/).filter(Boolean)
  const dirty = draft !== null && urls.join('\n') !== s.site_urls.join('\n')

  return (
    <section className="card">
      <h2>Проверка сайтов</h2>
      <p className="muted small">
        Через каждый рабочий конфиг загружаются эти страницы, как в браузере. Конфиги, через которые не открылся ни один сайт, в подписку не
        попадают; от доли открывшихся и скорости загрузки зависят 30 баллов из 100. От 1 до 10 сайтов, по одному на строку.
      </p>
      <textarea className="mono" rows={Math.max(3, urls.length + 1)} value={text} onChange={(e) => setDraft(e.target.value)} />
      <div className="row wrap actions">
        <NumberField
          label="Таймаут страницы, с"
          value={s.site_timeout}
          onSave={(v) => apply(api.updateSettings({ site_timeout: v }), 'Сохранено')}
        />
        <button
          className="btn primary align-end"
          disabled={!dirty}
          onClick={async () => (await apply(api.updateSettings({ site_urls: urls }), 'Список сайтов сохранён')) && setDraft(null)}
        >
          Сохранить сайты
        </button>
      </div>
    </section>
  )
}
