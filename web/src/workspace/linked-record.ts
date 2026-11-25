function focusRecord(node: HTMLElement | null) {
  node?.focus()
}

export function linkedRecord(id: string, requested?: string) {
  return id === requested ? { tabIndex: -1, ref: focusRecord } : {}
}
