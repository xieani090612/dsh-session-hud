/**
 * dsh-session-hud —— DSH 宿主侧插件
 * ============================================================================
 * 职责：把「正在运行的 DSH 会话在做什么」以及「有没有待批准的提示」聚合成一份
 * 快照，原子写入状态文件，交给外部的 WinUI 3 悬浮窗渲染。
 *
 * 为什么走状态文件而不是 HTTP：
 *   dsh 的 webServer 有信任栅栏（非回环 Host / 未认证请求会被拒），插件路由虽然
 *   能注册，但外部原生进程还要处理鉴权与端口发现。状态文件零鉴权、零端口、
 *   同用户可读，且天然支持「窗口比宿主后启动」的场景，所以选它做主通道。
 *
 * 观察手段（全部只读，绝不改变 DSH 行为）：
 *   - `session/event`          会话追加日志（turn/step/tool/assistant/user）
 *   - `agent/status`           会话运行/空闲
 *   - `approval/request`       批准提示（waterfall：观察后原样 next()）
 *   - `user-questions/request` 提问提示（同上）
 *   - `session/created|disposed` 生命周期
 *   - `ctx.sessions` / `ctx.sessionTitle` 冷启动时的初始枚举与标题
 *
 * 安全约束：本插件任何一处抛异常都不得影响 DSH 本身。所有回调都包了 try/catch，
 * 尤其是 approval/request —— 那是 waterfall，观察失败也必须让批准链正常继续。
 */

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const PACKAGE_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const DSH_HOME = process.env.DSH_HOME || path.join(os.homedir(), '.dsh')
const STATE_DIR = path.join(DSH_HOME, 'session-hud')
const STATE_FILE = path.join(STATE_DIR, 'state.json')

const SCHEMA_VERSION = 1
// 快照里带上插件版本：改完插件后可以直接读状态文件确认
// 「当前跑着的到底是哪一份代码」（DSH 热重载不一定覆盖到 node_modules 里的插件）。
const PLUGIN_VERSION = '1.1.0'

// HUD 可执行文件的位置候选（按优先级）。
// 第一项是**随 npm 包发布的正式位置**（build.ps1 -Pack 的产出）；
// 后面几项是本地开发时 build.ps1 的输出。
const APP_RELATIVE_CANDIDATES = [
  path.join('dist', 'DshSessionHud.exe'),
  path.join('app', 'bin', 'Release', 'net8.0-windows10.0.19041.0', 'win-x64', 'publish', 'DshSessionHud.exe'),
  path.join('app', 'bin', 'Release', 'net8.0-windows10.0.19041.0', 'win-x64', 'DshSessionHud.exe'),
  path.join('app', 'bin', 'Debug', 'net8.0-windows10.0.19041.0', 'win-x64', 'DshSessionHud.exe'),
]

// 网页里「打开会话 HUD」按钮打的路由（见 lib/client.js）。
const OPEN_ROUTE = '/dsh-session-hud/open'

// ---------------------------------------------------------------------------
// 工具函数
// ---------------------------------------------------------------------------

function safe(fn, fallback) {
  try {
    return fn()
  } catch {
    return fallback
  }
}

function truncate(text, max) {
  const s = String(text ?? '')
  if (s.length <= max) return s
  return s.slice(0, Math.max(0, max - 1)) + '…'
}

/** 把一行空白压平，便于在窄窗口里单行显示。 */
function flatten(text, max = 400) {
  return truncate(String(text ?? '').replace(/\s+/g, ' ').trim(), max)
}

/**
 * 从工具调用的 JSON 参数里挑出最能说明「这一步在干什么」的那个字段。
 * 认不出来就退回截断后的原始 JSON。
 */
