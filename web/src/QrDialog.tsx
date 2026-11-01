import QRCode from 'qrcode'
import { useEffect, useState } from 'react'
import { CopyButton } from './ui'

type Code = { label: string; hint: string; text: string }

/** QR-коды подписки: генерируются в браузере, ссылки никуда не отправляются. */
export function QrDialog({ title, codes, onClose }: { title: string; codes: Code[]; onClose: () => void }) {
  const [active, setActive] = useState(0)
  const [svg, setSvg] = useState('')
  const code = codes[active]

  useEffect(() => {
    // длинные happ://crypt5-ссылки влезают только с минимальной коррекцией ошибок
    QRCode.toString(code.text, { type: 'svg', errorCorrectionLevel: code.text.length > 300 ? 'L' : 'M', margin: 2 })
      .then(setSvg, () => setSvg(''))
  }, [code.text])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    addEventListener('keydown', onKey)
    return () => removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="modal-back" onClick={onClose}>
      <div className="modal card" role="dialog" aria-label={`QR-код: ${title}`} onClick={(e) => e.stopPropagation()}>
        <div className="card-head">
          <h2>{title}</h2>
          <button className="icon-btn" onClick={onClose} title="Закрыть">
            ✕
          </button>
        </div>
        {codes.length > 1 && (
          <div className="chips">
            {codes.map((c, i) => (
              <button key={c.label} className={`chip ${i === active ? 'active' : ''}`} onClick={() => setActive(i)}>
                {c.label}
              </button>
            ))}
          </div>
        )}
        {/* белая подложка: на тёмной теме камеры хуже читают инвертированный код */}
        <div className="qr" dangerouslySetInnerHTML={{ __html: svg }} />
        <p className="muted small">{code.hint}</p>
        <CopyButton text={code.text} label="Копировать ссылку" />
      </div>
    </div>
  )
}
