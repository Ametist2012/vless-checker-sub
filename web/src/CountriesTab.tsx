import { useMemo, useState } from 'react'
import { api, type State, type Status } from './api'
import { ALL_COUNTRIES, countryName, flag } from './countries'

type Props = { state: State; status: Status | null; apply: (p: Promise<State>, ok?: string) => Promise<boolean> }

export function CountriesTab({ state, status, apply }: Props) {
  const [query, setQuery] = useState('')
  const excluded = new Set(state.settings.exclude_countries)

  // сколько рабочих конфигов было в каждой стране при последней проверке (до фильтра), по всем группам
  const seen = useMemo(() => {
    const counts: Record<string, number> = {}
    for (const gs of Object.values(status?.groups ?? {}))
      for (const [cc, n] of Object.entries(gs.countries ?? {})) if (cc) counts[cc] = (counts[cc] ?? 0) + n
    return Object.entries(counts).sort((a, b) => b[1] - a[1])
  }, [status])

  const toggle = (code: string) => {
    const next = excluded.has(code) ? [...excluded].filter((c) => c !== code) : [...excluded, code]
    apply(api.updateSettings({ exclude_countries: next }), excluded.has(code) ? `${countryName(code)} разрешена` : `${countryName(code)} исключена`)
  }

  const q = query.trim().toLowerCase()
  const filtered = ALL_COUNTRIES.filter((c) => !q || c.name.toLowerCase().includes(q) || c.code.toLowerCase() === q)

  return (
    <div className="stack">
      <section className="card">
        <h2>Исключённые страны</h2>
        <p className="muted">
          Отмеченные страны не попадут в подписки. Страна определяется по реальному IP выхода сервера, а не по названию конфига. Изменения
          применятся при следующей проверке.
        </p>
        <div className="chips">
          {[...excluded].sort().map((c) => (
            <button key={c} className="chip excluded" onClick={() => toggle(c)} title="Разрешить">
              {flag(c)} {countryName(c)} ✕
            </button>
          ))}
          {excluded.size === 0 && <span className="muted">Ничего не исключено.</span>}
        </div>
      </section>

      {seen.length > 0 && (
        <section className="card">
          <h2>Встречались при последней проверке</h2>
          <p className="muted">Число рабочих конфигов до фильтра — видно, сколько отсекает каждая страна.</p>
          <div className="country-grid">
            {seen.map(([code, n]) => (
              <CountryBox key={code} code={code} count={n} checked={excluded.has(code)} onToggle={toggle} />
            ))}
          </div>
        </section>
      )}

      <section className="card">
        <div className="card-head">
          <h2>Все страны</h2>
          <input className="search" placeholder="Поиск: Германия или DE" value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>
        <div className="country-grid">
          {filtered.map((c) => (
            <CountryBox key={c.code} code={c.code} checked={excluded.has(c.code)} onToggle={toggle} />
          ))}
        </div>
      </section>
    </div>
  )
}

function CountryBox({ code, count, checked, onToggle }: { code: string; count?: number; checked: boolean; onToggle: (c: string) => void }) {
  return (
    <label className={`country ${checked ? 'checked' : ''}`}>
      <input type="checkbox" checked={checked} onChange={() => onToggle(code)} />
      <span>
        {flag(code)} {countryName(code)}
      </span>
      {count !== undefined && <span className="count">{count}</span>}
    </label>
  )
}
