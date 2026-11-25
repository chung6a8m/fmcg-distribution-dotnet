const routes = new Map([
  ['/api/synthetic/app02c2/session', 'c2Session'],
  ['/api/synthetic/app02c3/trips', 'c3Trips'],
  ['/api/synthetic/app02c4/trips', 'c4Trips'],
  ['/api/synthetic/app02d1/catalog', 'd1Catalog'],
  ['/api/synthetic/app02d2/trips', 'd2Trips'],
])

const maxObservedRequests = 256
let sequence = 0

export function observeApp03ReadHttp(context, origin, write = console.log) {
  const pending = new Map()
  let observed = 0

  function record(state, event) {
    const row = {
      pid: process.pid,
      id: state.id,
      route: state.route,
      event,
      utc: new Date().toISOString(),
      elapsedMs: Math.round((performance.now() - state.started) * 1000) / 1000,
    }
    if (Number.isInteger(state.status)) row.status = state.status
    const line = `APP03-READ-HTTP ${JSON.stringify(row)}`
    try {
      write(line)
    } catch {
      // A diagnostic sink failure must leave the browser test result intact.
    }
  }

  function finish(request, event) {
    const state = pending.get(request)
    if (!state) return
    pending.delete(request)
    record(state, event)
  }

  const listeners = {
    request(request) {
      if (observed >= maxObservedRequests || request.method() !== 'GET') return
      const url = URL.parse(request.url())
      if (!url || url.origin !== origin) return
      const route = routes.get(url.pathname)
      if (!route || pending.has(request)) return
      observed++
      const state = { id: ++sequence, route, started: performance.now() }
      pending.set(request, state)
      record(state, 'browserRequest')
    },
    response(response) {
      const state = pending.get(response.request())
      if (!state || state.status !== undefined) return
      state.status = response.status()
      record(state, 'browserResponse')
    },
    requestfinished: request => finish(request, 'browserFinished'),
    requestfailed: request => finish(request, 'browserFailed'),
  }

  for (const [event, listener] of Object.entries(listeners)) context.on(event, listener)
  return () => {
    for (const [event, listener] of Object.entries(listeners)) context.off(event, listener)
    for (const state of pending.values()) record(state, 'browserPending')
    pending.clear()
  }
}
