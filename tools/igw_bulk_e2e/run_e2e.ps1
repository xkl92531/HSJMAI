<#
.SYNOPSIS
  HSJM -> IGW bulk registration end-to-end test against a TEMPORARY IGW instance.

.DESCRIPTION
  1. Builds IGW.Runtime (Release) from a commit of the IGW bulk-tag-upsert worktree.
     The sources are exported with `git archive` into a cache directory under %TEMP%;
     nothing is built or written inside the IGW worktree / main working tree.
     The publish output is cached per commit (%TEMP%\igw-e2e-cache\<commit>\publish).
  2. Starts IGW.Runtime from the cache with:
       - a fresh temp data directory (IGW_DATA_DIR = %TEMP%\igw-e2e-<guid>\data), never %ProgramData%\IGW;
       - a free loopback REST port (Kestrel endpoint bound to 127.0.0.1 only);
       - OPC UA server disabled (seeded project.igwproj), log-center port set to a free port.
     Licensing: a new data directory gets IGW's own built-in 7-day local trial
     (LicenseManager keys trial state by data directory), so collection and REST run normally.
  3. Waits for GET /api/v1/health, sets IGW_E2E_URL / IGW_E2E_RESULTS and runs
       dotnet test api/HSJM.AI.IgwRegistration.Tests --filter Category=E2E
  4. Stops ONLY the process it started and deletes the temp run directory.

.PARAMETER IgwWorktree  IGW git worktree to export from (default: <repo parent>\IGW\.worktrees\bulk-tag-upsert).
.PARAMETER Commit       Commit to build (default: HEAD of the worktree).
.PARAMETER Rebuild      Ignore the cached publish output for this commit.
.PARAMETER KeepData     Keep the temp run directory (IGW data + logs) for inspection.
.PARAMETER Filter       dotnet test filter (default Category=E2E).
.PARAMETER NoTest       Start IGW, print the URL and wait until <run dir>\stop exists (manual exploration), then clean up.
#>
[CmdletBinding()]
param(
    [string]$IgwWorktree,
    [string]$Commit,
    [switch]$Rebuild,
    [switch]$KeepData,
    [string]$Filter = 'Category=E2E',
    [switch]$NoTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $IgwWorktree) {
    $IgwWorktree = Join-Path (Split-Path $repoRoot -Parent) 'IGW\.worktrees\bulk-tag-upsert'
}
if (-not (Test-Path $IgwWorktree)) { throw "IGW worktree not found: $IgwWorktree" }
$testProject = Join-Path $repoRoot 'api\HSJM.AI.IgwRegistration.Tests\HSJM.AI.IgwRegistration.Tests.csproj'
$bsdtar = Join-Path $env:SystemRoot 'System32\tar.exe'

function Write-Step([string]$text) { Write-Host ("[e2e] {0:HH:mm:ss} {1}" -f (Get-Date), $text) -ForegroundColor Cyan }

function Get-FreeTcpPort {
    # Never hand out the ports of a regularly installed / developer IGW.
    $reserved = @(4840, 48010, 48020, 28020, 1502)
    for ($i = 0; $i -lt 20; $i++) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
        $listener.Stop()
        if ($reserved -notcontains $port) { return $port }
    }
    throw 'No free TCP port found'
}

# ---------------------------------------------------------------- 1. build / cache
if (-not $Commit) { $Commit = (& git -C $IgwWorktree rev-parse HEAD).Trim() }
else { $Commit = (& git -C $IgwWorktree rev-parse $Commit).Trim() }
if ($LASTEXITCODE -ne 0 -or -not $Commit) { throw "Cannot resolve IGW commit in $IgwWorktree" }
$short = $Commit.Substring(0, 12)
$cacheRoot = Join-Path $env:TEMP "igw-e2e-cache\$short"
$publishDir = Join-Path $cacheRoot 'publish'
$marker = Join-Path $publishDir '.e2e-commit'
$exe = Join-Path $publishDir 'IGW.Runtime.exe'

if ($Rebuild -or -not (Test-Path $marker) -or ((Get-Content $marker -Raw).Trim() -ne $Commit) -or -not (Test-Path $exe)) {
    Write-Step "Building IGW.Runtime $short (git archive -> $cacheRoot)"
    if (Test-Path $cacheRoot) { Remove-Item $cacheRoot -Recurse -Force }
    $srcDir = Join-Path $cacheRoot 'src'
    New-Item -ItemType Directory -Force -Path $srcDir | Out-Null
    $tarFile = Join-Path $cacheRoot 'src.tar'
    & git -C $IgwWorktree archive --format=tar -o $tarFile $Commit
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
    & $bsdtar -xf $tarFile -C $srcDir
    if ($LASTEXITCODE -ne 0) { throw 'tar extract failed' }
    Remove-Item $tarFile -Force
    $runtimeProj = Join-Path $srcDir 'src\IGW.Runtime\IGW.Runtime.csproj'
    & dotnet publish $runtimeProj -c Release -o $publishDir --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish IGW.Runtime failed' }
    Set-Content -Path $marker -Value $Commit -Encoding ASCII
    Remove-Item $srcDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Step "Published to $publishDir"
}
else {
    Write-Step "Reusing cached IGW.Runtime publish for $short ($publishDir)"
}

