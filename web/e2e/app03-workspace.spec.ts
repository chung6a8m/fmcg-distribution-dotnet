import { expect, test, type Page } from '@playwright/test'

const app03 = '/api/synthetic/app03'
const c2 = '/api/synthetic/app02c2'
const c5 = '/api/synthetic/app02c5'
const tripA = '11111111-1111-1111-1111-111111111111'
const tripB = '22222222-2222-2222-2222-222222222222'
const tripPeer = '33333333-3333-3333-3333-333333333333'
const orderA = '44444444-4444-4444-4444-444444444444'
const pendingId = '55555555-5555-5555-5555-555555555555'

test.use({ serviceWorkers: 'block' })

async function mockWorkspace(page: Page) {
  const state = { revoked: false, noTrips: false, withoutTripA: false, successorArAssignment: false,
    peerArFilters: [] as string[], orderFilters: [] as string[], loadLookups: [] as string[],
    loadRights: [] as object[] }
  await page.route('**/health/live', route => route.fulfill({ json: {
    status: 'live', scope: 'APP-00A', businessWrites: false, workspaceProfile: 'APP-03.1a-v1',
  } }))
  await page.route('**/api/synthetic/**', async route => {
    const url = new URL(route.request().url())
    const path = url.pathname
    const token = route.request().headers().authorization
    const peer = token === 'Bearer peer'
    const actorId = peer ? 'nhan-vien-b' : 'nhan-vien-a'
    const identity = { tenantId: peer ? 'tenant-b' : 'tenant-a', actorId, sessionId: 'test-run',
      sales: true, warehouse: true }
    if (path === app03 + '/session') {
      if (state.revoked) return route.fulfill({ status: 403 })
      return route.fulfill({ json: { ...identity, returnRole: 'sales', expiresAt: Math.floor(Date.now() / 1000) + 60 } })
    }
    if (path === c2 + '/session') return route.fulfill({ json: identity })
    if (path === c2 + '/orders') return route.fulfill({ json: peer ? [] : [{ id: orderA, trip: 'Chuyến A' }] })
    if (path === `${c5}/ar/orders/${orderA}/access`) {
      return route.fulfill({ json: { allowed: !peer && !state.noTrips && (!state.withoutTripA || state.successorArAssignment) } })
    }
    if (path === c5 + '/ar') {
      const filter = url.searchParams.get('orderId') ?? ''
      if (peer) state.peerArFilters.push(filter)
      else state.orderFilters.push(filter)
      return route.fulfill({ json: { asOf: '2026-11-23T02:00:00Z', expected: null, reconciled: 0, customers: [], sources: [] } })
    }
    if (path.includes('/app02c3/') && path.includes('/commands/')) {
      state.loadLookups.push(path)
      return route.fulfill({ json: { status: 'APPLIED', sourceId: orderA } })
    }
    if (path.includes('/app02c3/') && path.endsWith('/rights')) return route.fulfill({ json: state.loadRights })
    if (path.endsWith('/trips') && state.noTrips) return route.fulfill({ json: [] })
    if (path.endsWith('/trips')) return route.fulfill({ json: peer ? [{ id: tripPeer, code: 'Chuyến đối chứng' }]
      : [{ id: tripA, code: 'Chuyến A' }, { id: tripB, code: 'Chuyến B' }].filter(row => !state.withoutTripA || row.id !== tripA) })
    return route.fulfill({ json: [] })
  })
  return state
}

async function login(page: Page, token: string) {
  await page.getByLabel('Mã truy cập DEV').fill(token)
  await page.getByRole('button', { name: 'Kết nối' }).click()
}

const nav = (page: Page, name: string) => page.getByRole('navigation', { name: 'Công việc DEV' })
  .getByRole('link', { name, exact: true })

