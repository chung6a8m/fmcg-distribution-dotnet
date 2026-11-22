import { useEffect, useEffectEvent, useId, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Button, Field, Notice, ThemeSelect } from '../ui/controls'
import {
  clearPending, HttpError, outcomeMessage, read, readError, request, restorePending, storePending,
  type Address, type CommandPath, type Customer, type Detail, type Identity,
  type Outcome, type Outlet, type Pending, type Registered,
} from './customer-api'
import './customers.css'

type Editor =
  | { kind: 'customer'; record?: Customer }
  | { kind: 'outlet'; record?: Outlet }
  | { kind: 'registered'; record?: Registered }
  | { kind: 'direct' }

type Feedback = { tone: 'success' | 'warning' | 'danger'; text: string }

function AddressFields({ address }: { address?: Address }) {
  const country = useId()
  const region = useId()
  return <>
    <div className="field-pair">
      <div className="field">
        <label htmlFor={country}>Quốc gia</label>
        <select id={country} name="country" defaultValue={address?.country ?? ''} required>
          <option value="">Chọn quốc gia</option><option value="VN">Việt Nam (VN)</option>
        </select>
      </div>
      <div className="field">
        <label htmlFor={region}>Vùng DEV</label>
        <select id={region} name="region" defaultValue={address?.region ?? ''} required>
          <option value="">Chọn vùng</option><option value="HCM">HCM</option><option value="HN">HN</option>
        </select>
      </div>
    </div>
    <Field name="locality" label="Khu vực" required maxLength={120} defaultValue={address?.locality} />
    <Field name="line" label="Địa chỉ giao" required maxLength={200} defaultValue={address?.line} />
    <Field name="recipient" label="Người nhận" required maxLength={120} defaultValue={address?.recipient} />
  </>
}

function EditorForm({ editor, detail, disabled, saving, onSave, onCancel }: {
  editor: Editor; detail: Detail | null; disabled: boolean; saving: boolean
  onSave: (path: CommandPath, fields: object, customerId?: string) => void
  onCancel: () => void
}) {
  const outlet = useId()
  const title = useId()
  const heading = {
    customer: editor.kind === 'customer' && editor.record ? 'Sửa khách hàng' : 'Thêm khách hàng',
    outlet: editor.kind === 'outlet' && editor.record ? 'Sửa điểm bán' : 'Thêm điểm bán',
    registered: editor.kind === 'registered' && editor.record ? 'Sửa địa chỉ đăng ký' : 'Thêm địa chỉ đăng ký',
    direct: 'Nhập địa chỉ một lần',
  }[editor.kind]
  const titleRef = useRef<HTMLHeadingElement>(null)
  useEffect(() => { titleRef.current?.focus() }, [])

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const values = new FormData(event.currentTarget)
    const field = (name: string) => String(values.get(name) ?? '')
    if (editor.kind === 'customer') {
      onSave('/customers/save', {
        id: editor.record?.id, version: editor.record?.version, code: field('code'), name: field('name'),
      })
    } else if (detail) {
      const customerId = detail.customer.id
      if (editor.kind === 'outlet') {
        onSave('/outlets/save', {
          customerId, id: editor.record?.id, version: editor.record?.version, name: field('name'),
        }, customerId)
      } else {
        const address = {
          country: field('country'), region: field('region'), locality: field('locality'),
          line: field('line'), recipient: field('recipient'),
        }
        const outletId = field('outletId') || null
        if (editor.kind === 'registered') {
          onSave('/registered-ship-tos/save', {
            customerId, id: editor.record?.id, revision: editor.record?.revision, outletId, address,
          }, customerId)
        } else {
          onSave('/ship-to-selections', { customerId, outletId, address }, customerId)
        }
      }
    }
  }

  return <form className="panel panel-content" aria-labelledby={title} onSubmit={submit}>
    <h2 id={title} ref={titleRef} tabIndex={-1}>{heading}</h2>
    <fieldset className="c1-fields" disabled={saving}>
      {editor.kind === 'customer' && <>
        <Field name="code" label="Mã khách hàng" required maxLength={32} defaultValue={editor.record?.code} />
        <Field name="name" label="Tên khách hàng" required maxLength={256} defaultValue={editor.record?.name} />
      </>}
      {editor.kind === 'outlet' &&
        <Field name="name" label="Tên điểm bán" required maxLength={256} defaultValue={editor.record?.name} />}
      {(editor.kind === 'registered' || editor.kind === 'direct') && <>
        <div className="field">
          <label htmlFor={outlet}>Điểm bán (tùy chọn)</label>
          <select id={outlet} name="outletId" defaultValue={editor.kind === 'registered' ? editor.record?.outletId ?? '' : ''}>
            <option value="">Không có điểm bán</option>
            {detail?.outlets.map(row => <option key={row.id} value={row.id}>{row.name}</option>)}
          </select>
        </div>
        <AddressFields address={editor.kind === 'registered' ? editor.record?.address : undefined} />
      </>}
    </fieldset>
    <div className="button-row">
      <Button type="submit" variant="primary" disabled={disabled}>{editor.kind === 'direct' ? 'Lưu lựa chọn' : 'Lưu'}</Button>
      <Button onClick={onCancel} disabled={saving}>Hủy</Button>
    </div>
  </form>
}

