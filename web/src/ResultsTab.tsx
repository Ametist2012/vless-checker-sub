import { useState } from 'react'
import type { State, Status } from './api'
import { countryName, flag } from './countries'
import { timeAgo } from './ui'

export function ResultsTab({ state, status }: { state: State; status: Status | null }) {
  const [current, setCurrent] = useState(state.groups[0]?.name ?? '')
  const name = state.groups.some((g) => g.name === current) ? current : state.groups[0]?.name
  const gs = name ? status?.groups[name] : undefined
  const m = state.settings
  const speed = m.check_speed || !!gs?.nodes?.some((n) => n.speed_mbps > 0)

  return (
    <div className="stack">
      <div className="chips">
        {state.groups.map((g) => (
          <button key={g.name} className={`chip ${g.name === name ? 'active' : ''}`} onClick={() => setCurrent(g.name)}>
            {g.title}
          </button>
        ))}
      </div>

      {!gs?.updated_ts ? (
        <section className="card muted">Эта группа ещё не проверялась. Первая проверка занимает несколько минут.</section>
      ) : (
        <>
          <section className="stats">
            <Stat label="в подписках" value={gs.total} />
            <Stat label="порт открыт" value={gs.tcp_alive} />
            <Stat label="принял xray" value={gs.xray_valid} />
            <Stat label="работают" value={gs.working} hint="после фильтра стран" />
            <Stat label="в подписке" value={gs.selected} accent />
            <Stat label="проверено" value={timeAgo(gs.updated_ts)} hint={`за ${gs.duration_s} с`} />
          </section>

          <section className="card table-card">
            <table>
              <thead>
                <tr>
                  <th>#</th>
                  <th>Страна</th>
                  {m.check_direct_ping && <th className="num">Пинг напрямую</th>}
                  <th className="num">{m.check_proxy_ping ? 'Пинг через конфиг' : 'Задержка'}</th>
                  {m.check_sites && <th className="num">Сайты</th>}
                  {speed && <th className="num">Скорость</th>}
                  <th className="num">Стабильность</th>
                  <th className="num">Баллы</th>
                  <th>Сервер</th>
                </tr>
              </thead>
              <tbody>
                {gs.nodes?.map((n) => (
                  <tr key={n.rank}>
                    <td className="muted">{n.rank}</td>
                    <td title={n.exit_ip}>
                      {flag(n.country)} {countryName(n.country)}
                    </td>
                    {m.check_direct_ping && (
                      <td className="num">{n.direct_ms ? `${n.direct_ms} мс` : <span className="muted">—</span>}</td>
                    )}
                    <td className="num">
                      <span className={n.latency_ms < 500 ? 'good' : n.latency_ms < 1500 ? 'ok' : 'bad'}>{n.latency_ms} мс</span>
                    </td>
                    {m.check_sites && <td className="num" title="открылось сайтов · среднее время загрузки страницы">
                      <span className={n.sites?.startsWith('0') ? 'bad' : n.sites?.split('/')[0] === n.sites?.split('/')[1] ? '' : 'ok'}>{n.sites ?? '—'}</span>
                      {n.page_ms ? <span className="muted"> · {n.page_ms} мс</span> : null}
                    </td>}
                    {speed && <td className="num">{n.speed_mbps ? `${n.speed_mbps} Мбит/с` : <span className="muted">—</span>}</td>}
                    <td className="num" title="успешных проверок из последних">
                      {n.uptime}
                    </td>
                    <td className="num">
                      <span className="score" style={{ ['--w' as string]: `${n.score}%` }}>
                        {n.score}
                      </span>
                    </td>
                    <td className="host" title={n.original_name}>
                      {n.host}:{n.port}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>
          <p className="muted small">
            Баллы: стабильность 40 (сейчас и за последние 12 часов) + 60 поровну между включёнными способами проверки (вкладка «Настройки»).
            {m.check_sites && ' Конфиги, через которые не открылся ни один сайт, в подписку не попадают.'}
          </p>
        </>
      )}
    </div>
  )
}

function Stat({ label, value, hint, accent }: { label: string; value?: number | string; hint?: string; accent?: boolean }) {
  return (
    <div className={`stat ${accent ? 'accent' : ''}`}>
      <div className="stat-value">{value ?? '—'}</div>
      <div className="stat-label">{label}</div>
      {hint && <div className="stat-hint">{hint}</div>}
    </div>
  )
}
