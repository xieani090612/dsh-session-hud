# 跑全部离线测试：
#   ① 宿主插件端到端（Node）    test\plugin.test.mjs
#   ② 浏览器半契约（Node + vm） test\client.test.mjs
#   ③ 打包契约（Node）          test\package.test.mjs
#   ④ Markdown 解析器（C#）     test\markdown.test
#
#   .\test\run-all.ps1
#
# 四套都不需要 DSH 在跑，也不需要开窗口。
# node / dotnet 优先用 DSH 与本机私有安装的那份，找不到就回落到 PATH。

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$failed = @()

$node = Join-Path $env:USERPROFILE '.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe'
if (-not (Test-Path $node)) { $node = 'node' }

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

Write-Host '== ① 宿主插件离线测试（假 Cordis context 驱动，检查落盘快照与路由栅栏）==' -ForegroundColor Cyan
& $node (Join-Path $root 'test\plugin.test.mjs')
if ($LASTEXITCODE -ne 0) { $failed += 'plugin.test.mjs' }

Write-Host "`n== ② 浏览器半契约测试（假 window + 假 React 驱动 lib/client.js）==" -ForegroundColor Cyan
& $node (Join-Path $root 'test\client.test.mjs')
if ($LASTEXITCODE -ne 0) { $failed += 'client.test.mjs' }

Write-Host "`n== ③ 打包契约测试（package.json 的 dsh.* / exports / files / 版本号）==" -ForegroundColor Cyan
& $node (Join-Path $root 'test\package.test.mjs')
if ($LASTEXITCODE -ne 0) { $failed += 'package.test.mjs' }

Write-Host "`n== ④ Markdown 解析器单元测试 ==" -ForegroundColor Cyan
& $dotnet run --project (Join-Path $root 'test\markdown.test') -v quiet --nologo
if ($LASTEXITCODE -ne 0) { $failed += 'markdown.test' }

if ($failed.Count -gt 0) {
    Write-Host "`n失败：$($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "`n全部测试通过 ✅" -ForegroundColor Green
