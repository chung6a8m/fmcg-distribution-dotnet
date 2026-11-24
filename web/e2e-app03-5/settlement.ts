import { expect, type Page, type APIRequestContext, type TestInfo } from '@playwright/test'

const d3 = '/api/synthetic/app02d3'
const c4 = '/api/synthetic/app02c4'
const auth = (token: string) => ({ Authorization: 'Bearer ' + token })

export async function verifySettlement(page: Page, request: APIRequestContext, info: TestInfo,
  sales: string, warehouse: string, peer: string, trip: string, bOnly: string, customerId: string,
  signIn: (page: Page, token: string) => Promise<void>) {
  const { APP03_CASHIER_TOKEN: cashier, APP03_FINANCE_TOKEN: finance, APP03_QA_TOKEN: qa } = process.env
  if (!cashier || !finance || !qa) throw new Error('Missing APP-03.6 credentials')
  const command = (actor: string) => ({
    commandId: crypto.randomUUID(), sourceNumber: crypto.randomUUID(), producerId: actor, tripId: trip,
  })
  const cashierPage = await page.context().newPage()
  const warehousePage = await page.context().newPage()
  const financePage = await page.context().newPage()
  const qaPage = await page.context().newPage()
  const settlement = '/work/settlement?trip=' + trip
  async function read(path: string, token: string) {
    const response = await request.get(path, { headers: auth(token) })
    expect(response.status(), await response.text()).toBe(200)
    return response.json()
  }
  const report = () => read(d3 + '/trips/' + trip, sales)
  await page.goto(settlement)
  await expect(page.getByText('20.000 VND', { exact: true })).not.toHaveCount(0)
  expect(await report()).toMatchObject({ custody: 20000, fund: 0, aHold: 3, bQuota: 1, tripQty: 4 })

  await page.getByLabel('Số tiền đề nghị nộp').fill('20000')
  await page.getByRole('button', { name: 'Gửi đề nghị nộp tiền' }).click()
  await expect(page.getByText('Đã gửi đề nghị nộp tiền.')).toBeVisible()
  const offered = await report()
  expect(offered).toMatchObject({ custody: 20000, fund: 0, pendingOfferAmount: 20000 })
  const accept = { ...command('app03-cashier'), offerId: offered.pendingOfferId, countedAmount: 20000 }
  expect((await request.post(d3 + '/accept', { headers: auth(sales), data: accept })).status()).toBe(403)
  expect((await request.get(d3 + '/trips/' + trip, { headers: auth(peer) })).status()).toBe(403)
  const blocked = await request.post(d3 + '/unload', {
    headers: auth(warehouse), data: command('f05-receiver'),
  })
  expect(await blocked.json()).toMatchObject({ status: 'REJECTED', reasonCode: 'CASH_HANDOVER_PENDING' })

  await cashierPage.goto('/work/settlement')
  await signIn(cashierPage, cashier)
  await expect(cashierPage.getByRole('combobox', { name: 'Chuyến', exact: true })).toHaveValue(trip)
  await expect(cashierPage.getByText('Đề nghị đang chờ nhận 20.000 VND.')).toBeVisible()
  await cashierPage.getByLabel('Số tiền kiểm đếm (VND)').fill('19999')
  await cashierPage.getByRole('button', { name: 'Xác nhận đã nhận tiền' }).click()
  await expect(cashierPage.getByText('Chứng cứ kiểm đếm chưa khớp đề nghị.')).toBeVisible()
  expect(await report()).toEqual(offered)
  let applied: { commandId: string; producerId: string; sourceNumber: string } | undefined
  await cashierPage.route('**' + d3 + '/accept', async route => {
    applied = route.request().postDataJSON()
    const response = await route.fetch()
    expect(await response.json()).toMatchObject({ status: 'APPLIED' })
    await route.abort()
  }, { times: 1 })
  await cashierPage.getByLabel('Số tiền kiểm đếm (VND)').fill('20000')
  await cashierPage.getByRole('button', { name: 'Xác nhận đã nhận tiền' }).click()
  await expect(cashierPage.getByText(/Chưa rõ kết quả/)).toBeVisible()
  await cashierPage.reload()
  await cashierPage.getByRole('button', { name: 'Tra cứu yêu cầu' }).click()
  await expect(cashierPage.getByText('Đã ghi nhận thủ quỹ nhận tiền.')).toBeVisible()
  const replay = await request.post(d3 + '/accept', { headers: auth(cashier), data: applied })
  expect(await replay.json()).toMatchObject({ status: 'APPLIED', replayed: true })
  expect((await request.post(d3 + '/accept', {
    headers: auth(cashier), data: { ...applied, countedAmount: 1 },
  })).status()).toBe(409)
  expect(await report()).toMatchObject({ custody: 0, fund: 20000, pendingOfferId: null })

  await warehousePage.goto(settlement)
  await signIn(warehousePage, warehouse)
  await warehousePage.getByRole('button', { name: 'Trả hàng tồn xe' }).click()
  await expect(warehousePage.getByText('Đã trả hàng tồn xe về kho.')).toBeVisible()
  expect(await report()).toMatchObject({ tripStatus: 'RECONCILING', tripQty: 0, aHold: 3, bQuota: 0 })
  const orders = await read('/api/synthetic/app03/handoff', sales)
  for (const [sku, held] of [['R-05', 1], ['Z-FIELD-EA', 2]] as const) {
    const line = orders.flatMap((order: { lines: unknown[] }) => order.lines)
      .find((line: { sku: string }) => line.sku === sku)
    expect(line).toMatchObject({ reserved: held, heldOnTrip: 0, handoff: { warehouseHeld: held } })
  }
  await warehousePage.screenshot({ path: info.outputPath('warehouse-reconciling.png'), fullPage: true })

  const arBefore = await read(c4 + '/trips/' + trip + '/ar', sales)
  const cashBefore = await report()
  await financePage.goto('/work/advances')
  await signIn(financePage, finance)
  await financePage.getByLabel('Nhân viên').selectOption('c1-dev-sales')
  await financePage.getByLabel('Số tiền (VND)').fill('500000')
  await financePage.getByLabel('Chứng từ DEV').fill('APP03-F02-ISSUE')
  await financePage.getByRole('button', { name: 'Phát ứng' }).click()
  await expect(financePage.getByTestId('advance-outstanding')).toHaveText('500.000 VND')
  await page.goto('/work/advances')
  await page.getByLabel('Thao tác').selectOption('RETURN_OFFER')
  await page.getByLabel('Số tiền (VND)').fill('200000')
  await page.getByLabel('Chứng từ DEV').fill('APP03-F02-OFFER')
  await page.getByRole('button', { name: 'Đề nghị hoàn ứng' }).click()
  await expect(page.getByText('Đã ghi sổ tạm ứng.')).toBeVisible()
  await cashierPage.goto('/work/advances')
  await cashierPage.getByLabel('Nguồn đề nghị').selectOption({ label: 'APP03-F02-OFFER' })
  await cashierPage.getByLabel('Số tiền (VND)').fill('200000')
  await cashierPage.getByLabel('Lý do xác nhận').fill('Đã nhận hoàn ứng')
  await cashierPage.getByLabel('Chứng từ DEV').fill('APP03-F02-ACCEPT')
  await cashierPage.getByRole('button', { name: 'Xác nhận đã nhận hoàn ứng' }).click()
  await expect(cashierPage.getByTestId('advance-outstanding')).toHaveText('300.000 VND')
  expect(await read(c4 + '/trips/' + trip + '/ar', sales)).toEqual(arBefore)
  expect(await report()).toEqual(cashBefore)

  await page.goto('/work/gifts')
  await page.getByLabel('Khách hàng', { exact: true }).selectOption(customerId)
  await page.getByLabel('Số lượng quà').fill('1')
  await page.getByLabel('Lý do').fill('Đủ mua hai EA có lô')
  await page.getByLabel('Chứng từ DEV').fill('APP03-F07-ISSUE')
  await page.getByRole('button', { name: 'Giao quà', exact: true }).click()
  await expect(page.getByText('Đã ghi nghiệp vụ quà.')).toBeVisible()

  await page.goto('/work/returns')
  await page.getByLabel('Nguồn giao và lô').selectOption({ index: 1 })
  await page.getByLabel('Số lượng trả').fill('1')
  await page.getByLabel('Lý do').fill('Khách trả một EA')
  await page.getByRole('button', { name: 'Đề nghị trả hàng' }).click()
  await expect(page.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  await warehousePage.goto('/work/returns')
  await warehousePage.getByRole('combobox', { name: 'Hồ sơ trả hàng', exact: true }).selectOption({ index: 1 })
  await warehousePage.getByLabel('Số lượng thực nhận').fill('1')
  await warehousePage.getByLabel('Lý do').fill('Đã kiểm hàng')
  await warehousePage.getByLabel('Chứng từ DEV').fill('APP03-F05-RECEIVE')
  await warehousePage.getByRole('button', { name: 'Nhận vào cách ly' }).click()
  await expect(warehousePage.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()

  await financePage.goto('/work/returns')
  await financePage.getByRole('combobox', { name: 'Hồ sơ trả hàng', exact: true }).selectOption({ index: 1 })
  await financePage.getByLabel('Lý do').fill('Chấp thuận sau kiểm tra')
  await financePage.getByLabel('Chứng từ DEV').fill('APP03-F05-APPROVE')
  await financePage.getByRole('button', { name: 'Duyệt trả hàng' }).click()
  await expect(financePage.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()
  await qaPage.goto('/work/returns')
  await signIn(qaPage, qa)
  await qaPage.getByRole('combobox', { name: 'Hồ sơ trả hàng', exact: true }).selectOption({ index: 1 })
  await qaPage.getByLabel('Lý do').fill('Đủ điều kiện nhập kho')
  await qaPage.getByLabel('Chứng từ DEV').fill('APP03-F05-RELEASE')
  await qaPage.getByRole('button', { name: 'Giải phóng hàng' }).click()
  await expect(qaPage.getByText('Đã ghi hồ sơ trả hàng.')).toBeVisible()

  await warehousePage.goto('/work/gifts')
  await warehousePage.getByLabel('Lần giao quà').selectOption({ index: 1 })
  await warehousePage.getByLabel('Số lượng quà thực nhận').fill('1')
  await warehousePage.getByLabel('Lý do').fill('Thu hồi quà theo lượng mua ròng')
  await warehousePage.getByLabel('Chứng từ DEV').fill('APP03-F07-RETURN')
  await warehousePage.getByRole('button', { name: 'Nhận quà trả' }).click()
  await expect(warehousePage.getByText('Đã ghi nghiệp vụ quà.')).toBeVisible()
  const emptyOffer = await request.post(d3 + '/offer', { headers: auth(sales),
    data: { ...command('c1-dev-sales'), tripId: bOnly, offeredAmount: 20000 } })
  const emptyOfferBody = await emptyOffer.json()
  expect(emptyOfferBody).toMatchObject({ status: 'APPLIED' })
  const emptyAccept = await request.post(d3 + '/accept', { headers: auth(cashier),
    data: { ...command('app03-cashier'), tripId: bOnly, offerId: emptyOfferBody.sourceId, countedAmount: 20000 } })
  expect(await emptyAccept.json()).toMatchObject({ status: 'APPLIED' })
  await warehousePage.goto('/work/settlement?trip=' + bOnly)
  await warehousePage.getByRole('button', { name: 'Bàn giao để đối soát' }).click()
  await expect(warehousePage.getByText('Đã ghi bàn giao chuyến để đối soát.')).toBeVisible()
  expect(await read(d3 + '/trips/' + bOnly, warehouse)).toMatchObject({
    tripStatus: 'RECONCILING', tripQty: 0, bQuota: 0, fund: 20000,
  })
  await Promise.all([cashierPage, warehousePage, financePage, qaPage].map(tab => tab.close()))
}
