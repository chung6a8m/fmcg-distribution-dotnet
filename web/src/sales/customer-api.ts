const base = '/api/synthetic/app02c1'

export type Identity = { tenantId: string; actorId: string; sessionId: string }
export type Customer = { id: string; code: string; name: string; isActive: boolean; version: string }
export type Outlet = { id: string; name: string; version: string }
export type Address = { country: string; region: string; locality: string; line: string; recipient: string }
export type Registered = { id: string; outletId: string | null; revision: number; address: Address }
export type Selection = {
  id: string; origin: 'DIRECT' | 'REGISTERED'; address: Address
  outletId: string | null; registeredRevision: number | null; capturedAtUtc: string
}
export type Detail = {
  customer: Customer; outlets: Outlet[]; registeredShipTos: Registered[]; selections: Selection[]
}
export type Outcome = { code: string; id: string | null; revision: number | null }

const paths = ['/customers/save', '/outlets/save', '/registered-ship-tos/save', '/ship-to-selections'] as const
export type CommandPath = typeof paths[number]
export type Pending = { path: CommandPath; commandId: string; body: string; customerId?: string }

export class HttpError extends Error {
  constructor(readonly status: number) { super('HTTP ' + status) }
}

export async function request(token: string, path: string, body?: string) {
  return requestSalesApi(base, token, path, body)
}

export async function requestSalesApi(api: string, token: string, path: string, body?: string) {
  const headers = new Headers({ Authorization: 'Bearer ' + token, Accept: 'application/json' })
  if (body !== undefined) headers.set('Content-Type', 'application/json')
  return fetch(api + path, {
    method: body === undefined ? 'GET' : 'POST', headers, body, cache: 'no-store',
    signal: AbortSignal.timeout(15_000), redirect: 'error',
  })
}

export async function read<T>(token: string, path: string): Promise<T> {
  const response = await request(token, path)
  if (!response.ok) throw new HttpError(response.status)
  return response.json() as Promise<T>
}

function pendingKey(identity: Identity) {
  return 'app02c1.pending.' + JSON.stringify([identity.sessionId, identity.tenantId, identity.actorId])
}

// A pane can unmount while its command is in flight. Retain the original
// request for this page even when access to sessionStorage is blocked.
const pendingInMemory = new Map<string, string | null>()

export function pendingJson(key: string) {
  if (pendingInMemory.has(key)) return pendingInMemory.get(key) ?? null
  try { return sessionStorage.getItem(key) } catch { return null }
}

export function rememberPending(key: string, command: object) {
  const value = JSON.stringify(command)
  pendingInMemory.set(key, value)
  try { sessionStorage.setItem(key, value) } catch { /* In-memory recovery stays available. */ }
}

export function forgetPending(key: string, originalId: string) {
  try {
    const raw = pendingJson(key)
    if (!raw) return
    const command = JSON.parse(raw)
    if ((command.commandId ?? command.id) !== originalId) return
    pendingInMemory.set(key, null)
    sessionStorage.removeItem(key)
  } catch { /* In-memory recovery stays available. */ }
}

export function restorePending(identity: Identity): Pending | null {
  try {
    const raw = pendingJson(pendingKey(identity))
    if (!raw) return null
    const item = JSON.parse(raw) as Pending
    return paths.includes(item.path) && /^[a-f0-9-]{36}$/.test(item.commandId)
      && typeof item.body === 'string' && JSON.parse(item.body).commandId === item.commandId ? item : null
  } catch {
    return null
  }
}

export function storePending(identity: Identity, pending: Pending) {
  rememberPending(pendingKey(identity), pending)
}

export function clearPending(identity: Identity, id: string) {
  forgetPending(pendingKey(identity), id)
}

const codes: Record<string, string> = {
  APPLIED: 'Đã ghi vào cơ sở dữ liệu.',
  CUSTOMER_FIELDS_INVALID: 'Kiểm tra mã và tên khách hàng.',
  CUSTOMER_CODE_IN_USE: 'Mã khách hàng đã được sử dụng.',
  CUSTOMER_NOT_FOUND: 'Khách hàng không tồn tại hoặc đã ngừng hoạt động.',
  OUTLET_FIELDS_INVALID: 'Kiểm tra tên điểm bán.',
  OUTLET_NOT_FOUND: 'Điểm bán không thuộc khách hàng này.',
  ADDRESS_INVALID: 'Kiểm tra quốc gia, vùng DEV, khu vực, địa chỉ và người nhận.',
  REGISTERED_SHIP_TO_NOT_FOUND: 'Không tìm thấy địa chỉ đăng ký của khách hàng.',
  STALE_VERSION: 'Dữ liệu đã thay đổi. Tải lại khách hàng trước khi sửa tiếp.',
  REVISION_INVALID: 'Phiên bản địa chỉ không hợp lệ.',
  SELECTION_INVALID: 'Thông tin chọn địa chỉ không hợp lệ.',
  COMMAND_PAYLOAD_CONFLICT: 'Mã yêu cầu đã được dùng cho nội dung khác.',
}

export function outcomeMessage(code: string) {
  return codes[code] ?? 'Yêu cầu không hợp lệ.'
}

export function readError(error: unknown) {
  if (error instanceof HttpError) {
    if (error.status === 401) return 'Phiên DEV hết hạn hoặc mã truy cập không hợp lệ.'
    if (error.status === 403) return 'Bạn không có quyền Sales hiện hành trong tenant DEV này.'
    if (error.status === 404) return 'Không tìm thấy dữ liệu hoặc phiên DEV C.1 chưa được bật.'
  }
  return 'Không tải được dữ liệu từ API.'
}
