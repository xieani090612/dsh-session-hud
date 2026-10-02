# 出一个可发布 / 可上传 Release 的包。
#
#   .\release.ps1                # 构建 dist → 打包 tgz → 算 SHA256
#   .\release.ps1 -SkipBuild     # dist\ 已经是最新的，只打包
#   .\release.ps1 -Zip           # 额外再出一个「给别人安装」的 zip
#   .\release.ps1 -Zip -ZipOut $env:USERPROFILE\Downloads
#
# 产物：
#   dsh-session-hud-<version>.tgz            插件包（给 DSH 插件管理器 / pnpm 装）
#   dsh-session-hud-<version>.tgz.sha256     校验和
#   dsh-session-hud-<version>-install.zip    解压即用的安装包（-Zip），额外带装机脚本与说明
#
# 发布前请先读 README 顶部那条提示（图标权利）。

param(
    [switch]$SkipBuild,
    [switch]$Zip,
    [string]$ZipOut = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pkg = Get-Content (Join-Path $root 'package.json') -Raw | ConvertFrom-Json
$packageName = $pkg.name
$version = $pkg.version
$tarball = "$packageName-$version.tgz"

# --- 1. 构建窗口 -----------------------------------------------------------

if (-not $SkipBuild) {
    Write-Host '构建窗口（self-contained）…' -ForegroundColor Cyan
    & (Join-Path $root 'build.ps1') -Pack
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 -Pack 失败（退出码 $LASTEXITCODE）" }
}

$exe = Join-Path $root 'dist\DshSessionHud.exe'
if (-not (Test-Path $exe)) {
    throw "找不到 $exe。先跑 .\build.ps1 -Pack，或者去掉 -SkipBuild。"
}

# --- 2. 找 node 与 pnpm ----------------------------------------------------

$dshHome = if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }

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
if (-not $node) { $node = (Get-Command node -ErrorAction SilentlyContinue).Source }
if (-not $node) { throw '找不到 node。装一个 Node.js 20+，或用 DSH 自带的那份。' }

$pnpm = Find-DshRuntime 'dependencies\pnpm\bin\pnpm.mjs'
if (-not $pnpm) {
    $pnpx = (Get-Command pnpm -ErrorAction SilentlyContinue).Source
    if ($pnpx) { $pnpm = $null; $pnpmCmd = $pnpx }
    else { throw '找不到 pnpm。装一个，或用 DSH 自带的那份。' }
}

# --- 3. 打包 ---------------------------------------------------------------

Write-Host "`n打包 $tarball …" -ForegroundColor Cyan
Remove-Item (Join-Path $root '*.tgz') -Force -ErrorAction SilentlyContinue

Push-Location $root
try {
    # pnpm pack 会把包里 500 多个文件逐个列出来，对一次发布来说太吵。
    # 正常时只留最后几行（含 Tarball Details），失败时把完整输出倒出来定位。
    if ($pnpm) {
        $packOutput = & $node $pnpm pack --pack-destination . 2>&1
    }
    else {
        $packOutput = & $pnpmCmd pack --pack-destination . 2>&1
    }
    $packExit = $LASTEXITCODE
    if ($packExit -ne 0) {
        $packOutput | ForEach-Object { Write-Host $_ }
        throw "打包失败（退出码 $packExit）"
    }
    $packOutput | Select-Object -Last 3 | ForEach-Object { Write-Host $_ }
}
finally {
    Pop-Location
}

# --- 4. 校验和 -------------------------------------------------------------

$tgzPath = Join-Path $root $tarball
if (-not (Test-Path $tgzPath)) { throw "打包结束了但没找到 $tarball" }

$hash = (Get-FileHash $tgzPath -Algorithm SHA256).Hash
"$hash  $tarball" | Set-Content "$tgzPath.sha256" -Encoding ASCII

$sizeMb = [math]::Round((Get-Item $tgzPath).Length / 1MB, 1)

# --- 5. 可选：给别人安装用的 zip -------------------------------------------

$zipInfo = ''
if ($Zip) {
    $folderName = "$packageName-$version"
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "hud-zip-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $inner = Join-Path $staging $folderName
    New-Item -ItemType Directory -Force -Path $inner | Out-Null

    try {
        # 内容和 npm 包一致，外加两份只在 zip 里出现的东西：
        # install.ps1（一键装）和 INSTALL.txt（给人看的说明）。
        # 刻意**不含** app/ 源码、test/、assets/（原始立绘）—— 装机用不上。
        #
        # package.json 必须显式列出：npm 会自动带上它，所以它**不在** files 里，
        # 只照 files 拷贝就会漏掉 —— 而没有 package.json 的包谁也装不起来。
        $entries = @($pkg.files) + @('package.json', 'LICENSE', 'install.ps1', 'INSTALL.txt')
        foreach ($entry in $entries) {
            $src = Join-Path $root $entry
            if (-not (Test-Path $src)) { throw "要打进 zip 的内容不存在：$entry" }
            Copy-Item $src -Destination $inner -Recurse -Force
        }

        # 打包前把「少了就装不起来」的文件逐个点一遍。
        # （package.json 就是这么漏过一次的。）
        $required = @(
            'package.json',
            'cordis.patch.yml',
            'lib\index.js',
            'lib\client.js',
            'dist\DshSessionHud.exe',
            'install.ps1',
            'INSTALL.txt'
        )
        $missing = @($required | Where-Object { -not (Test-Path (Join-Path $inner $_)) })
        if ($missing.Count -gt 0) {
            throw "zip 里缺少必需文件：$($missing -join ', ')"
        }

        if (-not (Test-Path $ZipOut)) { New-Item -ItemType Directory -Force -Path $ZipOut | Out-Null }
        $zipPath = Join-Path $ZipOut "$folderName-install.zip"
        Remove-Item $zipPath -Force -ErrorAction SilentlyContinue

        # 用 Windows 自带的 bsdtar（libarchive）：比 Compress-Archive 快得多，
        # 而且按 UTF-8 正确记录文件名。
        Push-Location $staging
        try {
            & tar -a -cf $zipPath $folderName
            if ($LASTEXITCODE -ne 0) { throw "tar 打包 zip 失败（退出码 $LASTEXITCODE）" }
        }
        finally {
            Pop-Location
        }

        $zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
        "$zipHash  $folderName-install.zip" | Set-Content "$zipPath.sha256" -Encoding ASCII
        $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

        if ($zipMb -gt 100) {
            Write-Warning "zip 有 $zipMb MB，超过 100 MB 了。"
        }

        $zipInfo = @"

给别人安装的 zip：
  $zipPath
  $zipMb MB（解压后约 $([math]::Round((Get-ChildItem $inner -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)) MB）
  SHA256：$zipHash
"@
    }
    finally {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host @"

完成：
  $tarball   ($sizeMb MB)
  $tarball.sha256
$zipInfo
SHA256：$hash

发布到 Release 之前：
  [ ] 图标权利已确认（PROVENANCE.md），或已换成自己的图
  [ ] CHANGELOG.md 有这一版的条目
  [ ] .\test\run-all.ps1 全绿
  [ ] dist\ 是这次重新构建的，不是旧的
"@ -ForegroundColor Green