function addressText(address: Address) {
  return [address.line, address.locality, address.region, address.country].join(', ')
}

const captureTime = new Intl.DateTimeFormat('vi-VN', { dateStyle: 'short', timeStyle: 'short', timeZone: 'UTC' })

function CustomerDetail({ detail, editing, disabled, edit, select }: {
  detail: Detail; editing: boolean; disabled: boolean
  edit: (editor: Editor) => void; select: (value: Registered) => void
}) {
  const { customer, outlets, registeredShipTos, selections } = detail
  const outletName = (id: string | null) => outlets.find(row => row.id === id)?.name ?? 'Không có điểm bán'
  const locked = editing || disabled || !customer.isActive
  return <>
    <section className="panel panel-content" aria-labelledby="c1-selected">
      <div className="panel-heading c1-section-heading">
        <div><h2 id="c1-selected">{customer.name}</h2><p>{customer.code} · Bên chịu công nợ</p></div>
        <Button disabled={editing || disabled} onClick={() => edit({ kind: 'customer', record: customer })}>Sửa khách hàng</Button>
      </div>
      {!customer.isActive && <p>Khách hàng đã ngừng hoạt động.</p>}
    </section>
    <section className="panel panel-content" aria-labelledby="c1-outlets">
      <div className="panel-heading c1-section-heading">
        <h2 id="c1-outlets">Điểm bán</h2>
        <Button disabled={locked} onClick={() => edit({ kind: 'outlet' })}>Thêm điểm bán</Button>
      </div>
      {outlets.length === 0 ? <p>Khách hàng chưa có điểm bán.</p> : <ul className="c1-rows">
        {outlets.map(row => <li key={row.id}><span>{row.name}</span>
          <Button disabled={locked} onClick={() => edit({ kind: 'outlet', record: row })}>Sửa điểm bán</Button>
        </li>)}
      </ul>}
    </section>
    <section className="panel panel-content" aria-labelledby="c1-registered">
      <div className="panel-heading c1-section-heading">
        <h2 id="c1-registered">Địa chỉ đã đăng ký</h2>
        <Button disabled={locked} onClick={() => edit({ kind: 'registered' })}>Thêm địa chỉ đăng ký</Button>
      </div>
      {registeredShipTos.length === 0 ? <p>Chưa có địa chỉ đăng ký.</p> : <ul className="c1-rows">
        {registeredShipTos.map(row => <li key={row.id}>
          <div><strong>{addressText(row.address)}</strong>
            <p>{row.address.recipient} · {outletName(row.outletId)} · Phiên bản {row.revision}</p></div>
          <div className="button-row">
            <Button disabled={locked} onClick={() => edit({ kind: 'registered', record: row })}>Sửa địa chỉ</Button>
            <Button disabled={locked} onClick={() => select(row)}>Chọn địa chỉ</Button>
          </div>
        </li>)}
      </ul>}
    </section>
    <section className="panel panel-content" aria-labelledby="c1-selections">
      <div className="panel-heading c1-section-heading">
        <h2 id="c1-selections">Lựa chọn địa chỉ giao</h2>
        <Button disabled={locked} onClick={() => edit({ kind: 'direct' })}>Nhập địa chỉ một lần</Button>
      </div>
      <p className="section-detail">Địa chỉ được lưu để dùng khi xác nhận đơn.</p>
      {selections.length === 0 ? <p>Chưa có lựa chọn.</p> : <ul className="c1-rows">
        {selections.map(row => <li key={row.id}><div>
          <strong>{addressText(row.address)}</strong>
          <p>{row.address.recipient} · {row.outletId ? `Điểm bán hiện tại: ${outletName(row.outletId)}` : 'Không có điểm bán'} · {
            row.origin === 'DIRECT' ? 'Địa chỉ một lần' : `Đăng ký · Phiên bản ${row.registeredRevision}`
          }</p>
          <p><time dateTime={row.capturedAtUtc}>{captureTime.format(new Date(row.capturedAtUtc))} UTC</time></p>
        </div></li>)}
      </ul>}
    </section>
  </>
}

