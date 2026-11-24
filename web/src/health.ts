/** Validate the API's own liveness contract and read the optional integrated workspace. */
export async function readApiHealth(response: Response): Promise<{ workspaceProfile?: string } | null> {
  const contentType = response.headers.get('content-type') ?? ''
  if (!response.ok || !/^application\/json(?:\s*;|$)/i.test(contentType)) return null

  try {
    const payload: unknown = await response.json()
    if (payload === null || typeof payload !== 'object' || Array.isArray(payload)) return null
    const body = payload as Record<string, unknown>
    if (body.status !== 'live' || body.scope !== 'APP-00A' || body.businessWrites !== false) return null
    return typeof body.workspaceProfile === 'string' ? { workspaceProfile: body.workspaceProfile } : {}
  } catch {
    return null
  }
}

export async function isLiveApiResponse(response: Response): Promise<boolean> {
  return (await readApiHealth(response)) !== null
}