test('APP-03 recovers an unknown draft with its original customer, trip, ShipTo and quantity', async ({ page }) => {
  await mockWorkspace(page)
  await page.route('**' + c2 + '/catalog', route => route.fulfill({ json: {
    customers: [{ id: tripPeer, label: 'Khách hàng thử' }],
    trips: [{ id: tripA, label: 'Chuyến A' }], skus: [{ id: orderA, label: 'SKU thử' }], warehouses: [],
  } }))
  const original = JSON.stringify({ commandId: pendingId, customerId: tripPeer, tripId: tripA, selectionId: tripB,
    lines: [{ skuId: orderA, quantity: 2, uomCode: 'PACK' }] })
  await page.goto('/work/orders')
  await login(page, 'source')
  await expect(page.getByLabel('Bước xử lý')).toBeVisible()
  await page.evaluate(([id, body]) => {
    sessionStorage.setItem('app02c2.pending.' + JSON.stringify(['test-run', 'tenant-a', 'nhan-vien-a']),
      JSON.stringify({ id, body, path: '/drafts' }))
  }, [pendingId, original])
  await page.reload()
  const recovery = page.getByRole('region', { name: 'Khôi phục lệnh' })
  for (const value of ['Lưu đơn nháp', 'Khách hàng thử', 'Chuyến A', tripB, 'SKU thử · 2 PACK'])
    await expect(recovery).toContainText(value)
  await expect(recovery.getByRole('button', { name: 'Gửi lại yêu cầu' })).toHaveCount(0)
  await recovery.getByRole('link', { name: 'Mở khu phục hồi lệnh' }).click()
  await expect(page).toHaveURL(/recover=1/)
  await expect(page.getByLabel('Bước xử lý')).toHaveCount(0)
  await page.route('**' + c2 + '/drafts', route => route.fulfill({ json: { status: 'APPLIED', sourceId: orderA } }))
  const replay = page.waitForRequest(req => req.url().endsWith(c2 + '/drafts') && req.method() === 'POST')
  await recovery.getByRole('button', { name: 'Gửi lại yêu cầu' }).click()
  expect((await replay).postData()).toBe(original)
  await expect(page.getByRole('status')).toContainText(orderA)
})

test('APP-03 uses one browser session and restores only the current identity’s trip', async ({ page }) => {
  const state = await mockWorkspace(page)
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'FMCG DMS · APP-03 DEV' })).toBeVisible()
  await login(page, 'source')
  await expect(page.getByRole('navigation', { name: 'Công việc DEV' })
    .getByRole('link', { name: /Mixed|Tạm ứng|Hoàn tiền/ })).toHaveCount(0)
  await nav(page, 'Nạp hàng lên xe').click()
  await page.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(tripB)
  await expect(page).toHaveURL(new RegExp(`trip=${tripB}`))
  await nav(page, 'Giao hàng A').click()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripB)
  await page.reload()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripB)
  await expect(page.getByLabel('Mã truy cập DEV')).toHaveCount(0)

  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await expect(page).not.toHaveURL(/trip=/)
  await login(page, 'peer')
  await expect(page.getByText('tenant-b · nhan-vien-b', { exact: true })).toBeVisible()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripPeer)
  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await login(page, 'source')
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripB)

  state.revoked = true
  await page.reload()
  await expect(page.getByRole('alert')).toContainText('quyền')
  await expect(page.getByLabel('Chuyến giao')).toHaveCount(0)
})

test('APP-03 strips another identity’s AR history on Back and Forward', async ({ page }) => {
  const state = await mockWorkspace(page)
  await page.goto('/work/ar?order=' + orderA)
  await login(page, 'source')
  const order = page.getByLabel('Mã đơn A')
  await expect(order).toHaveValue(orderA)
  await page.reload()
  await expect(order).toHaveValue(orderA)

  await nav(page, 'Trả hàng').click()
  await nav(page, 'Báo cáo AR').click()
  await page.goto('/work/ar?order=' + orderA)
  await expect(order).toHaveValue(orderA)
  await page.goBack()
  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await login(page, 'peer')
  await expect(page.getByText('tenant-b · nhan-vien-b')).toBeVisible()

  await page.goBack()
  await expect(nav(page, 'Trả hàng')).toHaveAttribute('aria-current', 'page')
  await page.goBack()
  await expect(order).toHaveValue('')
  await expect(page).not.toHaveURL(new RegExp(orderA))
  await page.goForward()
  await expect(nav(page, 'Trả hàng')).toHaveAttribute('aria-current', 'page')
  await page.goForward()
  await expect(order).toHaveValue('')
  await page.goForward()
  await expect(order).toHaveValue('')
  await expect(page).not.toHaveURL(new RegExp(orderA))

  await page.goto('/work/ar?order=' + orderA)
  await expect(page.getByRole('region', { name: 'Kết quả báo cáo AR' })).toBeVisible()
  await expect(order).toHaveValue('')
  await expect(page).not.toHaveURL(new RegExp(orderA))
  expect(state.peerArFilters.length).toBeGreaterThan(0)
  expect(state.peerArFilters.every(filter => filter === '')).toBe(true)

  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await page.goto('/')
  state.withoutTripA = true
  state.orderFilters.length = 0
  await login(page, 'source')
  await expect(page.getByText('tenant-a · nhan-vien-a')).toBeVisible()
  await page.goto('/work/ar?order=' + orderA)
  await expect(page.getByRole('region', { name: 'Kết quả báo cáo AR' })).toBeVisible()
  await expect(order).toHaveValue('')
  expect(state.orderFilters.length).toBeGreaterThan(0)
  expect(state.orderFilters.every(filter => filter === '')).toBe(true)
})

