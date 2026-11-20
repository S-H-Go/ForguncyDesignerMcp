[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('verify')][string]$Mode,[string]$BundleDirectory,[string]$V1103Root,[string]$V11104Root,[string]$V12102Root,[switch]$Serial)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workspace=$repo
$project=Join-Path $PSScriptRoot 'PluginCompatibilityTests.csproj'
$out=Join-Path $workspace ('.local/plugin-compatibility/run-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $out | Out-Null
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
dotnet restore $project --ignore-failed-sources -p:NuGetAudit=false
if($LASTEXITCODE -ne 0){throw 'Compatibility test restore failed.'}
dotnet build $project --no-restore -m:1 -nr:false -p:BuildInParallel=false -p:NuGetAudit=false
if($LASTEXITCODE -ne 0){throw 'Compatibility test build failed.'}
$test=Join-Path $PSScriptRoot 'bin/Debug/net8.0-windows/PluginCompatibilityTests.dll'
if($Mode -eq 'verify') {
 if([string]::IsNullOrWhiteSpace($BundleDirectory)){throw 'A candidate bundle directory is required.'}
 $BundleDirectory=[IO.Path]::GetFullPath($BundleDirectory)
}
$targets=@(
 @{Name='11.0.3.0';Root=$V1103Root},
 @{Name='11.0.104.0';Root=$V11104Root},
 @{Name='12.0.102.0';Root=$V12102Root}
)
# These processes only read the SDK/candidate DLLs and each binds an
# OS-assigned loopback port plus its own log. Await every child before packaging.
# -Serial runs the exact same probes in sequence for diagnostics/measurement.
$probeTimer=[Diagnostics.Stopwatch]::StartNew()
$failed=$false
$profiles=@()
$workers=[Collections.Generic.List[object]]::new()
try {
 foreach($target in $targets){
   if(-not [IO.Path]::IsPathFullyQualified($target.Root)){throw 'Each installed SDK must have an absolute root.'}
   $log=Join-Path $out ('verify-'+$target.Name+'.log')
   $err=$log+'.stderr'
   $psi=[Diagnostics.ProcessStartInfo]::new()
   $psi.FileName=(Get-Command dotnet -ErrorAction Stop).Source
   $psi.CreateNoWindow=$true
   $psi.UseShellExecute=$false
   $psi.RedirectStandardOutput=$true
   $psi.RedirectStandardError=$true
   $psi.Environment.Remove('FORGUNCY_TEST_ONLINE_HELP') | Out-Null
   foreach($arg in @($test,'--verify',$target.Root,$BundleDirectory)){$psi.ArgumentList.Add($arg)}
   $process=[Diagnostics.Process]::Start($psi)
   $worker=[pscustomobject]@{target=$target;process=$process;log=$log;err=$err;
       stdout=$process.StandardOutput.ReadToEndAsync();stderr=$process.StandardError.ReadToEndAsync()}
   $workers.Add($worker)
   if($Serial){$process.WaitForExit()}
 }
 foreach($worker in $workers){
   $worker.process.WaitForExit()
   $worker.stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath $worker.log -NoNewline
   $worker.stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath $worker.err -NoNewline
   $code=$worker.process.ExitCode
   Write-Output ($worker.target.Name+' exit='+$code+' log='+$worker.log)
   if($code -ne 0){$failed=$true;Get-Content -LiteralPath $worker.err -Tail 20;continue}
   $profile=Get-Content -LiteralPath $worker.log -Tail 1 | ConvertFrom-Json
   if($profile.designerVersion -ne $worker.target.Name){throw 'Probe returned a different native Designer version.'}
   $profiles+= $profile
 }
} finally {
 foreach($worker in $workers){$worker.process.WaitForExit();$worker.process.Dispose()}
}
Write-Output "PROBE_SECONDS $($probeTimer.Elapsed.TotalSeconds)"
if($failed){throw 'At least one native compatibility probe failed.'}
if($Mode -eq 'verify') {
  $own=@($profiles[0].ownFiles | Sort-Object path)
  $expected=($own | ConvertTo-Json -Compress)
  $runtime=@($profiles[0].dependencyFiles | Sort-Object path)
  $expectedRuntime=($runtime | ConvertTo-Json -Compress)
  foreach($profile in $profiles){
    if((@($profile.ownFiles | Sort-Object path) | ConvertTo-Json -Compress) -ne $expected){throw 'The six candidate DLL hashes changed between native process runs.'}
    if($profile.embeddedRuntimeExecuted -ne $true){throw 'The native plugin context did not execute the embedded runtime.'}
    if($profile.embeddedHttpStarted -ne $true){throw 'The native plugin context did not start and stop embedded HTTP.'}
    if((@($profile.dependencyFiles | Sort-Object path) | ConvertTo-Json -Compress) -ne $expectedRuntime){throw 'Runtime dependency DLL hashes changed between native process runs.'}
    foreach($item in @($profile.ownFiles) + @($profile.dependencyFiles)){
      if((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $BundleDirectory $item.path)).Hash -ne $item.sha256){throw 'A candidate DLL changed after the native process runs.'}
    }
  }
  $result=[ordered]@{schemaVersion=1;passed=$true;layer='isolated-native-plugin-compatibility';minimumDesignerVersion='11.0.3.0';installed=$false;designerStarted=$false;embeddedRuntimeExecuted=$true;embeddedHttpStarted=$true;ownFiles=$own;dependencyFiles=$runtime;profiles=$profiles}
  $path=Join-Path $out 'compatibility-result.json'
  $result | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 $path
  Write-Output ('RESULT '+$path)
}
