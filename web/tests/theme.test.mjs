import assert from 'node:assert/strict'
import { existsSync, readFileSync } from 'node:fs'
import { test } from 'node:test'
import { runInNewContext } from 'node:vm'

const runtimeUrl = new URL('../public/theme-runtime.js', import.meta.url)
const preferenceKey = 'fmcg.ui.theme.v1'
const lightColor = '#F6F8FB'
const darkColor = '#101827'

function startRuntime({ stored = null, osDark = false, readBlocked = false, mediaApi = 'available' } = {}) {
  assert.ok(existsSync(runtimeUrl), 'Expected the public theme runtime to exist')
  const source = readFileSync(runtimeUrl, 'utf8')
  const root = { dataset: {} }
  const colorScheme = { content: 'light' }
  const themeColor = { content: lightColor, dataset: { light: lightColor, dark: darkColor } }
  const meta = new Map([
    ['meta[name="color-scheme"]', colorScheme],
    ['meta[name="theme-color"]', themeColor]
  ])
  const document = { documentElement: root, querySelector: selector => meta.get(selector) ?? null }
  const nativeListeners = new Set()
  const media = {
    get matches() { return osDark },
    addEventListener(type, listener) {
      assert.equal(type, 'change')
      if (mediaApi === 'add-throws') throw new Error('Media listener unavailable')
      nativeListeners.add(listener)
    },
    removeEventListener(type, listener) {
      assert.equal(type, 'change')
      nativeListeners.delete(listener)
    }
  }
  let storageReads = 0
  let writesBlocked = false
  const window = {
    localStorage: {
      getItem(key) {
        assert.equal(key, preferenceKey)
        storageReads += 1
        if (readBlocked) throw new Error('Storage read blocked')
        return stored
      },
      setItem(key, value) {
        assert.equal(key, preferenceKey)
        if (writesBlocked) throw new Error('Storage write blocked')
        stored = value
      }
    }
  }
  if (mediaApi !== 'absent') {
    window.matchMedia = query => {
      assert.equal(query, '(prefers-color-scheme: dark)')
      if (mediaApi === 'query-throws') throw new Error('Media query unavailable')
      return media
    }
  }

  runInNewContext(source, { window, document }, { filename: 'theme-runtime.js' })

  return {
    controller: window.FmcgTheme,
    root,
    colorScheme,
    themeColor,
    nativeListeners,
    storedPreference: () => stored,
    storageReads: () => storageReads,
    blockWrites(blocked) { writesBlocked = blocked },
    setOsDark(value) {
      osDark = value
      for (const listener of nativeListeners) listener({ matches: value })
    }
  }
}

function assertResolved({ controller, root, colorScheme, themeColor }, resolved, color) {
  assert.equal(controller.getSnapshot().resolved, resolved)
  assert.equal(root.dataset.theme, resolved)
  assert.equal(colorScheme.content, resolved)
  assert.equal(themeColor.content, color)
}

for (const [stored, osDark, color] of [
  ['light', true, lightColor],
  ['dark', false, darkColor]
]) {
  test(`saved ${stored} takes precedence over the opposite OS setting at startup`, () => {
    const runtime = startRuntime({ stored, osDark })
    assertResolved(runtime, stored, color)
    assert.equal(runtime.controller.getSnapshot().preference, stored)
    assert.equal(runtime.controller.getSnapshot().persistent, true)
    const unsubscribe = runtime.controller.subscribe(() => assert.fail('Explicit theme should ignore OS changes'))
    assert.equal(runtime.nativeListeners.size, 0)
    runtime.setOsDark(!osDark)
    runtime.setOsDark(osDark)
    assertResolved(runtime, stored, color)
    unsubscribe()
  })
}

for (const [label, storage] of [
  ['missing', {}],
  ['invalid', { stored: 'sepia' }],
  ['read-blocked', { stored: 'dark', readBlocked: true }]
]) {
  test(`${label} storage defaults to unsaved Light on a Dark OS`, () => {
    const runtime = startRuntime({ osDark: true, ...storage })
    assertResolved(runtime, 'light', lightColor)
    assert.equal(runtime.controller.getSnapshot().preference, 'light')
    assert.equal(runtime.controller.getSnapshot().persistent, false)
  })
}

for (const mediaApi of ['absent', 'query-throws']) {
  test(`System falls back to Light with a media API that is ${mediaApi}`, () => {
    const runtime = startRuntime({ stored: 'system', osDark: true, mediaApi })
    const unsubscribe = runtime.controller.subscribe(() => assert.fail('No available System color change'))
    assertResolved(runtime, 'light', lightColor)
    assert.equal(runtime.controller.getSnapshot().preference, 'system')
    assert.equal(runtime.controller.getSnapshot().persistent, true)
    unsubscribe()
  })
}

test('a throwing media subscription leaves the sampled System preference active', () => {
  const runtime = startRuntime({ stored: 'system', osDark: true, mediaApi: 'add-throws' })
  const unsubscribe = runtime.controller.subscribe(() => assert.fail('The sampled theme should stay active'))
  assertResolved(runtime, 'dark', darkColor)
  assert.equal(runtime.nativeListeners.size, 0)
  unsubscribe()
})