test('APP-03 keeps the AR order filter for a Sales writer assigned only to its successor', async ({ page }) => {
  const state = await mockWorkspace(page)
  state.withoutTripA = true
  state.successorArAssignment = true
  await page.goto('/work/ar?order=' + orderA)
  await login(page, 'source')

  await expect(page.getByLabel('Mã đơn A')).toHaveValue(orderA)
  await expect(page).toHaveURL(new RegExp('order=' + orderA))
  await expect(page.getByText('Chưa có bằng chứng giao hàng trong phạm vi này.')).toBeVisible()
  expect(state.orderFilters).toEqual([orderA])

  state.successorArAssignment = false
  await page.reload()
  await expect(page.getByLabel('Mã đơn A')).toHaveValue('')
  await expect(page).not.toHaveURL(new RegExp(orderA))
  await expect.poll(() => state.orderFilters).toEqual([orderA, ''])
})

test('APP-03 keeps a stale order link scoped until Warehouse opens other work', async ({ page }) => {
  const state = await mockWorkspace(page)
  state.loadRights = [{ id: pendingId, orderId: tripPeer, warehouseId: 'main', warehouseCode: 'MAIN',
    skuCode: 'OTHER', baseUom: 'EA', granted: '4', loaded: '0', warehouseHeld: '4' }]
  const link = `/work/load?trip=${tripA}&order=${orderA}`
  await page.goto(link)
  await login(page, 'source')
  await expect(page.getByText('Đơn này không có dòng giữ hàng để nạp trên chuyến đã chọn.')).toBeVisible()
  await expect(page.getByLabel('Số lượng nạp dòng 1')).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Xem đơn và lịch sử lô' })).toHaveAttribute('href',
    `/work/orders?trip=${tripA}&order=${orderA}`)
  await page.getByRole('button', { name: 'Tất cả đơn trên chuyến' }).click()
  await expect(page.getByLabel('Số lượng nạp dòng 1')).toBeVisible()
  await expect(page).not.toHaveURL(/order=/)

  state.withoutTripA = true
  await page.goto(link)
  await expect(page.getByText('Chuyến trong liên kết không còn mở được với phân công hiện hành.')).toBeVisible()
  await expect(page.getByLabel('Số lượng nạp dòng 1')).toHaveCount(0)
  await page.getByRole('link', { name: 'Mở danh sách chuyến' }).click()
  await expect(page.getByRole('combobox', { name: 'Chuyến' })).toHaveValue(tripB)

  state.withoutTripA = false
  await page.evaluate(([tripId, commandId]) => {
    sessionStorage.setItem('app02c3.pending.v1.' + JSON.stringify(['test-run', 'tenant-a', 'nhan-vien-a']),
      JSON.stringify({ tripId, commandId, lines: [{ rightId: commandId, expectedWarehouseHeld: '4', quantity: '2' }] }))
  }, [tripB, pendingId])
  await page.goto(link)
  const recoveryTrip = page.getByRole('link', { name: 'Mở chuyến của lệnh nạp đang chờ' })
  await expect(recoveryTrip).toBeVisible()
  await expect(page.getByRole('button', { name: 'Gửi lại cùng yêu cầu' })).toHaveCount(0)
  await recoveryTrip.click()
  await expect(page.getByRole('combobox', { name: 'Chuyến' })).toHaveValue(tripB)
  await page.getByRole('button', { name: 'Tra cứu kết quả nạp' }).click()
  await expect.poll(() => state.loadLookups).toEqual([`/api/synthetic/app02c3/trips/${tripB}/commands/${pendingId}`])
})

