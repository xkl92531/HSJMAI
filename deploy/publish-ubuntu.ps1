#Requires -Version 5.1
<#
.SYNOPSIS
    HSJM 设备诊断平台 Ubuntu 服务器一键部署脚本（Windows 打包机侧）。

.DESCRIPTION
    在 Windows 打包机上执行一条命令，完成：
      1. 构建 Linux amd64 离线交付包（Online / Offline / 复用已有 Archive 三种模式）
      2. 自动打包仓库根目录 publish_images/ 下的 LineUp 工站图片与导入源数据
         （缺省开启；-NoAppData 跳过，-AppDataInit 指定自定义数据包）
      3. 通过 SSH/SCP 上传离线包（及图片数据包）到 Ubuntu 服务器
      4. 远端自动完成：SHA-256 校验 -> 解压 -> docker image load -> 端口选择 -> compose 启动
         -> 存活/就绪/MCP 验证 -> 图片数据灌卷（hsjm-ai_api-uploads，合并不覆盖、修正属主）
      5. 回传最终平台访问地址

    远端复用离线包内自带的 deploy.sh（install.sh + start.sh + verify.sh），
    行为与《README-Linux离线部署.md》完全一致，本脚本只是把"打包-上传-安装"串成一键。

    SSH 认证：优先配置免密（公钥）登录；密码认证时按提示交互输入
    （preflight / scp / deploy 三次 SSH 会话，最多输入 3 次密码）。

.EXAMPLE
    .\deploy\publish-ubuntu.ps1
        # 默认：自动版本号(yyyy.MM.dd)、Auto 构建模式、192.168.6.75 / hsjm / /opt/hsjm

.EXAMPLE
    .\deploy\publish-ubuntu.ps1 -Version 2026.09.08.2 -HttpPort 28500
        # 指定版本号与固定端口部署

.EXAMPLE
    .\deploy\publish-ubuntu.ps1 -LocalOnly
        # 手工模式：只本地打包（离线包 + 图片数据包），不连接服务器；
        # 之后自己上传，再到服务器上执行 ./server-deploy.sh

.EXAMPLE
    .\deploy\publish-ubuntu.ps1 -Archive .\release\hsjm-ai-linux-amd64-2026.09.03.tar.gz -ServerHost 192.168.6.75
        # 复用已构建好的离线包，跳过本地构建

.NOTES
    兼容 Windows PowerShell 5.1 与 PowerShell 7+。
#>
[CmdletBinding()]
param(
    # 部署版本号；留空自动使用 yyyy.MM.dd。仅含字母数字点下划线连字符。
    [string]$Version = '',

    # Auto：D:\sdk 存在走 Offline，否则 Online；Online 联网 buildx 构建；Offline 走 build-bundle-offline.ps1。
    [ValidateSet('Auto', 'Online', 'Offline')]
    [string]$BuildMode = 'Auto',

    # 复用已有离线包（跳过构建）。文件名必须形如 hsjm-ai-linux-amd64-<版本>.tar.gz。
    [string]$Archive = '',

    # Offline 模式的 SDK 离线缓存根目录（传给 build-bundle-offline.ps1）。
    [string]$SdkCacheRoot = 'D:\sdk',

    # 构建时强制 --no-cache。
    [switch]$NoCache,

    # 目标 Ubuntu 服务器。
    [string]$ServerHost = '192.168.6.75',
    [string]$ServerUser = 'hsjm',
    [ValidateRange(1, 65535)]
    [int]$ServerPort = 22,

    # 服务器上的部署根目录（必须已存在或当前用户可创建，且有写权限）。
    [string]$RemoteRoot = '/opt/hsjm',

    # 固定端口（1024-65535）；0 = 由服务器 start.sh 在 28000-28999 自动选择空闲端口。
    [ValidateRange(0, 65535)]
    [int]$HttpPort = 0,

    # 可选 Redis 连接覆盖（CSRedis 格式 host:port,password=CHANGE_ME,defaultDatabase=0），写入远端 .env。
    [string]$RedisConnection = '',

    # 可选时区覆盖，默认 Asia/Shanghai。
    [string]$TimeZone = 'Asia/Shanghai',

    # 可选启动健康等待秒数覆盖，0 = 使用离线包默认 240。
    [ValidateRange(0, 36000)]
    [int]$StartTimeoutSeconds = 0,

    # 可选：LineUp 历史图片/导入数据迁移包 appdata-init.tar.gz 的本地路径。
    # 缺省时自动从仓库根目录 publish_images/（LineUp + lineup-imports）打包生成。
    [string]$AppDataInit = '',

    # 跳过 publish_images/ 图片数据的自动打包与迁移（显式 -AppDataInit 不受影响）。
    [switch]$NoAppData,

    # 只在本地打包（构建离线包 + 图片数据包），不连接服务器、不上传、不远端执行。
    # 打包结束打印手工上传与服务器端 ./server-deploy.sh 的操作指引。
    [switch]$LocalOnly,

    # 跳过服务器磁盘剩余空间（离线包 3 倍）检查。
    [switch]$SkipDiskCheck,

    # 只打印将执行的命令，不实际执行（不上传、不部署）。
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ScriptDirectory = Split-Path -Parent $PSCommandPath
$script:RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $script:ScriptDirectory '..')).Path

