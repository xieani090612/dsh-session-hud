/**
 * 打包契约测试：证明这个包**作为 DSH bundle 是自洽的**。
 *
 * 这些字段一旦写错，症状是「装上去什么都没发生」或者「装的时候就报错」，
 * 而且都要等到用户那边才暴露。所以在本地就把它钉住：
 *   * package.json 里 dsh.* 声明的路径是否真的存在；
 *   * exports 指向的文件是否真的存在；
 *   * files 是否把该发的都发出去（尤其 lib/client.js，漏了就只有一半功能）；
 *   * 版本号三处是否一致（package.json / lib/index.js / CHANGELOG 最新一条）。
 */

import fs from 'node:fs'
import path from 'node:path'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const readJson = (p) => JSON.parse(fs.readFileSync(path.join(root, p), 'utf8'))
const exists = (p) => fs.existsSync(path.join(root, p))

const pkg = readJson('package.json')

let passed = 0
const failures = []
function check(name, ok, detail) {
  if (ok) { passed++; console.log(`  ok   ${name}`) }
  else { failures.push(name); console.log(`  FAIL ${name}${detail ? '  -> ' + detail : ''}`) }
}

// ---------------------------------------------------------------------------
console.log('== DSH bundle 声明 ==')
// ---------------------------------------------------------------------------

const patch = pkg.dsh?.bundle?.patch
check('dsh.bundle.patch 存在', typeof patch === 'string', JSON.stringify(pkg.dsh?.bundle))
check(`dsh.bundle.patch 指向的文件存在（${patch}）`, Boolean(patch) && exists(patch))

// cordis.patch.yml 至少要有 insert 一行，否则 bundle 挂载不到东西
if (patch && exists(patch)) {
  const yml = fs.readFileSync(path.join(root, patch), 'utf8')
  check('cordis.patch.yml 有 insert 段', /^-\s*insert:/m.test(yml))
  check('cordis.patch.yml 的 name 等于包名', yml.includes(`name: ${pkg.name}`))
}

console.log('\n== 客户端模块声明 ==')
const client = pkg.dsh?.client
check('dsh.client 存在', Boolean(client))
check('dsh.client.platform === "web"', client?.platform === 'web', client?.platform)
check('dsh.client.immediately 是布尔', typeof client?.immediately === 'boolean')
check('dsh.client.inject 是数组', Array.isArray(client?.inject))

console.log('\n== exports ==')
check('exports["."] 存在', typeof pkg.exports?.['.'] === 'string')
check('exports["."] 指向的文件存在', exists(pkg.exports?.['.'] ?? ''), pkg.exports?.['.'])
check('exports["./client"] 存在', typeof pkg.exports?.['./client'] === 'string')
check('exports["./client"] 指向的文件存在', exists(pkg.exports?.['./client'] ?? ''), pkg.exports?.['./client'])

console.log('\n== files（随包发布的内容）==')
check('files 是数组', Array.isArray(pkg.files))
// lib/ 必须整个发布：客户端半就在里面，漏了按钮就没了
check('files 覆盖 lib（宿主半 + 浏览器半）', pkg.files?.includes('lib'))
check('files 覆盖 dist（预编译窗口）', pkg.files?.includes('dist'))
check('files 覆盖 cordis.patch.yml', pkg.files?.includes('cordis.patch.yml'))
check('lib/client.js 会随包发布', pkg.files?.includes('lib') && exists('lib/client.js'))
check('lib/index.js 会随包发布', pkg.files?.includes('lib') && exists('lib/index.js'))
// 源码与测试不该进包
check('files 不包含 app/（C# 源码）', !pkg.files?.includes('app'))
check('files 不包含 test/', !pkg.files?.includes('test'))

// LICENSE / README 由 npm 强制包含，但也确认一下文件真的在
check('LICENSE 存在', exists('LICENSE'))
check('README.md 存在', exists('README.md'))
check('PROVENANCE.md 存在', exists('PROVENANCE.md'))
check('CHANGELOG.md 存在', exists('CHANGELOG.md'))

// README 里引用的每一张图都必须随包发布，否则在 npm 上看 README 全是裂图。
// （图片在 docs/ 下，而 files 里必须显式列上 docs。）
{
  const readme = fs.readFileSync(path.join(root, 'README.md'), 'utf8')
  const images = [...readme.matchAll(/!\[[^\]]*\]\(([^)]+)\)/g)]
    .map((m) => m[1])
    .filter((href) => !/^https?:/i.test(href))
  const missingOnDisk = images.filter((href) => !exists(href))
  check(`README 引用的本地图片都存在（${images.length} 张）`, missingOnDisk.length === 0, missingOnDisk.join(', '))

  const notShipped = images.filter((href) => !(pkg.files ?? []).some((entry) => href.startsWith(entry + '/')))
  check('README 引用的图片都会被发布', notShipped.length === 0, notShipped.join(', '))
}

// ---------------------------------------------------------------------------
console.log('\n== 版本号一致性 ==')
// ---------------------------------------------------------------------------

const indexSource = fs.readFileSync(path.join(root, 'lib/index.js'), 'utf8')
const pluginVersion = /const PLUGIN_VERSION = '([^']+)'/.exec(indexSource)?.[1]
check(`lib/index.js 的 PLUGIN_VERSION 等于 package.json（${pkg.version}）`, pluginVersion === pkg.version, pluginVersion)

