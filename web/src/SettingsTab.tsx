import { api, type Settings, type State } from './api'
import { AccountCard } from './AccountCard'
import { PanelAddressCard } from './PanelAddressCard'
import { RoutingCard } from './RoutingCard'
import { MethodsCard } from './MethodsCard'
import { SitesCard } from './SitesCard'
import { NumberField } from './ui'

type Props = {
  state: State
  apply: (p: Promise<State>, ok?: string) => Promise<boolean>
  login: string
  notify: (text: string, error?: boolean) => void
}

export function SettingsTab({ state, apply, login, notify }: Props) {
  const s = state.settings
  const save = (patch: Partial<Settings>) => apply(api.updateSettings(patch), 'Сохранено')

  return (
    <div className="stack">
      <section className="card">
        <h2>Расписание</h2>
        <div className="row wrap">
          <NumberField
            label="Интервал проверки, минут"
            value={s.interval_minutes}
            hint="от начала одной проверки до начала следующей; минимум 1"
            onSave={(v) => save({ interval_minutes: Math.round(v) })}
          />
        </div>
      </section>

      <section className="card">
        <h2>Доступ к подпискам</h2>
        <label className="switch">
          <input type="checkbox" checked={s.happ_only} onChange={(e) => save({ happ_only: e.target.checked })} />
          <span>Отдавать подписки только приложению Happ</span>
        </label>
        <p className="muted small">
          Проверка по User-Agent: браузер и другие клиенты получат отказ. Это мешает случайному копированию, но не заменяет секретность ссылки.
          Сменить ссылку отдельной группы можно на вкладке «Подписки».
        </p>
      </section>

      <RoutingCard state={state} apply={apply} />

      <AccountCard login={login} notify={notify} />

      <PanelAddressCard state={state} notify={notify} />

      <MethodsCard state={state} apply={apply} />

      {s.check_sites && <SitesCard state={state} apply={apply} />}

      {s.check_speed && (
        <section className="card">
          <h2>Speedtest</h2>
          <div className="row wrap">
            <NumberField label="Замерять у лучших" value={s.speed_test_top} onSave={(v) => save({ speed_test_top: Math.round(v) })} />
            <NumberField
              label="Размер файла, МБ"
              step={0.5}
              value={Math.round(s.speed_bytes / 100_000) / 10}
              onSave={(v) => save({ speed_bytes: Math.round(v * 1_000_000) })}
            />
            <NumberField label="Скорость для полных баллов, Мбит/с" value={s.speed_cap_mbps} onSave={(v) => save({ speed_cap_mbps: v })} />
          </div>
          <p className="muted small">≈ {Math.round((s.speed_test_top * s.speed_bytes) / 1e6)} МБ трафика на группу за проверку.</p>
        </section>
      )}

      <section className="card">
        <h2>Нагрузка на сервер</h2>
        <p className="muted small">Меньше значения — меньше память и процессор, но проверка идёт дольше. На VPS с 1 ГБ RAM не стоит поднимать пачку выше ~30.</p>
        <div className="row wrap">
          <NumberField label="Конфигов в одном xray" value={s.batch_size} onSave={(v) => save({ batch_size: Math.round(v) })} />
          <NumberField label="Параллельных проверок" value={s.workers} onSave={(v) => save({ workers: Math.round(v) })} />
          <NumberField label="Параллельных замеров скорости" value={s.speed_workers} onSave={(v) => save({ speed_workers: Math.round(v) })} />
        </div>
        <div className="row wrap">
          <NumberField label="Попыток на задержку" value={s.attempts} onSave={(v) => save({ attempts: Math.round(v) })} />
          <NumberField label="Таймаут запроса, с" value={s.timeout} onSave={(v) => save({ timeout: v })} />
          <NumberField label="Таймаут порта, с" value={s.tcp_timeout} onSave={(v) => save({ tcp_timeout: v })} />
          <NumberField label="Таймаут скорости, с" value={s.speed_timeout} onSave={(v) => save({ speed_timeout: v })} />
        </div>
      </section>
    </div>
  )
}
