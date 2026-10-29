param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^https://[a-z0-9-]+\.supabase\.co/?$')][string]$ProjectUrl
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
& dotnet publish (Join-Path $repoRoot 'AltMate.SyncAgent/AltMate.SyncAgent.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $outputRoot
if ($LASTEXITCODE -ne 0) { throw 'Agent publication failed' }
$shared = Get-Content (Join-Path $repoRoot 'supabase/functions/_shared/sync.ts') -Raw
$entry = Get-Content (Join-Path $repoRoot 'supabase/functions/sync/index.ts') -Raw
$entry = $entry.Replace('import { handleSync } from "../_shared/sync.ts";', '')
# Preserve the checked TypeScript types for Supabase's dashboard editor.
[System.IO.File]::WriteAllText((Join-Path $outputRoot 'sync-function.ts'), $shared + [Environment]::NewLine + $entry, [System.Text.UTF8Encoding]::new($false))
$initialSql = Get-Content (Join-Path $repoRoot 'supabase/migrations/202610290001_authenticated_sync.sql') -Raw
$upgradeSql = Get-Content (Join-Path $repoRoot 'supabase/migrations/202610290002_dashboard_views.sql') -Raw
[System.IO.File]::WriteAllText((Join-Path $outputRoot 'supabase-sync.sql'), $initialSql + [Environment]::NewLine + $upgradeSql, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText((Join-Path $outputRoot 'dashboard-v2.sql'), $upgradeSql, [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $outputRoot 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/sync-setup-ja.txt') -Destination (Join-Path $outputRoot 'README.txt')
$commands = [ordered]@{
    '1-pair.cmd' = "pair `"$($ProjectUrl.TrimEnd('/'))`""
    '2-start-sync.cmd' = 'watch'
    '3-sync-once.cmd' = 'sync'
    '4-status.cmd' = 'status'
    '5-forget-local.cmd' = 'forget'
}
foreach ($file in $commands.Keys) {
    $text = "@echo off`r`n`"%~dp0AltMate.SyncAgent.exe`" $($commands[$file])`r`npause`r`n"
    [System.IO.File]::WriteAllText((Join-Path $outputRoot $file), $text, [System.Text.Encoding]::ASCII)
}
[System.IO.File]::WriteAllText((Join-Path $outputRoot 'SOURCE.txt'), "Source (AGPL-3.0-or-later): https://github.com/naco-ff14/AltMate/tree/feature/web-dashboard`r`n", [System.Text.Encoding]::UTF8)
Get-FileHash (Join-Path $outputRoot 'AltMate.SyncAgent.exe') -Algorithm SHA256 | Select-Object Algorithm,Hash
