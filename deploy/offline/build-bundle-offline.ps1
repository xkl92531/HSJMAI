[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z._-]*$')]
    [string]$Version,

    [string]$SdkCacheRoot = 'D:\sdk',

    [string]$ReleaseDirectory,

    [switch]$ValidateOnly,

    [switch]$NoCache
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $scriptDirectory '..\..')).Path

$requiredImageArchives = @(
    'dockerfile-1.7-linux-amd64.tar',
    'dotnet-aspnet-10.0-noble-linux-amd64.tar',
    'dotnet-sdk-10.0-noble-linux-amd64.tar',
    'hsjm-runtime-deps-20260903-linux-amd64.tar',
    'hsjm-web-deps-pnpm-10.26.2-linux-amd64.tar',
    'node-22-alpine-linux-amd64.tar'
)

function Stop-OfflineBuild {
    param(
        [Parameter(Mandatory = $true)][string]$Code,
        [Parameter(Mandatory = $true)][string]$Message
    )

    throw "CACHE_ERROR=$Code $Message"
}

function Assert-DirectoryExists {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        Stop-OfflineBuild -Code 'missing-directory' -Message "缺少目录：$Path"
    }
}

function Test-DockerImage {
    param([Parameter(Mandatory = $true)][string]$Tag)

    & docker image inspect $Tag *> $null
    return $LASTEXITCODE -eq 0
}

function Invoke-Docker {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        Stop-OfflineBuild -Code 'docker-command-failed' -Message "Docker 命令失败：docker $($Arguments -join ' ')"
    }
}

function Resolve-LinkTargetDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $item = Get-Item -LiteralPath $Path -Force
    if ($item.LinkType -and $item.Target) {
        $target = [string]($item.Target | Select-Object -First 1)
        if (-not [System.IO.Path]::IsPathRooted($target)) {
            $target = Join-Path $item.Parent.FullName $target
        }
        return (Resolve-Path -LiteralPath $target).Path
    }
    return (Resolve-Path -LiteralPath $item.FullName).Path
}

function Write-Sha256Manifest {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$OutputPath
    )

    $lines = Get-ChildItem -LiteralPath $Directory -Recurse -File |
        Where-Object { $_.FullName -ne $OutputPath } |
        Sort-Object FullName |
        ForEach-Object {
            # WinPS 5.1 兼容：Path::GetRelativePath 为 .NET 5+ API；此处文件必在 $Directory 下，前缀截断等价
            $relative = $_.FullName.Substring($Directory.TrimEnd('\').Length + 1).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
    [System.IO.File]::WriteAllLines($OutputPath, $lines, [System.Text.UTF8Encoding]::new($false))
}

function Assert-ChecksumManifest {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$BasePath,
        [Parameter(Mandatory = $true)][string]$Kind,
        [switch]$AllowMetadata,
        [string]$GitSourceCommit
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        Stop-OfflineBuild -Code 'missing-manifest' -Message "缺少 $Kind 校验文件：$ManifestPath"
    }

    $checked = 0
    foreach ($line in Get-Content -LiteralPath $ManifestPath) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
            continue
        }
        if ($AllowMetadata -and $line -match '^[A-Za-z][A-Za-z0-9]*=') {
            continue
        }
        if ($line -notmatch '^([0-9A-Fa-f]{64})\s+(.+)$') {
            Stop-OfflineBuild -Code 'invalid-manifest' -Message "$Kind 校验行格式错误：$line"
        }

        $expected = $Matches[1].ToUpperInvariant()
        $relativePath = $Matches[2]
        if ([System.IO.Path]::IsPathRooted($relativePath) -or $relativePath.Contains('..')) {
            Stop-OfflineBuild -Code 'unsafe-manifest-path' -Message "$Kind 校验路径不安全：$relativePath"
        }

        $filePath = Join-Path $BasePath $relativePath
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            Stop-OfflineBuild -Code 'missing-file' -Message "$Kind 缺少文件：$filePath"
        }

        $actual = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToUpperInvariant()
        $normalizedActual = $actual
        if ($AllowMetadata -and $actual -ne $expected) {
            $sourceBytes = [System.IO.File]::ReadAllBytes($filePath)
            $normalizedBytes = [System.Collections.Generic.List[byte]]::new($sourceBytes.Length)
            for ($index = 0; $index -lt $sourceBytes.Length; $index++) {
                if (
                    $sourceBytes[$index] -eq 13 -and
                    $index + 1 -lt $sourceBytes.Length -and
                    $sourceBytes[$index + 1] -eq 10
                ) {
                    $normalizedBytes.Add(10)
                    $index++
                    continue
                }
                $normalizedBytes.Add($sourceBytes[$index])
            }
            # WinPS 5.1 兼容：SHA256::HashData / Convert::ToHexString 均为 .NET 5+ API，改用等价写法
            $sha256Provider = [System.Security.Cryptography.SHA256]::Create()
            try {
                $normalizedHash = $sha256Provider.ComputeHash($normalizedBytes.ToArray())
            } finally {
                $sha256Provider.Dispose()
            }
            $normalizedActual = [BitConverter]::ToString($normalizedHash).Replace('-', '')
        }
        $gitEquivalent = $false
        if (
            $AllowMetadata -and
            $actual -ne $expected -and
            $normalizedActual -ne $expected -and
            $GitSourceCommit -match '^[0-9A-Fa-f]{40}$'
        ) {
            $gitPath = $relativePath.Replace('\', '/')
            $expectedBlob = & git -C $BasePath rev-parse "$GitSourceCommit`:$gitPath" 2>$null
            if ($LASTEXITCODE -eq 0) {
                $actualBlob = & git -C $BasePath hash-object "--path=$gitPath" $filePath 2>$null
                $gitEquivalent = $LASTEXITCODE -eq 0 -and $actualBlob.Trim() -eq $expectedBlob.Trim()
            }
        }
        if ($actual -ne $expected -and $normalizedActual -ne $expected -and -not $gitEquivalent) {
            Stop-OfflineBuild -Code 'checksum-mismatch' -Message "$Kind 校验失败：$filePath"
        }
        $checked++
    }

    if ($checked -eq 0) {
        Stop-OfflineBuild -Code 'empty-manifest' -Message "$Kind 校验文件没有有效条目：$ManifestPath"
    }
}