# ---------------------------------------------------------------------------
# 输出与失败处理
# ---------------------------------------------------------------------------

function Write-Info {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "[INFO] $Message"
}

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Stop-Publish {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "[ERROR] $Message" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# 通用工具
# ---------------------------------------------------------------------------

function Assert-Command {
    param([Parameter(Mandatory = $true)][string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Stop-Publish "缺少命令：$Name。请先安装并加入 PATH。"
    }
}

function Resolve-TarTool {
    # 优先 System32 bsdtar，避免 Git Bash 的 MSYS tar 把 E:\ 当 host:path。
    $systemTar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (Test-Path -LiteralPath $systemTar) {
        return $systemTar
    }
    Assert-Command 'tar'
    return 'tar'
}

function Get-FileSha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )
    $display = "$FilePath $($Arguments -join ' ')"
    if ($DryRun) {
        Write-Host "[DRYRUN] $display"
        return 0
    }
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        Stop-Publish "命令失败(exit=$LASTEXITCODE)：$display"
    }
    return $LASTEXITCODE
}

function Write-Utf8NoBomText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Text
    )
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function New-BundleSha256Manifest {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )
    $lines = Get-ChildItem -LiteralPath $Directory -Recurse -File |
        Where-Object { $_.FullName -ne $OutputPath } |
        Sort-Object FullName |
        ForEach-Object {
            # WinPS 5.1 无 Path::GetRelativePath，文件必在目录下，前缀截断等价。
            $relative = $_.FullName.Substring($Directory.TrimEnd('\').Length + 1).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
    [System.IO.File]::WriteAllLines($OutputPath, $lines, [System.Text.UTF8Encoding]::new($false))
}

# ---------------------------------------------------------------------------
# 输入校验
# ---------------------------------------------------------------------------

function Assert-DeploymentInputs {
    if ($ServerHost -notmatch '^[A-Za-z0-9._-]+$') {
        Stop-Publish "ServerHost 含非法字符：$ServerHost"
    }
    if ($ServerUser -notmatch '^[A-Za-z0-9._-]+$') {
        Stop-Publish "ServerUser 含非法字符：$ServerUser"
    }
    if ($RemoteRoot -notmatch '^/[A-Za-z0-9._/-]+$') {
        Stop-Publish "RemoteRoot 必须是不含空格的绝对路径(POSIX)，当前：$RemoteRoot"
    }
    if ($RedisConnection -ne '' -and $RedisConnection -notmatch '^[A-Za-z0-9:,._=@%+~!?*^|-]+$') {
        Stop-Publish "RedisConnection 含不被支持的特殊字符（避免引号/空格/反引号），请改在服务器上手工维护 .env。"
    }
    if ($TimeZone -ne '' -and $TimeZone -notmatch '^[A-Za-z0-9_+\-/]+$') {
        Stop-Publish "TimeZone 含非法字符：$TimeZone"
    }
    if ($Version -ne '' -and $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
        Stop-Publish "版本号只能包含字母、数字、点、下划线和连字符，且以字母或数字开头：$Version"
    }
    if ($AppDataInit -ne '') {
        $AppDataInit = [System.IO.Path]::GetFullPath($AppDataInit)
        if (-not (Test-Path -LiteralPath $AppDataInit -PathType Leaf)) {
            Stop-Publish "AppDataInit 文件不存在：$AppDataInit"
        }
    }
}

# ---------------------------------------------------------------------------
# 构建模式选择与执行
# ---------------------------------------------------------------------------

function Resolve-BuildMode {
    if ($BuildMode -ne 'Auto') {
        return $BuildMode
    }
    if (Test-Path -LiteralPath $SdkCacheRoot) {
        return 'Offline'
    }
    return 'Online'
}

function Assert-LocalBuildEnvironment {
    if ($DryRun) {
        if (-not (Get-Command 'docker' -ErrorAction SilentlyContinue)) {
            Write-Host "[DRYRUN] 警告：当前打包机未安装/未启动 docker，正式执行时将在构建步骤失败。" -ForegroundColor Yellow
        }
        return
    }
    Assert-Command 'docker'
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        Stop-Publish "Docker Engine 未运行或当前用户无权访问。请先启动 Docker Desktop（或安装 Docker Engine）。"
    }
    & docker buildx version *> $null
    if ($LASTEXITCODE -ne 0) {
        Stop-Publish "需要 Docker Buildx。"
    }
}

function Build-OnlineBundle {
    param([Parameter(Mandatory = $true)][string]$BuildVersion)

    Assert-LocalBuildEnvironment

    $releaseRoot = Join-Path $script:RepositoryRoot 'release'
    $bundleName = "hsjm-ai-linux-amd64-$BuildVersion"
    $bundleDirectory = Join-Path $releaseRoot $bundleName
    $archivePath = Join-Path $releaseRoot "$bundleName.tar.gz"
    $archiveChecksumPath = "$archivePath.sha256"

    if (-not $DryRun) {
        New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
        foreach ($outputPath in @($bundleDirectory, $archivePath, $archiveChecksumPath)) {
            if (Test-Path -LiteralPath $outputPath) {
                Stop-Publish "输出已存在：$outputPath（同版本重打包请换 -Version，例如加 .2 后缀）"
            }
        }
    }

    Write-Info "联网构建镜像 hsjm-ai-api:$BuildVersion（linux/amd64）……"
    $buildArguments = @(
        'buildx', 'build',
        '--platform', 'linux/amd64',
        '--load',
        '--file', (Join-Path $script:RepositoryRoot 'deploy\docker\api.Dockerfile'),
        '--tag', "hsjm-ai-api:$BuildVersion"
    )
    if ($NoCache) {
        $buildArguments += '--no-cache'
    }
    $buildArguments += $script:RepositoryRoot
    Invoke-Native -FilePath 'docker' -Arguments $buildArguments | Out-Null

    if (-not $DryRun) {
        $platform = (& docker image inspect --format '{{.Os}}/{{.Architecture}}' "hsjm-ai-api:$BuildVersion").Trim()
        if ($LASTEXITCODE -ne 0 -or $platform -ne 'linux/amd64') {
            Stop-Publish "镜像架构不是 linux/amd64：$platform"
        }
    }

    Write-Info "组装离线包目录……"
    if (-not $DryRun) {
        New-Item -ItemType Directory -Path (Join-Path $bundleDirectory 'images') -Force | Out-Null
        $savedImage = Join-Path $bundleDirectory "images\hsjm-ai-api-$BuildVersion-linux-amd64.tar"
        Invoke-Native -FilePath 'docker' -Arguments @('image', 'save', '--output', $savedImage, "hsjm-ai-api:$BuildVersion") | Out-Null

        Copy-Item -LiteralPath (Join-Path $script:RepositoryRoot 'deploy\compose.yaml') -Destination (Join-Path $bundleDirectory 'compose.yaml')
        Copy-Item -LiteralPath (Join-Path $script:RepositoryRoot 'deploy\.env.example') -Destination (Join-Path $bundleDirectory '.env.example')
        Copy-Item -LiteralPath (Join-Path $script:RepositoryRoot 'deploy\README-Linux离线部署.md') -Destination (Join-Path $bundleDirectory 'README-Linux离线部署.md')
        foreach ($manualName in @(
                'HSJM-MCP流式诊断与Docker部署手册.docx',
                'HSJM-6.131-Docker部署与MCP调用手册.docx')) {
            $manual = Join-Path $script:RepositoryRoot "docs\$manualName"
            if (Test-Path -LiteralPath $manual -PathType Leaf) {
                Copy-Item -LiteralPath $manual -Destination (Join-Path $bundleDirectory $manualName)
            }
        }
        Copy-Item -Path (Join-Path $script:RepositoryRoot 'deploy\scripts\*.sh') -Destination $bundleDirectory
        Write-Utf8NoBomText -Path (Join-Path $bundleDirectory 'VERSION') -Text "$BuildVersion`n"
        New-BundleSha256Manifest -Directory $bundleDirectory -OutputPath (Join-Path $bundleDirectory 'SHA256SUMS')

        $tarTool = Resolve-TarTool
        Invoke-Native -FilePath $tarTool -Arguments @('-C', $releaseRoot, '-czf', $archivePath, $bundleName) | Out-Null
        $archiveHash = Get-FileSha256Hex -Path $archivePath
        Write-Utf8NoBomText -Path $archiveChecksumPath -Text "$archiveHash  $bundleName.tar.gz`n"
    }
    else {
        # DryRun 时给用户展示关键产物路径
        Write-Host "[DRYRUN] 产物目录：$bundleDirectory"
        Write-Host "[DRYRUN] 压缩包：$archivePath"
    }

    return @{
        ArchivePath = $archivePath
        BundleName  = $bundleName
        Version     = $BuildVersion
    }
}

function Build-OfflineBundle {
    param([Parameter(Mandatory = $true)][string]$BuildVersion)

    if (-not (Test-Path -LiteralPath $SdkCacheRoot)) {
        Stop-Publish "Offline 模式需要 SDK 离线缓存目录（-SdkCacheRoot），不存在：$SdkCacheRoot。可改用 -BuildMode Online 或 -Archive。"
    }

    $offlineScript = Join-Path $script:RepositoryRoot 'deploy\offline\build-bundle-offline.ps1'
    if (-not (Test-Path -LiteralPath $offlineScript -PathType Leaf)) {
        Stop-Publish "缺少离线构建脚本：$offlineScript"
    }

    $releaseRoot = Join-Path $script:RepositoryRoot 'release'
    $bundleName = "hsjm-ai-linux-amd64-$BuildVersion"
    $archivePath = Join-Path $releaseRoot "$bundleName.tar.gz"

    $offlineArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', $offlineScript,
        '-Version', $BuildVersion,
        '-SdkCacheRoot', $SdkCacheRoot
    )
    if ($NoCache) {
        $offlineArguments += '-NoCache'
    }

    Write-Info "调用离线构建（独立 PowerShell 进程）：powershell $($offlineArguments -join ' ')"
    Invoke-Native -FilePath 'powershell.exe' -Arguments $offlineArguments | Out-Null

    if (-not $DryRun -and -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        Stop-Publish "离线构建结束但未找到产物：$archivePath"
    }

    return @{
        ArchivePath = $archivePath
        BundleName  = $bundleName
        Version     = $BuildVersion
    }
}