function summarizeToolArgs(name, argsJson) {
  const raw = String(argsJson ?? '')
  let args
  try {
    args = JSON.parse(raw)
  } catch {
    return flatten(raw, 300)
  }
  if (args === null || typeof args !== 'object') return flatten(raw, 300)

  const pick = (...keys) => {
    for (const key of keys) {
      const value = args[key]
      if (typeof value === 'string' && value.trim()) return flatten(value, 300)
      if (Array.isArray(value) && value.length) return flatten(value.join(' · '), 300)
    }
    return undefined
  }

  switch (name) {
    case 'pwsh':
    case 'bash':
    case 'shell':
      return pick('command', 'script') ?? flatten(raw, 300)
    case 'read':
    case 'write':
    case 'edit':
      return pick('file_path', 'path', 'filename') ?? flatten(raw, 300)
    case 'grep':
      return [pick('pattern'), pick('path')].filter(Boolean).join('  in  ') || flatten(raw, 300)
    case 'glob':
      return [pick('pattern'), pick('path')].filter(Boolean).join('  in  ') || flatten(raw, 300)
    case 'web_fetch':
      return pick('url') ?? flatten(raw, 300)
    case 'web_search':
      return pick('queries', 'query') ?? flatten(raw, 300)
    case 'subagent':
    case 'subagent_fork':
      return pick('description', 'prompt') ?? flatten(raw, 300)
    case 'workflow':
      return pick('description') ?? flatten(raw, 300)
    default:
      break
  }
  return pick('description', 'command', 'file_path', 'path', 'query', 'url', 'prompt') ?? flatten(raw, 300)
}

function shortId(id) {
  const s = String(id ?? '')
  const m = /([0-9a-f]{6,})$/.exec(s)
  return m ? m[1].slice(0, 8) : truncate(s, 8)
}

function basename(p) {
  if (!p) return ''
  const parts = String(p).split(/[\\/]/).filter(Boolean)
  return parts.length ? parts[parts.length - 1] : String(p)
}

/** 会话标题可能是字符串，也可能是带 title/text 的对象——两种都兜住。 */
function readTitle(ctx, session) {
  const service = ctx.get('sessionTitle')
  if (!service || typeof service.get !== 'function') return ''
  const snap = safe(() => service.get(session), undefined)
  if (!snap) return ''
  if (typeof snap === 'string') return snap
  for (const key of ['title', 'text', 'value', 'summary']) {
    if (typeof snap[key] === 'string' && snap[key].trim()) return snap[key]
  }
  return ''
}

/** 从 user/message 内容块里提取纯文本，用于在标题缺失时给出话题。 */
function userMessageText(message) {
  const content = message?.content
  if (typeof content === 'string') return flatten(content, 200)
  if (!Array.isArray(content)) return ''
  const parts = []
  for (const block of content) {
    if (!block) continue
    if (typeof block === 'string') parts.push(block)
    else if (block.type === 'text' && typeof block.text === 'string') parts.push(block.text)
  }
  return flatten(parts.join(' '), 200)
}

function assistantMessageText(message) {
  const content = message?.content
  if (typeof content === 'string') return flatten(content, 600)
  if (!Array.isArray(content)) return ''
  const parts = []
  for (const block of content) {
    if (!block) continue
    if (typeof block === 'string') parts.push(block)
    else if (block.type === 'text' && typeof block.text === 'string') parts.push(block.text)
  }
  return flatten(parts.join(' '), 600)
}

// ---------------------------------------------------------------------------
// 会话追踪
// ---------------------------------------------------------------------------

class SessionRecord {
  constructor(id) {
    this.id = String(id)
    this.title = ''
    this.cwd = ''
    this.kind = 'root'
    this.parentId = null
    this.running = false
    this.turn = 0
    this.step = 0
    this.model = ''
    this.provider = ''
    this.approvalPolicy = ''
    this.createdAt = Date.now()
    this.updatedAt = Date.now()
    this.turnStartedAt = 0
    this.stepStartedAt = 0
    this.current = null // { kind, name, detail, since, callId }
    this.lastText = ''
    this.lastUserText = ''
    this.usage = { input: 0, output: 0, total: 0 }
    this.tools = [] // 最近的工具调用，最新的在数组尾部
    this.pendingApproval = null
    this.lastOutcome = null
  }

