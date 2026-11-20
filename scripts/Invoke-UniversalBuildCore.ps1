#requires -Version 7.2
param([string]$V1103Root,[string]$V11104Root,[string]$V12102Root)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'SourceInputs.ps1')
$commit=Get-McpSourceGitCommit $root
$inputs=@(Get-McpSourceInputs $root)
$sourceHash=Get-McpSourceInputHash $inputs
$verificationPaths=@('Build-Universal.ps1','Invoke-UniversalBuildCore.ps1','Invoke-SerialBuild.ps1') | ForEach-Object {"scripts/$_"}
foreach($suite in @('DevelopmentPreview','PluginCompatibilityTests')){
    $verificationPaths+=@(Get-ChildItem (Join-Path $root "tests/$suite") -File | ForEach-Object {"tests/$suite/$($_.Name)"})
}
$testInputs=@($verificationPaths | Sort-Object -Unique | ForEach-Object {[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $root $_)).Hash}})
$testHash=Get-McpSourceInputHash $testInputs
$run=Join-Path $root ('.local/standalone-verification/'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($run) | Out-Null
$checks=[Collections.Generic.List[object]]::new()
$timer=[Diagnostics.Stopwatch]::StartNew()
function Stage([string]$Name,[scriptblock]$Action) {
    $log=Join-Path $run ($Name+'.log')
    $start=[DateTimeOffset]::UtcNow
    $code=1
    try {
        $global:LASTEXITCODE=0
        & $Action *> $log
        if($LASTEXITCODE){throw "$Name failed with exit code $LASTEXITCODE (see $log)"}
        $code=0
    } finally { $checks.Add([ordered]@{name=$Name;exitCode=$code;log=$log;startedAtUtc=$start;completedAtUtc=[DateTimeOffset]::UtcNow}) }
    Write-Host "PASS $Name"
}
Push-Location -LiteralPath $root
try {
    $project=Join-Path $root 'tests/DevelopmentPreview/DevelopmentPreview.csproj'
    Stage 'public-development-preview' { dotnet run --project $project -c Release -v quiet }
    Stage 'universal-payload' { & (Join-Path $PSScriptRoot 'Build-IntegrationPayload.ps1') -DesignerVersion '11.0.3.0' -Universal -V1103Root $V1103Root -V11104Root $V11104Root -V12102Root $V12102Root }
    $payloadLine=@(Get-Content (Join-Path $run 'universal-payload.log') | Where-Object {$_ -like 'PAYLOAD *'})
    if($payloadLine.Count -ne 1){throw 'No unique payload manifest reported.'}
    $payload=$payloadLine[0].Substring(8)
    $bundle=Join-Path ([IO.Path]::GetDirectoryName($payload)) 'plugin'
    Stage 'universal-native-compatibility' { & (Join-Path $root 'tests/PluginCompatibilityTests/Run.ps1') -Mode verify -BundleDirectory $bundle -V1103Root $V1103Root -V11104Root $V11104Root -V12102Root $V12102Root }
    $result=@(Get-Content (Join-Path $run 'universal-native-compatibility.log') | Where-Object {$_ -like 'RESULT *'})
    if($result.Count -ne 1){throw 'No unique compatibility report.'}
    $receipt=Join-Path $run 'verification.json'
    if((Get-McpSourceGitCommit $root) -ne $commit -or (Get-McpSourceInputHash (Get-McpSourceInputs $root)) -ne $sourceHash){throw 'Source changed during verification.'}
    foreach($record in $testInputs){if((Get-FileHash -LiteralPath (Join-Path $root $record.path)).Hash -ne $record.sha256){throw 'Verification inputs changed during verification.'}}
    [ordered]@{completed=$true;failure=$null;layer='public-isolated-components-and-protocol';sourceGitCommit=$commit;sourceInputs=$inputs;sourceInputSha256=$sourceHash;verificationInputs=$testInputs;verificationInputSha256=$testHash;checks=$checks.ToArray();nativeMcpStarted=$false;designerStarted=$false;installed=$false;elapsedSeconds=$timer.Elapsed.TotalSeconds} |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receipt -Encoding utf8
    & (Join-Path $PSScriptRoot 'Build-CrossVersionPackage.ps1') -PayloadManifest $payload -ComponentEvidence $receipt -CompatibilityEvidence ($result[0].Substring(7))
    Write-Output "VERIFICATION $receipt"
    Write-Output "ELAPSED_SECONDS $($timer.Elapsed.TotalSeconds)"
} finally { Pop-Location }
