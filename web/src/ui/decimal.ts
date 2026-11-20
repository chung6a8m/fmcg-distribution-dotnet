// UI-02 demonstration rule only: positive decimal with up to 3 fractional digits,
// no grouping separators. Real precision, min/max and units come from source contracts.
export function parseDemoQuantity(raw: string): number | null {
  const value = raw.trim()
  if (!/^\d+(?:[.,]\d{1,3})?$/.test(value)) return null
  const amount = Number(value.replace(',', '.'))
  return Number.isFinite(amount) && amount > 0 ? amount : null
}

export const demoQuantityError = 'Nhập số dương (ví dụ 1,25 hoặc 1.25), tối đa 3 chữ số thập phân; không nhập dấu phân nhóm.'
