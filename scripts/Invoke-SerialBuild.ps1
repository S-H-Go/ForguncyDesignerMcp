#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ScriptPath,
    [string[]]$ScriptArguments = @(),
    [ValidateRange(1,3600)][int]$WaitSeconds = 1800
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$scope = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($sourceRoot.ToUpperInvariant()))).Substring(0,24)
$commandFile = [IO.Path]::GetFullPath($ScriptPath)
if (-not [IO.File]::Exists($commandFile) -or [IO.Path]::GetExtension($commandFile) -ne '.ps1') {
    throw 'ScriptPath must identify an existing reviewed PowerShell build/test script.'
}
$mutex = [Threading.Mutex]::new($false, "Local\ForguncyDesignerMcp.Build.$scope")
$held = $false
$fileLease = $null
try {
    try { $held = $mutex.WaitOne([TimeSpan]::FromSeconds($WaitSeconds)) }
    catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held) { throw 'Timed out waiting for the shared build/test window; no build was dispatched.' }
    # The filesystem lease also serializes sandboxed and elevated sessions,
    # whose Windows named-object namespaces need not be the same.
    $workspaceRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..\..'))
    $lockRoot = Join-Path $workspaceRoot '.local'
    [IO.Directory]::CreateDirectory($lockRoot) | Out-Null
    $lockPath = Join-Path $lockRoot 'designer-mcp-build.lock'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($WaitSeconds)
    while ($null -eq $fileLease) {
        try { $fileLease = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Timed out waiting for the shared build file lease; no build was dispatched.' }
            Start-Sleep -Milliseconds 200
        }
    }
    $leaseBytes = [Text.Encoding]::UTF8.GetBytes((@{
        processId = $PID
        scriptPath = $commandFile
        acquiredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Compress))
    $fileLease.SetLength(0)
    $fileLease.Write($leaseBytes, 0, $leaseBytes.Length)
    $fileLease.Flush()
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & $commandFile @ScriptArguments
    if (-not $?) { throw 'The serialized build/test script failed.' }
} finally {
    if ($null -ne $fileLease) { $fileLease.Dispose() }
    if ($held) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
