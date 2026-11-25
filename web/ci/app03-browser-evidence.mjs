import { randomUUID } from 'node:crypto'
import { lstatSync, mkdirSync, realpathSync } from 'node:fs'
import { lstat, mkdir, readFile, readdir, rename, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repository = fileURLToPath(new URL('../../', import.meta.url))
const screenshots = {
  'app03-4': ['handoff-desktop.png', 'handoff-320-dark.png'],
  'app03-5': ['field-320.png', 'warehouse-reconciling.png'],
}
const sessionTokens = ['APP03_SOURCE_TOKEN', 'APP03_RECEIVER_TOKEN', 'APP03_PEER_TOKEN',
  'APP03_FINANCE_TOKEN', 'APP03_EXPIRED_TOKEN', 'APP03_CASHIER_TOKEN', 'APP03_QA_TOKEN']

function validRun(profile, id) {
  return Object.hasOwn(screenshots, profile) && typeof id === 'string' && id.length === 32 && /^[0-9a-f]+$/.test(id)
}

function plainDirectory(directory) {
  const stat = lstatSync(directory)
  return stat.isDirectory() && !stat.isSymbolicLink()
}

export function evidenceDirectory(root, profile, id, supplied) {
  if (!validRun(profile, id) || typeof supplied !== 'string' || !path.isAbsolute(supplied)) return null
  let directory = realpathSync(root)
  for (const part of ['TestResults', profile, id]) {
    directory = path.join(directory, part)
    if (!plainDirectory(directory)) return null
  }
  return path.relative(directory, path.resolve(supplied)) === '' ? directory : null
}

function stagingDirectory(root, profile, id, tempRoot) {
  if (!validRun(profile, id) || !path.isAbsolute(tempRoot)) throw new Error('Invalid diagnostic staging identity.')
  const temp = realpathSync(tempRoot)
  const directory = path.join(temp, 'fmcg-app03-private-' + profile + '-' + id)
  const relative = path.relative(realpathSync(root), directory)
  if ((!relative.startsWith('..' + path.sep) && !path.isAbsolute(relative))
      || directory.split(path.sep).some(part => part.toLowerCase() === 'testresults')) {
    throw new Error('Diagnostic staging must be outside the evidence and checkout trees.')
  }
  mkdirSync(directory, { recursive: true, mode: 0o700 })
  if (!plainDirectory(directory)) throw new Error('Invalid diagnostic staging directory.')
  return directory
}

export function browserEvidenceConfig(profile, env = process.env, root = repository) {
  const id = env.APP02C1_RUN_ID
  const enabled = env.FMCG_APP03_READ_DIAGNOSTICS === '1'
  const output = enabled ? stagingDirectory(root, profile, id, env.RUNNER_TEMP || tmpdir()) : `../TestResults/${profile}/${id}`
  const reporter = [['list'], ['junit', { outputFile: output + '/junit.xml' }]]
  if (enabled) reporter.push(['./ci/app03-read-reporter.mjs', { profile }])
  return { outputDir: output + '/browser', reporter }
}

async function safeWrite(directory, relative, bytes) {
  let parent = directory
  for (const part of relative.slice(0, -1)) {
    parent = path.join(parent, part)
    await mkdir(parent, { recursive: true })
    if (!plainDirectory(parent)) throw new Error('Invalid publication directory.')
  }
  const destination = path.join(parent, relative.at(-1))
  const temporary = path.join(parent, '.app03-' + randomUUID() + '.tmp')
  try {
    await writeFile(temporary, bytes, { flag: 'wx' })
    await rename(temporary, destination)
  } finally { await rm(temporary, { force: true }) }
}

export async function publishBrowserEvidence(profile, env = process.env, root = repository) {
  const id = env.APP02C1_RUN_ID
  const destination = evidenceDirectory(root, profile, id, env.APP03_EVIDENCE_DIR)
  if (!destination) throw new Error('Invalid evidence destination.')
  const receipt = { status: 'withheld', published: 0, withheld: 0 }
  const raw = stagingDirectory(root, profile, id, env.RUNNER_TEMP || tmpdir())
  const tokens = sessionTokens.map(name => env[name]).filter(Boolean)
  const requiredTokens = sessionTokens.slice(0, 3).every(name => env[name])
  async function publish(relative) {
    try {
      const source = path.join(raw, ...relative)
      const stat = await lstat(source)
      if (!requiredTokens || !stat.isFile() || stat.isSymbolicLink() || stat.size > 16 * 1024 * 1024) throw new Error()
      let bytes = await readFile(source)
      if (relative.at(-1).endsWith('.png')) {
        if (!bytes.subarray(0, 8).equals(Buffer.from('89504e470d0a1a0a', 'hex'))
            || !bytes.subarray(-12).equals(Buffer.from('0000000049454e44ae426082', 'hex'))
            || tokens.some(token => bytes.includes(token))) throw new Error()
      } else {
        let text = new TextDecoder('utf-8', { fatal: true }).decode(bytes)
        for (const token of tokens) text = text.replaceAll(token, '[REDACTED]')
        // A credential outside this harness's known set cannot be safely published.
        if (/\bBearer[ \t]+(?!\[REDACTED\])[^\s<"']+/i.test(text)) throw new Error()
        bytes = Buffer.from(text)
      }
      await safeWrite(destination, relative, bytes)
      receipt.published++
    } catch { receipt.withheld++ }
  }

  // Playwright may add new artifact types over time. Keep them private until explicitly supported.
  for (const entry of await readdir(raw)) {
    if (entry !== 'junit.xml' && entry !== 'browser') receipt.withheld++
  }
  await publish(['junit.xml'])
  try {
    const browser = path.join(raw, 'browser')
    if (plainDirectory(browser)) {
      for (const entry of await readdir(browser, { withFileTypes: true })) {
        if (entry.name === '.last-run.json' && entry.isFile()) continue
        const prefix = profile === 'app03-4' ? 'handoff-' : 'field-'
        if (!entry.isDirectory() || entry.isSymbolicLink() || !entry.name.startsWith(prefix) || !/^[A-Za-z0-9_-]{1,200}$/.test(entry.name)) {
          receipt.withheld++
          continue
        }
        for (const child of await readdir(path.join(browser, entry.name), { withFileTypes: true })) {
          if (child.isFile() && (child.name === 'error-context.md' || screenshots[profile].includes(child.name))) {
            await publish(['browser', entry.name, child.name])
          } else receipt.withheld++
        }
      }
    } else receipt.withheld++
  } catch (error) { if (error.code !== 'ENOENT') receipt.withheld++ }
  receipt.status = receipt.withheld ? 'withheld' : 'complete'
  await safeWrite(destination, ['browser-publication.json'], JSON.stringify(receipt) + '\n')
  return receipt
}
