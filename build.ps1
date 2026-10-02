# 构建 DSH 会话 HUD（WinUI 3 窗口）
#
#   .\build.ps1            # 常规 Release 构建（开发用，输出在 app\bin\...）
#   .\build.ps1 -Pack      # 发布到仓库根的 dist\ —— 这就是随 npm 包发布的正式位置
#   .\build.ps1 -Clean     # 先清 bin/obj/dist
#
# 说明：
#   * 本机没有全局 .NET SDK，脚本优先用装在 %USERPROFILE%\.dotnet 的私有 SDK。
#   * Windows App SDK 采用 self-contained 模式：最终 exe 不依赖预装的
#     Windows App Runtime，也不需要 MSIX 打包，拷到同架构 Windows 上就能跑。
#     代价是 dist\ 会带上完整运行时（约 160MB+）。
#   * 发布目录会被裁掉 .pdb（调试符号，随包发布没意义）。

param(
    [switch]$Pack,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$root = $PSScriptRoot
$appDir = Join-Path $root 'app'
$distDir = Join-Path $root 'dist'

if (-not (Test-Path $appDir)) { throw "找不到 app 目录：$appDir" }

if ($Clean) {
    Write-Host '清理 bin/obj/dist …' -ForegroundColor Cyan
    foreach ($dir in @((Join-Path $appDir 'bin'), (Join-Path $appDir 'obj'), $distDir)) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
}

Push-Location $appDir
try {
    if ($Pack) {
        Write-Host '发布（self-contained, win-x64）-> dist\' -ForegroundColor Cyan
        if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }
        & $dotnet publish -c Release -r win-x64 --self-contained true -o $distDir -v minimal
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 退出码 $LASTEXITCODE" }
    }
    else {
        Write-Host '构建（Release）…' -ForegroundColor Cyan
        & $dotnet build -c Release -v minimal
        if ($LASTEXITCODE -ne 0) { throw "dotnet build 退出码 $LASTEXITCODE" }
    }
}
finally {
    Pop-Location
}

if ($Pack) {
    # 调试符号不随包发布（保留也无用，还多占体积）。
    $pdbs = @(Get-ChildItem $distDir -Filter '*.pdb' -Recurse -File -ErrorAction SilentlyContinue)
    if ($pdbs.Count -gt 0) {
        $freed = ($pdbs | Measure-Object Length -Sum).Sum
        $pdbs | Remove-Item -Force
        Write-Host ("已裁掉 {0} 个 .pdb（{1:N0} 字节）" -f $pdbs.Count, $freed) -ForegroundColor DarkGray
    }

    # exe 旁边的 app.ico 是必须的：窗口启动时用它调 AppWindow.SetIcon。
    if (-not (Test-Path (Join-Path $distDir 'app.ico'))) {
        Write-Warning 'dist\ 里没有 app.ico —— 窗口图标会回落到 exe 内嵌图标。'
    }

    $exe = Join-Path $distDir 'DshSessionHud.exe'
    if (-not (Test-Path $exe)) { throw "发布成功但没找到 $exe" }

    $files = @(Get-ChildItem $distDir -Recurse -File)
    $sizeMb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB, 1)
    Write-Host "`n打包完成：$exe" -ForegroundColor Green
    Write-Host ("  {0} 个文件，{1} MB" -f $files.Count, $sizeMb) -ForegroundColor Green
    Write-Host '  下一步：npm pack（生成可安装的 .tgz）' -ForegroundColor DarkGray
}
else {
    $exe = Join-Path $appDir 'bin\Release\net8.0-windows10.0.19041.0\win-x64\DshSessionHud.exe'
    if (Test-Path $exe) { Write-Host "`n构建完成：$exe" -ForegroundColor Green }
    else { Write-Warning "构建似乎成功，但没找到预期的 exe：$exe" }
}
