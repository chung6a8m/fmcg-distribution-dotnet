import { useId, useLayoutEffect, useRef, useState } from 'react'
import type { ComponentPropsWithRef, ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { setThemePreference, useTheme, type ThemePreference } from './theme'

export type Density = 'compact' | 'comfortable' | 'field'
type Tone = 'success' | 'info' | 'warning' | 'danger'

type ButtonProps = ComponentPropsWithRef<'button'> & {
  variant?: 'primary' | 'secondary' | 'destructive'
}

export function Button({ variant = 'secondary', type = 'button', className, ...props }: ButtonProps) {
  return <button {...props} type={type} className={['button', 'button-' + variant, className].filter(Boolean).join(' ')} />
}

type FieldProps = ComponentPropsWithRef<'input'> & { label: string; help?: string; error?: string }

export function Field({ label, help, error, id, ...props }: FieldProps) {
  const generatedId = useId()
  const inputId = id ?? generatedId
  const descriptions = [props['aria-describedby'], help && inputId + '-help', error && inputId + '-error']
    .filter(Boolean).join(' ')
  return <div className="field">
    <label htmlFor={inputId}>{label}</label>
    <input {...props} id={inputId} aria-describedby={descriptions || undefined}
      aria-invalid={error ? true : props['aria-invalid']} />
    {help && <p className="field-help" id={inputId + '-help'}>{help}</p>}
    {error && <p className="field-error" id={inputId + '-error'}>{error}</p>}
  </div>
}

export function Badge({ tone, children }: { tone: Tone; children: ReactNode }) {
  return <span className="badge" data-tone={tone}>{children}</span>
}

export function Notice({ tone, title, children }: { tone: Tone; title: string; children: ReactNode }) {
  return <div className="notice" data-tone={tone}>
    <strong>{title}</strong>
    <p>{children}</p>
  </div>
}

export function ThemeSelect() {
  const id = useId()
  const { preference, persistent } = useTheme()
  const [attempted, setAttempted] = useState(false)
  const unsaved = attempted && !persistent
  return <div className="field">
    <label htmlFor={id}>Chế độ hiển thị</label>
    <select id={id} value={preference} aria-describedby={unsaved ? id + '-help' : undefined}
      onChange={event => {
        setAttempted(true)
        setThemePreference(event.currentTarget.value as ThemePreference)
      }}>
      <option value="light">Light</option>
      <option value="dark">Dark</option>
      <option value="system">System</option>
    </select>
    {unsaved && <p className="field-help" id={id + '-help'} role="status">
      Chưa lưu được lựa chọn. Chế độ này áp dụng cho lần mở trang hiện tại.
    </p>}
  </div>
}

export function DemoDialog({ density }: { density: Density }) {
  const title = useId()
  const dialog = useRef<HTMLDialogElement>(null)
  const opener = useRef<HTMLButtonElement>(null)
  const input = useRef<HTMLInputElement>(null)
  const [note, setNote] = useState('')
  const [open, setOpen] = useState(false)
  useLayoutEffect(() => {
    const element = dialog.current
    if (!open || !element) return
    element.showModal()
    input.current?.focus()
    return () => { if (element.open) element.close() }
  }, [open])

  return <>
    <Button ref={opener} onClick={() => setOpen(true)}>Mở hộp thoại</Button>
    {open && createPortal(<dialog className="demo-dialog" data-density={density} ref={dialog}
      aria-labelledby={title} onClose={() => {
        setOpen(false)
        opener.current?.focus()
      }}>
      <div className="panel-content">
        <h2 id={title}>Hộp thoại mẫu</h2>
        <Field label="Ghi chú trong hộp thoại" ref={input} value={note}
          onChange={event => setNote(event.currentTarget.value)} />
        <ThemeSelect />
        <div className="button-row">
          {note && <Button variant="destructive" onClick={() => {
            setNote('')
            input.current?.focus()
          }}>Xóa ghi chú mẫu</Button>}
          <Button onClick={() => dialog.current?.close()}>Đóng hộp thoại</Button>
        </div>
      </div>
    </dialog>, document.body)}
  </>
}
