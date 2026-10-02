# 安全说明

## 报告问题

如果你发现安全问题，请**不要**开公开 issue。用仓库的
[Security Advisories](https://github.com/xieani090612/dsh-session-hud/security/advisories/new)
私下报告，或直接开一条只写「想报告安全问题，请给联系方式」的 issue，由维护者接洽。

我们会尽快确认，并在修复后于 `CHANGELOG.md` 里说明。

## 这个插件会做什么、不会做什么

知道边界比读一遍代码更快：

**会做的**

- **只读**观察 DSH 的会话状态：订阅 `session/event`、`agent/status`、`approval/request`、
  `user-questions/request`、`session/created|disposed`，读取 `ctx.sessions` / `ctx.sessionTitle` / `ctx.approval`。
- 把观察到的内容写成一个本机 JSON 状态文件（原子写：临时文件 + rename）。
- **启动**一个本机进程 `DshSessionHud.exe`（HUD 窗口），并在它退出后不再自动重启。
- 注册一个宿主路由 `POST /dsh-session-hud/open`，供 DSH 网页里的按钮调用。
- 注册一个 `/hud` 会话命令。

**不会做的**

- 不改 DSH 的任何行为：`approval/request` / `user-questions/request` 是 waterfall 事件，
  插件只观察并**总是** `next()` 把决定权交回去；缺少 `next()` 时原样放行。
- 不联网、不上传、不下载。没有任何出站请求。
- 不读会话正文、不读文件内容、不碰凭证。状态文件里只有：会话标题、工作区路径、
  工具名与参数摘要、批准提示文本、用量计数。
- 不注册 Windows 服务、不写注册表、不要求管理员权限。

## 那条路由的防护

`POST /dsh-session-hud/open` 会启动一个进程，所以它自带信任栅栏：

1. `Host` 必须是回环权威（`localhost` / `*.localhost` / `127.0.0.0/8` / `::1`），
   **逐段校验**，所以 `127.0.0.1.evil.com` 这类相似域名不算；
2. `Sec-Fetch-Site: cross-site` 一律拒；
3. 带 `Origin` 时必须与 `Host` 同源；
4. 非 `POST` 一律 405；
5. 宿主自带栅栏（`connection.requestRejection`）可用时再委托它，且**它抛异常按拒绝处理**；
6. 以上任何一步出现异常都 fail closed。

对应的测试在 `test/plugin.test.mjs` 的第 11 组 —— 伪造 Host、跨站、异源、非 POST 都有断言。

## 已知的信任假设

- 插件以**当前用户的权限**运行，和 DSH 自身一致。能在你机器上跑代码的人本来就能做这些事。
- 状态文件写在 `%USERPROFILE%\.dsh\session-hud\state.json`，窗口设置写在
  `%LOCALAPPDATA%\DshSessionHud\window.json`。两者都只在本机，权限继承目录默认值。
- 窗口进程会读状态文件并渲染其中的文本。按 Markdown 只解析**行内样式子集**，
  链接只认 `http`/`https`/`mailto`，其它一律按字面量显示（见 `app/MarkdownParser.cs`）。
