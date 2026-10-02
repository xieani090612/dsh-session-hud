/**
 * dsh-session-hud 宿主插件的离线端到端测试。
 *
 * 用一个假的 Cordis context 驱动插件：捕获事件监听器 → 手动喂事件 →
 * 检查落盘的状态文件。这样可以在不起 DSH、不真跑窗口的前提下验证
 * 「观察 → 聚合 → 快照」这条主链路，包括批准提示的开始与结束。
 *
 * 运行：
 *   node --experimental-vm-modules test/plugin.test.mjs     （普通 node 即可）
 */

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'

// 必须在 import 插件之前设好：插件模块顶层的常量会读它。
const TMP = fs.mkdtempSync(path.join(os.tmpdir(), 'dsh-hud-test-'))
process.env.DSH_HOME = TMP

const STATE = path.join(TMP, 'session-hud', 'state.json')

const { default: plugin, findAppExecutable, isLoopbackAuthority } = await import('../lib/index.js')

// ---------------------------------------------------------------------------
// 假的 Cordis context
// ---------------------------------------------------------------------------

const handlers = new Map()
const disposers = []
const capturedRoutes = []

// 假的 webServer：只把注册的路由收下来，好让测试直接调 handler。
const fakeWebServer = {
  register(route) {
    capturedRoutes.push(route)
    return () => {}
  },
}

const fakeCtx = {
  get: (name) => (name === 'webServer' ? fakeWebServer : undefined),
}

const root = {
  get: () => undefined,
  on(name, fn) {
    handlers.set(name, fn)
    return () => handlers.delete(name)
  },
  inject(names, cb) {
    // sessions 就绪 → 启动插件；webServer 就绪 → 注册网页按钮打的路由。
    // commands 分支的 ctx.get 返回 undefined，自然跳过。
    if (names.includes('sessions') || names.includes('webServer')) cb(fakeCtx)
    return () => {}
  },
  effect(fn) {
    const dispose = fn()
    if (typeof dispose === 'function') disposers.push(dispose)
    return () => {}
  },
}

assert.equal(plugin.name, 'dsh-session-hud')
plugin.apply(root, {
  autoLaunch: false,
  flushIntervalMs: 60,
  heartbeatMs: 500,
  // 指向一个**已存在但不是可执行文件**的路径：这样 resolveAppPath() 会返回它
  // （测试拿到的 exe 字段为真），而真正 spawn 时会立刻失败，
  // 不会在跑测试时弹出一个 HUD 窗口。
  appPath: path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..'),
})