function Resolve-ExistingArchive {
    $Archive = [System.IO.Path]::GetFullPath($Archive)
    if (-not (Test-Path -LiteralPath $Archive -PathType Leaf)) {
        Stop-Publish "Archive 离线包不存在：$Archive"
    }
    $fileName = Split-Path -Leaf $Archive
    if ($fileName -notmatch '^hsjm-ai-linux-amd64-(.+)\.tar\.gz$') {
        Stop-Publish "离线包文件名必须形如 hsjm-ai-linux-amd64-<版本>.tar.gz，当前：$fileName"
    }
    $archiveVersion = $Matches[1]
    if ($Version -ne '' -and $Version -ne $archiveVersion) {
        Stop-Publish "-Version($Version) 与离线包文件名中的版本($archiveVersion)不一致。"
    }

    $checksumPath = "$Archive.sha256"
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        if ($DryRun) {
            Write-Host "[DRYRUN] 缺少 .sha256，正常执行时会在本地重新计算生成。"
        }
        else {
            Write-Info "缺少 .sha256 校验文件，本地重新计算生成：$checksumPath"
            $hash = Get-FileSha256Hex -Path $Archive
            Write-Utf8NoBomText -Path $checksumPath -Text "$hash  $fileName`n"
        }
    }

    return @{
        ArchivePath = $Archive
        BundleName  = "hsjm-ai-linux-amd64-$archiveVersion"
        Version     = $archiveVersion
    }
}