test('APP-03 publishes the restored pending trip and the allowed fallback', async ({ page }) => {
  const state = await mockWorkspace(page)
  await page.goto('/work/load')
  await login(page, 'source')
  const loadTrip = page.getByRole('combobox', { name: 'Chuyến', exact: true })
  await expect(loadTrip).toHaveValue(tripA)
  await expect(nav(page, 'Giao hàng A')).toHaveAttribute('href', '/work/delivery?trip=' + tripA)
  await loadTrip.selectOption(tripB)
  await expect(page).toHaveURL(new RegExp('trip=' + tripB))
  await page.evaluate(([tripId, commandId]) => {
    sessionStorage.setItem('app02c3.pending.v1.' + JSON.stringify(['test-run', 'tenant-a', 'nhan-vien-a']),
      JSON.stringify({ tripId, commandId, lines: [] }))
  }, [tripA, pendingId])
  await page.reload()
  await expect(loadTrip).toHaveValue(tripA)
  await expect(page).toHaveURL(new RegExp('trip=' + tripA))
  await page.getByRole('button', { name: 'Tra cứu kết quả nạp' }).click()
  await expect.poll(() => state.loadLookups).toEqual([`/api/synthetic/app02c3/trips/${tripA}/commands/${pendingId}`])
  await expect(page.getByRole('button', { name: 'Tra cứu kết quả nạp' })).toHaveCount(0)
  await nav(page, 'Giao hàng A').click()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripA)
  await page.reload()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripA)

  await page.evaluate(([tripId, commandId, manifestLineId]) => {
    sessionStorage.setItem('app02c4.delivery.v1.' + JSON.stringify(['test-run', 'tenant-a', 'nhan-vien-a']),
      JSON.stringify({ tripId, commandId, manifestLineId, shipToVersion: 1, quantity: '1' }))
  }, [tripB, pendingId, orderA])
  await page.reload()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripB)
  await expect(nav(page, 'Nạp hàng lên xe')).toHaveAttribute('href', '/work/load?trip=' + tripB)
  await expect(page.getByRole('button', { name: 'Tra cứu kết quả giao' })).toBeVisible()

  state.noTrips = true
  await page.reload()
  await expect(page.getByText('Chưa có chuyến giao được phân công.')).toBeVisible()
  await expect(page).not.toHaveURL(/trip=/)
  await expect(nav(page, 'Nạp hàng lên xe')).toHaveAttribute('href', '/work/load')
})
test('APP-03 opens cashier handoff using the shared session', async ({ page }) => {
  await mockWorkspace(page)
  await page.route('**/health/live', route => route.fulfill({ json: {
    status: 'live', scope: 'APP-00A', businessWrites: false, workspaceProfile: 'APP-03.6-v1',
  } }))
  await page.route('**/api/synthetic/app03/session', route => route.fulfill({ json: {
    tenantId: 'tenant-a', actorId: 'cashier', sessionId: 'test-run', sales: false, warehouse: false,
    handoffRoles: ['cashier'], returnRole: null, expiresAt: Math.floor(Date.now() / 1000) + 60,
  } }))
  let reads = 0
  await page.route('**/api/synthetic/app02d3/trips', route => {
    reads++
    return route.fulfill({ json: [] })
  })
  await page.goto('/work/settlement')
  await login(page, 'cashier')
  await expect(nav(page, 'Đối soát chuyến và nộp tiền')).toBeVisible()
  await expect(page.getByText('Phiên này chưa được phân công chuyến.')).toBeVisible()
  await expect(page.getByLabel('Mã truy cập DEV')).toHaveCount(0)
  await page.getByRole('button', { name: 'Tải lại danh sách chuyến' }).click()
  await expect.poll(() => reads).toBe(2)
})

