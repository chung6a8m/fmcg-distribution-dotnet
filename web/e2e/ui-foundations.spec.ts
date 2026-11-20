import { expect, test, type Locator } from '@playwright/test'

import { attachEvidence } from './support/evidence'

test.use({ serviceWorkers: 'block' })

const demo = '/ui/foundations'
const name = 'Tên điểm bán'
const theme = 'Chế độ hiển thị'

test('opens from the health shell and keeps native field semantics', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('link', { name: 'Nền tảng giao diện' }).click()
  await expect(page).toHaveURL(new RegExp(demo + '$'))
  await expect(page.getByRole('heading', { name: 'Nền tảng giao diện', level: 1 })).toBeVisible()
  const outlet = page.getByLabel(name, { exact: true })
  await expect(outlet).toHaveValue(/Cửa hàng.*[Đđ]/)
  await outlet.fill('Điểm bán Nguyễn Huệ — phường Bến Nghé')
  await outlet.evaluate((input: HTMLInputElement) => input.setSelectionRange(4, 8))
  const density = page.getByLabel('Mật độ')
  await expect(density).toHaveValue('compact')
  await density.selectOption('comfortable')
  await expect(outlet).toHaveValue('Điểm bán Nguyễn Huệ — phường Bến Nghé')
  expect(await outlet.evaluate((input: HTMLInputElement) => [input.selectionStart, input.selectionEnd])).toEqual([4, 8])
  const mode = page.getByLabel(theme)
  await mode.selectOption('dark')
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  await expect(density).toHaveValue('comfortable')
  const reference = page.getByLabel('Mã tham chiếu (chỉ đọc)')
  await reference.focus()
  await expect(reference).toBeFocused()
  await reference.press('ControlOrMeta+a')
  await reference.press('Backspace')
  await expect(reference).toHaveValue('UI-01-DEMO')
  await expect(reference).not.toBeEditable()
  const disabled = page.getByLabel('Mã nội bộ (không khả dụng)')
  await expect(disabled).toBeDisabled()
  await expect(disabled).toHaveAccessibleDescription('Mã này được đặt sẵn trong bản minh họa.')
  const invalid = page.getByLabel('Số lượng mẫu', { exact: true })
  await expect(invalid).toHaveAttribute('aria-invalid', 'true')
  await expect(invalid).toHaveAccessibleDescription(/số nguyên dương/i)
  await invalid.fill('3')
  await expect(invalid).not.toHaveAttribute('aria-invalid', 'true')
  await page.getByRole('link', { name: 'Trạng thái kết nối' }).click()
  await expect(page.getByRole('heading', { name: 'FMCG Distribution · APP-00A' })).toBeVisible()
})

test('pending guards duplicate activations and reports one local simulation', async ({ page }) => {
  const dataRequests: string[] = []
  page.on('request', request => {
    if (['fetch', 'xhr'].includes(request.resourceType())) dataRequests.push(request.url())
  })
  await page.goto(demo)
  const run = page.getByRole('button', { name: 'Chạy tác vụ mẫu' })
  await page.clock.install()
  await page.clock.pauseAt(new Date(Date.now() + 60_000))
  await run.evaluate((button: HTMLButtonElement) => { button.click(); button.click() })
  await expect(run).toBeDisabled()
  const status = page.getByRole('status')
  await expect(status).toContainText('Đang chạy')
  await page.clock.fastForward(1000)
  await expect(status).toHaveText('Đã hoàn tất mô phỏng lần 1.')
  await expect(run).toBeEnabled()
  await run.press('Enter')
  await page.clock.fastForward(1000)
  await expect(status).toHaveText('Đã hoàn tất mô phỏng lần 2.')
  expect(dataRequests).toEqual([])
})

