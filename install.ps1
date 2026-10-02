# 一键把 DSH 会话 HUD 装进一个 DSH profile。
#
#   .\install.ps1                          # 装进 desktop profile（默认）
#   .\install.ps1 -Profile other           # 装进别的 profile
#   .\install.ps1 -SkipBuild               # 不构建窗口（dist\DshSessionHud.exe 已存在时）
#   .\install.ps1 -Force                   # 覆盖已有的依赖声明（比如从 tgz 换成 link）
#
# 做的事：确保 dist\ 里有窗口 → 往 profile 的 package.json 里声明依赖与 bundle
#         → 在 profile 目录跑一次 pnpm install → 告诉你重启 DSH。
#
# 用的是 link: 依赖：仓库改了代码不用重装（但插件代码要重启 DSH 才生效）。
# 想装成独立副本请用包管理器装 .tgz，见 README。

param(
    [string]$Profile = 'desktop',
    [switch]$SkipBuild,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$packageName = 'dsh-session-hud'

# --- 1. 窗口可执行文件 ------------------------------------------------------

$exe = Join-Path $root 'dist\DshSessionHud.exe'
if (-not (Test-Path $exe)) {
    if ($SkipBuild) {
        throw "找不到 $exe，而且指定了 -SkipBuild。先跑一次 .\build.ps1 -Pack。"
    }
    # 别人拿到的安装包应该已经带好 dist\。走到这里说明解压不完整，
    # 或者拿到的是源码仓库 —— 两者要给的提示完全不同。
    $builder = Join-Path $root 'build.ps1'
    if (-not (Test-Path $builder)) {
        throw @"
找不到 dist\DshSessionHud.exe，也找不到 build.ps1。

这个目录既不是完整的安装包，也不是完整的源码仓库。请：
  * 重新解压一份完整的安装包（里面应该已经有 dist\ 和几百个文件）；或者
  * 从仓库克隆完整源码，在那里跑 .\build.ps1 -Pack。
"@
    }
    Write-Host '第一次安装：先构建窗口（需要 .NET 8 SDK，约 1–2 分钟）…' -ForegroundColor Cyan
    & $builder -Pack
    if (-not (Test-Path $exe)) { throw "构建结束但没产出 $exe" }
}
Write-Host "窗口就绪：$exe" -ForegroundColor Green

# --- 2. 找 DSH_HOME 与 profile ---------------------------------------------

$dshHome = if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }
$profileDir = Join-Path $dshHome "profiles\$Profile"
$manifestPath = Join-Path $profileDir 'package.json'
if (-not (Test-Path $manifestPath)) {
    throw "找不到 profile 的 package.json：$manifestPath（-Profile 名字对吗？）"
}

# --- 3. 改 profile 的依赖与 bundle 列表 ------------------------------------

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$spec = 'link:' + ($root -replace '\\', '/')

if (-not $manifest.PSObject.Properties['dependencies']) {
    $manifest | Add-Member -NotePropertyName dependencies -NotePropertyValue ([pscustomobject]@{})
}
$current = $manifest.dependencies.PSObject.Properties[$packageName]
if ($current -and -not $Force) {
    Write-Host "依赖已存在：$($current.Value)（要换成 link 加 -Force）" -ForegroundColor Yellow
}
else {
    if ($current) { $current.Value = $spec }
    else { $manifest.dependencies | Add-Member -NotePropertyName $packageName -NotePropertyValue $spec }
    Write-Host "依赖已写入：$spec" -ForegroundColor Green
}

# bundle 列表里必须有这个包名，否则 patch 不会被加载。
if (-not $manifest.PSObject.Properties['dsh'] -or -not $manifest.dsh.PSObject.Properties['profile']) {
    throw "profile 的 package.json 里没有 dsh.profile 段，结构不对：$manifestPath"
}
$bundles = @($manifest.dsh.profile.bundles)
if ($bundles -notcontains $packageName) {
    $manifest.dsh.profile.bundles = @($bundles + $packageName)
    Write-Host "bundle 列表已追加：$packageName" -ForegroundColor Green
}
else {
    Write-Host "bundle 列表已包含：$packageName" -ForegroundColor Yellow
}

# 先备份再写：这个文件坏了 DSH 会起不来，留一份能立刻救回来。
$backup = "$manifestPath.bak"
Copy-Item $manifestPath $backup -Force
$manifest | ConvertTo-Json -Depth 10 | Set-Content $manifestPath -Encoding UTF8
Write-Host "已写入 $manifestPath（备份：$backup）" -ForegroundColor Green

# --- 4. 在 profile 里跑 pnpm install ---------------------------------------

function Find-DshRuntime([string]$Relative) {
    $base = Join-Path $dshHome 'dsh-runtimes'
    if (-not (Test-Path $base)) { return $null }
    foreach ($runtime in Get-ChildItem $base -Directory -ErrorAction SilentlyContinue) {
        $candidate = Join-Path $runtime.FullName $Relative
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

$node = Find-DshRuntime 'dependencies\node\bin\node.exe'
$pnpm = Find-DshRuntime 'dependencies\pnpm\bin\pnpm.mjs'

if ($node -and $pnpm) {
    Write-Host "`n在 profile 里安装依赖…" -ForegroundColor Cyan
    & $node $pnpm install -C $profileDir
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "pnpm install 退出码 $LASTEXITCODE —— 上面有原因。修好之后也可以直接用 DSH 的插件管理器装。"
    }
}
else {
    Write-Warning @'
没找到 DSH 自带的 node/pnpm，已跳过依赖安装这一步。
请用 DSH 的插件管理器再装一次（它内部会跑包管理器），或者手动在 profile 目录跑 pnpm install。
'@
}

# --- 5. 收尾 ---------------------------------------------------------------

Write-Host @"

装好了。**请重启 DSH** —— 插件模块路径变了之后必须重启才会加载新包。

重启后应该能看到三样东西：
  * 悬浮窗随 DSH 一起打开；
  * 侧边栏底部、设置按钮旁边多了一个小窗口图标（打开 / 前置 HUD）；
  * 会话里可以用 /hud 命令。
"@ -ForegroundColor Green