try {
    Assert-DirectoryExists -Path $SdkCacheRoot
    $sdkRoot = (Resolve-Path -LiteralPath $SdkCacheRoot).Path
    $imageDirectory = Join-Path $sdkRoot 'docker\images'
    Assert-DirectoryExists -Path $imageDirectory

    foreach ($archiveName in $requiredImageArchives) {
        $archivePath = Join-Path $imageDirectory $archiveName
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            Stop-OfflineBuild -Code 'missing-image-archive' -Message "缺少镜像归档：$archivePath"
        }
    }

    Assert-ChecksumManifest `
        -ManifestPath (Join-Path $imageDirectory 'SHA256SUMS.txt') `
        -BasePath $imageDirectory `
        -Kind 'Docker 镜像'

    Assert-DirectoryExists -Path (Join-Path $sdkRoot 'nuget\packages')
    $hsjmNugetPackages = Join-Path $sdkRoot 'nuget\hsjm-packages'
    Assert-DirectoryExists -Path $hsjmNugetPackages
    Assert-ChecksumManifest `
        -ManifestPath (Join-Path $sdkRoot 'nuget\HSJM-PACKAGES-SHA256SUMS.txt') `
        -BasePath $hsjmNugetPackages `
        -Kind 'HSJM NuGet'
    Assert-DirectoryExists -Path (Join-Path $sdkRoot 'pnpm\physical-store\v10')
    Assert-DirectoryExists -Path (Join-Path $sdkRoot 'python\wheels')

    $sourceManifestPath = Join-Path $sdkRoot 'HSJM-CACHE-MANIFEST.txt'
    $sourceCommit = Get-Content -LiteralPath $sourceManifestPath |
        Where-Object { $_ -match '^SourceCommit=([0-9A-Fa-f]{40})$' } |
        Select-Object -First 1
    $sourceCommitValue = if ($sourceCommit -match '^SourceCommit=([0-9A-Fa-f]{40})$') {
        $Matches[1]
    }
    else {
        ''
    }

    Assert-ChecksumManifest `
        -ManifestPath $sourceManifestPath `
        -BasePath $repositoryRoot `
        -Kind '项目构建输入' `
        -AllowMetadata `
        -GitSourceCommit $sourceCommitValue

    Write-Output 'OFFLINE_CACHE_VALID=1'
    Write-Output "SDK_CACHE_ROOT=$sdkRoot"

    if ($ValidateOnly) {
        exit 0
    }

    foreach ($commandName in @('docker', 'tar')) {
        if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
            Stop-OfflineBuild -Code 'missing-command' -Message "缺少命令：$commandName"
        }
    }
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) {
        Stop-OfflineBuild -Code 'docker-unavailable' -Message 'Docker Engine 未运行，或当前用户无权访问 Docker。'
    }
    & docker buildx version *> $null
    if ($LASTEXITCODE -ne 0) {
        Stop-OfflineBuild -Code 'buildx-unavailable' -Message '需要 Docker Buildx。'
    }

    $requiredImages = @(
        @{
            Tag = 'docker/dockerfile:1.7'
            Archive = 'dockerfile-1.7-linux-amd64.tar'
        },
        @{
            Tag = 'mcr.microsoft.com/dotnet/sdk:10.0-noble'
            Archive = 'dotnet-sdk-10.0-noble-linux-amd64.tar'
        },
        @{
            Tag = 'hsjm-cache/web-deps:pnpm-10.26.2'
            Archive = 'hsjm-web-deps-pnpm-10.26.2-linux-amd64.tar'
        },
        @{
            Tag = 'hsjm-cache/runtime-deps:20260903'
            Archive = 'hsjm-runtime-deps-20260903-linux-amd64.tar'
        }
    )
    foreach ($image in $requiredImages) {
        if (-not (Test-DockerImage -Tag $image.Tag)) {
            $archivePath = Join-Path $imageDirectory $image.Archive
            Write-Output "导入离线镜像：$($image.Tag)"
            Invoke-Docker -Arguments @('image', 'load', '--input', $archivePath)
        }
        if (-not (Test-DockerImage -Tag $image.Tag)) {
            Stop-OfflineBuild -Code 'missing-image' -Message "导入后仍缺少镜像：$($image.Tag)"
        }
    }

    $nugetPackages = Resolve-LinkTargetDirectory -Path (Join-Path $sdkRoot 'nuget\hsjm-packages')
    if (-not (Get-ChildItem -LiteralPath $nugetPackages -Directory -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        Stop-OfflineBuild -Code 'empty-nuget-cache' -Message "NuGet 离线目录为空：$nugetPackages"
    }

    $offlineDockerfile = Join-Path $repositoryRoot 'deploy\docker\api.offline.Dockerfile'
    if (-not (Test-Path -LiteralPath $offlineDockerfile -PathType Leaf)) {
        Stop-OfflineBuild -Code 'missing-offline-dockerfile' -Message "缺少离线 Dockerfile：$offlineDockerfile"
    }

    $imageTag = "hsjm-ai-api:$Version"
    $buildArguments = @(
        'buildx', 'build',
        '--platform', 'linux/amd64',
        '--network=none',
        '--pull=false',
        '--provenance=false',
        '--build-context', "nuget=$nugetPackages",
        '--file', $offlineDockerfile,
        '--tag', $imageTag,
        '--load'
    )
    if ($NoCache) {
        $buildArguments += '--no-cache'
    }
    $buildArguments += $repositoryRoot

    Write-Output "构建离线 API 镜像：$imageTag"
    Invoke-Docker -Arguments $buildArguments

    $platform = & docker image inspect --format '{{.Os}}/{{.Architecture}}' $imageTag
    if ($LASTEXITCODE -ne 0 -or $platform.Trim() -ne 'linux/amd64') {
        Stop-OfflineBuild -Code 'invalid-image-platform' -Message "镜像架构不是 linux/amd64：$platform"
    }

    $releaseRoot = if ($ReleaseDirectory) {
        [System.IO.Path]::GetFullPath($ReleaseDirectory)
    }
    else {
        Join-Path $repositoryRoot 'release'
    }
    $bundleName = "hsjm-ai-linux-amd64-$Version"
    $bundleDirectory = Join-Path $releaseRoot $bundleName
    $archivePath = Join-Path $releaseRoot "$bundleName.tar.gz"
    $archiveChecksumPath = "$archivePath.sha256"

    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    foreach ($outputPath in @($bundleDirectory, $archivePath, $archiveChecksumPath)) {
        if (Test-Path -LiteralPath $outputPath) {
            Stop-OfflineBuild -Code 'output-exists' -Message "输出已存在：$outputPath"
        }
    }

    New-Item -ItemType Directory -Path (Join-Path $bundleDirectory 'images') -Force | Out-Null
    $savedImage = Join-Path $bundleDirectory "images\hsjm-ai-api-$Version-linux-amd64.tar"
    Invoke-Docker -Arguments @('image', 'save', '--output', $savedImage, $imageTag)

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'deploy\compose.yaml') -Destination (Join-Path $bundleDirectory 'compose.yaml')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'deploy\.env.example') -Destination (Join-Path $bundleDirectory '.env.example')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'deploy\README-Linux离线部署.md') -Destination (Join-Path $bundleDirectory 'README-Linux离线部署.md')
    $manual = Join-Path $repositoryRoot 'docs\HSJM-6.131-Docker部署与MCP调用手册.docx'
    if (Test-Path -LiteralPath $manual -PathType Leaf) {
        Copy-Item -LiteralPath $manual -Destination (Join-Path $bundleDirectory 'HSJM-6.131-Docker部署与MCP调用手册.docx')
    }
    Copy-Item -Path (Join-Path $repositoryRoot 'deploy\scripts\*.sh') -Destination $bundleDirectory
    [System.IO.File]::WriteAllText(
        (Join-Path $bundleDirectory 'VERSION'),
        "$Version`n",
        [System.Text.UTF8Encoding]::new($false)
    )
    Write-Sha256Manifest `
        -Directory $bundleDirectory `
        -OutputPath (Join-Path $bundleDirectory 'SHA256SUMS')

    & tar -C $releaseRoot -czf $archivePath $bundleName
    if ($LASTEXITCODE -ne 0) {
        Stop-OfflineBuild -Code 'archive-failed' -Message "创建离线压缩包失败：$archivePath"
    }
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        $archiveChecksumPath,
        "$archiveHash  $bundleName.tar.gz`n",
        [System.Text.UTF8Encoding]::new($false)
    )

    Write-Output "OFFLINE_BUILD_COMPLETE=1"
    Write-Output "OFFLINE_BUNDLE_DIR=$bundleDirectory"
    Write-Output "OFFLINE_ARCHIVE=$archivePath"
    Write-Output "OFFLINE_ARCHIVE_SHA256=$archiveChecksumPath"
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
