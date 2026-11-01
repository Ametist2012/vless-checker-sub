import { useState } from 'react'

export function CopyButton({ text, label = 'Копировать' }: { text: string; label?: string }) {
  const [done, setDone] = useState(false)
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
    } catch {
      // без https clipboard API недоступен — копируем через скрытое поле
      const ta = Object.assign(document.createElement('textarea'), { value: text })
      document.body.append(ta)
      ta.select()
      document.execCommand('copy')
      ta.remove()
    }
    setDone(true)
    setTimeout(() => setDone(false), 1500)
  }
  return (
    <button type="button" className="btn small" onClick={copy}>
      {done ? 'Скопировано' : label}
    </button>
  )
}

export function NumberField({
  label,
  value,
  onSave,
  hint,
  step = 1,
}: {
  label: string
  value: number
  onSave: (v: number) => void
  hint?: string
  step?: number
}) {
  const [draft, setDraft] = useState<string | null>(null)
  const commit = () => {
    if (draft === null) return
    const v = Number(draft)
    setDraft(null)
    if (Number.isFinite(v) && v > 0 && v !== value) onSave(v)
  }
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      <input
        type="number"
        min={0}
        step={step}
        value={draft ?? value}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => e.key === 'Enter' && commit()}
      />
      {hint && <span className="field-hint">{hint}</span>}
    </label>
  )
}

export function timeAgo(ts?: number): string {
  if (!ts) return 'ещё не было'
  const s = Math.round(Date.now() / 1000 - ts)
  if (s < 60) return 'только что'
  if (s < 3600) return `${Math.round(s / 60)} мин назад`
  if (s < 86400) return `${Math.round(s / 3600)} ч назад`
  return new Date(ts * 1000).toLocaleString('ru')
}

export function timeIn(ts?: number): string {
  if (!ts) return '—'
  const s = Math.round(ts - Date.now() / 1000)
  if (s <= 0) return 'вот-вот'
  if (s < 60) return `через ${s} с`
  return `через ${Math.round(s / 60)} мин`
}