  touch() {
    this.updatedAt = Date.now()
  }

  beginTool(callId, name, detail, at) {
    this.current = { kind: 'tool', name, detail, since: at, callId }
    this.tools.push({ callId, name, detail, state: 'running', at, endedAt: 0, ms: 0, error: '' })
  }

  endTool(callId, at, error) {
    // 从尾部往前找，避免同名工具的并行调用串味。
    for (let i = this.tools.length - 1; i >= 0; i -= 1) {
      const entry = this.tools[i]
      if (entry.callId === callId && entry.state === 'running') {
        entry.state = error ? 'error' : 'ok'
        entry.endedAt = at
        entry.ms = Math.max(0, at - entry.at)
        entry.error = error ? flatten(error, 200) : ''
        break
      }
    }
    if (this.current && this.current.kind === 'tool' && this.current.callId === callId) {
      this.current = null
    }
  }

  toJSON(now) {
    return {
      id: this.id,
      shortId: shortId(this.id),
      title: this.title || this.lastUserText || basename(this.cwd) || shortId(this.id),
      topic: this.lastUserText,
      cwd: this.cwd,
      workspace: basename(this.cwd),
      kind: this.kind,
      parentId: this.parentId,
      running: this.running,
      status: this.running ? 'running' : 'idle',
      turn: this.turn,
      step: this.step,
      model: this.model,
      provider: this.provider,
      approvalPolicy: this.approvalPolicy,
      createdAt: this.createdAt,
      updatedAt: this.updatedAt,
      // 当前动作已持续多久：工具在跑就是工具耗时，否则是本步/本轮的耗时。
      turnStartedAt: this.turnStartedAt,
      stepStartedAt: this.stepStartedAt,
      idleMs: Math.max(0, now - this.updatedAt),
      current: this.current
        ? { ...this.current, elapsedMs: Math.max(0, now - this.current.since) }
        : this.running
          ? {
              kind: 'model',
              name: this.model || 'model',
              detail: this.step ? `step ${this.step}` : 'thinking',
              since: this.stepStartedAt || this.turnStartedAt || this.updatedAt,
              elapsedMs: Math.max(0, now - (this.stepStartedAt || this.turnStartedAt || this.updatedAt)),
            }
          : null,
      lastText: this.lastText,
      usage: this.usage,
      tools: this.tools.slice(-12).reverse(),
      pendingApproval: this.pendingApproval,
      lastOutcome: this.lastOutcome,
    }
  }
}

// ---------------------------------------------------------------------------
// 插件路由的信任栅栏
// ---------------------------------------------------------------------------

/**
 * 只接受来自本机页面的请求。
 *
 * 宿主自带的栅栏并不覆盖插件注册的路由，而这条路由会**启动一个进程** ——
 * 所以必须自己挡一层：
 *   1. Host 必须是回环（localhost / *.localhost / 127.0.0.0/8 / ::1）——
 *      挡掉 DNS 重绑定（伪造 Host 指向 127.0.0.1）；
 *   2. `Sec-Fetch-Site: cross-site` 一律拒；
 *   3. 带 Origin 时必须与 Host 同源；
 *   4. 宿主栅栏（connection.requestRejection）可用时再委托它，**它抛异常按拒绝处理**。
 *
 * 任何异常都 fail closed（返回 false）——这条路由宁可不响应，也不能被外部触发。
 */