test('APP-03 blocks fresh unload until a reconnect refreshes the trip report', async ({ page, context }) => {
  const d3 = '/api/synthetic/app02d3'
  await mockWorkspace(page)
  await page.route('**/health/live', route => route.fulfill({ json: {
    status: 'live', scope: 'APP-00A', businessWrites: false, workspaceProfile: 'APP-03.6-v1',
  } }))
  await page.route('**/api/synthetic/app03/session', route => route.fulfill({ json: {
    tenantId: 'tenant-a', actorId: 'receiver', sessionId: 'test-run', sales: false, warehouse: true,
    handoffRoles: ['warehouse'], returnRole: null, expiresAt: Math.floor(Date.now() / 1000) + 120,
  } }))
  let failList = false
  let failReport = false
  let tripQty = 1
  const postedIds: string[] = []
  const lookedUpIds: string[] = []
  let releaseList!: () => void
  const listGate = new Promise<void>(resolve => { releaseList = resolve })
  let listStarted = false
  await page.route('**' + d3 + '/trips', async route => {
    if (failList) {
      listStarted = true
      await listGate
      return route.fulfill({ status: 503 })
    }
    return route.fulfill({ json: [{ tripId: tripA, label: 'Chuyến A' }] })
  })
  await page.route('**' + d3 + '/trips/' + tripA, route => failReport
    ? route.fulfill({ status: 503 })
    : route.fulfill({ json: {
      tripId: tripA, label: 'Chuyến A', tripStatus: 'PREPARED', canUnload: true,
      custody: 0, fund: 0, arA: 0, arB: 0, mainQty: 5, mainCost: 20000, tripQty, tripCost: 4000 * tripQty,
      aHold: tripQty, bQuota: 0, soldQty: 1, cogs: 4000, customerCredit: 0, totalAllocated: 0,
    } }))
  await page.route('**' + d3 + '/unload', route => {
    postedIds.push(route.request().postDataJSON().commandId)
    return route.abort()
  })
  await page.route('**' + d3 + '/trips/' + tripA + '/commands/receiver/*', route => {
    lookedUpIds.push(new URL(route.request().url()).pathname.split('/').at(-1)!)
    return route.fulfill({ status: 404 })
  })
  await page.goto('/work/settlement?trip=' + tripA)
  await login(page, 'receiver')
  const unload = page.getByRole('button', { name: 'Trả hàng tồn xe' })
  const refresh = page.getByRole('button', { name: /Tải lại (danh sách )?chuyến/ })
  await expect(unload).toBeEnabled()
  await expect(page.getByText('1 EA / 4.000 VND')).toBeVisible()

  failList = true
  await context.setOffline(true)
  await expect(unload).toBeDisabled()
  await context.setOffline(false)
  try {
    await expect.poll(() => listStarted).toBe(true)
    await expect(unload).toBeDisabled()
    await unload.evaluate(button => button.closest('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
    expect(postedIds).toHaveLength(0)
  } finally {
    releaseList()
  }
  await expect(page.getByText('Không tải được dữ liệu chuyến.')).toBeVisible()
  await expect(unload).toBeDisabled()

  failList = false
  failReport = true
  await refresh.click()
  await expect(page.getByText('Không tải được dữ liệu chuyến.')).toBeVisible()
  await expect(unload).toBeDisabled()

  failReport = false
  tripQty = 2
  await refresh.click()
  await expect(page.getByText('2 EA / 8.000 VND')).toBeVisible()
  await expect(unload).toBeEnabled()
  expect(postedIds).toHaveLength(0)
  await unload.click()
  await expect(page.getByText(/Chưa rõ kết quả/)).toBeVisible()
  expect(postedIds).toHaveLength(1)

  failReport = true
  await refresh.click()
  await expect(page.getByText('Không tải được dữ liệu chuyến.')).toBeVisible()
  await page.getByRole('button', { name: 'Tra cứu yêu cầu' }).click()
  await expect.poll(() => lookedUpIds).toEqual(postedIds)
  await expect(unload).toBeDisabled()
})

test('APP-03 Finance joins the shared session and keeps settlement cases in their existing workspace', async ({ page }) => {
  await mockWorkspace(page)
  await page.route('**/health/live', route => route.fulfill({ json: {
    status: 'live', scope: 'APP-00A', businessWrites: false, workspaceProfile: 'APP-03.7-v1',
  } }))
  await page.route('**' + app03 + '/session', route => route.fulfill({ json: {
    tenantId: 'tenant-a', actorId: 'finance', sessionId: 'finance-run', sales: false, warehouse: false,
    returnRole: 'finance', advanceRole: 'finance', giftRole: 'finance', adjustmentRole: 'finance',
    refundRole: 'finance', accountingRoles: ['accountant'], expiresAt: Math.floor(Date.now() / 1000) + 60,
  } }))
  await page.route('**/app02d4/accounting/queue', route => route.fulfill({ json: { sources: [], reports: [], cases: [], corrections: [] } }))
  await page.goto('/')
  await login(page, 'finance')
  await nav(page, 'Hàng đợi kế toán').click()
  await expect(page.getByRole('heading', { name: 'Hàng đợi kế toán' })).toBeVisible()
  await nav(page, 'Điều chỉnh giá vốn').click()
  await expect(page.getByLabel('Mã truy cập DEV')).toHaveCount(0)
  await nav(page, 'Credit và hoàn tiền').click()
  await expect(page.getByLabel('Mã truy cập DEV')).toHaveCount(0)
  await nav(page, 'Trả hàng').click()
  await expect(page).toHaveURL(/\/work\/returns/)
})
