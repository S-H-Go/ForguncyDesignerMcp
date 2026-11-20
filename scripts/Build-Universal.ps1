#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$V1103Root,
    [Parameter(Mandatory)][string]$V11104Root,
    [Parameter(Mandatory)][string]$V12102Root
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function FailUnlessFullSdkRoots {
    foreach($sdk in @($V1103Root,$V11104Root,$V12102Root)){
        if(-not [IO.Path]::IsPathFullyQualified($sdk)){throw 'Pass absolute paths to the three locally installed SDK roots.'}
    }
}
FailUnlessFullSdkRoots
$entry=Join-Path $PSScriptRoot 'Invoke-UniversalBuildCore.ps1'
& (Join-Path $PSScriptRoot 'Invoke-SerialBuild.ps1') -ScriptPath $entry -ScriptArguments @($V1103Root,$V11104Root,$V12102Root)