function isTrustedRequest(root, req) {
  try {
    const headers = (req && req.headers) || {}
    const host = String(headers.host || '').toLowerCase()
    if (!isLoopbackAuthority(host)) return false

    if (String(headers['sec-fetch-site'] || '').toLowerCase() === 'cross-site') return false

    const origin = headers.origin
    if (origin) {
      let sameOrigin = false
      try {
        sameOrigin = new URL(String(origin)).host.toLowerCase() === host
      } catch {
        sameOrigin = false
      }
      if (!sameOrigin) return false
    }

    const connection = safe(() => root.get('connection'), undefined)
    if (connection && typeof connection.requestRejection === 'function') {
      const code = safe(() => connection.requestRejection(req), undefined)
      if (code !== undefined && code !== null && code !== false) return false
    }

    return true
  } catch {
    return false
  }
}

/** Host 头是不是回环权威（逐段校验，防 `127.0.0.1.evil.com` 这类相似域名）。导出供测试。 */
export function isLoopbackAuthority(host) {
  const name = String(host || '').toLowerCase().replace(/:\d+$/, '').replace(/^\[/, '').replace(/\]$/, '')
  if (!name) return false
  if (name === 'localhost' || name.endsWith('.localhost')) return true
  if (name === '::1') return true
  const parts = /^127\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(name)
  if (!parts) return false
  return parts.slice(1).every((segment) => Number(segment) <= 255)
}

// ---------------------------------------------------------------------------
// 找 HUD 可执行文件
// ---------------------------------------------------------------------------

/**
 * 按优先级找 HUD 的 exe，找不到返回空串。
 *
 * 做成模块级纯函数（并导出）是为了可测：打包后可以直接从**解包出来的 npm 包**里
 * 调它，确认 dist/ 布局真的能被认出来 —— 不用起 DSH，也不用真的开窗。
 *
 * @param {string} [explicitPath] 配置里手写的路径，优先级最高。
 */
export function findAppExecutable(explicitPath) {
  const candidates = []
  if (explicitPath) candidates.push(String(explicitPath))
  for (const relative of APP_RELATIVE_CANDIDATES) {
    candidates.push(path.join(PACKAGE_ROOT, relative))
  }
  for (const candidate of candidates) {
    if (safe(() => fs.existsSync(candidate), false)) return candidate
  }
  return ''
}

// ---------------------------------------------------------------------------
// HUD 核心
// ---------------------------------------------------------------------------