# ---------------------------------------------------------------------------
# LineUp 图片数据（publish_images）自动打包
# ---------------------------------------------------------------------------

function Resolve-AutoAppDataArchive {
    param([Parameter(Mandatory = $true)][string]$BuildVersion)

    $publishRoot = Join-Path $script:RepositoryRoot 'publish_images'
    $lineUpSource = Join-Path $publishRoot 'LineUp'
    $importsSource = Join-Path $publishRoot 'lineup-imports'
    $hasLineUp = Test-Path -LiteralPath $lineUpSource -PathType Container
    $hasImports = Test-Path -LiteralPath $importsSource -PathType Container
    if (-not ($hasLineUp -or $hasImports)) {
        return ''
    }

    $releaseRoot = Join-Path $script:RepositoryRoot 'release'
    $stagingRoot = Join-Path $releaseRoot "appdata-init-staging-$BuildVersion"
    $archivePath = Join-Path $releaseRoot "appdata-init-$BuildVersion.tar.gz"

    $parts = @()
    if ($hasLineUp) { $parts += 'LineUp 工站图片' }
    if ($hasImports) { $parts += 'lineup-imports 导入源' }

    if ($DryRun) {
        Write-Host "[DRYRUN] 自动打包图片数据（$($parts -join ' + ')）：$archivePath"
        return $archivePath
    }

    Write-Info "检测到 publish_images 图片数据（$($parts -join ' + ')），自动打包 appdata 迁移包……"
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    foreach ($stalePath in @($stagingRoot, $archivePath)) {
        if (Test-Path -LiteralPath $stalePath) {
            Remove-Item -LiteralPath $stalePath -Recurse -Force
        }
    }

    # 远端约定：包内顶层目录固定为 appdata-init/（LineUp/ + lineup-imports/）。
    $stagingAppData = Join-Path $stagingRoot 'appdata-init'
    if ($hasLineUp) {
        New-Item -ItemType Directory -Path (Join-Path $stagingAppData 'LineUp') -Force | Out-Null
        Copy-Item -Path (Join-Path $lineUpSource '*') -Destination (Join-Path $stagingAppData 'LineUp') -Recurse -Force
    }
    if ($hasImports) {
        New-Item -ItemType Directory -Path (Join-Path $stagingAppData 'lineup-imports') -Force | Out-Null
        Copy-Item -Path (Join-Path $importsSource '*') -Destination (Join-Path $stagingAppData 'lineup-imports') -Recurse -Force
    }

    $tarTool = Resolve-TarTool
    Invoke-Native -FilePath $tarTool -Arguments @('-C', $stagingRoot, '-czf', $archivePath, 'appdata-init') | Out-Null

    $sizeMB = [math]::Round((Get-Item -LiteralPath $archivePath).Length / 1MB, 1)
    Write-Info ("appdata 迁移包：{0}（{1} MB）" -f $archivePath, $sizeMB)
    return $archivePath
}

