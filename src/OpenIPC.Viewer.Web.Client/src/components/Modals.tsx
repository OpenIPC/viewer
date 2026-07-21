import { useState, type FormEvent } from 'react'
import { useI18n } from '../i18n'

// Small reusable dialogs so the app doesn't fall back to the browser's native
// prompt()/confirm() (ugly in an embedded webview / on mobile). Styled with the
// shared .backdrop/.modal skin.

export function TextPromptModal({
  title,
  label,
  initial = '',
  submitLabel,
  onSubmit,
  onCancel,
}: {
  title: string
  label: string
  initial?: string
  submitLabel: string
  onSubmit: (value: string) => void
  onCancel: () => void
}) {
  const { t } = useI18n()
  const [value, setValue] = useState(initial)

  const submit = (e: FormEvent) => {
    e.preventDefault()
    const v = value.trim()
    if (v) onSubmit(v)
  }

  return (
    <div className="backdrop" onClick={onCancel}>
      <div className="modal" style={{ maxWidth: 380 }} onClick={(e) => e.stopPropagation()}>
        <h2>{title}</h2>
        <form onSubmit={submit} className="form-grid">
          <label>
            {label}
            <input autoFocus value={value} onChange={(e) => setValue(e.target.value)} />
          </label>
          <div className="modal-actions">
            <button type="button" onClick={onCancel}>
              {t('Common.Cancel')}
            </button>
            <button type="submit" className="primary" disabled={!value.trim()}>
              {submitLabel}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

export function ConfirmModal({
  title,
  message,
  confirmLabel,
  danger = false,
  onConfirm,
  onCancel,
}: {
  title: string
  message?: string
  confirmLabel: string
  danger?: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  const { t } = useI18n()
  return (
    <div className="backdrop" onClick={onCancel}>
      <div className="modal" style={{ maxWidth: 380 }} onClick={(e) => e.stopPropagation()}>
        <h2>{title}</h2>
        {message && <p className="muted" style={{ marginTop: 0 }}>{message}</p>}
        <div className="modal-actions">
          <button type="button" onClick={onCancel}>
            {t('Common.Cancel')}
          </button>
          <button type="button" className={danger ? 'primary danger-btn' : 'primary'} onClick={onConfirm}>
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
