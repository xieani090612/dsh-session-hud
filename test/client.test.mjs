/**
 * 浏览器半（lib/client.js）的离线单元测试。
 *
 * 客户端模块的约定是「往 window.__ModuleLoader__.load 注册一个 lazy factory」，
 * 所以这里用 node:vm 造一个假的 window + 假的 React，把源码跑起来，
 * 再检查：模块 id、factory 形状、注册到哪个槽、组件能渲染出什么。
 *
 * 这样不用起 DSH、不用开浏览器，就能在改客户端代码时立刻发现契约被破坏。
 */

import fs from 'node:fs'
import path from 'node:path'
import assert from 'node:assert/strict'
import vm from 'node:vm'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const source = fs.readFileSync(path.join(here, '..', 'lib', 'client.js'), 'utf8')

let passed = 0
const failures = []
function check(name, ok, detail) {
  if (ok) { passed++; console.log(`  ok   ${name}`) }
  else { failures.push(name); console.log(`  FAIL ${name}${detail ? '  -> ' + detail : ''}`) }
}

// ---------------------------------------------------------------------------
// 假的浏览器环境
// ---------------------------------------------------------------------------

/** 假 React：createElement 产出可检查的普通对象，useState 返回固定初值。 */
function makeReact() {
  const stateWrites = []
  return {
    stateWrites,
    createElement(type, props, ...children) {
      return { type, props: props || {}, children }
    },
    useState(initial) {
      return [initial, (next) => stateWrites.push(next)]
    },
  }
}

const react = makeReact()
let loaded = null
const fetchCalls = []

const sandbox = {
  window: { __ModuleLoader__: { load(definition) { loaded = definition } } },
  fetch: async (url, init) => {
    fetchCalls.push({ url, init })
    return { ok: true, status: 200, json: async () => ({ ok: true, launched: true }) }
  },
  setTimeout: () => 0,
  console,
}
sandbox.globalThis = sandbox
vm.createContext(sandbox)
vm.runInContext(source, sandbox, { filename: 'client.js' })

console.log('== 模块注册 ==')
check('调用了 window.__ModuleLoader__.load', loaded !== null)
check('模块 id 等于包名', loaded && loaded.id === 'dsh-session-hud', loaded && loaded.id)
check('factory 是函数', loaded && typeof loaded.factory === 'function')

// ---------------------------------------------------------------------------
// factory 返回的插件形状
// ---------------------------------------------------------------------------

console.log('\n== 插件形状 ==')
const required = []
const plugin = loaded.factory((name) => {
  required.push(name)
  return react
})
check('factory 通过 require 拿 React', required.length === 1 && required[0] === 'react', required.join(','))
check('插件注入 slots 服务', Array.isArray(plugin.inject) && plugin.inject.includes('slots'), JSON.stringify(plugin.inject))
check('插件有 apply', typeof plugin.apply === 'function')

// ---------------------------------------------------------------------------
// 占座：注册到哪个槽
// ---------------------------------------------------------------------------

console.log('\n== 槽位注册 ==')
const injections = []
const registrations = []
const fakeCtx = {
  slots: {
    inject(key, callback) {
      injections.push(key)
      const disposer = callback()
      check('inject 回调返回 disposer', typeof disposer === 'function')
      return () => {}
    },
    register(options, component) {
      registrations.push({ options, component })
      return () => {}
    },
  },
}
plugin.apply(fakeCtx)

check('inject 的槽位是 sidebar.footer.action', injections.length === 1 && injections[0] === 'sidebar.footer.action', injections.join(','))
check('注册了一次', registrations.length === 1)
const entry = registrations[0]
check('register 的 name 与槽位一致', entry && entry.options.name === 'sidebar.footer.action', entry && entry.options.name)
check('register 带了稳定 id', Boolean(entry && entry.options.id))
check('register 带了 order', typeof (entry && entry.options.order) === 'number')
check('register 带了 label', typeof (entry && entry.options.label) === 'string')

// ---------------------------------------------------------------------------
// 组件渲染
// ---------------------------------------------------------------------------

console.log('\n== 组件渲染 ==')
const element = entry.component({})
check('渲染出一个 button', element && element.type === 'button', element && element.type)
check('button 有 aria-label', Boolean(element && element.props['aria-label']))
check('button 有 title 提示', typeof (element && element.props.title) === 'string')
check('button 有 onClick', typeof (element && element.props.onClick) === 'function')
check('内联的是 svg 图标', Array.isArray(element.children) && element.children[0] && element.children[0].type === 'svg')
check(
  '样式只用了宿主主题 token（没有硬编码颜色）',
  JSON.stringify(element.props.style).includes('--dsw-alias-'),
  JSON.stringify(element.props.style),
)

// ---------------------------------------------------------------------------
// 点击行为：打 POST 到宿主路由
// ---------------------------------------------------------------------------

console.log('\n== 点击行为 ==')
await element.props.onClick()
check('点击后发了一次请求', fetchCalls.length === 1, `发了 ${fetchCalls.length} 次`)
check('打到 /dsh-session-hud/open', fetchCalls[0] && fetchCalls[0].url === '/dsh-session-hud/open', fetchCalls[0] && fetchCalls[0].url)
check('方法是 POST', fetchCalls[0] && fetchCalls[0].init && fetchCalls[0].init.method === 'POST', fetchCalls[0] && fetchCalls[0].init && fetchCalls[0].init.method)
check('点击过程中进入 busy 态', react.stateWrites.includes('busy'), JSON.stringify(react.stateWrites))

// ---------------------------------------------------------------------------

console.log()
if (failures.length === 0) {
  console.log(`全部通过 ✅  (${passed} 项)`)
  process.exit(0)
}
console.log(`失败 ${failures.length} 项 / 共 ${passed + failures.length} 项：`)
for (const f of failures) console.log('  - ' + f)
process.exit(1)
