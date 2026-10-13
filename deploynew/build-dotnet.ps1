#Requires -Version 5.1
<#
.SYNOPSIS
    本地打包（无需 Docker）：构建前端 + dotnet publish 后端 -> 整合成程序包 zip。

.DESCRIPTION
    产物（deploynew\ 目录，打包后自动清理临时目录，只留最终压缩包）：
      hsjm-ai-app-<版本>.zip           程序包（后端 publish + 前端 dist + Dockerfile + compose + 服务器启动脚本）
      hsjm-ai-app-<版本>.zip.sha256    校验文件
      appdata-init-<版本>.tar.gz       LineUp 图片数据（publish_images\ 自动打包）
    服务器上用 Docker 跑：以已有 hsjm-ai-api 镜像为基底叠加本程序包（见 server-start.sh）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\deploynew\build-dotnet.ps1
    powershell -ExecutionPolicy Bypass -File .\deploynew\build-dotnet.ps1 -Version 2026.09.10.2
    powershell -ExecutionPolicy Bypass -File .\deploynew\build-dotnet.ps1 -Test    # 测试版（数据库强制 hsjm_ai_new_test 隔离）
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    # 跳过前端构建（web\dist 已是最新时）
    [switch]$SkipWebBuild,
    # 测试版：打包 deploy\compose-test.yaml 作为包内 compose.yaml
    # （数据库强制 hsjm_ai_new_test、Redis db1、关闭 MCP 自注册——与正式版数据级隔离）
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path $tar)) { $tar = 'tar' }

function Die([string]$m) { Write-Host "[错误] $m" -ForegroundColor Red; exit 1 }

# --- 前置检查 ---------------------------------------------------------------
if ($Version -and $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') { Die "版本号非法：$Version" }
if (-not $Version) { $Version = Get-Date -Format 'yyyy.MM.dd' }
foreach ($p in @('web\package.json', 'api\HSJM.AI\HSJM.AI.csproj', 'deploy\compose.yaml') + $(if ($Test) { @('deploy\compose-test.yaml') } else { @() })) {
    if (-not (Test-Path (Join-Path $root $p))) { Die "缺少 $p（请在仓库根目录执行本脚本）" }
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Die "未找到 dotnet 命令。" }
if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) { Die "未找到 pnpm 命令。" }

# --- 输出目录（产物直接放 deploynew\，不再用 release\）-----------------------
$rel = $PSScriptRoot
$name = "hsjm-ai-app-$Version"
$stage = Join-Path $rel "$name.tmp"
$zip = Join-Path $rel "$name.zip"
if (Test-Path $stage) { Die "临时目录已存在：$stage（上次未完成？删掉后重跑）" }
if (Test-Path $zip) { Die "输出已存在：$zip（重打包请换 -Version，如加 .2 后缀）" }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'app'), (Join-Path $stage 'webdist') | Out-Null