function createHud(root, config) {
  const flushIntervalMs = Math.min(5000, Math.max(80, Number(config?.flushIntervalMs) || 300))
  const maxTimeline = Math.min(50, Math.max(3, Number(config?.maxTimeline) || 12))
  // 心跳间隔：窗口端默认 15 秒没有新快照才判定宿主断开，
  // 这里 2 秒一次留足余量，同时避免无谓的磁盘写入。
  const heartbeatMs = Math.min(30_000, Math.max(500, Number(config?.heartbeatMs) || 2000))

  /** @type {Map<string, SessionRecord>} */
  const sessions = new Map()
  let dirty = true
  let disposed = false
  let timer = null
  let lastWriteError = ''

  function record(session) {
    const id = String(session?.id ?? session ?? '')
    if (!id) return undefined
    let rec = sessions.get(id)
    if (!rec) {
      rec = new SessionRecord(id)
      // 冷启动补全：会话可能在我们监听之前就存在了。
      safe(() => {
        const header = session?.header
        if (header) {
          rec.cwd = header.cwd || ''
          rec.createdAt = header.createdAt || rec.createdAt
          rec.parentId = header.parentSession ? String(header.parentSession) : null
          rec.kind = header.parentSession || header.origin === 'subagent' ? 'subagent' : 'root'
        }
      }, undefined)
      sessions.set(id, rec)
      dirty = true
    }
    if (session?.header) {
      if (session.header.cwd) rec.cwd = session.header.cwd
      if (session.header.parentSession) {
        rec.parentId = String(session.header.parentSession)
        rec.kind = 'subagent'
      }
    }
    return rec
  }

  // ---- 事件回调（全部 try/catch，绝不冒泡到 DSH） ----

  function onSessionEvent(session, event) {
    try {
      const rec = record(session)
      if (!rec || !event) return
      const now = Date.now()
      const data = event.data || {}

      switch (event.type) {
        case 'turn/start':
          rec.running = true
          rec.turn = data.turn ?? rec.turn + 1
          rec.step = 0
          rec.turnStartedAt = now
          rec.stepStartedAt = 0
          rec.current = null
          break

        case 'turn/end':
          rec.running = false
          rec.turn = data.turn ?? rec.turn
          rec.current = null
          rec.stepStartedAt = 0
          break

        case 'step/start':
          rec.running = true
          rec.turn = data.turn ?? rec.turn
          rec.step = data.step ?? rec.step
          rec.stepStartedAt = now
          rec.current = null
          break

        case 'step/end':
          rec.current = null
          break

        case 'user/message': {
          const text = userMessageText(data)
          if (text) rec.lastUserText = text
          rec.running = true
          break
        }

        case 'assistant/message': {
          const text = assistantMessageText(data.message)
          if (text) rec.lastText = text
          const usage = data.usage
          if (usage) {
            rec.usage.input += Number(usage.inputTokens) || 0
            rec.usage.output += Number(usage.outputTokens) || 0
            rec.usage.total += Number(usage.totalTokens) || 0
          }
          const source = data.message?.source
          if (source?.provider) rec.provider = source.provider
          if (source?.model) rec.model = source.model
          rec.current = null
          break
        }

        case 'tool/call':
          rec.running = true
          rec.beginTool(
            String(data.callId ?? ''),
            String(data.name ?? 'tool'),
            summarizeToolArgs(data.name, data.arguments),
            now,
          )
          break

        case 'tool/result':
          rec.endTool(String(data.message?.toolCallId ?? ''), now, data.error ? (data.error.reason || data.error.code || data.error.name) : '')
          break

        case 'request/header': {
          const cfg = data.header?.config
          if (cfg?.provider) rec.provider = cfg.provider
          if (cfg?.model) rec.model = cfg.model
          break
        }

        default:
          return
      }

      rec.touch()
      dirty = true
    } catch {
      // 观察失败不影响 DSH
    }
  }

  function onAgentStatus(payload) {
    try {
      const rec = record(payload?.agent)
      if (!rec) return
      rec.running = payload.status === 'running'
      if (!rec.running) rec.current = null
      rec.touch()
      dirty = true
    } catch {
      /* ignore */
    }
  }

  function onSessionCreated(session) {
    try {
      record(session)?.touch()
      dirty = true
    } catch {
      /* ignore */
    }
  }

  function onSessionDisposed(session) {
    try {
      sessions.delete(String(session?.id ?? session ?? ''))
      dirty = true
    } catch {
      /* ignore */
    }
  }

  /**
   * 批准提示。这是 waterfall —— 我们只观察，必须原样把决定权交回给 DSH 自己的
   * answerer 链。任何异常都退化成「透明放行」，不能吞掉或改写结果。
   */
  async function onApprovalRequest(req, next) {
    // waterfall 一定会提供 next；万一某个版本没给（或签名变了），
    // 直接原样返回 undefined 放行——绝不因为「观察」失败而改变批准语义。
    if (typeof next !== 'function') return undefined

    const startedAt = Date.now()
    let rec
    try {
      rec = record(req?.agent)
      if (rec) {
        rec.pendingApproval = {
          id: `${rec.id}:${req?.callId ?? startedAt}`,
          sessionId: rec.id,
          toolName: String(req?.toolName ?? 'tool'),
          reason: flatten(req?.reason ?? '', 300),
          displayReason: flatten(req?.displayReason?.zh ?? req?.displayReason?.en ?? '', 300),
          callId: req?.callId ? String(req.callId) : '',
          askedAt: startedAt,
        }
        rec.touch()
        dirty = true
      }
    } catch {
      /* 观察失败也要继续 */
    }

    let outcome
    try {
      outcome = await next()
    } catch (err) {
      if (rec) {
        rec.pendingApproval = null
        rec.lastOutcome = { toolName: String(req?.toolName ?? 'tool'), outcome: 'error', at: Date.now() }
        rec.touch()
        dirty = true
      }
      throw err
    }

    try {
      if (rec) {
        rec.pendingApproval = null
        rec.lastOutcome = { toolName: String(req?.toolName ?? 'tool'), outcome: String(outcome), at: Date.now() }
        rec.touch()
        dirty = true
      }
    } catch {
      /* ignore */
    }
    return outcome
  }

  /** 结构化提问（ask_user_question）：同样只观察。 */
  async function onUserQuestion(request, next) {
    if (typeof next !== 'function') return undefined
    let rec
    try {
      rec = record(request?.agent)
      if (rec) {
        const questions = Array.isArray(request?.questions) ? request.questions : []
        const first = questions[0]
        rec.pendingApproval = {
          id: `${rec.id}:question:${Date.now()}`,
          sessionId: rec.id,
          toolName: 'ask_user_question',
          reason: flatten(first?.question ?? '', 300),
          displayReason: '',
          callId: '',
          askedAt: Date.now(),
        }
        rec.touch()
        dirty = true
      }
    } catch {
      /* ignore */
    }
    try {
      return await next()
    } finally {
      try {
        if (rec) {
          rec.pendingApproval = null
          rec.touch()
          dirty = true
        }
      } catch {
        /* ignore */
      }
    }
  }

  // ---- 快照与落盘 ----

  function refreshPolicies() {
    const approval = safe(() => root.get('approval'), undefined)
    if (!approval || typeof approval.overrideOf !== 'function') return
    for (const rec of sessions.values()) {
      const session = safe(() => root.get('sessions')?.get(rec.id), undefined)
      if (!session) continue
      const policy = safe(() => approval.overrideOf(session), undefined)
      if (typeof policy === 'string' && policy) rec.approvalPolicy = policy
    }
  }

  function seedKnownSessions() {
    const store = safe(() => root.get('sessions'), undefined)
    const list = safe(() => (typeof store?.list === 'function' ? store.list() : []), [])
    for (const session of list || []) {
      const rec = record(session)
      if (!rec) continue
      const title = safe(() => readTitle(root, session), '')
      if (title) rec.title = title
    }
  }

  function buildSnapshot() {
    const now = Date.now()

    // 增量补标题：sessionTitle 是 log-backed fold，标题可能在首轮之后才出现。
    const titleService = safe(() => root.get('sessionTitle'), undefined)
    if (titleService && typeof titleService.get === 'function') {
      const store = safe(() => root.get('sessions'), undefined)
      for (const rec of sessions.values()) {
        if (rec.title) continue
        const session = safe(() => store?.get(rec.id), undefined)
        if (!session) continue
        const title = safe(() => readTitle(root, session), '')
        if (title) rec.title = title
      }
    }
    refreshPolicies()

    const all = [...sessions.values()].map((rec) => rec.toJSON(now))

    // 排序：待批准 → 运行中 → 最近活动。窗口直接按此顺序渲染。
    all.sort((a, b) => {
      const pa = a.pendingApproval ? 1 : 0
      const pb = b.pendingApproval ? 1 : 0
      if (pa !== pb) return pb - pa
      if (a.running !== b.running) return a.running ? -1 : 1
      return b.updatedAt - a.updatedAt
    })

    const approvals = []
    for (const s of all) {
      if (s.pendingApproval) approvals.push({ ...s.pendingApproval, sessionTitle: s.title, workspace: s.workspace })
    }

    return {
      schema: SCHEMA_VERSION,
      generatedAt: now,
      host: {
        pid: process.pid,
        cwd: process.cwd(),
        platform: process.platform,
        dshHome: DSH_HOME,
        stateFile: STATE_FILE,
        pluginVersion: PLUGIN_VERSION,
      },
      totals: {
        sessions: all.length,
        running: all.filter((s) => s.running).length,
        pendingApprovals: approvals.length,
      },
      approvals,
      sessions: all,
      error: lastWriteError || undefined,
    }
  }

  function flush(force) {
    if (disposed) return
    if (!dirty && !force) return
    dirty = false
    try {
      fs.mkdirSync(STATE_DIR, { recursive: true })
      const payload = JSON.stringify(buildSnapshot())
      // 原子替换：先写临时文件再 rename，窗口永远读不到半截 JSON。
      const tmp = `${STATE_FILE}.${process.pid}.tmp`
      fs.writeFileSync(tmp, payload, 'utf8')
      fs.renameSync(tmp, STATE_FILE)
      lastWriteError = ''
    } catch (err) {
      lastWriteError = String(err?.message || err)
    }
  }

  // ---- 拉起 HUD 窗口 ----

  let child = null
  let childExited = true

  function resolveAppPath() {
    return findAppExecutable(config?.appPath)
  }

  function launchApp(force) {
    if (!force && config?.autoLaunch === false) return false
    const exe = resolveAppPath()
    if (!exe) return false
    if (!childExited && child) return true
    try {
      // detached + unref：HUD 是独立进程，DSH 退出后它自己决定何时关闭。
      // --exit-after-stale 90：宿主心跳消失 90 秒后自行关闭，避免留下空窗口；
      // 但 DSH 在 90 秒内重启时插件会重新拉起，单实例互斥体会把已有窗口叫到前台。
      child = spawn(exe, ['--state', STATE_FILE, '--exit-after-stale', '90'], {
        detached: true,
        stdio: 'ignore',
        windowsHide: false,
        cwd: path.dirname(exe),
      })
      childExited = false
      child.on('exit', () => {
        childExited = true
        child = null
      })
      child.on('error', () => {
        childExited = true
        child = null
      })
      child.unref()
      return true
    } catch {
      childExited = true
      child = null
      return false
    }
  }

  // ---- 生命周期 ----

  function start() {
    seedKnownSessions()
    flush(true)
    // 定时落盘。心跳必须**无条件**定期重写，不能只在有会话运行时写：
    // 否则 DSH 空闲（所有会话都在等用户输入）时文件长时间不变，
    // 窗口会误判成「宿主已退出」而弹断开提示。
    let lastHeartbeat = 0
    timer = setInterval(() => {
      try {
        const now = Date.now()
        if (now - lastHeartbeat >= heartbeatMs) {
          lastHeartbeat = now
          dirty = true
        }
        flush(false)
      } catch {
        /* ignore */
      }
    }, flushIntervalMs)
    if (typeof timer.unref === 'function') timer.unref()
    launchApp()
  }

  function dispose() {
    if (disposed) return
    disposed = true
    if (timer) clearInterval(timer)
    timer = null
    // 留一份「宿主已停止」的快照，窗口据此显示断开而不是一直转圈。
    try {
      fs.mkdirSync(STATE_DIR, { recursive: true })
      const payload = JSON.stringify({
        schema: SCHEMA_VERSION,
        generatedAt: Date.now(),
        host: { pid: process.pid, stopped: true, stateFile: STATE_FILE },
        totals: { sessions: 0, running: 0, pendingApprovals: 0 },
        approvals: [],
        sessions: [],
      })
      const tmp = `${STATE_FILE}.${process.pid}.tmp`
      fs.writeFileSync(tmp, payload, 'utf8')
      fs.renameSync(tmp, STATE_FILE)
    } catch {
      /* ignore */
    }
  }

  return {
    onSessionEvent,
    onAgentStatus,
    onSessionCreated,
    onSessionDisposed,
    onApprovalRequest,
    onUserQuestion,
    start,
    dispose,
    launchApp,
    resolveAppPath,
    isChildAlive: () => !childExited && Boolean(child),
    stateFile: STATE_FILE,
  }
}

