import assert from 'node:assert/strict'

export async function measureTable(page) {
  return page.evaluate(() => {
    const rect = element => {
      const {left, top, right, bottom, width, height} = element.getBoundingClientRect()
      return {left, top, right, bottom, width, height}
    }
    const text = selector => {
      const element = document.querySelector(selector)
      const range = document.createRange()
      range.selectNodeContents(element)
      return {bounds: rect(element), lines: [...range.getClientRects()].map(({left, top, right, bottom, width, height}) =>
        ({left, top, right, bottom, width, height})), scroll: [element.scrollWidth, element.scrollHeight],
      client: [element.clientWidth, element.clientHeight]}
    }
    const styles = (selector, names) => {
      const style = getComputedStyle(document.querySelector(selector))
      return Object.fromEntries(names.split(' ').map(name => [name, style.getPropertyValue(name)]))
    }
    const inViewport = ({left, top, right, bottom}) =>
      left >= 0 && top >= 0 && right <= innerWidth && bottom <= innerHeight
    const row = kind => document.querySelector(`tr[data-kind="${kind}"]`)
    const table = document.querySelector('table')
    const root = document.documentElement
    const editor = document.querySelector('#editor')
    const invalid = document.querySelector('#invalid')
    const editorStyle = getComputedStyle(editor)
    const bounds = rect(editor)
    const outset = (parseFloat(editorStyle.outlineWidth) || 0) + (parseFloat(editorStyle.outlineOffset) || 0)
    const ring = {left: bounds.left - outset, top: bounds.top - outset,
      right: bounds.right + outset, bottom: bounds.bottom + outset}
    const controlStyle = 'min-height box-sizing display font-family font-size line-height padding-inline-start padding-block-start border-top-width background-color border-top-color outline-width outline-offset outline-style outline-color color-scheme'
    const cellStyle = 'padding-inline-start padding-inline-end padding-block-start padding-block-end border-bottom-width border-bottom-color font-size line-height overflow'
    const translated = {}
    // Resolve semantic custom properties through computed color, including CSS aliases.
    const probe = document.createElement('span')
    root.append(probe)
    for (const [name, token] of Object.entries({
      page: 'surface-page', table: 'surface-card', header: 'surface-subtle', alternate: 'surface-row-alternate',
      text: 'text-primary', error: 'status-danger-foreground', invalidBorder: 'status-danger-foreground',
      statusBackground: 'status-info-background', statusText: 'status-info-foreground', focus: 'focus-ring',
      editorBorder: 'border-control', divider: 'border-subtle',
    })) {
      probe.style.color = `var(--fmcg-color-${token})`
      translated[name] = getComputedStyle(probe).color
    }
    probe.remove()
    const color = (selector, property) => getComputedStyle(document.querySelector(selector))[property]
    return {
      viewport: [innerWidth, innerHeight], dpr: devicePixelRatio, visualViewportScale: visualViewport.scale,
      documentWidth: root.scrollWidth, lang: root.lang, theme: root.dataset.theme,
      rootScheme: getComputedStyle(root).colorScheme, prefersDark: matchMedia('(prefers-color-scheme: dark)').matches,
      table: {bounds: rect(table), style: styles('table', 'display border-collapse border-spacing font-family'),
        rowspan: table.querySelectorAll('[rowspan]').length,
        usefulRows: [...table.tBodies[0].rows].filter(item => inViewport(rect(item))).length,
        usefulColumns: [...table.tHead.rows[0].cells].filter(item => inViewport(rect(item))).length,
        totalRows: table.tBodies[0].rows.length, totalColumns: table.tHead.rows[0].cells.length},
      rows: Object.fromEntries(['read', 'before', 'edit', 'long', 'validation'].map(kind => [kind, rect(row(kind))])),
      rowDisplay: getComputedStyle(row('read')).display,
      readCell: styles('[data-kind=read] td', cellStyle),
      editCell: {bounds: rect(editor.closest('td')), style: styles('[data-kind=edit] .editor-cell', cellStyle)},
      button: {bounds: rect(document.querySelector('button')), style: styles('button', controlStyle)},
      editor: {bounds, ring, style: styles('#editor', controlStyle), focusVisible: editor.matches(':focus-visible')},
      invalid: {bounds: rect(invalid), value: invalid.value, state: invalid.getAttribute('aria-invalid'),
        errorId: invalid.getAttribute('aria-describedby')},
      long: text('[data-kind=long] .product > span'), error: text('#quantity-error'),
      actualColors: {
        page: color('body', 'backgroundColor'), table: color('table', 'backgroundColor'),
        header: color('thead', 'backgroundColor'), alternate: color('[data-kind=before]', 'backgroundColor'),
        text: color('table', 'color'), error: color('#quantity-error', 'color'),
        invalidBorder: color('#invalid', 'borderTopColor'), editorBorder: editorStyle.borderTopColor,
        statusBackground: color('[data-kind=edit] .status', 'backgroundColor'),
        statusText: color('[data-kind=edit] .status', 'color'), focus: editorStyle.outlineColor,
        divider: color('[data-kind=read] td', 'borderBottomColor'),
      },
      semanticColors: translated,
    }
  })
}

// Rects are CSS px. Chromium quantizes fractional layout to 1/64px, so permit
// at most one layout unit in geometric edge comparisons, never for CSS styles.
const epsilon = 1 / 64
export function contains(outer, inner) {
  return inner.left >= outer.left - epsilon && inner.right <= outer.right + epsilon &&
    inner.top >= outer.top - epsilon && inner.bottom <= outer.bottom + epsilon
}

