/** Validate the API's own liveness contract, not merely an HTTP 200 from SPA fallback. */
export async function isLiveApiResponse(response: Response): Promise<boolean> {
  const contentType = response.headers.get('content-type') ?? ''
  if (!response.ok || !/^application\/json(?:\s*;|$)/i.test(contentType)) return false

  try {
    const payload: unknown = await response.json()
    if (payload === null || typeof payload !== 'object' || Array.isArray(payload)) return false
    const body = payload as Record<string, unknown>
    return body.status === 'live' && body.scope === 'APP-00A' && body.businessWrites === false
  } catch {
    return false
  }
}
