[CmdletBinding()]
param(
    [string]$SdkCacheRoot = 'D:\sdk'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $scriptDirectory '..\..')).Path
$builder = Join-Path $repositoryRoot 'deploy\offline\build-bundle-offline.ps1'
$version = 'integration-test'
$releaseDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "hsjm-offline-build-$([guid]::NewGuid().ToString('N'))"
$archive = Join-Path $releaseDirectory "hsjm-ai-linux-amd64-$version.tar.gz"
$archiveChecksum = "$archive.sha256"

try {
    & $builder `
        -Version $version `
        -SdkCacheRoot $SdkCacheRoot `
        -ReleaseDirectory $releaseDirectory `
        -NoCache
    if ($LASTEXITCODE -ne 0) {
        throw "离线构建脚本退出码为 $LASTEXITCODE"
    }

    $platform = & docker image inspect --format '{{.Os}}/{{.Architecture}}' "hsjm-ai-api:$version"
    if ($LASTEXITCODE -ne 0 -or $platform.Trim() -ne 'linux/amd64') {
        throw "离线镜像平台错误：$platform"
    }
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw "缺少离线压缩包：$archive"
    }
    if (-not (Test-Path -LiteralPath $archiveChecksum -PathType Leaf)) {
        throw "缺少压缩包校验文件：$archiveChecksum"
    }

    Write-Output 'OFFLINE_BUILD_INTEGRATION=PASS'
}
finally {
    if (Test-Path -LiteralPath $releaseDirectory) {
        Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
    }
}