# ---------------------------------------------------------------------------
# SSH / SCP
# ---------------------------------------------------------------------------

$script:SshBaseArgs = @(
    '-o', 'StrictHostKeyChecking=accept-new',
    '-o', 'ConnectTimeout=15'
)

function Invoke-RemotePreflight {
    param([Parameter(Mandatory = $true)][long]$RequiredKB)

    $preflightScript = @'
set -Eeuo pipefail
fail() { echo "PREFLIGHT_FAILED: $1" >&2; exit 1; }
ROOT="$1"; NEED_KB="$2"
ARCH="$(uname -m)"
echo "PREFLIGHT_ARCH=$ARCH"
[ "$ARCH" = "x86_64" ] || fail "server arch is $ARCH, need x86_64"
docker info >/dev/null 2>&1 || fail "docker unavailable: install Docker Engine or add user to docker group"
echo "PREFLIGHT_DOCKER=ok"
docker compose version >/dev/null 2>&1 || fail "docker compose v2 missing: install docker-compose-plugin"
echo "PREFLIGHT_COMPOSE=ok"
mkdir -p "$ROOT" 2>/dev/null || fail "cannot create directory: $ROOT"
[ -w "$ROOT" ] || fail "directory not writable: $ROOT (try: sudo mkdir -p $ROOT && sudo chown $USER $ROOT)"
echo "PREFLIGHT_ROOT=ok"
AVAIL_KB="$(df -Pk "$ROOT" | awk 'NR==2{print $4}')"
echo "PREFLIGHT_AVAIL_KB=$AVAIL_KB"
if [ "$NEED_KB" -gt 0 ] && [ "$AVAIL_KB" -lt "$NEED_KB" ]; then
  fail "disk space low: avail ${AVAIL_KB}KB < required ${NEED_KB}KB (about 3x bundle size)"
fi
echo "PREFLIGHT_OK=1"
'@

    $sshArguments = $script:SshBaseArgs + @('-p', "$ServerPort", "$ServerUser@$ServerHost", 'bash', '-s', '--', $RemoteRoot, "$RequiredKB")
    Write-Info "服务器预检：ssh $($sshArguments -join ' ')"

    if ($DryRun) {
        Write-Host "[DRYRUN] 预检脚本内容："
        Write-Host $preflightScript
        return
    }

    $output = $preflightScript | & ssh @sshArguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host "  [remote] $_" }
    if ($exitCode -ne 0) {
        Stop-Publish "服务器预检失败（exit=$exitCode）。请根据上方 [remote] 输出处理后重试。"
    }
    $flat = ($output | Out-String)
    if ($flat -notmatch 'PREFLIGHT_OK=1') {
        Stop-Publish "服务器预检未通过（未收到 PREFLIGHT_OK）。"
    }
    if ($flat -notmatch 'PREFLIGHT_ARCH=x86_64') {
        Stop-Publish "服务器架构不是 x86_64。"
    }
}