export function verifyTable(data, {theme, density, viewport}) {
  const [rowMin, controlMin] = density === 'compact' ? [36, 32] : [44, 40]
  assert.deepEqual(data.viewport, viewport)
  assert.equal(data.dpr, 1)
  assert.equal(data.visualViewportScale, 1)
  assert.equal(data.documentWidth, viewport[0])
  assert.equal(data.lang, 'vi')
  assert.equal(data.theme, theme)
  assert.equal(data.rootScheme, theme)
  assert.equal(data.editor.style['color-scheme'], theme)
  assert.equal(data.prefersDark, theme === 'light', 'Opposite OS preference must not override root choice')
  assert.deepEqual(data.actualColors, data.semanticColors)
  assert.equal(data.table.style.display, 'table')
  assert.equal(data.table.style['border-collapse'], 'separate')
  assert.equal(data.table.style['border-spacing'], '0px')
  assert.equal(data.table.rowspan, 0)
  assert.equal(data.rowDisplay, 'table-row')
  assert.equal(data.table.usefulColumns, 7)
  assert.ok(data.table.usefulRows >= 5)
  for (const [kind, bounds] of Object.entries(data.rows)) assert.ok(bounds.height >= rowMin, kind)
  assert.equal(data.rows.read.height, rowMin, 'This fixture uses the floor for single-line read rows')
  for (const control of [data.editor, data.invalid, data.button]) {
    assert.ok(control.bounds.height >= controlMin && control.bounds.width >= controlMin)
  }
  for (const cell of [data.readCell, data.editCell.style]) {
    assert.equal(cell['font-size'], '13px')
    assert.equal(cell['line-height'], '18px')
    assert.equal(cell['padding-inline-start'], '8px')
    assert.equal(cell['padding-inline-end'], '8px')
    assert.equal(cell['border-bottom-width'], '1px')
    assert.equal(cell.overflow, 'visible')
  }
  assert.equal(data.readCell['padding-block-start'], '0px')
  assert.equal(data.readCell['padding-block-end'], '0px')
  assert.equal(data.editCell.style['padding-block-start'], '4px')
  assert.equal(data.editCell.style['padding-block-end'], '4px')
  assert.equal(data.editor.style['box-sizing'], 'border-box')
  assert.equal(data.editor.style.display, 'block')
  assert.equal(data.editor.style['font-size'], '13px')
  assert.equal(data.editor.style['line-height'], '18px')
  for (const [content, row] of [[data.long, data.rows.long], [data.error, data.rows.validation]]) {
    assert.ok(content.lines.length > 1 && row.height > data.rows.read.height)
    assert.deepEqual(content.client, content.scroll)
    assert.ok(contains(row, content.bounds))
    for (const line of content.lines) assert.ok(contains(row, line), 'Each line stays inside its row')
  }
  assert.equal(data.invalid.state, 'true')
  assert.equal(data.invalid.errorId, 'quantity-error')
  assert.equal(data.invalid.value, '36')
  assert.ok(data.error.bounds.top >= data.invalid.bounds.bottom + 4)
  assert.ok(data.editor.focusVisible)
  assert.equal(data.editor.style['outline-width'], '2px')
  assert.equal(data.editor.style['outline-offset'], '2px')
  assert.equal(data.editor.style['outline-style'], 'solid')
  assert.ok(contains(data.rows.edit, data.editor.ring), 'Focus outline crosses into an adjacent row')
  assert.ok(contains(data.editCell.bounds, data.editor.ring), 'Focus outline crosses the editor cell')
}

// At DPR 1 sample both pixels of the four straight sides, away from rounded
// corners. Compare each with the matching pixel before focus to prove paint.
export async function measurePaint(page, before, after, {bounds, ring, style}) {
  return page.evaluate(async ({before, after, bounds, ring, color}) => {
    const x = Math.floor((bounds.left + bounds.right) / 2)
    const y = Math.floor((bounds.top + bounds.bottom) / 2)
    const points = {
      top: [[x, Math.floor(ring.top) + 1], [x, Math.floor(ring.top)]],
      bottom: [[x, Math.ceil(ring.bottom) - 2], [x, Math.ceil(ring.bottom) - 1]],
      left: [[Math.floor(ring.left) + 1, y], [Math.floor(ring.left), y]],
      right: [[Math.ceil(ring.right) - 2, y], [Math.ceil(ring.right) - 1, y]],
    }
    async function sample(base64) {
      const image = new Image()
      image.src = `data:image/png;base64,${base64}`
      await image.decode()
      const canvas = document.createElement('canvas')
      canvas.width = image.width
      canvas.height = image.height
      const ctx = canvas.getContext('2d', {willReadFrequently: true})
      ctx.drawImage(image, 0, 0)
      return Object.fromEntries(Object.entries(points).map(([side, coordinates]) => [side,
        coordinates.map(([x, y]) => [...ctx.getImageData(x, y, 1, 1).data])]))
    }
    return {points, expected: [...color.matchAll(/\d+/g)].map(match => Number(match[0])).concat(255),
      before: await sample(before), after: await sample(after)}
  }, {before: before.toString('base64'), after: after.toString('base64'), bounds, ring, color: style['outline-color']})
}
