[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('11.0.3.0','11.0.104.0','12.0.102.0')][string]$DesignerVersion,
    [switch]$NoRestore,

    [switch]$Universal,
    [string]$V1103Root,[string]$V11104Root,[string]$V12102Root
)
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'SourceInputs.ps1')
$sourceInputs=Get-McpSourceInputs $sourceRoot
$sourceInputHash=Get-McpSourceInputHash $sourceInputs
$sourceGitCommit=Get-McpSourceGitCommit $sourceRoot
if($Universal -and $DesignerVersion -ne '11.0.3.0'){throw 'The universal bridge must be compiled against the minimum supported 11.0.3.0 SDK.'}
$profiles=@{
    '11.0.3.0'=@{root=$V1103Root;hash='AD42655737D449128A0DC4AF1EEE53FF228C933E7D0FEECAEC9A1155DDD741CF'}
    '11.0.104.0'=@{root=$V11104Root;hash='07E51B2AD8A70CE2AF337ED9A6071C81E87ACA421817ED1B8FB10A2F04CDA9BF'}
    '12.0.102.0'=@{root=$V12102Root;hash='A6047895A74DC5C608D500376534C27BD32EBBBC2C2D423D5C4B2FFDAC885246'}
}
$profile=$profiles[$DesignerVersion]
$supportedVersions=if($Universal){@('11.0.3.0','11.0.104.0','12.0.102.0')}else{@($DesignerVersion)}
$supportedProfiles=@(foreach($supportedVersion in $supportedVersions) {
    $supported=$profiles[$supportedVersion]
    $supportedFile=Join-Path $supported.root 'WebSite\designerBin\Forguncy.dll'
    if((Get-FileHash -LiteralPath $supportedFile -Algorithm SHA256).Hash -ne $supported.hash){throw 'A supported Designer fingerprint changed.'}
    [ordered]@{designerVersion=$supportedVersion;designerSha256=$supported.hash}
})
$nativeFile=Join-Path $profile.root 'WebSite\designerBin\Forguncy.dll'
if((Get-FileHash -LiteralPath $nativeFile -Algorithm SHA256).Hash -ne $profile.hash){throw 'Designer fingerprint changed.'}
$outputRoot=Join-Path $sourceRoot ('dist\payloads\'+$DesignerVersion+'-'+[Guid]::NewGuid().ToString('N'))
if(-not $outputRoot.StartsWith($sourceRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe output path.'}
$pluginOutput=Join-Path $outputRoot 'plugin'
$hostOutput=Join-Path $outputRoot 'host'
New-Item -ItemType Directory -Path $pluginOutput,$hostOutput -Force | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$pluginProject=Join-Path $sourceRoot 'ForguncyDesignerMcp\ForguncyDesignerMcp.csproj'
$hostProject=Join-Path $sourceRoot 'ForguncyDesignerMcp.Host\ForguncyDesignerMcp.Host.csproj'
$restoreArguments=@()
if($NoRestore){$restoreArguments += '--no-restore'}
& dotnet build $pluginProject -c Release --disable-build-servers -m:1 -nr:false -p:DebugType=None "-p:ForguncyRoot=$($profile.root)" @restoreArguments -v minimal
if($LASTEXITCODE -ne 0){throw 'SDK-specific bridge build failed.'}
$sourceBin=Join-Path $sourceRoot 'ForguncyDesignerMcp\bin'
$ownAssemblies=@('ForguncyDesignerMcp.dll','ForguncyDesignerMcp.Core.dll','ForguncyDesignerMcp.Adapters.V11.dll','ForguncyDesignerMcp.Adapters.V12.dll')
foreach($name in $ownAssemblies){
    $source=Join-Path $sourceBin $name
    $destination=Join-Path $pluginOutput $name
    $before=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    [IO.File]::Copy($source,$destination,$false)
    if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $before -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $before){throw 'A compiled component changed while copying; rebuild a consistent payload.'}
}
& dotnet publish $hostProject -c Release --no-self-contained --disable-build-servers -m:1 -nr:false -p:DebugType=None @restoreArguments -o $hostOutput -v minimal
if($LASTEXITCODE -ne 0){throw 'Independent Host/runtime publish failed.'}
foreach($name in @('ForguncyDesignerMcp.Host.dll','ForguncyDesignerMcp.PlatformRuntime.dll','Jint.dll','Acornima.dll','JsonPath.Net.dll','Json.More.dll')) {
    $source=Join-Path $hostOutput $name
    if (-not [IO.File]::Exists($source)) { throw "Missing embedded runtime dependency: $name" }
    $destination=Join-Path $pluginOutput $name
    $hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    [IO.File]::Copy($source,$destination,$false)
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $hash -or (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw "Dependency changed while copying: $name" }
}
if((Get-FileHash -LiteralPath (Join-Path $pluginOutput 'ForguncyDesignerMcp.Core.dll') -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $hostOutput 'ForguncyDesignerMcp.Core.dll') -Algorithm SHA256).Hash){throw 'Core changed during the parallel build; plugin and Host do not share an identical contract implementation.'}
$observerAssembly=$null
if((Get-McpSourceInputHash (Get-McpSourceInputs $sourceRoot)) -ne $sourceInputHash){throw 'Product source changed during the build; freeze the work and rebuild one consistent payload.'}
if((Get-McpSourceGitCommit $sourceRoot) -ne $sourceGitCommit){throw 'Product source commit changed during the build.'}
$manifest=@(Get-ChildItem -LiteralPath $outputRoot -File -Recurse | ForEach-Object {@{path=[IO.Path]::GetRelativePath($outputRoot,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
$record=@{designerVersion=$DesignerVersion;designerRoot=$profile.root;designerSha256=$profile.hash;payloadRoot=$outputRoot;pluginAssembly=(Join-Path $pluginOutput 'ForguncyDesignerMcp.dll');hostAssembly=(Join-Path $hostOutput 'ForguncyDesignerMcp.Host.dll');observerAssembly=$observerAssembly;observerIsTestOnly=$true;nativeMcpRequired=$false;installed=$false;liveAcceptanceExecuted=$false;builtAtUtc=[DateTimeOffset]::UtcNow.ToString('O');files=$manifest}
$record.sourceInputs=$sourceInputs
$record.sourceInputSha256=$sourceInputHash
$record.sourceGitCommit=$sourceGitCommit
$record.packageMode=if($Universal){'universal'}else{'exactSdk'}
$record.minimumDesignerVersion=$DesignerVersion
$record.supportedDesignerProfiles=$supportedProfiles
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputRoot 'payload.json') -Encoding utf8
$record | ConvertTo-Json -Depth 3
Write-Output 'Integration payload only: no installation, process launch or shared setting change.'
Write-Output ("PAYLOAD "+(Join-Path $outputRoot "payload.json"))
