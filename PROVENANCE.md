# 素材来源与许可范围（PROVENANCE）

本插件的**代码**按 MIT 许可（见 [`LICENSE`](LICENSE)）。下面三类内容**不在 MIT 覆盖范围内**，各自按其来源的条款对待。

## 一、许可范围

| 范围 | 许可 |
|---|---|
| `lib/`、`app/`（C# / XAML 源码）、`cordis.patch.yml`、`package.json`、文档与脚本 | **MIT**（见 `LICENSE`） |
| `assets/icon-source.webp`、`app/app.ico`、`docs/icon-*.png` | **不适用 MIT** —— 立绘由**使用者自行提供**，仅用于生成本插件窗口/任务栏图标（见第二节） |
| `dist/**` | **不适用 MIT** —— 内含 Microsoft 的可再分发运行时（见第三节） |

这样划分的原因和 dsh-whale-widget 一致：代码可以明确授权，而美术素材与第三方运行时的权利状态不该替它们背书。与其给一个站不住的授权，不如如实标注范围。

## 二、图标立绘

`assets/icon-source.webp` 是**使用者在本地提供**的一张 Q 版角色立绘，本仓库不对其主张任何权利，也不授予任何再许可。

- `app/app.ico` 由 `assets/make-icon.py` 从该图生成（裁掉透明边 + 缩放成 9 档尺寸），**像素内容完全来自原图**，没有二次创作；
- `docs/icon-256.png` 与 `docs/icon-preview.png` 也是同一脚本生成的**派生预览图**，只给 README 看。
  它们放在 `docs/`（随包发布）而不是 `assets/`（不随包发布），所以 **npm 包里不会有原始立绘
  `icon-source.webp`**，只有这两张派生图与窗口图标 `app.ico`；
- 因此本插件**不随包分发这张立绘的商业使用许可**。如果你要公开分发这个包，请先确认该图的权利状态，或换成你自己拥有权利的图 —— 换图只需要替换 `assets/icon-source.webp` 再跑一次 `python assets/make-icon.py`；
- 如果你是该图的作者并认为这里的使用不当，开一条 issue 说明文件名与依据，我们会立即移除或替换。

## 三、随包的可执行文件（dist/）

`dist/` 是 `build.ps1 -Pack` 产出的 **self-contained** 发布目录，目标是「拷到哪台同架构 Windows 上都能直接跑」，因此里面带了完整的运行时：

| 内容 | 来源 / 许可 |
|---|---|
| `DshSessionHud.exe`、`DshSessionHud.dll`、`*.xbf`、`resources.pri` | 本插件，**MIT** |
| .NET 运行时（`System.*.dll`、`coreclr.dll`、`hostpolicy.dll` 等） | Microsoft .NET，**MIT** |
| Windows App SDK / WinUI（`Microsoft.ui.xaml.dll`、`Microsoft.WinUI.dll`、`Microsoft.WindowsAppRuntime.*` 等） | Microsoft，适用其**可再分发条款**（Windows App SDK 许可） |
| Windows SDK 投影（`Microsoft.Windows.SDK.NET.dll`、`*.winmd`） | Microsoft，适用其可再分发条款 |

这些第三方二进制**没有**被修改，只是原样随包分发。若你要把这个包发布到公开渠道，请自行确认上述再分发条款对你的分发方式成立。

## 四、不含用户数据

插件不收集也不上传任何东西：状态文件 `%USERPROFILE%\.dsh\session-hud\state.json` 只在**本机**由宿主进程写、由窗口进程读，内容只有会话标题、工作区路径、工具名与批准提示。窗口的位置/开关/设置存在 `%LOCALAPPDATA%\DshSessionHud\window.json`，同样只在本机。
