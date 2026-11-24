import { expect, test } from '@playwright/test'

const app03 = '/api/synthetic/app03'
const c2 = '/api/synthetic/app02c2'
const tripA = '11111111-1111-1111-1111-111111111111'
const tripB = '22222222-2222-2222-2222-222222222222'
const tripPeer = '33333333-3333-3333-3333-333333333333'

test.use({ serviceWorkers: 'block' })

test('APP-03 uses one browser session and restores only the current identity’s trip', async ({ page }) => {
  let revoked = false
  await page.route('**/health/live', route => route.fulfill({ json: {
    status: 'live', scope: 'APP-00A', businessWrites: false, workspaceProfile: 'APP-03.1a-v1',
  } }))
  await page.route('**/api/synthetic/**', async route => {
    const path = new URL(route.request().url()).pathname
    const token = route.request().headers().authorization
    const peer = token === 'Bearer peer'
    const actorId = peer ? 'nhan-vien-b' : 'nhan-vien-a'
    const identity = { tenantId: peer ? 'tenant-b' : 'tenant-a', actorId, sessionId: 'test-run',
      sales: true, warehouse: true }
    if (path === app03 + '/session') {
      if (revoked) return route.fulfill({ status: 403 })
      return route.fulfill({ json: { ...identity, returnRole: 'sales', expiresAt: Math.floor(Date.now() / 1000) + 60 } })
    }
    if (path === c2 + '/session') return route.fulfill({ json: identity })
    if (path.endsWith('/trips')) return route.fulfill({ json: peer ? [{ id: tripPeer, code: 'Chuyến đối chứng' }]
      : [{ id: tripA, code: 'Chuyến A' }, { id: tripB, code: 'Chuyến B' }] })
    return route.fulfill({ json: [] })
  })

  async function login(token: string) {
    await page.getByLabel('Mã truy cập DEV').fill(token)
    await page.getByRole('button', { name: 'Kết nối' }).click()
  }

  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'FMCG DMS · APP-03 DEV' })).toBeVisible()
  await login('source')
  const nav = page.getByRole('navigation', { name: 'Công việc DEV' })
  await expect(nav.getByRole('link', { name: /Mixed|Tạm ứng|Hoàn tiền/ })).toHaveCount(0)
  await nav.getByRole('link', { name: 'Nạp hàng lên xe' }).click()
  await page.getByRole('combobox', { name: 'Chuyến', exact: true }).selectOption(tripB)
  await expect(page).toHaveURL(new RegExp(`trip=${tripB}`))
  await nav.getByRole('link', { name: 'Giao hàng A' }).click()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripB)
  await page.reload()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripB)
  await expect(page.getByLabel('Mã truy cập DEV')).toHaveCount(0)

  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await expect(page).not.toHaveURL(/trip=/)
  await login('peer')
  await expect(page.getByText('tenant-b · nhan-vien-b', { exact: true })).toBeVisible()
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripPeer)
  await page.getByRole('button', { name: 'Đổi danh tính' }).click()
  await login('source')
  await expect(page.getByLabel('Chuyến giao')).toHaveValue(tripB)

  revoked = true
  await page.reload()
  await expect(page.getByRole('alert')).toContainText('quyền')
  await expect(page.getByLabel('Chuyến giao')).toHaveCount(0)
})
