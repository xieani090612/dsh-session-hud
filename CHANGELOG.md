# 更新日志

## 1.1.0

- **DSH 侧边栏加了「打开会话 HUD」按钮**（设置按钮旁边）。以前只能靠 `/hud` 命令叫回窗口，
  现在网页上直接点。按钮走客户端模块（`dsh.client`）注册进 `sidebar.footer.action` 槽，
  样式只用宿主主题 token，跟随明暗主题；点击时短暂显示成功/失败态。
- 宿主半新增 `POST /dsh-session-hud/open` 路由供按钮调用，并带**信任栅栏**：
  只接受回环 Host、拒绝 `Sec-Fetch-Site: cross-site`、带 Origin 时必须同源、
  非 POST 一律 405；宿主自带栅栏可用时再委托它（它抛异常按拒绝处理）。
- 新增浏览器半契约测试（`test/client.test.mjs`，假 `window` + 假 React 驱动 `lib/client.js`），
  宿主测试补上路由与栅栏用例。
- 包结构补上 DSH 客户端模块所需字段：`exports`（`.` 与 `./client`）与 `dsh.client`
  （`platform` / `immediately` / `inject`）。
- 说明：**打开 DSH 时联动拉起窗口**在 1.0.0 就有（`autoLaunch`），本次未改动。

**仓库整理（为公开发布）**

- 去掉提交文件里最后一处个人绝对路径；`test/package.test.mjs` 会持续盯着这一点。
- 新增 `CONTRIBUTING.md`（开发环境、测试、五个踩过的坑、PR 检查单）、
  `SECURITY.md`（能力边界 + 路由防护 + 报告方式）、`.editorconfig`。
- 新增 `install.ps1`（检查 `dist/` → 写 profile → 跑 pnpm → 提示重启）与
  `release.ps1`（构建 → 打包 → SHA256 → 发布检查单）。
- 新增 `.github/workflows/ci.yml`：四套离线测试 + `dotnet build`（同时证明无需 Visual Studio）
  + `npm pack` 冒烟。
- 新增**打包契约测试** `test/package.test.mjs`：校验 `dsh.*` / `exports` / `files` 声明指向的文件
  真的存在、版本号三处一致、提交文件里没有个人路径。
- `package.json` 补上 `repository` / `bugs` / `homepage`，指向
  <https://github.com/xieani090612/dsh-session-hud>。
- `files` 增加 `docs/`：README 里的截图随包发布，否则在 npm 上会显示成裂图。
- README 重写为公开发布的入口：目录、它解决什么、环境要求、桌面版/命令行两种安装路径、
  打包与发布、发布前检查单。

## 1.0.0

首个版本。一个宿主插件 + 一个 WinUI 3 悬浮窗，实时显示运行中会话的当前工作与批准提示。

**宿主插件（`lib/`）**

- 只读观察 `session/event`、`agent/status`、`approval/request`、`user-questions/request`、`session/created|disposed`，
  以及 `ctx.sessions` / `ctx.sessionTitle` / `ctx.approval`；不改变 DSH 的任何行为。
- `approval/request` 是 waterfall：只观察并**总是** `next()` 交回决定权，观察异常一律吞掉；
  缺少 `next()` 时原样放行。
- 原子写状态文件（临时文件 + rename），窗口永远读不到半截 JSON。
- **无条件心跳**（默认 2 秒）：只在有会话运行时写会让空闲的 DSH 被误判成「已退出」。
- 退出时写一帧「宿主已退出」的快照，窗口据此显示断开而不是一直等。
- `/hud` 命令：窗口被关掉后不必重启 DSH 就能叫回来（单实例互斥体保证不重复开窗）。

**窗口（`app/`，C# / WinUI 3）**

- 自定义标题栏整条可拖动、边缘可缩放、可置顶；标题按档位自动换短名，长文本一律 `TextTrimming` 不溢出。
- 跟随系统明暗（`UISettings.ColorValuesChanged`），也可手动锁定浅色/深色。
- 随窗口大小分层自适应：字号、留白、按键尺寸、列宽、区块取舍（窄 / 标准 / 宽 + 矮窗口降级）。
- 列表 / 格子两种视图。格子用 `UniformGridLayout` 按可用宽度自动决定列数。
- 「精简」在两个视图下都有可见效果（列表折叠时间线；格子隐藏摘要行并降低磁贴高度）。
- 设置面板：显示会话上限、空闲隐藏分钟数、明暗主题。两条过滤规则都对「待批准 / 运行中」豁免，
  且会话上限不依赖快照自身顺序（自己再排一次序再取前 N）。
- 「上一条输出」按 Markdown 渲染（粗体/斜体/行内代码/删除线/链接/转义/标题/列表/引用），
  重点在**不误伤**普通文本（`3 * 4 = 12`、`snake_case_name`、未闭合的 `**` 都保持原样）。
- 会话标题、模型、回合、用量、批准策略；工具调用时间线（含成功/失败/耗时）。
- 应用图标由 `assets/make-icon.py` 从一张立绘生成（9 档尺寸打包进一个 ico）。

**打包**

- `build.ps1 -Pack` 产出 self-contained 的 `dist/`：目标机器不需要预装 .NET 或 Windows App Runtime。
- 符合 DSH bundle 标准：`package.json` 的 `dsh.bundle.patch` + `cordis.patch.yml` 插入 loader 条目。
- 两份离线测试：插件端到端（`test/plugin.test.mjs`）与 Markdown 解析器单元测试（`test/markdown.test`）。
