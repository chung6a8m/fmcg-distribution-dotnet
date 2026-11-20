import { useId, useMemo, useRef, useState } from 'react'
import type { Ref } from 'react'

export type LookupOption = { id: string; code: string; name: string; detail: string }
export type LookupSourceState = 'ready' | 'loading' | 'error' | 'forbidden'

export function lookupLabel(item: LookupOption): string {
  return item.code + ' · ' + item.name
}

export function searchKey(value: string): string {
  return value.normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/đ/gi, 'd').toLocaleLowerCase('vi-VN')
}

type Props = {
  label: string
  options: readonly LookupOption[]
  selected: LookupOption | null
  onSelect: (value: LookupOption | null) => void
  onCommit?: () => void
  inputRef?: Ref<HTMLInputElement>
  error?: string
  sourceState?: LookupSourceState
}

// Query is intentionally NOT an identity. Only an explicit candidate activation commits an ID.
// Escape may restore the last explicitly selected option; Tab never auto-selects a suggestion.
export function LookupField({
  label, options, selected, onSelect, onCommit, inputRef, error, sourceState = 'ready'
}: Props) {
  const id = useId()
  const listId = id + '-list'
  const localRef = useRef<HTMLInputElement>(null)
  const previous = useRef<LookupOption | null>(selected)
  const [query, setQuery] = useState(selected ? lookupLabel(selected) : '')
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(-1)

  const matches = useMemo(() => {
    if (sourceState !== 'ready') return []
    const needle = searchKey(query.trim())
    return options.filter(item => searchKey(item.code + ' ' + item.name + ' ' + item.detail).includes(needle)).slice(0, 20)
  }, [options, query, sourceState])

  function choose(item: LookupOption) {
    previous.current = item
    setQuery(lookupLabel(item))
    setOpen(false)
    setActive(-1)
    onSelect(item)
    onCommit?.()
  }

  function restore() {
    setOpen(false)
    setActive(-1)
    if (previous.current && options.some(item => item.id === previous.current?.id)) {
      setQuery(lookupLabel(previous.current))
      onSelect(previous.current)
    }
  }

  function edit(text: string) {
    setQuery(text)
    setOpen(true)
    setActive(-1)
    // Invalidate the committed identity immediately, without losing the last valid option for Escape.
    if (selected) {
      previous.current = selected
      onSelect(null)
    }
  }

  const hasUncommittedText = query.trim().length > 0 && (!selected || lookupLabel(selected) !== query)
  const description = [error ? id + '-error' : null, hasUncommittedText ? id + '-unselected' : null]
    .filter(Boolean).join(' ') || undefined

  return <div className="field lookup-field" onBlurCapture={event => {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
      setOpen(false)
      setActive(-1)
    }
  }}>
    <label htmlFor={id}>{label}</label>
    <div className="lookup-input-row">
      <input ref={node => {
        localRef.current = node
        if (typeof inputRef === 'function') inputRef(node)
        else if (inputRef) inputRef.current = node
      }} id={id} role="combobox" aria-autocomplete="list" aria-haspopup="listbox"
        aria-controls={open ? listId : undefined} aria-expanded={open} aria-activedescendant={
          open && active >= 0 && matches[active] ? listId + '-' + matches[active].id : undefined
        } aria-invalid={Boolean(error)} aria-describedby={description} value={query}
        autoComplete="off" placeholder="Nhập mã hoặc tên để tìm"
        onFocus={() => setOpen(true)}
        onChange={event => edit(event.currentTarget.value)}
        onKeyDown={event => {
          if (event.nativeEvent.isComposing || event.keyCode === 229) {
            // An IME confirmation is not an implicit form submit or an option activation.
            if (event.key === 'Enter') event.preventDefault()
            return
          }
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault()
            setOpen(true)
            if (!matches.length) return
            setActive(current => event.key === 'ArrowDown'
              ? (current + 1) % matches.length
              : (current < 0 ? matches.length - 1 : (current - 1 + matches.length) % matches.length))
          } else if (event.key === 'Enter') {
            // A combobox inside a form must not submit the form, including when nothing is active.
            event.preventDefault()
            if (open && active >= 0 && matches[active]) choose(matches[active])
          } else if (event.key === 'Escape' && (open || hasUncommittedText)) {
            event.preventDefault()
            restore()
          } else if (event.key === 'Tab') {
            setOpen(false)
            setActive(-1)
          }
        }} />
      {(query || selected) && <button className="lookup-clear button button-secondary" type="button"
        aria-label={'Xóa lựa chọn ' + label} onClick={() => {
          previous.current = null
          setQuery('')
          setActive(-1)
          onSelect(null)
          localRef.current?.focus()
        }}>Xóa</button>}
    </div>
    {hasUncommittedText && <p id={id + '-unselected'} className="field-help">
      Chưa chọn mã hợp lệ; gõ tên không xác nhận identity.
    </p>}
    {error && <p id={id + '-error'} className="field-error">{error}</p>}
    {open && <div className="lookup-menu" id={listId} role="listbox" aria-label={'Kết quả ' + label}>
      {sourceState === 'loading' ? <p role="status">Đang tải danh mục mẫu…</p>
        : sourceState === 'error' ? <p role="status">Không đọc được danh mục mẫu.</p>
          : sourceState === 'forbidden' ? <p role="status">Không có quyền xem danh mục mẫu.</p>
            : matches.length === 0 ? <p role="status">Không có kết quả phù hợp.</p>
              : matches.map((item, index) => <div key={item.id} id={listId + '-' + item.id}
                role="option" aria-selected={index === active} data-active={index === active}
                className="lookup-option" onPointerDown={event => {
                  event.preventDefault()
                  choose(item)
                }}>
                <strong>{item.code} · {item.name}</strong><span>{item.detail}</span>
              </div>)}
    </div>}
  </div>
}