export function CustomerWorkspace({ workspaceToken }: { workspaceToken?: string }) {
  const [session, setSession] = useState<{ token: string; identity: Identity } | null>(null)
  const [customers, setCustomers] = useState<Customer[]>([])
  const [detail, setDetail] = useState<Detail | null>(null)
  const [editor, setEditor] = useState<Editor | null>(null)
  const [pending, setPending] = useState<Pending | null>(null)
  const [loading, setLoading] = useState(false)
  const [sending, setSending] = useState(false)
  const [offline, setOffline] = useState(!navigator.onLine)
  const [feedback, setFeedback] = useState<Feedback | null>(null)
  const [loadError, setLoadError] = useState('')
  const operation = useRef(false)
  const feedbackRef = useRef<HTMLDivElement>(null)
  const editOpener = useRef<HTMLElement | null>(null)
  const loginField = useRef<HTMLInputElement>(null)
  const lastRequested = useRef<string | undefined>(undefined)

  useEffect(() => {
    const sync = () => setOffline(!navigator.onLine)
    window.addEventListener('online', sync)
    window.addEventListener('offline', sync)
    return () => {
      window.removeEventListener('online', sync)
      window.removeEventListener('offline', sync)
    }
  }, [])

  async function signIn(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const token = String(new FormData(event.currentTarget).get('token') ?? '').trim()
    if (loginField.current) loginField.current.value = ''
    void signInToken(token)
  }

  const openWorkspace = useEffectEvent((token: string) => { void signInToken(token) })
  useEffect(() => {
    if (workspaceToken) openWorkspace(workspaceToken)
  }, [workspaceToken])

  async function signInToken(token: string) {
    setLoading(true)
    setLoadError('')
    try {
      const [identity, list] = await Promise.all([
        read<Identity>(token, '/session'), read<Customer[]>(token, '/customers'),
      ])
      setSession({ token, identity })
      setCustomers(list)
      setPending(restorePending(identity))
    } catch (error) {
      setLoadError(readError(error))
    } finally { setLoading(false) }
  }

  async function refresh(token: string, id?: string) {
    lastRequested.current = id
    setLoading(true)
    setLoadError('')
    try {
      const [list, selected] = await Promise.all([
        read<Customer[]>(token, '/customers'),
        id ? read<Detail>(token, '/customers/' + id) : Promise.resolve(null),
      ])
      setCustomers(list)
      setDetail(selected)
    } catch (error) {
      setLoadError(readError(error))
    } finally { setLoading(false) }
  }

  function updateEditor(value: Editor) {
    editOpener.current = document.activeElement instanceof HTMLElement ? document.activeElement : null
    setFeedback(null)
    setEditor(value)
  }

  function closeEditor() {
    setEditor(null)
    requestAnimationFrame(() => editOpener.current?.focus())
  }

  async function resolve(command: Pending, lookup: boolean) {
    if (!session || !navigator.onLine || operation.current) return
    operation.current = true
    setSending(true)
    setFeedback(null)
    try {
      const response = lookup
        ? await request(session.token, '/commands/' + command.commandId)
        : await request(session.token, command.path, command.body)
      if (response.status === 401 || response.status === 403) {
        setFeedback({ tone: 'danger', text: readError(new HttpError(response.status)) })
        return
      }
      if (lookup && response.status === 404) {
        setFeedback({ tone: 'warning', text: 'Chưa tìm thấy kết quả. Có thể gửi lại đúng yêu cầu này.' })
        return
      }
      if (response.status >= 500) throw new Error('Uncertain response')
      const result: Outcome = await response.json()
      if (typeof result.code !== 'string') throw new Error('Unrecognized response')
      if (result.code === 'COMMAND_PAYLOAD_CONFLICT') {
        setFeedback({ tone: 'danger', text: outcomeMessage(result.code) + ' Tra cứu yêu cầu đã ghi.' })
        return
      }
      clearPending(session.identity, command.commandId)
      setPending(null)
      setFeedback({ tone: result.code === 'APPLIED' ? 'success' : 'danger', text: outcomeMessage(result.code) })
      if (result.code === 'APPLIED') {
        setEditor(null)
        const id = command.path === '/customers/save' ? result.id ?? undefined : command.customerId
        await refresh(session.token, id)
      }
    } catch {
      setFeedback({ tone: 'warning', text: 'Chưa rõ kết quả. Tra cứu hoặc gửi lại yêu cầu với mã hiện tại.' })
    } finally {
      operation.current = false
      setSending(false)
      requestAnimationFrame(() => feedbackRef.current?.focus())
    }
  }

  function save(path: CommandPath, fields: object, customerId?: string) {
    if (!session || pending || !navigator.onLine || operation.current) return
    const commandId = crypto.randomUUID()
    const command = { path, commandId, body: JSON.stringify({ commandId, ...fields }), customerId }
    setPending(command)
    storePending(session.identity, command)
    void resolve(command, false)
  }

  const busy = loading || sending
  const selectionLocked = busy || editor !== null || pending !== null
  const unavailable = busy || offline || pending !== null || loadError !== ''
  const Container = workspaceToken ? 'section' : 'main'
  return <Container className={workspaceToken ? 'sales-pane c1-page' : 'page c1-page'}>
    {workspaceToken ? <h2>Khách hàng và địa chỉ giao</h2> : <header className="page-heading c1-header">
      <div>
        <a className="page-link" href="/">Trang chính</a>
        <h1>Khách hàng và địa chỉ giao</h1>
        <p className="section-detail">APP-02C.1 · DEV tổng hợp{session && ` · ${session.identity.tenantId}`}</p>
      </div>
      <div className="c1-session-actions">
        <ThemeSelect />
        {session && <Button disabled={busy} onClick={() => {
          setSession(null)
          setCustomers([])
          setDetail(null)
          setEditor(null)
          setPending(null)
          lastRequested.current = undefined
          setFeedback(null)
          setLoadError('')
        }}>Đổi phiên DEV</Button>}
      </div>
    </header>}
    {offline && <div role="status"><Notice tone="warning" title="Đang ngoại tuyến">Kết nối mạng để gửi yêu cầu.</Notice></div>}
    {!session ? workspaceToken
      ? <Button disabled={busy || offline} onClick={() => void signInToken(workspaceToken)}>Tải màn hình</Button>
      : <form className="panel panel-content c1-sign-in" onSubmit={signIn}>
      <h2>Kết nối phiên DEV</h2>
      <Field ref={loginField} label="Mã truy cập DEV" name="token" type="password" autoComplete="off" spellCheck={false} required />
      <Button type="submit" variant="primary" disabled={busy || offline}>Kết nối</Button>
    </form> : <div className="c1-layout">
      <aside className="panel panel-content" aria-labelledby="c1-list">
        <h2 id="c1-list">Khách hàng</h2>
        <Button variant="primary" disabled={unavailable || editor !== null} onClick={() => updateEditor({ kind: 'customer' })}>
          Thêm khách hàng
        </Button>
        {customers.length === 0 && <p>Chưa có khách hàng.</p>}
        <ul className="c1-customers">
          {customers.map(row => <li key={row.id}><Button aria-current={detail?.customer.id === row.id ? 'true' : undefined}
            disabled={selectionLocked || offline} onClick={() => {
              setFeedback(null)
              void refresh(session.token, row.id)
            }}><strong>{row.code}</strong><span>{row.name}</span></Button></li>)}
        </ul>
      </aside>
      <div className="c1-detail">
        {(feedback || pending) && <div ref={feedbackRef} tabIndex={-1} role="status" className="panel panel-content">
          {feedback && <Notice tone={feedback.tone} title="Kết quả yêu cầu">{feedback.text}</Notice>}
          {pending && <><p>Mã yêu cầu <code>{pending.commandId}</code></p><div className="button-row">
            <Button disabled={busy || offline} onClick={() => void resolve(pending, true)}>Tra cứu kết quả</Button>
            <Button disabled={busy || offline} onClick={() => void resolve(pending, false)}>Gửi lại yêu cầu</Button>
          </div></>}
          {!pending && feedback?.tone === 'danger' && <Button disabled={busy || offline} onClick={() => {
            setEditor(null)
            setFeedback(null)
            void refresh(session.token, detail?.customer.id)
          }}>Tải lại dữ liệu</Button>}
        </div>}
        {editor && <EditorForm editor={editor} detail={detail} disabled={unavailable}
          saving={sending || pending !== null} onSave={save} onCancel={closeEditor} />}
        {detail ? <CustomerDetail detail={detail} editing={editor !== null} disabled={unavailable} edit={updateEditor}
          select={row => save('/ship-to-selections', {
            customerId: detail.customer.id, registeredId: row.id, revision: row.revision,
          }, detail.customer.id)} />
          : !editor && <section className="panel panel-content"><h2>Chi tiết khách hàng</h2><p>Chọn một khách hàng để xem chi tiết.</p></section>}
      </div>
    </div>}
    {busy && <p role="status">{sending ? 'Đang gửi yêu cầu…' : 'Đang tải dữ liệu…'}</p>}
    {loadError && <div role="alert"><Notice tone="danger" title="Lỗi kết nối">{loadError}</Notice>
      {session && <Button disabled={busy || offline} onClick={() => void refresh(session.token, lastRequested.current)}>Thử tải lại</Button>}
    </div>}
  </Container>
}
