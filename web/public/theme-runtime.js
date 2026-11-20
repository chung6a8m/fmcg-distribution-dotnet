(() => {
  const storageKey = 'fmcg.ui.theme.v1'
  const colorScheme = document.querySelector('meta[name="color-scheme"]')
  const themeColor = document.querySelector('meta[name="theme-color"]')
  const subscribers = new Set()
  const darkMode = readDarkMode()
  let observing = false

  function isPreference(value) {
    return value === 'light' || value === 'dark' || value === 'system'
  }

  function readPreference() {
    try {
      const value = window.localStorage.getItem(storageKey)
      if (isPreference(value)) return value
    } catch {
      // Storage can be unavailable for the current document.
    }
    return undefined
  }

  function savePreference(preference) {
    try {
      window.localStorage.setItem(storageKey, preference)
      return true
    } catch {
      return false
    }
  }

  function readDarkMode() {
    try {
      return window.matchMedia?.('(prefers-color-scheme: dark)')
    } catch {
      return undefined
    }
  }

  function resolve(preference) {
    if (preference !== 'system') return preference
    try {
      return darkMode?.matches ? 'dark' : 'light'
    } catch {
      return 'light'
    }
  }

  function applyTheme(resolved) {
    document.documentElement.dataset.theme = resolved
    colorScheme.content = resolved
    themeColor.content = themeColor.dataset[resolved]
  }

  const saved = readPreference()
  const initialPreference = saved ?? 'light'
  let snapshot = Object.freeze({
    preference: initialPreference,
    resolved: resolve(initialPreference),
    persistent: saved !== undefined
  })
  applyTheme(snapshot.resolved)

  function publish(preference, persistent) {
    const resolved = resolve(preference)
    if (preference === snapshot.preference && resolved === snapshot.resolved && persistent === snapshot.persistent) return

    const colorChanged = resolved !== snapshot.resolved
    snapshot = Object.freeze({ preference, resolved, persistent })
    if (colorChanged) applyTheme(resolved)
    for (const notify of subscribers) notify()
  }

  function onOsChange() {
    if (snapshot.preference === 'system' && subscribers.size > 0) {
      publish('system', snapshot.persistent)
    }
  }

  function observeOs(shouldObserve) {
    if (!darkMode || observing === shouldObserve) return
    try {
      if (typeof darkMode.addEventListener !== 'function' || typeof darkMode.removeEventListener !== 'function') return
      if (shouldObserve) darkMode.addEventListener('change', onOsChange)
      else darkMode.removeEventListener('change', onOsChange)
      observing = shouldObserve
    } catch {
      // The media query can still be sampled when listener methods are unavailable.
    }
  }

  window.FmcgTheme = {
    getSnapshot: () => snapshot,
    setPreference(preference) {
      if (!isPreference(preference)) return
      const alreadySaved = preference === snapshot.preference && snapshot.persistent
      const persistent = alreadySaved || savePreference(preference)
      observeOs(preference === 'system' && subscribers.size > 0)
      publish(preference, persistent)
    },
    subscribe(listener) {
      const notify = () => listener()
      subscribers.add(notify)
      if (subscribers.size === 1 && snapshot.preference === 'system') {
        observeOs(true)
        publish('system', snapshot.persistent)
      }
      return () => {
        subscribers.delete(notify)
        if (subscribers.size === 0) observeOs(false)
      }
    }
  }
})()
