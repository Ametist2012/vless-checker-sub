import { api, type Settings, type State } from './api'

type Props = { state: State; apply: (p: Promise<State>, ok?: string) => Promise<boolean> }

const METHODS: { key: keyof Settings; title: string; text: string }[] = [
  {
    key: 'check_direct_ping',
    title: 'Пинг напрямую',
    text: 'Время TCP-подключения к серверу с VPS, без прокси. Быстро и без трафика, но не доказывает, что VPN работает.',
  },
  {
    key: 'check_proxy_ping',
    title: 'Пинг через подключение',
    text: '«Реальная задержка», как в клиентах: несколько запросов через сам конфиг. Первая неудача — отбраковка.',
  },
  {
    key: 'check_sites',
    title: 'Загрузка сайтов',
    text: 'Открывает выбранные сайты через конфиг, как браузер. Не открылся ни один — конфиг не попадает в подписку.',
  },
  {
    key: 'check_speed',
    title: 'Speedtest',
    text: 'Скорость скачивания файла через конфиг у лучших по задержке. Учтите: на части серверов speedtest блокируют.',
  },
]

export function MethodsCard({ state, apply }: Props) {
  const s = state.settings
  const enabled = METHODS.filter((m) => s[m.key])
  return (
    <section className="card">
      <h2>Как проверять</h2>
      <p className="muted small">
        Баллы: 40 за стабильность и 60 поровну между отмеченными способами. Нужен хотя бы «пинг через подключение» или «загрузка сайтов» —
        иначе не проверить, что через конфиг идёт трафик. IP выхода и страна проверяются всегда.
      </p>
      <div className="methods">
        {METHODS.map((m) => (
          <label key={m.key} className={`method ${s[m.key] ? 'on' : ''}`}>
            <input
              type="checkbox"
              checked={!!s[m.key]}
              onChange={(e) => apply(api.updateSettings({ [m.key]: e.target.checked }), 'Сохранено — применится со следующей проверки')}
            />
            <span>
              <b>{m.title}</b>
              {s[m.key] && <span className="weight"> · {Math.round(60 / enabled.length)} баллов</span>}
              <span className="muted small">{m.text}</span>
            </span>
          </label>
        ))}
      </div>
    </section>
  )
}
