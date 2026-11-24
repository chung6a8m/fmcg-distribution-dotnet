import { expect, test, type Page } from '@playwright/test'

const app03 = '/api/synthetic/app03'
const c2 = '/api/synthetic/app02c2'
const tripA = '11111111-1111-1111-1111-111111111111'
const tripB = '22222222-2222-2222-2222-222222222222'
const tripPeer = '33333333-3333-3333-3333-333333333333'
const orderA = '44444444-4444-4444-4444-444444444444'
const pendingId = '55555555-5555-5555-5555-555555555555'

test.use({ serviceWorkers: 'block' })

async function mockWorkspace(page: Page) {
  const state = { revoked: false, noTrips: false, withoutTripA: false,
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
    if (path === '/api/synthetic/app02c5/ar') {
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
