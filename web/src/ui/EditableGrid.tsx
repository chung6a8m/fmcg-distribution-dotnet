import { useLayoutEffect, useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import { demoQuantityError, parseDemoQuantity } from './decimal'

export type DraftLine = { id: string; code: string; name: string; unit: string; quantity: string }
export type GridSourceState = 'ready' | 'loading' | 'error' | 'forbidden' | 'empty'

type Column = 0 | 1 | 2 | 3
type Cell = { rowId: string; column: Column }
type Props = {
  label: string
  rows: readonly DraftLine[]
  onRowsChange: (rows: DraftLine[]) => void
  selectedId: string | null
  onSelect: (id: string) => void
  onEditingChange: (editing: boolean) => void
  onExit: () => void
  state?: GridSourceState
  filtered?: boolean
}

export function EditableGrid({
  label, rows, onRowsChange, selectedId, onSelect, onEditingChange, onExit,
  state = 'ready', filtered = false
}: Props) {
  const [active, setActive] = useState<Cell>(() => ({ rowId: rows[0]?.id ?? '', column: 0 }))
  const [editingId, setEditingId] = useState<string | null>(null)
  const [buffer, setBuffer] = useState('')
  const [error, setError] = useState('')
  const editorRef = useRef<HTMLInputElement>(null)
  const cellRefs = useRef<Record<string, HTMLTableCellElement | null>>({})
  const focusPending = useRef<string | null>(null)

  // When filtering hides the roving cell, the first visible cell becomes tabbable.
  // Selection remains ID-based and is never silently changed.
  const tabbable = rows.some(row => row.id === active.rowId)
    ? active : { rowId: rows[0]?.id ?? '', column: 0 as Column }

  function cellKey(id: string, column: Column) { return id + ':' + column }

  useLayoutEffect(() => {
    if (editingId) {
      editorRef.current?.focus()
      return
    }
    const key = focusPending.current
    focusPending.current = null
    if (key === '__exit__') onExit()
    else if (key) cellRefs.current[key]?.focus()
  }, [editingId, active.rowId, active.column, onExit])

  function goTo(id: string, column: Column) {
    focusPending.current = cellKey(id, column)
    setActive({ rowId: id, column })
  }

  function begin(row: DraftLine) {
    setBuffer(row.quantity)
    setError('')
    setEditingId(row.id)
    onEditingChange(true)
  }

  function cancel(row: DraftLine) {
    setEditingId(null)
    setError('')
    onEditingChange(false)
    goTo(row.id, 3)
  }

  function commit(row: DraftLine, direction: 'stay' | 'next' | 'previous') {
    if (parseDemoQuantity(buffer) === null) {
      setError(demoQuantityError)
      return
    }
    onRowsChange(rows.map(line => line.id === row.id ? { ...line, quantity: buffer } : line))
    setEditingId(null)
    setError('')
    onEditingChange(false)
    const index = rows.findIndex(line => line.id === row.id)
    const other = direction === 'next' ? rows[index + 1]
      : direction === 'previous' ? rows[index - 1] : row
    if (other) goTo(other.id, 3)
    else focusPending.current = '__exit__'
  }

  function navigate(row: DraftLine, col: Column, key: string) {
    let rowIndex = rows.findIndex(item => item.id === row.id)
    let column = col as number
    if (key === 'ArrowRight') column = Math.min(3, column + 1)
    if (key === 'ArrowLeft') column = Math.max(0, column - 1)
    if (key === 'ArrowDown') rowIndex = Math.min(rows.length - 1, rowIndex + 1)
    if (key === 'ArrowUp') rowIndex = Math.max(0, rowIndex - 1)
    goTo(rows[rowIndex].id, column as Column)
  }

  function cellKeyDown(event: KeyboardEvent<HTMLTableCellElement>, row: DraftLine, column: Column) {
    if (event.target !== event.currentTarget || event.nativeEvent.isComposing) return
    if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
      event.preventDefault()
      navigate(row, column, event.key)
    } else if ((event.key === 'Enter' || event.key === 'F2') && column === 3) {
      event.preventDefault()
      begin(row)
    } else if ((event.key === 'Enter' || event.key === ' ') && column !== 3) {
      event.preventDefault()
      onSelect(row.id)
    }
  }

  if (state !== 'ready') {
    const labels = {
      loading: 'Đang tải các dòng mẫu…', error: 'Không đọc được các dòng mẫu.',
      forbidden: 'Không có quyền xem các dòng mẫu.', empty: 'Chưa có dòng mẫu.'
    }
    return <p className="grid-state" role="status">{labels[state]}</p>
  }

  return <div className="grid-scroll" role="region" aria-label={'Cuộn ngang ' + label} tabIndex={0}>
    <table role="grid" aria-label={label} className="entry-grid" aria-rowcount={rows.length + 1}>
      <thead><tr>
        {['Mã hàng', 'Tên mặt hàng', 'ĐVT', 'Số lượng'].map(text =>
          <th key={text} role="columnheader" scope="col">{text}</th>)}
      </tr></thead>
      <tbody>{rows.length === 0 ? <tr><td colSpan={4}>
        {filtered ? 'Không khớp bộ lọc; dữ liệu gốc không bị xóa.' : 'Chưa có dòng mẫu.'}
      </td></tr> : rows.map(row => <tr key={row.id} aria-selected={selectedId === row.id}
        data-selected={selectedId === row.id}>
        {([row.code, row.name, row.unit, row.quantity] as const).map((value, columnIndex) => {
          const column = columnIndex as Column
          const key = cellKey(row.id, column)
          const isEditing = editingId === row.id && column === 3
          return <td key={key} role="gridcell" tabIndex={!isEditing && tabbable.rowId === row.id && tabbable.column === column ? 0 : -1}
            ref={node => { cellRefs.current[key] = node }} aria-label={
              ['Mã hàng ', 'Tên mặt hàng ', 'Đơn vị ', 'Số lượng '][column] + row.code + ': ' + value
            } data-col={column} data-row={row.id}
            onFocus={() => {
              if (active.rowId !== row.id || active.column !== column) setActive({ rowId: row.id, column })
            }}
            onClick={event => {
              if (editingId && editingId !== row.id) return
              if (!isEditing) {
                onSelect(row.id)
                event.currentTarget.focus()
              }
            }}
            onKeyDown={event => cellKeyDown(event, row, column)}>
            <span className="grid-card-label" aria-hidden="true">{['Mã hàng', 'Tên mặt hàng', 'ĐVT', 'Số lượng'][column]}</span>
            {isEditing ? <span className="grid-editor">
              <input ref={editorRef} type="text" inputMode="decimal"
                aria-label={'Số lượng ' + row.code}
                aria-invalid={Boolean(error)} aria-describedby={error ? 'grid-error-' + row.id : undefined}
                value={buffer} onChange={event => {
                  setBuffer(event.currentTarget.value)
                  if (error) setError('')
                }} onKeyDown={event => {
                  event.stopPropagation()
                  if (event.nativeEvent.isComposing || event.keyCode === 229) return
                  if (event.key === 'Escape') {
                    event.preventDefault()
                    cancel(row)
                  } else if (event.key === 'Enter') {
                    event.preventDefault()
                    commit(row, 'stay')
                  } else if (event.key === 'Tab') {
                    event.preventDefault()
                    commit(row, event.shiftKey ? 'previous' : 'next')
                  }
                  // Arrows are deliberately unhandled here: they move the caret, not grid focus.
                }} />
              {error && <span className="field-error" id={'grid-error-' + row.id}>{error}</span>}
            </span> : <span className={column === 3 ? 'numeric' : ''}>{value}</span>}
          </td>
        })}</tr>)}</tbody>
    </table>
  </div>
}
