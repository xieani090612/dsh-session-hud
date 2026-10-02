# 参与开发

欢迎 issue 和 PR。这个插件的结构是「一个宿主插件 + 一个原生窗口」，
两边都可以单独开发和单独测试。

## 环境

| 需要 | 用途 | 说明 |
| --- | --- | --- |
| Windows 10 1809+ | 跑窗口 | WinUI 3 要求 |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 构建窗口 | **只有开发需要**；使用者用预编译的 `dist/` 就不用装 |
| Node.js 20+ | 跑插件的离线测试 | 用 DSH 自带的那份也行 |

不需要 Visual Studio，也不需要预装 Windows App Runtime（构建走 self-contained）。

## 上手

```powershell
git clone https://github.com/xieani090612/dsh-session-hud.git
cd dsh-session-hud
.\build.ps1 -Pack        # 构建窗口到 dist\（第一次约 1–2 分钟）
.\test\run-all.ps1       # 三套离线测试
```

然后把它装进一个 DSH profile：

```powershell
# 图形界面：插件管理器里填 link:<仓库绝对路径>
# 或直接编辑 profile 的 package.json（见 README「安装」），再重启 DSH
```

`link:` 装法改完代码不用重装，但**插件代码（`lib/`）改动需要重启 DSH** 才生效 ——
HMR 不跟踪 link 进来的 node_modules。窗口（`app/`）改动只要重启窗口，不用动 DSH。

## 目录

```
lib/index.js       宿主半：观察会话 → 写状态文件 → 拉起窗口 / 注册路由与命令
lib/client.js      浏览器半：DSH 侧边栏那个「打开会话 HUD」按钮
app/               WinUI 3 窗口（C# / XAML）
app/HudMetrics.cs  所有尺寸分层的唯一来源（窄 / 标准 / 宽 × 矮窗口降级）
test/              三套离线测试，见下
docs/              README 用的截图
dist/              build.ps1 -Pack 的产物，随包发布，不入库
```

## 测试

`.\test\run-all.ps1` 一次跑完三套：

| 套件 | 驱动方式 | 覆盖 |
| --- | --- | --- |
| `test/plugin.test.mjs` | 假 Cordis context | 会话/回合/步骤追踪、工具参数抽取、批准提示全过程、`next()` 缺失时放行、原子写、exe 查找顺序、路由信任栅栏 |
| `test/client.test.mjs` | `node:vm` 假 `window` + 假 React | 模块 id、factory 形状、槽位注册、渲染结果、样式只用主题 token、点击打到正确的路由 |
| `test/markdown.test` | 纯 `net8.0` 控制台工程 | Markdown 行内子集的 34 项断言，重点在**不要误伤**普通文本 |

三套都不需要 DSH 在跑，也不需要开窗口。窗口自身的布局靠截图 + 像素测量验证（见下）。

## 改代码时要注意的几件事

这些都是踩过的坑，源码注释里也写了原因：

**1. `app/` 里 DIP 与物理像素不能混用。**
XAML 布局和尺寸分层判断用 **DIP**；`AppWindow.Position/Size` 和 `DisplayArea.WorkArea` 用**物理像素**。
换算只允许发生在 `MainWindow.Scale` 那一处。另外构造函数里 `RootGrid.XamlRoot` 还是 `null`，
所以 DPI 必须用 `GetDpiForWindow(hwnd)` 拿，不能用 `RasterizationScale`。

**2. `StackPanel` 会给子元素无限宽度，`TextTrimming` 永远不会触发。**
需要硬约束宽度时必须用 `Grid` 的固定列。工具名那一列就是这么改的。

**3. `ItemsRepeater` 的 `Layout` / `ItemTemplate` 是普通属性，可以直接换；
`ItemsControl` 的排布藏在 `ItemsPanelTemplate` 里，拿不到也换不掉。**
列表 / 格子切换就靠这一点。

**4. 精简开关必须在两个视图下都有可见效果。**
最初格子模式下它只把「当前工作」从 2 行改成 1 行 —— 文本短的时候完全看不出来，
用起来就像「格子模式强制精简、而且关不掉」。现在它同时控制摘要行与磁贴高度。

**5. 网页按钮走槽位，不要碰 DOM。**
注册进 `sidebar.footer.action` 这个官方座位，样式只用 `--dsw-alias-*` 主题 token，
不去猜别的插件的 DOM 结构，也不改 app root。

## 窗口改动的验证方式

窗口是原生进程，没有单元测试框架，所以布局改动靠**截图 + 像素测量**：

1. 用 `%LOCALAPPDATA%\DshSessionHud\window.json` 指定主题、视图模式、尺寸，启动窗口；
2. 截窗口矩形；
3. 用脚本量列位置 / 行带 / 间隙宽度，和预期值比对。

两个必须注意的坑：

- 截图前要 `SetProcessDPIAware()`，否则量到的是缩放后的坐标；
- **窗口要放在屏幕内**（`X × 1.25 + 宽度 ≤ 屏幕宽度`）。放到屏幕外时截图里那片区域是黑的，
  很容易误判成「UI 溢出」——这件事我真的误判过一次。

生成 README 截图的做法就在 `docs/` 里那几张图的尺寸上（Dark/Light/List/Grid + DIP 尺寸）。

## PR 检查单

- [ ] `.\test\run-all.ps1` 全绿
- [ ] 改了窗口布局的话，贴上改动前后的截图（并说明是不是量过像素）
- [ ] `CHANGELOG.md` 加一条
- [ ] 新增的注释解释**为什么**，尤其是踩坑的地方；不要只写「做了什么」
- [ ] 不要在提交的文件里写死个人绝对路径

## 提交信息

不强制格式。把「为什么」写清楚比写对前缀有用。