test('System updates the root metadata before notifying subscribers about an OS change', () => {
  const runtime = startRuntime({ stored: 'system', osDark: true })
  assertResolved(runtime, 'dark', darkColor)
  let notifications = 0
  const unsubscribe = runtime.controller.subscribe(() => {
    notifications += 1
    assertResolved(runtime, 'light', lightColor)
  })
  runtime.setOsDark(false)
  assert.equal(notifications, 1)
  assert.equal(runtime.controller.getSnapshot().preference, 'system')
  assert.equal(runtime.controller.getSnapshot().persistent, true)
  unsubscribe()
})

test('System tracks the OS only while subscribed and resamples it after resubscription', () => {
  const runtime = startRuntime({ stored: 'system' })
  const { controller, nativeListeners } = runtime
  assert.equal(nativeListeners.size, 0)
  runtime.setOsDark(true)
  assertResolved(runtime, 'light', lightColor)

  let firstNotifications = 0
  let secondNotifications = 0
  const unsubscribeFirst = controller.subscribe(() => { firstNotifications += 1 })
  assertResolved(runtime, 'dark', darkColor)
  const unsubscribeSecond = controller.subscribe(() => { secondNotifications += 1 })
  assert.equal(nativeListeners.size, 1)
  runtime.setOsDark(false)
  assert.equal(firstNotifications, 2)
  assert.equal(secondNotifications, 1)

  unsubscribeFirst()
  assert.equal(nativeListeners.size, 1)
  runtime.setOsDark(true)
  assert.equal(firstNotifications, 2)
  assert.equal(secondNotifications, 2)
  unsubscribeSecond()
  assert.equal(nativeListeners.size, 0)
  runtime.setOsDark(false)
  assertResolved(runtime, 'dark', darkColor)

  const unsubscribe = controller.subscribe(() => {})
  assertResolved(runtime, 'light', lightColor)
  assert.equal(nativeListeners.size, 1)
  unsubscribe()
  assert.equal(nativeListeners.size, 0)
})

test('selecting System samples the OS and an explicit selection detaches its listener', () => {
  const runtime = startRuntime({ stored: 'light', osDark: true })
  const { controller, nativeListeners } = runtime
  const unsubscribe = controller.subscribe(() => {})
  controller.setPreference('system')
  assertResolved(runtime, 'dark', darkColor)
  assert.equal(nativeListeners.size, 1)
  assert.equal(runtime.storedPreference(), 'system')

  controller.setPreference('dark')
  assert.equal(nativeListeners.size, 0)
  runtime.setOsDark(false)
  assertResolved(runtime, 'dark', darkColor)
  assert.equal(runtime.storedPreference(), 'dark')
  unsubscribe()
})

test('a failed write preserves the selected theme through unsubscribe and resubscribe', () => {
  const runtime = startRuntime({ stored: 'dark' })
  const { controller } = runtime
  const unsubscribe = controller.subscribe(() => {})
  runtime.blockWrites(true)
  controller.setPreference('light')
  const unsaved = controller.getSnapshot()
  assert.equal(unsaved.preference, 'light')
  assert.equal(unsaved.persistent, false)
  assert.equal(runtime.storedPreference(), 'dark')
  unsubscribe()

  const cleanup = controller.subscribe(() => assert.fail('Resubscription should preserve the unsaved choice'))
  assert.equal(runtime.storageReads(), 1)
  assert.equal(controller.getSnapshot(), unsaved)
  assertResolved(runtime, 'light', lightColor)
  cleanup()
})

test('snapshots stay frozen and cached until a field changes', () => {
  const { controller } = startRuntime({ stored: 'light', osDark: true })
  const initial = controller.getSnapshot()
  assert.equal(Object.isFrozen(initial), true)
  assert.equal(controller.getSnapshot(), initial)

  let notifications = 0
  const unsubscribe = controller.subscribe(() => { notifications += 1 })
  controller.setPreference('light')
  assert.equal(controller.getSnapshot(), initial)
  assert.equal(notifications, 0)

  controller.setPreference('dark')
  const changed = controller.getSnapshot()
  assert.notEqual(changed, initial)
  assert.equal(Object.isFrozen(changed), true)
  assert.equal(controller.getSnapshot(), changed)
  assert.equal(initial.resolved, 'light')
  assert.equal(notifications, 1)
  unsubscribe()
})

test('a preference change notifies even when the resolved color is unchanged', () => {
  const { controller } = startRuntime({ stored: 'light' })
  const initial = controller.getSnapshot()
  const snapshots = []
  const unsubscribe = controller.subscribe(() => snapshots.push(controller.getSnapshot()))
  controller.setPreference('system')
  assert.deepEqual(snapshots, [controller.getSnapshot()])
  assert.notEqual(snapshots[0], initial)
  assert.equal(snapshots[0].preference, 'system')
  assert.equal(snapshots[0].resolved, 'light')
  assert.equal(snapshots[0].persistent, true)
  unsubscribe()
})

test('retrying the same choice notifies only when storage succeeds', () => {
  const runtime = startRuntime()
  const { controller } = runtime
  const initial = controller.getSnapshot()
  const snapshots = []
  const unsubscribe = controller.subscribe(() => snapshots.push(controller.getSnapshot()))
  runtime.blockWrites(true)
  controller.setPreference('light')
  assert.equal(controller.getSnapshot(), initial)
  assert.equal(snapshots.length, 0)

  runtime.blockWrites(false)
  controller.setPreference('light')
  assert.equal(snapshots.length, 1)
  assert.equal(snapshots[0].preference, 'light')
  assert.equal(snapshots[0].resolved, 'light')
  assert.equal(snapshots[0].persistent, true)
  assert.equal(runtime.storedPreference(), 'light')
  unsubscribe()
})