function Invoke-RemoteDeploy {
    param(
        [Parameter(Mandatory = $true)][string]$DeployVersion,
        [Parameter(Mandatory = $true)][string]$DeployBundleName,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$AppDataRemoteName
    )

    # 注意：本脚本通过 stdin 传输，内容必须保持纯 ASCII（避免 WinPS 5.1 管道编码问题）。
    $deployScript = @'
set -Eeuo pipefail
fail() { echo "REMOTE_DEPLOY_FAILED: $1" >&2; exit 1; }
VERSION="$1"; BUNDLE="$2"; ROOT="$3"
HTTP_PORT="$4";    [ "$HTTP_PORT" = "-" ]    && HTTP_PORT=""
REDIS_CONN="$5";   [ "$REDIS_CONN" = "-" ]   && REDIS_CONN=""
TZ_NAME="$6";      [ "$TZ_NAME" = "-" ]      && TZ_NAME=""
START_TIMEOUT="$7";[ "$START_TIMEOUT" = "-" ]&& START_TIMEOUT=""
APPDATA_NAME="$8"

cd "$ROOT" || fail "cannot cd to $ROOT"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum missing on server"
command -v tar >/dev/null 2>&1 || fail "tar missing on server"

echo "== [1/4] verify bundle checksum =="
sha256sum --check "$BUNDLE.tar.gz.sha256" || fail "checksum mismatch: $BUNDLE.tar.gz"

echo "== [2/4] extract bundle =="
tar -xzf "$BUNDLE.tar.gz" || fail "tar extract failed"
cd "$BUNDLE" || fail "bundle directory missing: $BUNDLE"
chmod +x ./*.sh

NEED_ENV=0
[ -z "$HTTP_PORT" ] || NEED_ENV=1
[ -z "$REDIS_CONN" ] || NEED_ENV=1
[ -z "$TZ_NAME" ] || NEED_ENV=1
[ -z "$START_TIMEOUT" ] || NEED_ENV=1
if [ "$NEED_ENV" = "1" ]; then
  cp -f .env.example .env
  [ -z "$HTTP_PORT" ]    || sed -i "s#^HSJM_HTTP_PORT=.*#HSJM_HTTP_PORT=$HTTP_PORT#" .env
  [ -z "$TZ_NAME" ]      || sed -i "s#^HSJM_TIME_ZONE=.*#HSJM_TIME_ZONE=$TZ_NAME#" .env
  [ -z "$START_TIMEOUT" ]|| sed -i "s#^HSJM_START_TIMEOUT_SECONDS=.*#HSJM_START_TIMEOUT_SECONDS=$START_TIMEOUT#" .env
  if [ -n "$REDIS_CONN" ]; then
    printf '\nHSJM_REDIS_CONNECTION=%s\n' "$REDIS_CONN" >> .env
  fi
  chmod 0600 .env
  echo "custom .env written"
fi

echo "== [3/4] install + start + verify (deploy.sh) =="
./deploy.sh || fail "deploy.sh failed"

PORT="$(awk -F= '$1=="HSJM_HTTP_PORT"{sub(/\r$/,"",$2); print $2}' .env)"
[ -n "$PORT" ] || fail "cannot read HSJM_HTTP_PORT from .env"
IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
[ -n "$IP" ] || IP="127.0.0.1"
echo "PLATFORM_URL=http://$IP:$PORT"

if [ "$APPDATA_NAME" != "-" ]; then
  echo "== [4/4] migrate appdata (LineUp images / import sources) =="
  # Reuse the bundle's own migrate-appdata.sh: merge copy (cp -a -n, never overwrites
  # server-side newer data), chown app:app (USER app container, root dirs break writes -> 422),
  # and APPDATA_*_COUNT echoes. Behavior identical to the manual ./server-deploy.sh path.
  ./migrate-appdata.sh "$ROOT/$APPDATA_NAME" || fail "appdata migration failed"
else
  echo "== [4/4] appdata migration skipped =="
fi
echo "REMOTE_DEPLOY_OK=1"
'@

    # 空值统一用 '-' 占位：ssh 会把参数拼成远端命令字符串，
    # 空参数经 shell 分词会被丢弃导致 $1..$8 位置错位；远端脚本再归一化为空。
    $httpPortArg = '-'
    if ($HttpPort -gt 0) { $httpPortArg = "$HttpPort" }
    $timeZoneArg = '-'
    if ($TimeZone -ne 'Asia/Shanghai' -and $TimeZone -ne '') { $timeZoneArg = $TimeZone }
    $timeoutArg = '-'
    if ($StartTimeoutSeconds -gt 0) { $timeoutArg = "$StartTimeoutSeconds" }
    $redisArg = '-'
    if ($RedisConnection -ne '' -and $RedisConnection -ne '-') { $redisArg = $RedisConnection }
    $appDataArg = '-'
    if ($AppDataRemoteName -ne '') { $appDataArg = $AppDataRemoteName }

    $remoteArguments = @(
        "$DeployVersion", "$DeployBundleName", "$RemoteRoot", "$httpPortArg",
        "$redisArg", "$timeZoneArg", "$timeoutArg", "$appDataArg"
    )

    $sshArguments = $script:SshBaseArgs + @('-p', "$ServerPort", "$ServerUser@$ServerHost", 'bash', '-s', '--') + $remoteArguments
    Write-Info "远端部署：ssh $($sshArguments -join ' ')"

    if ($DryRun) {
        Write-Host "[DRYRUN] 远端部署脚本内容："
        Write-Host $deployScript
        return $null
    }

    # 实时透出远端输出，同时保留副本用于解析最终地址。
    $deployScript | & ssh @sshArguments 2>&1 | Tee-Object -Variable remoteLines
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Stop-Publish "远端部署失败（exit=$exitCode）。请根据上方输出排障，常见原因见 README-Ubuntu一键部署.md 排障一节。"
    }

    $platformUrl = $null
    foreach ($line in $remoteLines) {
        if ("$line" -match '^PLATFORM_URL=(.+)$') {
            $platformUrl = $Matches[1].Trim()
        }
    }
    return $platformUrl
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

Assert-DeploymentInputs

Write-Host "==================================================="
Write-Host " HSJM 设备诊断平台 Ubuntu 一键部署"
Write-Host "==================================================="
Write-Info "仓库根目录：$script:RepositoryRoot"
Write-Info "目标服务器：$ServerUser@${ServerHost}:$ServerPort  部署目录：$RemoteRoot"
if ($DryRun) {
    Write-Host "[INFO] [DryRun] 只打印命令，不实际执行。" -ForegroundColor Yellow
}

# --- 第 1 步：构建或定位离线包 -------------------------------------------
Write-Step "第 1 步：构建/定位离线交付包"

$bundle = $null
if ($Archive -ne '') {
    Write-Info "指定 -Archive，跳过本地构建。"
    $bundle = Resolve-ExistingArchive
}
else {
    $effectiveVersion = $Version
    if ([string]::IsNullOrWhiteSpace($effectiveVersion)) {
        $effectiveVersion = (Get-Date -Format 'yyyy.MM.dd')
    }
    $effectiveMode = Resolve-BuildMode
    Write-Info "构建模式：$effectiveMode，版本：$effectiveVersion"
    if ($effectiveMode -eq 'Online') {
        $bundle = Build-OnlineBundle -BuildVersion $effectiveVersion
    }
    else {
        $bundle = Build-OfflineBundle -BuildVersion $effectiveVersion
    }
}

$archivePath = $bundle.ArchivePath
$bundleName = $bundle.BundleName
$deployVersion = $bundle.Version
$checksumPath = "$archivePath.sha256"

if (-not $DryRun) {
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        Stop-Publish "离线包不存在：$archivePath"
    }
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        Stop-Publish "离线包校验文件不存在：$checksumPath"
    }
    $archiveItem = Get-Item -LiteralPath $archivePath
    $archiveSizeMB = [math]::Round($archiveItem.Length / 1MB, 1)
    Write-Info ("离线包：{0}（{1} MB，版本 {2}）" -f $archivePath, $archiveSizeMB, $deployVersion)
}
else {
    Write-Info "离线包（预期）：$archivePath（版本 $deployVersion）"
}

# --- 第 1.5 步：图片数据（publish_images）自动打包 -------------------------
if ($AppDataInit -ne '') {
    $AppDataInit = [System.IO.Path]::GetFullPath($AppDataInit)
    Write-Info "使用指定的图片数据包：$AppDataInit"
}
elseif ($NoAppData) {
    Write-Info "-NoAppData：跳过 publish_images 图片数据自动打包与迁移。"
    $AppDataInit = ''
}
else {
    $autoArchive = Resolve-AutoAppDataArchive -BuildVersion $deployVersion
    if ($autoArchive -ne '') {
        $AppDataInit = $autoArchive
    }
    else {
        Write-Info "未检测到 publish_images/ 图片数据，跳过 LineUp 图片迁移（如需迁移：放置 publish_images/LineUp、publish_images/lineup-imports 或改用 -AppDataInit 指定数据包）。"
    }
}

# --- LocalOnly：本地打包完成，打印手工指引后退出 ---------------------------
if ($LocalOnly) {
    $appDataName = ''
    if ($AppDataInit -ne '') { $appDataName = Split-Path -Leaf $AppDataInit }

    $uploadPaths = @($archivePath, $checksumPath)
    if ($appDataName -ne '') { $uploadPaths += $AppDataInit }
    $scpHint = "scp -P $ServerPort $($uploadPaths -join ' ') ${ServerUser}@${ServerHost}:$RemoteRoot/"

    Write-Host ""
    Write-Host "==================================================="
    Write-Host " 本地打包完成（手工上传模式 -LocalOnly）"
    Write-Host "===================================================" -ForegroundColor Cyan
    if ($DryRun) {
        Write-Host " [DryRun] 注意：本次为演练，以上产物路径为预期输出，尚未实际打包。" -ForegroundColor Yellow
    }
    Write-Host " 产物（release\ 目录）："
    foreach ($f in @("$bundleName.tar.gz（离线包）", "$bundleName.tar.gz.sha256（校验）")) {
        Write-Host "   $f"
    }
    if ($appDataName -ne '') {
        Write-Host "   $appDataName（LineUp 图片/导入源数据包）"
    }
    else {
        Write-Host "   （未生成图片数据包：未检测到 publish_images/ 或指定了 -NoAppData）"
    }
    Write-Host ""
    Write-Host " 第 1 步 · 自己上传到服务器（示例，目标目录 $RemoteRoot）："
    Write-Host "   $scpHint"
    Write-Host ""
    Write-Host " 第 2 步 · 在服务器上执行（一次性）："
    Write-Host "   cd $RemoteRoot"
    Write-Host "   sha256sum --check $bundleName.tar.gz.sha256"
    Write-Host "   tar -xzf $bundleName.tar.gz"
    Write-Host "   cd $bundleName"
    Write-Host "   chmod +x ./*.sh"
    Write-Host "   ./server-deploy.sh"
    if ($appDataName -ne '') {
        Write-Host "     （server-deploy.sh 会自动检测同级目录的 $appDataName 并迁移图片；"
        Write-Host "        也可显式指定：./server-deploy.sh $RemoteRoot/$appDataName）"
    }
    Write-Host ""
    Write-Host " 可选："
    Write-Host "   - 固定端口：./server-deploy.sh 前先 cp .env.example .env 并修改 HSJM_HTTP_PORT"
    Write-Host "   - 单独补迁移图片：./migrate-appdata.sh $RemoteRoot/$appDataName"
    Write-Host "   - 升级/重装：重复上面两步即可（数据卷保留，图片合并不覆盖）"
    Write-Host "==================================================="
    exit 0
}

# --- 第 2 步：上传 ---------------------------------------------------------
Write-Step "第 2 步：上传离线包到服务器"

Assert-Command 'ssh'
Assert-Command 'scp'

$appDataRemoteName = ''
$uploadFiles = @($archivePath, $checksumPath)
if ($AppDataInit -ne '') {
    $appDataRemoteName = Split-Path -Leaf ([System.IO.Path]::GetFullPath($AppDataInit))
    $uploadFiles += [System.IO.Path]::GetFullPath($AppDataInit)
}

$requiredKB = [long]0
if (-not $DryRun -and -not $SkipDiskCheck) {
    $archiveBytes = (Get-Item -LiteralPath $archivePath).Length
    $extraBytes = [long]0
    if ($AppDataInit -ne '') {
        $extraBytes = (Get-Item -LiteralPath ([System.IO.Path]::GetFullPath($AppDataInit))).Length
    }
    # README 要求：磁盘预留"离线压缩包大小的 3 倍"（解压 + docker load + 容器可写层）。
    $requiredKB = [math]::Floor(($archiveBytes * 3 + $extraBytes) / 1KB)
}

Invoke-RemotePreflight -RequiredKB $requiredKB

$scpArguments = @('-P', "$ServerPort") + $uploadFiles + @("$ServerUser@$ServerHost`:$RemoteRoot/")
Write-Info "上传：scp $($scpArguments -join ' ')"
if (-not $DryRun) {
    & scp @scpArguments
    if ($LASTEXITCODE -ne 0) {
        Stop-Publish "scp 上传失败（exit=$LASTEXITCODE）。"
    }
}
else {
    Write-Host "[DRYRUN] scp $($scpArguments -join ' ')"
}

# --- 第 3 步：远端部署 -----------------------------------------------------
Write-Step "第 3 步：远端安装 + 启动 + 验证"

$platformUrl = Invoke-RemoteDeploy -DeployVersion $deployVersion -DeployBundleName $bundleName -AppDataRemoteName $appDataRemoteName

# --- 第 4 步：汇总 ---------------------------------------------------------
Write-Host ""
Write-Host "==================================================="
if ($DryRun) {
    Write-Host " [DryRun] 演练完成，未执行任何实际操作。"
    Write-Host "==================================================="
    exit 0
}

if ($null -eq $platformUrl) {
    $platformUrl = "http://${ServerHost}:<见远端 .env 的 HSJM_HTTP_PORT>"
}
Write-Host " 一键部署完成！" -ForegroundColor Green
Write-Host " 平台地址：$platformUrl"
Write-Host " 部署目录：$RemoteRoot/$bundleName"
Write-Host " 镜像标签：hsjm-ai-api:$deployVersion"
if ($AppDataInit -ne '') {
    Write-Host " 图片数据：已迁移 LineUp 工站图片/导入源 -> hsjm-ai_api-uploads 卷（合并，不覆盖服务器新增）"
}
Write-Host ""
Write-Host " 日常运维（在服务器 $RemoteRoot/$bundleName 下执行）："
Write-Host "   ./status.sh      查看状态"
Write-Host "   ./logs.sh        查看脱敏日志（--follow 持续）"
Write-Host "   ./stop.sh        停止（保留容器与卷）"
Write-Host "   ./start.sh       再次启动"
Write-Host ""
Write-Host " 清理远端压缩包（可选）："
Write-Host "   ssh -p $ServerPort $ServerUser@$ServerHost 'rm $RemoteRoot/$bundleName.tar.gz*'"
Write-Host "==================================================="

$remoteTarget = "$ServerUser@$ServerHost"
$deployDir = "$RemoteRoot/$bundleName"
Write-Info "完成：$remoteTarget 部署目录 $deployDir，版本 $deployVersion"