test('native portal dialog shares theme and returns keyboard focus', async ({ page }) => {
  await page.goto(demo)
  const opener = page.getByRole('button', { name: 'Mở hộp thoại' })
  await opener.press('Enter')
  const dialog = page.getByRole('dialog', { name: 'Hộp thoại mẫu' })
  await expect(dialog.getByLabel('Ghi chú trong hộp thoại')).toBeFocused()
  expect(await dialog.evaluate(el => el.parentElement === document.body && el.matches(':modal'))).toBe(true)
  // The native modal makes outside controls inert even under programmatic focus.
  await page.locator('main input').first().evaluate(el => el.focus())
  await expect(dialog.getByLabel('Ghi chú trong hộp thoại')).toBeFocused()
  await dialog.getByLabel(theme).selectOption('dark')
  await expect(dialog).toHaveCSS('color-scheme', 'dark')
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await expect(opener).toBeFocused()
  await expect(page.getByLabel(theme)).toHaveValue('dark')
  await opener.click()
  await dialog.getByRole('button', { name: 'Đóng hộp thoại' }).click()
  await expect(opener).toBeFocused()
})

async function height(locator: Locator) {
  return locator.evaluate(el => el.getBoundingClientRect().height)
}

for (const size of [
  { width: 1440, height: 900 }, { width: 1024, height: 768 },
  { width: 430, height: 932 }, { width: 320, height: 800 }
]) {
  test('density, table and readable Vietnamese at ' + size.width + ' CSS px', async ({ page, browser }, info) => {
    await page.setViewportSize(size)
    await page.goto(demo)
    const density = page.getByLabel('Mật độ')
    const table = page.getByRole('table', { name: 'Danh mục mẫu' })
    const reader = table.getByRole('row').filter({ hasText: 'SP-001' })
    const editor = page.getByRole('spinbutton', { name: 'Số lượng SP-003' })
    await expect(reader).toContainText('không đường')
    await expect(editor).toBeEditable()
    await editor.fill('17')
    await page.evaluate(() => document.fonts.ready)
    const measurements = []
    try {
      for (const choice of ['compact', 'comfortable', 'field']) {
        await density.selectOption(choice)
        const field = size.width < 768 || choice === 'field'
        await expect(page.getByRole('main')).toHaveCSS('font-size', field ? '16px' : '14px')
        await expect(page.getByRole('main')).toHaveCSS('line-height', field ? '24px' : '20px')
        await expect(editor).toHaveValue('17')
        const control = await height(density)
        const row = await height(reader)
        const editorRow = await height(table.getByRole('row').filter({ has: editor }))
        const geometry = await page.evaluate(() => ({
          pageWidth: document.documentElement.scrollWidth, viewport: document.documentElement.clientWidth,
          targets: [...document.querySelectorAll<HTMLElement>('main a, main button, main input, main select')]
            .filter(el => el.getClientRects().length > 0)
            .map(el => ({ name: el.id || el.textContent?.trim(), width: el.offsetWidth, height: el.offsetHeight }))
        }))
        measurements.push({ choice, field, control, row, editorRow, ...geometry })
        expect(geometry.pageWidth).toBeLessThanOrEqual(geometry.viewport)
        if (field) {
          for (const target of geometry.targets) {
            expect(Math.min(target.width, target.height), target.name).toBeGreaterThanOrEqual(48)
          }
          await expect(reader.getByText('Số lượng', { exact: true })).toBeVisible()
        } else {
          const compact = choice === 'compact'
          expect(control).toBe(compact ? 32 : 40)
          expect(row).toBeGreaterThanOrEqual(compact ? 36 : 44)
          expect(editorRow).toBeGreaterThanOrEqual(compact ? 41 : 49)
          await expect(reader).toHaveCSS('font-size', '13px')
          await expect(reader).toHaveCSS('line-height', '18px')
        }
        if (choice === 'compact') await info.attach('foundations-' + size.width, {
          body: await page.screenshot({ fullPage: true }), contentType: 'image/png'
        })
      }
    } finally {
      await attachEvidence(info, 'measurements-' + size.width, browser.version(), measurements)
    }
  })
}