// 不该有任何服务被真的触发
for (const required of ['session/event', 'agent/status', 'approval/request', 'user-questions/request']) {
  assert.ok(handlers.has(required), `应注册监听器：${required}`)
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const read = () => JSON.parse(fs.readFileSync(STATE, 'utf8'))

function emit(name, ...args) {
  const fn = handlers.get(name)
  assert.ok(fn, `监听器不存在：${name}`)
  return fn(...args)
}

const SESSION = { id: 'session-test-0001', header: { cwd: 'C:\\work\\demo', createdAt: Date.now() } }

// ---------------------------------------------------------------------------
// 1. 会话创建 + 一轮开始
// ---------------------------------------------------------------------------

emit('session/created', SESSION)
emit('session/event', SESSION, { type: 'turn/start', data: { turn: 3 } })
emit('session/event', SESSION, { type: 'step/start', data: { turn: 3, step: 5 } })
await sleep(150)

let snap = read()
assert.equal(snap.sessions.length, 1)
let s = snap.sessions[0]
assert.equal(s.id, 'session-test-0001')
assert.equal(s.turn, 3)
assert.equal(s.step, 5)
assert.equal(s.workspace, 'demo')
assert.equal(s.running, true)
console.log('✓ 会话创建 / turn / step / workspace')

// ---------------------------------------------------------------------------
// 2. 工具调用：参数字段抽取（这条最容易被写错）
// ---------------------------------------------------------------------------

const cases = [
  ['pwsh', '{"command":"netstat -ano | Select-String :19387"}', 'netstat -ano'],
  ['read', '{"file_path":"C:\\\\a\\\\b\\\\index.js"}', 'index.js'],
  ['grep', '{"pattern":"approval/request","path":"lib"}', 'approval/request'],
  ['web_fetch', '{"url":"https://example.com/x"}', 'example.com'],
  ['subagent', '{"description":"audit the config","prompt":"..."}', 'audit the config'],
  ['mystery_tool', '{"description":"fallback via description"}', 'fallback via description'],
  ['broken_json', 'not json at all', 'not json at all'],
]

for (const [name, args, expected] of cases) {
  emit('session/event', SESSION, {
    type: 'tool/call',
    data: { turn: 3, step: 5, callId: `call-${name}`, name, arguments: args },
  })
  await sleep(90)
  s = read().sessions[0]
  assert.equal(s.current.kind, 'tool', `${name}: kind`)
  assert.equal(s.current.name, name, `${name}: name`)
  assert.ok(
    s.current.detail.includes(expected),
    `${name}: 期望 detail 含 "${expected}"，实际 "${s.current.detail}"`,
  )
}
console.log('✓ 工具参数抽取（7 种形态）')

// ---------------------------------------------------------------------------
// 3. 工具结果：running → ok / error
// ---------------------------------------------------------------------------

// 先结束 call-pwsh。它不是「当前」那个工具（当前是最后发起的 broken_json），
// 所以 current 必须保持不变 —— 交错/并行工具调用时这是正确行为，
// 错误实现会因为「结束了任意一个工具」就把当前工作清空。
emit('session/event', SESSION, {
  type: 'tool/result',
  data: { message: { toolCallId: 'call-pwsh' } },
})
await sleep(90)
s = read().sessions[0]
let entry = s.tools.find((t) => t.callId === 'call-pwsh')
assert.equal(entry.state, 'ok', '成功结果应记为 ok')
assert.ok(entry.ms >= 0, '应记录耗时')
assert.equal(s.current.callId, 'call-broken_json', '结束非当前工具不应清空 current')

// 结束当前工具 → 回落到「模型生成中」，而不是留空。
// （会话仍在运行，此刻它确实在等模型出下一步。）
emit('session/event', SESSION, {
  type: 'tool/result',
  data: { message: { toolCallId: 'call-broken_json' } },
})
await sleep(90)
s = read().sessions[0]
assert.equal(s.current.kind, 'model', '当前工具结束后应回落到模型生成中')

// 错误结果要带上原因
emit('session/event', SESSION, {
  type: 'tool/result',
  data: { message: { toolCallId: 'call-read' }, error: { name: 'E', code: 'ENOENT', reason: 'not found' } },
})
await sleep(90)
s = read().sessions[0]
entry = s.tools.find((t) => t.callId === 'call-read')
assert.equal(entry.state, 'error')
assert.equal(entry.error, 'not found')
console.log('✓ 工具结果 ok / error、耗时、以及交错调用的 current 语义')

// ---------------------------------------------------------------------------
// 4. 批准提示：pending → 结束（这是 waterfall，必须原样放行）
// ---------------------------------------------------------------------------

let releaseApproval
const approvalGate = new Promise((resolve) => {
  releaseApproval = resolve
})
let nextCalled = false

const approvalPromise = emit(
  'approval/request',
  { agent: SESSION, toolName: 'pwsh', callId: 'call-approve', reason: 'raw reason', displayReason: { en: 'en text', zh: '需要批准执行命令' } },
  async () => {
    nextCalled = true
    return approvalGate
  },
)

await sleep(120)
snap = read()
assert.ok(snap.approvals.length === 1, '待批准应出现在 approvals 里')
assert.equal(snap.approvals[0].toolName, 'pwsh')
assert.equal(snap.approvals[0].displayReason, '需要批准执行命令', '中文 displayReason 优先')
assert.equal(snap.approvals[0].sessionTitle.length > 0, true)
assert.equal(snap.totals.pendingApprovals, 1)
assert.equal(snap.sessions[0].pendingApproval.toolName, 'pwsh')
console.log('✓ 批准提示进入 pending 状态（含中文 displayReason 与总数）')

releaseApproval('rejected')
const outcome = await approvalPromise
assert.equal(outcome, 'rejected', '必须原样返回 answerer 链的结果')
assert.equal(nextCalled, true, '必须调用 next() 交回决定权')

await sleep(120)
snap = read()
assert.equal(snap.approvals.length, 0, '批准结束后应从 approvals 移除')
assert.equal(snap.sessions[0].pendingApproval, null)
assert.equal(snap.sessions[0].lastOutcome.outcome, 'rejected')
assert.equal(snap.totals.pendingApprovals, 0)
console.log('✓ 批准结束后清理，并把结果原样交回 DSH')

// ---------------------------------------------------------------------------
// 5. 防御性：没有 next 的批准事件不能崩，也不能改写语义
// ---------------------------------------------------------------------------

const noNext = await emit('approval/request', { agent: SESSION, toolName: 'pwsh' }, undefined)
assert.equal(noNext, undefined)
console.log('✓ 缺少 next() 时安全放行（不抛异常、不改语义）')

// ---------------------------------------------------------------------------
// 6. 助手消息：正文与用量累积
// ---------------------------------------------------------------------------

emit('session/event', SESSION, {
  type: 'assistant/message',
  data: {
    message: {
      role: 'assistant',
      content: [{ type: 'text', text: '第一步已经完成，接下来处理批准逻辑。' }],
      source: { kind: 'model', provider: 'deepseek-account', model: 'deepseek-flash' },
    },
    usage: { inputTokens: 1000, outputTokens: 200, totalTokens: 1200 },
  },
})
await sleep(120)
s = read().sessions[0]
assert.ok(s.lastText.includes('第一步已经完成'))
assert.equal(s.model, 'deepseek-flash')
assert.equal(s.provider, 'deepseek-account')
assert.equal(s.usage.input, 1000)
assert.equal(s.usage.output, 200)
console.log('✓ 助手正文 / 模型名 / 用量累积')

// ---------------------------------------------------------------------------
// 7. 会话空闲与销毁
// ---------------------------------------------------------------------------

emit('agent/status', { agent: SESSION, status: 'idle' })
await sleep(120)
s = read().sessions[0]
assert.equal(s.running, false)
assert.equal(s.status, 'idle')
assert.equal(s.current, null, '空闲后不应还挂着 current')

emit('session/disposed', SESSION)
await sleep(120)
snap = read()
assert.equal(snap.sessions.length, 0)
assert.equal(snap.totals.sessions, 0)
console.log('✓ 运行/空闲切换与会话销毁')

// ---------------------------------------------------------------------------
// 8. 快照形状
// ---------------------------------------------------------------------------

assert.equal(snap.schema, 1)
assert.equal(typeof snap.generatedAt, 'number')
assert.equal(typeof snap.host.pid, 'number')
assert.ok(snap.host.stateFile.endsWith('state.json'))
console.log('✓ 快照顶层形状（schema / generatedAt / host）')

// ---------------------------------------------------------------------------
// 9. 不留下临时文件（原子写的副产品）
// ---------------------------------------------------------------------------

const leftovers = fs.readdirSync(path.dirname(STATE)).filter((f) => f.includes('.tmp'))
assert.equal(leftovers.length, 0, `rename 后不应残留临时文件：${leftovers}`)
console.log('✓ 原子写不残留 .tmp 文件')

// ---------------------------------------------------------------------------
// 10. HUD 可执行文件的查找顺序（打包布局能不能被认出来）
// ---------------------------------------------------------------------------

const selfPath = fileURLToPath(import.meta.url)

// 显式给出的路径如果存在，优先级最高。
assert.equal(findAppExecutable(selfPath), selfPath, '显式路径应优先')

// 显式路径不存在时回落到包内候选（dist/ 优先，其次 app/bin/...）。
const foundExe = findAppExecutable('C:/definitely/not/here/DshSessionHud.exe')
if (foundExe) {
  const normalized = foundExe.replace(/\\/g, '/')
  assert.ok(normalized.endsWith('DshSessionHud.exe'), `应指向 HUD 的 exe，实际 ${foundExe}`)
  assert.ok(
    normalized.includes('/dist/') || normalized.includes('/app/bin/'),
    `应落在 dist/ 或 app/bin/ 下，实际 ${foundExe}`,
  )
  console.log(`✓ findAppExecutable 查找顺序（命中 ...${normalized.slice(normalized.indexOf('/dsh-session-hud/'))}）`)
}
else {
  console.log('· findAppExecutable 未命中 exe（dist/ 与 app/bin 都没构建，跳过）')
}

// ---------------------------------------------------------------------------
// 11. 网页「打开 HUD」按钮打的那个路由 + 信任栅栏
// ---------------------------------------------------------------------------

const openRoute = capturedRoutes.find((r) => r.path === '/dsh-session-hud/open')
assert.ok(openRoute, '应注册 /dsh-session-hud/open 路由')
assert.equal(openRoute.kind, 'exact')

function fakeResponse() {
  return {
    statusCode: 200,
    allow: null,
    body: '',
    setHeader(name, value) { if (name === 'Allow') this.allow = value },
    writeHead(code) { this.statusCode = code },
    end(payload) { this.body = payload || '' },
  }
}
function hitRoute(headers, method = 'POST') {
  const res = fakeResponse()
  openRoute.handler({ method, headers }, res)
  return res
}

// 正常：本机页面发来的 POST
let res = hitRoute({ host: '127.0.0.1:19387' })
assert.equal(res.statusCode, 200, `回环 POST 应 200，实际 ${res.statusCode}`)
assert.ok(res.body.includes('"ok"'), `响应应是 JSON：${res.body}`)

// Host 不是回环 → 403（DNS 重绑定）
assert.equal(hitRoute({ host: 'evil.com' }).statusCode, 403)
assert.equal(hitRoute({ host: '127.0.0.1.evil.com' }).statusCode, 403, '相似域名不能被当成回环')
assert.equal(hitRoute({ host: 'localhost.evil.com' }).statusCode, 403, '相似域名不能被当成回环')
assert.equal(hitRoute({}).statusCode, 403, '没有 Host 头应拒绝')

// 跨站 / 异源 → 403
assert.equal(hitRoute({ host: '127.0.0.1:19387', 'sec-fetch-site': 'cross-site' }).statusCode, 403)
assert.equal(hitRoute({ host: '127.0.0.1:19387', origin: 'http://evil.com' }).statusCode, 403)
assert.equal(hitRoute({ host: '127.0.0.1:19387', origin: 'http://127.0.0.1:19387' }).statusCode, 200, '同源 Origin 应放行')

// 其它回环写法
assert.equal(hitRoute({ host: 'localhost:19387' }).statusCode, 200)
assert.equal(hitRoute({ host: '[::1]:19387' }).statusCode, 200)

// 方法必须是 POST
const getRes = hitRoute({ host: '127.0.0.1:19387' }, 'GET')
assert.equal(getRes.statusCode, 405, 'GET 应 405')
assert.equal(getRes.allow, 'POST')

console.log('✓ 网页按钮路由：回环放行、伪造 Host / 跨站 / 异源 / GET 全部拒绝')

// isLoopbackAuthority 的逐段校验
assert.equal(isLoopbackAuthority('127.0.0.1'), true)
assert.equal(isLoopbackAuthority('127.255.255.255:80'), true)
assert.equal(isLoopbackAuthority('127.0.0.256'), false, '每段不得超过 255')
assert.equal(isLoopbackAuthority('128.0.0.1'), false)
assert.equal(isLoopbackAuthority('[::1]:19387'), true)
assert.equal(isLoopbackAuthority('localhost'), true)
assert.equal(isLoopbackAuthority('a.localhost'), true)
assert.equal(isLoopbackAuthority('localhost.evil.com'), false)
console.log('✓ isLoopbackAuthority 逐段校验')

// ---------------------------------------------------------------------------

for (const d of disposers) {
  try {
    await d()
  } catch {
    /* ignore */
  }
}
await sleep(50)

fs.rmSync(TMP, { recursive: true, force: true })
console.log('\n全部通过 ✅')