# ---------------------------------------------------------------- 2. start temp IGW
$runDir = Join-Path $env:TEMP ("igw-e2e-" + [guid]::NewGuid().ToString('N'))
$dataDir = Join-Path $runDir 'data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$restPort = Get-FreeTcpPort
$opcUaPort = Get-FreeTcpPort
$logPort = Get-FreeTcpPort

# Seed the legacy JSON project: IGW migrates it into project.db on first start.
# OPC UA server disabled so no opc.tcp port is opened at all.
$seed = @"
{
  "name": "HSJM-E2E",
  "settings": { "restPort": $restPort, "opcUaPort": $opcUaPort },
  "opcUa": { "enabled": false, "port": $opcUaPort }
}
"@
[System.IO.File]::WriteAllText((Join-Path $dataDir 'project.igwproj'), $seed, [System.Text.UTF8Encoding]::new($false))

$baseUrl = "http://127.0.0.1:$restPort/"
$envVars = @{
    'IGW_DATA_DIR'                   = $dataDir
    'IGW_REST_PORT'                  = "$restPort"
    'IGW_OPENOBSERVE_PORT'           = "$logPort"
    # Kestrel endpoint configuration overrides UseUrls(0.0.0.0): listen on loopback only.
    'Kestrel__Endpoints__Http__Url'  = "http://127.0.0.1:$restPort"
    'DOTNET_ENVIRONMENT'             = 'Production'
    'ASPNETCORE_ENVIRONMENT'         = 'Production'
}
$saved = @{}
foreach ($k in $envVars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process'); [Environment]::SetEnvironmentVariable($k, $envVars[$k], 'Process') }

$proc = $null
$testExit = 1
$results = Join-Path $runDir 'results.md'
try {
    Write-Step "Starting IGW.Runtime (REST $baseUrl, data $dataDir)"
    $proc = Start-Process -FilePath $exe -WorkingDirectory $runDir -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $runDir 'stdout.log') -RedirectStandardError (Join-Path $runDir 'stderr.log')
}
finally {
    foreach ($k in $envVars.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
}

try {
    $deadline = (Get-Date).AddSeconds(180)
    $health = $null
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) { throw "IGW.Runtime exited early with code $($proc.ExitCode)" }
        try {
            $health = Invoke-RestMethod -Uri ($baseUrl + 'api/v1/health') -TimeoutSec 5
            break
        }
        catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $health) { throw 'IGW did not become ready within 180 s' }
    Write-Step ("IGW ready (pid {0}): {1}" -f $proc.Id, ($health | ConvertTo-Json -Compress -Depth 4))

    if ($NoTest) {
        $stopFile = Join-Path $runDir 'stop'
        Write-Host "IGW_E2E_URL=$baseUrl"
        Write-Host "Create '$stopFile' (or kill this script) to stop IGW and clean up."
        while (-not (Test-Path $stopFile) -and -not $proc.HasExited) { Start-Sleep -Seconds 1 }
        $testExit = 0
    }
    else {
        $env:IGW_E2E_URL = $baseUrl
        $env:IGW_E2E_RESULTS = $results
        try {
            Write-Step "dotnet test --filter $Filter"
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            & dotnet test $testProject --filter $Filter --logger 'console;verbosity=normal' --nologo
            $testExit = $LASTEXITCODE
            Write-Step ("dotnet test exit code {0} after {1:0.0}s" -f $testExit, $sw.Elapsed.TotalSeconds)
        }
        finally {
            Remove-Item Env:\IGW_E2E_URL -ErrorAction SilentlyContinue
            Remove-Item Env:\IGW_E2E_RESULTS -ErrorAction SilentlyContinue
        }
        if (Test-Path $results) {
            Write-Host ''
            Write-Host '================ E2E results ================'
            Get-Content $results -Encoding UTF8 | Write-Host
            Copy-Item $results (Join-Path $PSScriptRoot 'last-run-results.md') -Force
        }
    }
    if ($testExit -ne 0) {
        Write-Host '---- IGW stdout (tail) ----'
        Get-Content (Join-Path $runDir 'stdout.log') -Tail 60 -ErrorAction SilentlyContinue | Write-Host
    }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Write-Step "Stopping IGW.Runtime pid $($proc.Id)"
        Stop-Process -Id $proc.Id -Force
        $proc.WaitForExit(30000) | Out-Null
    }
    if ($KeepData) {
        Write-Step "Kept run directory $runDir"
    }
    else {
        for ($i = 0; $i -lt 10 -and (Test-Path $runDir); $i++) {
            try { Remove-Item $runDir -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
        }
        if (Test-Path $runDir) { Write-Warning "Could not delete $runDir" } else { Write-Step "Deleted $runDir" }
    }
}
exit $testExit