const changelog = fs.readFileSync(path.join(root, 'CHANGELOG.md'), 'utf8')
const topVersion = /^##\s+(\d+\.\d+\.\d+)/m.exec(changelog)?.[1]
check(`CHANGELOG 最新一条等于 package.json（${pkg.version}）`, topVersion === pkg.version, topVersion)

check('version 是合法 semver', /^\d+\.\d+\.\d+$/.test(pkg.version ?? ''), pkg.version)
check('license 已声明', typeof pkg.license === 'string' && pkg.license.length > 0, pkg.license)
check('name 与 DSH 插件目录约定一致（小写 + 连字符）', /^[a-z0-9-]+$/.test(pkg.name ?? ''), pkg.name)

// ---------------------------------------------------------------------------
// 占位符标记拼出来而不是写死：否则这个测试文件自己就会被下面那次全仓扫描命中
// （"提交的文件里没有占位符" 这条会指向 test/package.test.mjs），成了自我误报。
const PLACEHOLDER = ['github.com', 'OWNER'].join('/')

console.log('\n== 仓库元数据 ==')
// 这几项写错的症状是「README 上的徽章加载不出来」「issue 链接 404」，
// 而且都要等推到 GitHub 上才看得见 —— 所以在这里对齐。
const repoMatch = /github\.com\/([^/\s]+)\/([^/#\s.]+)/.exec(pkg.repository?.url ?? '')
const slug = repoMatch ? `${repoMatch[1]}/${repoMatch[2]}` : ''
check('repository.url 指向 GitHub 仓库', Boolean(repoMatch), pkg.repository?.url)
check('repository.url 不是占位符', !/OWNER|YOUR[-_]?NAME|<[^>]+>/i.test(pkg.repository?.url ?? ''), pkg.repository?.url)
check('仓库名等于包名', Boolean(slug) && slug.endsWith(`/${pkg.name}`), slug)
check('bugs.url 指向同一个仓库', (pkg.bugs?.url ?? '').includes(slug), pkg.bugs?.url)
check('homepage 指向同一个仓库', (pkg.homepage ?? '').includes(slug), pkg.homepage)

{
  const readme = fs.readFileSync(path.join(root, 'README.md'), 'utf8')
  check('README 里不再有仓库占位符', !readme.includes(PLACEHOLDER))
  // README 里出现的每个 GitHub 仓库地址都必须是本仓库，否则徽章会指向别人的项目。
  const referenced = [...new Set(
    [...readme.matchAll(/github\.com\/([^/\s)]+)\/([^/\s)#]+)/g)].map((m) => `${m[1]}/${m[2]}`),
  )]
  const foreign = referenced.filter((s) => s !== slug)
  check('README 引用的 GitHub 地址都属于本仓库', foreign.length === 0, foreign.join(', '))
}

// ---------------------------------------------------------------------------
console.log('\n== 开源发布卫生 ==')
// ---------------------------------------------------------------------------

const pkgText = fs.readFileSync(path.join(root, 'package.json'), 'utf8')
check('package.json 不含私钥/令牌字样', !/token|secret|password/i.test(pkgText))
check('没有 private: true（那会挡住发布）', pkg.private !== true)

// 提交的文件里不该有个人绝对路径，也不该留占位符，
// 而且所有指向**本包**的 GitHub 地址必须是同一个 owner/repo。
//
// 说明一下这条能防什么、不能防什么：owner 整个拼错、但**每个文件都错得一样**时，
// 离线测试看不出来 —— 只有真的去拉那个 URL 才知道（还真发生过一次）。
// 它能防的是「只改了一部分」：比如换了 owner 却漏了 SECURITY.md 或徽章地址。
const personal = []
const placeholders = []
const foreignRepo = []
function scan(dir, depth) {
  if (depth > 3) return
  for (const entry of fs.readdirSync(path.join(root, dir), { withFileTypes: true })) {
    const rel = dir ? `${dir}/${entry.name}` : entry.name
    if (/^(bin|obj|dist|node_modules|\.git)$/.test(entry.name)) continue
    if (entry.isDirectory()) { scan(rel, depth + 1); continue }
    if (!/\.(md|json|ya?ml|js|mjs|ps1|cs|xaml|csproj|py|editorconfig|txt)$/.test(entry.name)) continue
    if (entry.name === 'package-lock.json') continue
    const text = fs.readFileSync(path.join(root, rel), 'utf8')
    if (/[A-Z]:[\\/]Users[\\/][^\\/\s"']+/i.test(text)) personal.push(rel)
    if (text.includes(PLACEHOLDER)) placeholders.push(rel)
    for (const m of text.matchAll(/github\.com\/([^/\s)"'`]+)\/([^/\s)#"'`]+)/g)) {
      const repoName = m[2].replace(/\.git$/, '')
      if (repoName === pkg.name && `${m[1]}/${repoName}` !== slug) {
        foreignRepo.push(`${rel} -> ${m[1]}/${repoName}`)
      }
    }
  }
}
scan('', 0)
check('提交的文件里没有个人绝对路径', personal.length === 0, personal.join(', '))
check('提交的文件里没有仓库占位符', placeholders.length === 0, placeholders.join(', '))
check('所有指向本包的 GitHub 地址用同一个 owner', foreignRepo.length === 0, foreignRepo.join(', '))

// ---------------------------------------------------------------------------

console.log()
if (failures.length === 0) {
  console.log(`全部通过 ✅  (${passed} 项)`)
  process.exit(0)
}
console.log(`失败 ${failures.length} 项 / 共 ${passed + failures.length} 项：`)
for (const f of failures) console.log('  - ' + f)
process.exit(1)