// ---------------------------------------------------------------------------
// Cordis 插件入口
// ---------------------------------------------------------------------------

export const name = 'dsh-session-hud'

export default {
  name: 'dsh-session-hud',
  apply(root, config) {
    const hud = createHud(root, config || {})

    root.effect(() => () => {
      safe(() => hud.dispose(), undefined)
    })

    // 事件监听：立即注册，不等任何服务 —— 事件是全局 emit，早订阅才不漏。
    root.on('session/event', (session, event) => hud.onSessionEvent(session, event))
    root.on('agent/status', (payload) => hud.onAgentStatus(payload))
    root.on('session/created', (session) => hud.onSessionCreated(session))
    root.on('session/disposed', (session) => hud.onSessionDisposed(session))
    root.on('approval/request', (req, next) => hud.onApprovalRequest(req, next))
    root.on('user-questions/request', (request, next) => hud.onUserQuestion(request, next))

    // 服务相关部分（枚举已有会话、标题、策略）等 sessions 就绪后再做，
    // 但不阻塞上面的观察。
    root.inject(['sessions'], () => {
      safe(() => hud.start(), undefined)
    })

    // `/hud` 命令：窗口被用户关掉之后，不必重启 DSH 也能叫回来。
    // 单实例互斥体保证「已经在跑」时只是把已有窗口前置。
    root.inject(['commands'], (ctx) => {
      const commands = safe(() => ctx.get('commands'), undefined)
      if (!commands || typeof commands.register !== 'function') return
      const dispose = safe(
        () =>
          commands.register({
            name: 'hud',
            description: '显示 / 前置 DSH 会话 HUD 悬浮窗',
            handler: async () => {
              const exe = hud.resolveAppPath()
              if (!exe) {
                return {
                  kind: 'error',
                  text: '找不到 HUD 可执行文件。请在插件目录运行 build.ps1 生成 app/bin/Release/.../DshSessionHud.exe',
                }
              }
              const launched = hud.launchApp(true)
              if (!launched) {
                return { kind: 'error', text: `无法启动 HUD：${exe}` }
              }
              return {
                kind: 'success',
                text: `已请求显示 HUD 窗口（${exe}）。窗口已在运行时会被前置，不会重复打开。`,
              }
            },
          }),
        undefined,
      )
      if (dispose) {
        root.effect(() => () => {
          safe(() => dispose(), undefined)
        })
      }
    })

    // 网页里那个「打开会话 HUD」按钮（lib/client.js）打的就是这个路由。
    // 客户端模块拿不到 host.call 那套受限面，但它是正常浏览器环境，
    // 所以宿主这边开一个同源 POST 路由最直白。
    root.inject(['webServer'], (ctx) => {
      const webServer = safe(() => ctx.get('webServer'), undefined)
      if (!webServer || typeof webServer.register !== 'function') return
      const dispose = safe(
        () =>
          webServer.register({
            kind: 'exact',
            path: OPEN_ROUTE,
            handler: (req, res) => {
              if (!isTrustedRequest(root, req)) {
                res.statusCode = 403
                res.end()
                return
              }
              if (req.method !== 'POST') {
                res.statusCode = 405
                res.setHeader?.('Allow', 'POST')
                res.end()
                return
              }
              const exe = hud.resolveAppPath()
              const launched = exe ? hud.launchApp(true) : false
              res.writeHead(200, {
                'Content-Type': 'application/json; charset=utf-8',
                'Cache-Control': 'no-store',
              })
              res.end(JSON.stringify({ ok: Boolean(exe), launched, exe: exe || null }))
            },
          }),
        undefined,
      )
      if (dispose) {
        root.effect(() => () => {
          safe(() => dispose(), undefined)
        })
      }
    })
  },
}