# --- [1/4] 前端构建 ---------------------------------------------------------
# 必须在 web 目录内执行：corepack 读 web/package.json 钉定的 pnpm@10.26.2
#（在仓库根目录跑 --dir web 会命中根 package.json 的空钉定 -> 取最新版 -> 版本冲突）。
if ($SkipWebBuild -and (Test-Path (Join-Path $root 'web\dist\index.html'))) {
    Write-Host "[1/4] 跳过前端构建（沿用 web\dist）"
}
else {
    Write-Host "[1/4] 前端构建（pnpm build）……"
    Push-Location (Join-Path $root 'web')
    try {
        if (Get-Command corepack -ErrorAction SilentlyContinue) {
            & corepack pnpm --config.verify-deps-before-run=false build
        }
        else {
            & pnpm --config.verify-deps-before-run=false build
        }
        $webBuildCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($webBuildCode -ne 0) { Die "前端构建失败" }
    if (-not (Test-Path (Join-Path $root 'web\dist\index.html'))) { Die "web\dist\index.html 不存在" }
}
Copy-Item (Join-Path $root 'web\dist\*') (Join-Path $stage 'webdist') -Recurse -Force

# --- [2/4] 后端 dotnet publish（框架依赖版，服务器由基础镜像提供运行时）------
Write-Host "[2/4] 后端构建（dotnet publish Release）……"
& dotnet publish (Join-Path $root 'api\HSJM.AI\HSJM.AI.csproj') -c Release `
    -o (Join-Path $stage 'app') -p:NuGetAudit=false --nologo
if ($LASTEXITCODE -ne 0) { Die "dotnet publish 失败" }
if (-not (Test-Path (Join-Path $stage 'app\HSJM.AI.dll'))) { Die "publish 输出缺少 HSJM.AI.dll" }

# --- 清理开发期运行时残留（与 .dockerignore 对齐，不进包）--------------------
# temp_plc_upload：本地上传解压缓存（可达数百 MB，且任务 ID 目录超长会撞 WinPS 压缩 260 上限）
# skills/plc-analyzer/temp：解析临时文件
foreach ($junkRel in @('app\wwwroot\temp_plc_upload', 'app\wwwroot\skills\plc-analyzer\temp')) {
    $junk = Join-Path $stage $junkRel
    if (Test-Path $junk) {
        Write-Host "  清理开发期残留（不进包）：$junkRel"
        & cmd.exe /c rd /s /q "\\?\$junk"    # \\?\ 前缀：绕开 260 字符路径限制
    }
}
# 超长路径提前拦截（WinPS 5.1 Compress-Archive 上限约 260 字符）
$tooLong = @(Get-ChildItem $stage -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName.Length -gt 240 } | Select-Object -First 5)
if ($tooLong.Count -gt 0) {
    $tooLong | ForEach-Object { Write-Host "  超长路径：$($_.FullName)" -ForegroundColor Yellow }
    Die "存在超过 240 字符的文件路径，WinPS 5.1 压缩必失败；请清理上述开发期残留后重跑。"
}

# --- [3/4] 组装程序包 -------------------------------------------------------
Write-Host "[3/4] 组装程序包（Dockerfile + compose + 启动脚本）……"
$composeSrc = Join-Path $root 'deploy\compose.yaml'
if ($Test) {
    $composeSrc = Join-Path $root 'deploy\compose-test.yaml'
    Write-Host "  [Test] 打包测试版 compose（数据库强制 hsjm_ai_new_test、Redis db1、关 MCP 自注册）"
}
Copy-Item $composeSrc (Join-Path $stage 'compose.yaml')
Copy-Item (Join-Path $PSScriptRoot 'Dockerfile.runtime') (Join-Path $stage 'Dockerfile')
Copy-Item (Join-Path $PSScriptRoot 'server-start.sh') $stage
[System.IO.File]::WriteAllText((Join-Path $stage 'VERSION'), "$Version`n")

# --- [4/4] 压缩 zip + 校验文件 ----------------------------------------------
# 用 System32 bsdtar 生成 zip（-a 按扩展名选格式）：
# WinPS 5.1 的 Compress-Archive 会把条目分隔符写成反斜杠（\），不合 zip 规范，
# Linux unzip 解出来的目录结构会乱，docker build 报 "checking context: can't stat"。
Write-Host "[4/4] 压缩为 zip ……"
& $tar -a -cf $zip -C $stage .
if ($LASTEXITCODE -ne 0) {
    & cmd.exe /c rd /s /q "\\?\$stage"
    Die "zip 压缩失败（tar exit $LASTEXITCODE；已清理临时目录，排查后可直接重跑）"
}
if (-not (Test-Path $zip)) {
    & cmd.exe /c rd /s /q "\\?\$stage"
    Die "zip 未生成（未知原因），已清理临时目录。"
}
# 抽查条目分隔符，防止回归
Add-Type -AssemblyName System.IO.Compression.FileSystem
$check = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $bad = @($check.Entries | Where-Object { $_.FullName.Contains('\') } | Select-Object -First 1)
    if ($bad.Count -gt 0) { Die "zip 条目含反斜杠（$($bad[0].FullName)），打包器异常，请反馈。" }
}
finally { $check.Dispose() }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
[System.IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n")
# 打包成功，清掉临时目录（deploynew 里只留最终产物）
& cmd.exe /c rd /s /q "\\?\$stage"

# --- 图片数据包（publish_images -> appdata-init-<版本>.tar.gz）--------------
$lineUp = Join-Path $root 'publish_images\LineUp'
$imports = Join-Path $root 'publish_images\lineup-imports'
$appdataTgz = ''
if ((Test-Path $lineUp) -or (Test-Path $imports)) {
    Write-Host "打包图片数据（publish_images\）……"
    $staging = Join-Path $rel "appdata-staging-$Version"
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    $ad = Join-Path $staging 'appdata-init'
    if (Test-Path $lineUp) {
        New-Item -ItemType Directory -Force -Path (Join-Path $ad 'LineUp') | Out-Null
        Copy-Item "$lineUp\*" (Join-Path $ad 'LineUp') -Recurse -Force
    }
    if (Test-Path $imports) {
        New-Item -ItemType Directory -Force -Path (Join-Path $ad 'lineup-imports') | Out-Null
        Copy-Item "$imports\*" (Join-Path $ad 'lineup-imports') -Recurse -Force
    }
    $appdataTgz = Join-Path $rel "appdata-init-$Version.tar.gz"
    if (Test-Path $appdataTgz) { Remove-Item $appdataTgz -Force }
    & $tar -C $staging -czf $appdataTgz appdata-init
    if ($LASTEXITCODE -ne 0) { Die "图片数据打包失败" }
    Remove-Item $staging -Recurse -Force
}

# --- 完成指引 ---------------------------------------------------------------
Write-Host ""
Write-Host "================ 本地打包完成 ================" -ForegroundColor Cyan
Write-Host "产物（deploynew\）：$name.zip + .sha256$(if ($appdataTgz) { ' + appdata-init-' + $Version + '.tar.gz' })"
Write-Host ""
Write-Host "第 2 步 · 上传到服务器（示例 /opt/hsjm）："
$files = @("$zip", "$zip.sha256")
if ($appdataTgz) { $files += $appdataTgz }
Write-Host "  scp $($files -join ' ') hsjm@192.168.6.75:/opt/hsjm/"
Write-Host ""
Write-Host "第 3 步 · 服务器上执行："
Write-Host "  cd /opt/hsjm && unzip -o $name.zip -d $name && cd $name"
Write-Host "  bash server-start.sh          # 以已有 hsjm-ai-api 镜像为基底叠加新程序，compose 启动"
Write-Host "============================================="
