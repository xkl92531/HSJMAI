# 从后端同步 plc-analyzer 脚本到工具 runtime/，并记录来源 commit。
# 单一事实源在 api\HSJM.AI\wwwroot\skills\plc-analyzer\，此处只读不改。
param(
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..\.."),
    [string]$RuntimeDir = (Join-Path $PSScriptRoot "..\runtime")
)
$ErrorActionPreference = 'Stop'

$source = Join-Path $RepoRoot 'api\HSJM.AI\wwwroot\skills\plc-analyzer'
if (-not (Test-Path $source)) { throw "找不到 plc-analyzer 源目录: $source" }

$target = Join-Path $RuntimeDir 'plc-analyzer'

# HsjmTia.Cli 和 HsjmTia.App 都在各自 csproj 里挂了这个 BeforeTargets="Build" 脚本，
# `dotnet build` 默认并行编译项目，两边会同一时刻对同一个 runtime/plc-analyzer 目录
# 做 Remove-Item -> New-Item -> Copy-Item，谁先 Remove、谁后 Copy 是竞态的——
# 轻则报 "目录不是空的" / "找不到路径" 之类的瞬时警告，重则让目录停在半份拷贝的状态。
# 用一个跨进程命名互斥量把"清空 -> 拷贝 -> 写版本文件"这一段串行化，避免上述竞态。
$mutex = New-Object System.Threading.Mutex($false, 'Global\HsjmTia.SyncAnalyzer')
$acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(2))
if (-not $acquired) { throw "等待 sync-analyzer 互斥量超时" }
try
{
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    Copy-Item (Join-Path $source 'scripts') $target -Recurse -Force
    Copy-Item (Join-Path $source 'brands')  $target -Recurse -Force
    # __pycache__ 与 temp 不进产物。
    # -Include 在 -Path 不带通配符时只在检索层面按“顶层”生效，不会真正过滤 -Recurse 递归到的
    # 子目录——必须让 -Path 以 \* 结尾（或用 -Filter）才会对每一层都应用 -Include，否则这条
    # 管道什么也删不掉，__pycache__/temp 会原样进 runtime/（已用临时目录验证过这个行为）。
    Get-ChildItem (Join-Path $target '*') -Recurse -Directory -Include '__pycache__','temp' |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    $commit = (git -C $RepoRoot rev-parse --short HEAD).Trim()
    @{ analyzerCommit = $commit; syncedAt = (Get-Date).ToString('o') } |
        ConvertTo-Json | Set-Content (Join-Path $RuntimeDir 'analyzer.version.json') -Encoding utf8

    Write-Host "已同步 plc-analyzer @ $commit -> $target"
}
finally
{
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
