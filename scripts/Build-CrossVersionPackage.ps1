#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PayloadManifest,
    [Parameter(Mandatory=$true)][string]$ComponentEvidence,
    [string]$CompatibilityEvidence
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = $sourceRoot
. (Join-Path $PSScriptRoot 'SourceInputs.ps1')
$profiles = @{
    '11.0.3.0' = @{ label='V11-11.0.3.0'; hash='AD42655737D449128A0DC4AF1EEE53FF228C933E7D0FEECAEC9A1155DDD741CF' }
    '11.0.104.0' = @{ label='V11-11.0.104.0'; hash='07E51B2AD8A70CE2AF337ED9A6071C81E87ACA421817ED1B8FB10A2F04CDA9BF' }
    '12.0.102.0' = @{ label='V12-12.0.102.0'; hash='A6047895A74DC5C608D500376534C27BD32EBBBC2C2D423D5C4B2FFDAC885246' }
}
$ownPluginNames = @('ForguncyDesignerMcp.dll','ForguncyDesignerMcp.Core.dll',
    'ForguncyDesignerMcp.Adapters.V11.dll','ForguncyDesignerMcp.Adapters.V12.dll','ForguncyDesignerMcp.Host.dll','ForguncyDesignerMcp.PlatformRuntime.dll')
$runtimeDependencies = @('Jint.dll','Acornima.dll','JsonPath.Net.dll','Json.More.dll')

function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Assert-NoTestArtifact([string]$Identity) {
    $normalized = $Identity.Replace('\','/')
    if ($normalized -match '(^|/)(tests?|test-observer)(/|$)' -or
        $normalized -match '(^|/)[^/]*(PlatformConformance|NativeObserver|StartupHook|test[-_.]?observer)[^/]*$') {
        throw "Test observer/startup artifacts cannot enter a distributable package: $Identity"
    }
}
function Get-ChildPath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Contains(':') -or $Relative.Contains([char]0)) { throw "Invalid relative path: $Relative" }
    $normalized = $Relative.Replace('\','/')
    if ($normalized.Split('/') | Where-Object { $_ -in @('','.', '..') -or $_.EndsWith(' ') -or $_.EndsWith('.') }) { throw "Unsafe relative path: $Relative" }
    $fullRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    $result = [IO.Path]::GetFullPath([IO.Path]::Combine($fullRoot,$normalized.Replace('/',[IO.Path]::DirectorySeparatorChar)))
    if (-not $result.StartsWith($fullRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw "Path leaves its root: $Relative" }
    return $result
}
function Assert-NoLink([string]$Path, [string]$Root) {
    $boundary = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    for ($part=[IO.Path]::GetFullPath($Path); $part; $part=[IO.Path]::GetDirectoryName($part)) {
        if (([IO.File]::Exists($part) -or [IO.Directory]::Exists($part)) -and (([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw "Linked files/directories are not accepted: $part" }
        if ($part.Equals($boundary,[StringComparison]::OrdinalIgnoreCase)) { break }
    }
}
function Get-SafeFiles([string]$Root) {
    Assert-NoLink $Root $Root
    foreach ($file in Get-ChildItem -LiteralPath $Root -File -Force) {
        Assert-NoLink $file.FullName $Root
        $file.FullName
    }
    foreach ($directory in Get-ChildItem -LiteralPath $Root -Directory -Force) {
        Assert-NoLink $directory.FullName $Root
        Get-SafeFiles $directory.FullName
    }
}
function Copy-Checked([string]$Source, [string]$Destination, [string]$ExpectedHash) {
    if ((Get-Hash $Source) -ne $ExpectedHash) { throw "Input changed before copying: $Source" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    [IO.File]::Copy($Source,$Destination,$false)
    if ((Get-Hash $Source) -ne $ExpectedHash -or (Get-Hash $Destination) -ne $ExpectedHash) { throw "Input changed while copying: $Source" }
}
function Write-Json([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path,($Value | ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop | Out-Null
}
function Get-OwnVersion([string]$Path) {
    $assembly = [Reflection.AssemblyName]::GetAssemblyName($Path)
    if ([IO.Path]::GetFileNameWithoutExtension($Path) -cne $assembly.Name -or -not $assembly.Name.StartsWith('ForguncyDesignerMcp',[StringComparison]::Ordinal)) { throw "Not the declared own assembly: $Path" }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ([string]::IsNullOrWhiteSpace($info.FileVersion) -or [string]::IsNullOrWhiteSpace($info.ProductVersion)) { throw "Own assembly has no verifiable file/product version: $Path" }
    $fileVersion = [Version]::new($info.FileMajorPart,$info.FileMinorPart,$info.FileBuildPart,$info.FilePrivatePart)
    $productNumeric = ($info.ProductVersion -split '[+\-]',2)[0]
    $productVersion = [Version]::Parse($productNumeric)
    if ($assembly.Version -ne $fileVersion -or $productVersion.Major -ne $fileVersion.Major -or $productVersion.Minor -ne $fileVersion.Minor -or $productVersion.Build -ne $fileVersion.Build) { throw "Own assembly version fields disagree: $Path" }
    [ordered]@{ name=$assembly.Name; assemblyVersion=$assembly.Version.ToString(); fileVersion=$fileVersion.ToString(); productVersion=$info.ProductVersion; sha256=(Get-Hash $Path) }
}
function Get-AssemblyReferences([string]$Path) {
    # Metadata-only inspection; no plugin code or vendor assembly is loaded/executed.
    $stream = [IO.File]::OpenRead($Path)
    $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reader)
        foreach ($handle in $metadata.AssemblyReferences) {
            $reference = $metadata.GetAssemblyReference($handle)
            [pscustomobject]@{ name=$metadata.GetString($reference.Name); version=$reference.Version.ToString() }
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
function Get-FileManifest([string]$Root) {
    @(Get-SafeFiles $Root | Sort-Object | ForEach-Object { [ordered]@{ path=[IO.Path]::GetRelativePath($Root,$_).Replace('\','/'); sha256=(Get-Hash $_); length=([IO.FileInfo]::new($_)).Length } })
}
function New-CheckedZip([string]$Zip, [string]$Root, [object[]]$Files) {
    # Explicit entries allow the offline ZIP to live in its unique output folder
    # without accidentally including itself or launching an external archiver.
    $output = [IO.File]::Open($Zip,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($output,[IO.Compression.ZipArchiveMode]::Create,$false)
    try {
        foreach ($file in $Files) {
            $source = Get-ChildPath $Root $file.path
            if ((Get-Hash $source) -ne $file.sha256) { throw "Output changed before archiving: $source" }
            $entry = $archive.CreateEntry($file.path,[IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $input = [IO.File]::OpenRead($source)
            try { $input.CopyTo($entryStream) } finally { $input.Dispose(); $entryStream.Dispose() }
        }
    } finally { $archive.Dispose(); $output.Dispose() }
    $readback = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        if ($readback.Entries.Count -ne $Files.Count) { throw "ZIP entry count differs: $Zip" }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $readback.Entries) {
            if (-not $seen.Add($entry.FullName)) { throw "ZIP has duplicate entries: $($entry.FullName)" }
            $expected = @($Files | Where-Object { $_.path -ceq $entry.FullName })
            if ($expected.Count -ne 1 -or $entry.Length -ne $expected[0].length) { throw "ZIP has an unexpected entry: $($entry.FullName)" }
            $input = $entry.Open()
            try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)) } finally { $input.Dispose() }
            if ($actual -ne $expected[0].sha256) { throw "ZIP entry failed SHA-256 read-back: $($entry.FullName)" }
        }
    } finally { $readback.Dispose() }
}

$manifestPath = [IO.Path]::GetFullPath($PayloadManifest)
if (-not [IO.File]::Exists($manifestPath)) { throw 'PayloadManifest must name an existing immutable payload.json.' }
$manifestHash = Get-Hash $manifestPath
$payload = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
if(-not $payload.PSObject.Properties['sourceGitCommit'] -or
   (Get-McpSourceGitCommit $sourceRoot) -ne [string]$payload.sourceGitCommit){throw 'Package source must match its committed independent-repository payload.'}
if(-not $payload.PSObject.Properties['sourceInputs'] -or -not $payload.PSObject.Properties['sourceInputSha256'] -or
    (Get-McpSourceInputHash @($payload.sourceInputs)) -ne [string]$payload.sourceInputSha256 -or
    (Get-McpSourceInputHash (Get-McpSourceInputs $sourceRoot)) -ne [string]$payload.sourceInputSha256) {
    throw 'Current product source/resources do not match the source-frozen payload. Freeze the work and rebuild the payload.'
}
$componentSummary=$null
if($ComponentEvidence) {
    $evidencePath=[IO.Path]::GetFullPath($ComponentEvidence)
    $evidenceRoot=[IO.Path]::GetDirectoryName($evidencePath)
    if(-not $evidencePath.StartsWith((Join-Path $workspaceRoot '.local')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Component evidence must come from an owned workspace .local verification run.'
    }
    Assert-NoLink $evidencePath (Join-Path $workspaceRoot '.local')
    $evidenceHash=Get-Hash $evidencePath
    $verification=Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json -ErrorAction Stop
    if($verification.completed -ne $true -or $verification.failure -or
       $verification.sourceGitCommit -ne $payload.sourceGitCommit -or
       $verification.sourceInputSha256 -ne $payload.sourceInputSha256 -or
       (Get-McpSourceInputHash @($verification.sourceInputs)) -ne $payload.sourceInputSha256 -or
       (Get-McpSourceInputHash @($verification.verificationInputs)) -ne $verification.verificationInputSha256 -or
       $verification.layer -ne 'public-isolated-components-and-protocol' -or
       $verification.nativeMcpStarted -ne $false -or $verification.designerStarted -ne $false -or
       $verification.installed -ne $false -or @($verification.checks).Count -eq 0) {
        throw 'Component evidence is incomplete, has failures, or does not match this exact product source snapshot.'
    }
    $expectedChecks=@('public-development-preview','universal-payload','universal-native-compatibility')
    if((@($verification.checks | ForEach-Object name) -join ';') -cne ($expectedChecks -join ';')){throw 'Public verification stage list does not match the required release workflow.'}
    foreach($record in @($verification.verificationInputs)){
        $inputPath=Get-ChildPath $sourceRoot ([string]$record.path)
        Assert-NoLink $inputPath $sourceRoot
        if((Get-Hash $inputPath) -ne [string]$record.sha256){throw 'Public verification inputs no longer match the receipt.'}
    }
    $checkRecords=@(foreach($check in $verification.checks) {
        if($check.exitCode -ne 0){throw 'A recorded component check failed.'}
        $logPath=[IO.Path]::GetFullPath([string]$check.log)
        if(-not $logPath.StartsWith($evidenceRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Component log leaves its verification run.'}
        Assert-NoLink $logPath $evidenceRoot
        [ordered]@{name=[string]$check.name;exitCode=0;logSha256=(Get-Hash $logPath)}
    })
    if((Get-Hash $evidencePath) -ne $evidenceHash){throw 'Component evidence changed while being read.'}
    $componentSummary=[ordered]@{sourceInputSha256=[string]$payload.sourceInputSha256;verificationInputSha256=[string]$verification.verificationInputSha256;verificationSha256=$evidenceHash;layer=$verification.layer;checks=$checkRecords;liveDesignerCrudValidated=$false;liveCollaborationValidated=$false;allModulesCrudValidated=$false}
}
if ($payload.PSObject.Properties['observerAssembly'] -and -not [string]::IsNullOrWhiteSpace([string]$payload.observerAssembly)) {
    throw 'Test-only observer payloads cannot be packaged; build a separate payload without -WithObserver.'
}
foreach ($declaredPath in @($payload.payloadRoot,$payload.designerRoot,$payload.pluginAssembly,$payload.hostAssembly)) {
    if (-not [IO.Path]::IsPathFullyQualified([string]$declaredPath)) { throw 'Payload root, SDK root and declared assembly paths must be absolute.' }
}
$version = [string]$payload.designerVersion
if (-not $profiles.ContainsKey($version)) { throw 'The payload is not one of the three pinned exact SDK profiles.' }
$profile = $profiles[$version]
$mode=if($payload.PSObject.Properties['packageMode']){[string]$payload.packageMode}else{'exactSdk'}
if($mode -notin @('exactSdk','universal')){throw 'Unknown package mode.'}
$isUniversal=$mode -eq 'universal'
$supportedProfiles=@($payload.supportedDesignerProfiles)
$expectedVersions=if($isUniversal){@('11.0.3.0','11.0.104.0','12.0.102.0')}else{@($version)}
if($supportedProfiles.Count -ne $expectedVersions.Count -or
   @($supportedProfiles | ForEach-Object designerVersion | Sort-Object -Unique).Count -ne $expectedVersions.Count -or
   ($isUniversal -and $version -ne '11.0.3.0') -or [string]$payload.minimumDesignerVersion -ne $version){throw 'Package minimum SDK or supported-profile declaration is inconsistent.'}
foreach($supported in $supportedProfiles) {
    if([string]$supported.designerVersion -notin $expectedVersions -or
       [string]$supported.designerSha256 -ne $profiles[[string]$supported.designerVersion].hash){throw 'Unsupported package profile or fingerprint.'}
}
$packageLabel=if($isUniversal){'Universal-V11-V12'}else{$profile.label}
$payloadRoot = [IO.Path]::GetFullPath([string]$payload.payloadRoot)
if (-not [IO.Directory]::Exists($payloadRoot) -or -not [IO.Path]::GetDirectoryName($manifestPath).Equals($payloadRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'payload.json must reside at its declared payloadRoot.' }
Assert-NoLink $manifestPath $payloadRoot
$designerRoot = [IO.Path]::GetFullPath([string]$payload.designerRoot)
$nativeFile = Join-Path $designerRoot 'WebSite\designerBin\Forguncy.dll'
if ([string]$payload.designerSha256 -ne $profile.hash -or (Get-Hash $nativeFile) -ne $profile.hash -or [Reflection.AssemblyName]::GetAssemblyName($nativeFile).Version.ToString() -ne $version) { throw 'Payload/installed Designer fingerprint or precise version differs from the SDK profile.' }
if ($payload.nativeMcpRequired -ne $false -or $payload.installed -ne $false) { throw 'Expected an uninstalled, independent immutable integration payload.' }

$inputFiles = @{}
foreach ($file in @($payload.files)) {
    $relative = [string]$file.path
    Assert-NoTestArtifact $relative
    if ($relative -cne $relative.Replace('\','/') -or $relative -notmatch '^(plugin|host)/' -or [string]$file.sha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw "Invalid payload file record: $relative" }
    if ($inputFiles.ContainsKey($relative)) { throw "Duplicate payload path: $relative" }
    $path = Get-ChildPath $payloadRoot $relative
    Assert-NoLink $path $payloadRoot
    if (-not [IO.File]::Exists($path) -or (Get-Hash $path) -ne [string]$file.sha256) { throw "Immutable payload SHA-256 mismatch: $relative" }
    $name = [IO.Path]::GetFileName($relative)
    if ($name -match '^(GrapeCity\.|Forguncy\.|ServerDesignerCommon\.|CommonUtilities\.)') { throw "Vendor files must not be redistributed: $relative" }
    if ([IO.Path]::GetExtension($name) -ieq '.dll') {
        $identity = [Reflection.AssemblyName]::GetAssemblyName($path)
        Assert-NoTestArtifact $identity.Name
        if ([IO.Path]::GetFileNameWithoutExtension($name) -cne $identity.Name) { throw "Managed payload DLL identity differs from its file name: $relative" }
        foreach ($reference in @(Get-AssemblyReferences $path)) { Assert-NoTestArtifact $reference.name }
    }
    if ($relative.StartsWith('host/',[StringComparison]::Ordinal) -and [IO.Path]::GetExtension($name) -ieq '.json' -and
        (Get-Content -LiteralPath $path -Raw) -match '(PlatformConformance|NativeObserver|StartupHook|test[-_.]?observer|DOTNET_STARTUP_HOOKS)') {
        throw "Host metadata references a test observer/startup artifact: $relative"
    }
    $inputFiles[$relative] = [pscustomobject]@{path=$relative;fullPath=$path;sha256=([string]$file.sha256).ToUpperInvariant()}
}
$actualFiles = @(Get-SafeFiles $payloadRoot | Where-Object { -not $_.Equals($manifestPath,[StringComparison]::OrdinalIgnoreCase) })
if ($actualFiles.Count -ne $inputFiles.Count) { throw 'Payload contains missing/unlisted files; it is no longer the frozen file set.' }
foreach ($file in $actualFiles) { if (-not $inputFiles.ContainsKey([IO.Path]::GetRelativePath($payloadRoot,$file).Replace('\','/'))) { throw "Unlisted payload file: $file" } }
$deps = Get-Content -LiteralPath $inputFiles['host/ForguncyDesignerMcp.Host.deps.json'].fullPath -Raw | ConvertFrom-Json -AsHashtable
foreach ($identity in @('Jint/4.17.0','Acornima/1.7.0','JsonPath.Net/2.1.1','Json.More.Net/2.1.1')) {
    if (-not $deps.libraries.ContainsKey($identity)) { throw "Host dependency version missing: $identity" }
}
foreach ($name in $runtimeDependencies) {
    if (-not $inputFiles.ContainsKey('host/'+$name) -or -not $inputFiles.ContainsKey('plugin/'+$name)) { throw "Missing runtime dependency: $name" }
    if ($inputFiles['host/'+$name].sha256 -ne $inputFiles['plugin/'+$name].sha256) { throw "Runtime dependency differs: $name" }
    if ([Reflection.AssemblyName]::GetAssemblyName($inputFiles['plugin/'+$name].fullPath).Name -cne [IO.Path]::GetFileNameWithoutExtension($name)) { throw "Runtime dependency identity differs: $name" }
}
$pluginRecords = @($inputFiles.Values | Where-Object { $_.path.StartsWith('plugin/',[StringComparison]::Ordinal) })
if ($pluginRecords.Count -ne ($ownPluginNames.Count + $runtimeDependencies.Count)) { throw 'The plugin payload must contain exactly the six own DLLs and four runtime dependencies.' }
foreach ($name in ($ownPluginNames + $runtimeDependencies)) { if (-not $inputFiles.ContainsKey('plugin/'+$name)) { throw "Missing own plugin DLL: $name" } }
foreach ($name in @('ForguncyDesignerMcp.Host.dll','ForguncyDesignerMcp.Host.deps.json','ForguncyDesignerMcp.Host.runtimeconfig.json','ForguncyDesignerMcp.Core.dll','ForguncyDesignerMcp.PlatformRuntime.dll')) {
    if (-not $inputFiles.ContainsKey('host/'+$name)) { throw "Missing independent Host/runtime file: $name" }
}
if (-not ([IO.Path]::GetFullPath([string]$payload.pluginAssembly)).Equals($inputFiles['plugin/ForguncyDesignerMcp.dll'].fullPath,[StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFullPath([string]$payload.hostAssembly)).Equals($inputFiles['host/ForguncyDesignerMcp.Host.dll'].fullPath,[StringComparison]::OrdinalIgnoreCase)) { throw 'Declared main assembly paths do not match the frozen file list.' }
if ($inputFiles['plugin/ForguncyDesignerMcp.PlatformRuntime.dll'].sha256 -ne $inputFiles['host/ForguncyDesignerMcp.PlatformRuntime.dll'].sha256 -or $inputFiles['plugin/ForguncyDesignerMcp.Host.dll'].sha256 -ne $inputFiles['host/ForguncyDesignerMcp.Host.dll'].sha256) { throw 'Embedded Host/runtime differs from published Host.' }
if ($inputFiles['plugin/ForguncyDesignerMcp.Core.dll'].sha256 -ne $inputFiles['host/ForguncyDesignerMcp.Core.dll'].sha256) { throw 'Plugin and Host do not contain the identical Core contract.' }

$ownVersions = @($inputFiles.Values | Where-Object { $_.path -match '/ForguncyDesignerMcp[^/]*\.dll$' } | Sort-Object path | ForEach-Object {
    $record = Get-OwnVersion $_.fullPath
    $record['path'] = $_.path
    $record
})
$main = $ownVersions | Where-Object { $_.path -ceq 'plugin/ForguncyDesignerMcp.dll' }
$packageVersion = [string]$main.fileVersion
if(@($ownVersions | Where-Object { $_.fileVersion -ne $packageVersion }).Count -ne 0) {
    throw 'All six own product components must share the same package version. Rebuild one consistent payload.'
}
$compatibilitySummary=$null
if($isUniversal) {
    if(-not $CompatibilityEvidence){throw 'A universal package requires actual source-DLL-matched native compatibility evidence for all three supported profiles.'}
    $compatibilityPath=[IO.Path]::GetFullPath($CompatibilityEvidence)
    $compatibilityHash=Get-Hash $compatibilityPath
    $compatibility=Get-Content -LiteralPath $compatibilityPath -Raw | ConvertFrom-Json -ErrorAction Stop
    if($compatibility.schemaVersion -ne 1 -or $compatibility.passed -ne $true -or
       $compatibility.layer -ne 'isolated-native-plugin-compatibility' -or
       $compatibility.minimumDesignerVersion -ne '11.0.3.0' -or
       $compatibility.installed -ne $false -or $compatibility.designerStarted -ne $false){throw 'Native universal compatibility evidence is incomplete or has an unexpected validation layer.'}
    $compatibilityFiles=@($compatibility.ownFiles)
    if($compatibilityFiles.Count -ne $ownPluginNames.Count -or
       @($compatibilityFiles | ForEach-Object path | Sort-Object -Unique).Count -ne $ownPluginNames.Count){throw 'Universal compatibility evidence must identify exactly the six own plugin DLLs.'}
    foreach($name in $ownPluginNames) {
        $proof=@($compatibilityFiles | Where-Object path -CEQ $name)
        if($proof.Count -ne 1 -or [string]$proof[0].sha256 -ne $inputFiles['plugin/'+$name].sha256){throw 'Universal compatibility evidence was produced for different plugin DLL bytes.'}
    }
    $dependencyFiles=@($compatibility.dependencyFiles)
    if ($compatibility.embeddedRuntimeExecuted -ne $true -or $dependencyFiles.Count -ne $runtimeDependencies.Count) { throw 'Embedded runtime compatibility evidence is incomplete.' }
    foreach ($name in $runtimeDependencies) {
        $proof=@($dependencyFiles | Where-Object path -CEQ $name)
        if ($proof.Count -ne 1 -or [string]$proof[0].sha256 -ne $inputFiles['plugin/'+$name].sha256) { throw "Dependency evidence differs: $name" }
    }
    $compatibilityProfiles=@($compatibility.profiles)
    if($compatibilityProfiles.Count -ne 3 -or @($compatibilityProfiles | ForEach-Object designerVersion | Sort-Object -Unique).Count -ne 3){throw 'Universal compatibility requires three distinct exact profiles.'}
    foreach($supported in $supportedProfiles) {
        $proof=@($compatibilityProfiles | Where-Object designerVersion -CEQ $supported.designerVersion)
        if($proof.Count -ne 1 -or [string]$proof[0].designerSha256 -ne [string]$supported.designerSha256 -or
           $proof[0].dependencyAccepted -ne $true -or $proof[0].nativePluginLoader -ne $true -or $proof[0].abiMembersResolved -ne $true -or
           $proof[0].typesLoaded -ne $true -or $proof[0].cellTypeCreated -ne $true -or
           $proof[0].designerCreated -ne $true -or $proof[0].adapterResolved -ne $true){throw 'A supported SDK lacks actual native dependency, plugin type or adapter compatibility proof.'}
    }
    foreach ($profileProof in $compatibilityProfiles) {
        if ($profileProof.embeddedRuntimeExecuted -ne $true -or $profileProof.embeddedHttpStarted -ne $true) { throw 'Profile lacks embedded HTTP runtime execution.' }
        $profileDependencies=@($profileProof.dependencyFiles)
        if ($profileDependencies.Count -ne $runtimeDependencies.Count) { throw 'Profile dependency evidence incomplete.' }
        foreach ($name in $runtimeDependencies) {
            $proof=@($profileDependencies | Where-Object path -CEQ $name)
            if ($proof.Count -ne 1 -or [string]$proof[0].sha256 -ne $inputFiles['plugin/'+$name].sha256) { throw "Profile dependency evidence differs: $name" }
        }
    }
    if((Get-Hash $compatibilityPath) -ne $compatibilityHash){throw 'Universal compatibility evidence changed while being read.'}
    $compatibilitySummary=[ordered]@{evidenceSha256=$compatibilityHash;layer=$compatibility.layer;minimumDesignerVersion=$compatibility.minimumDesignerVersion;ownFiles=$compatibilityFiles;profiles=@(foreach($p in $compatibilityProfiles){
        [ordered]@{designerVersion=$p.designerVersion;designerSha256=$p.designerSha256;
            dependencyAccepted=$p.dependencyAccepted;nativePluginLoader=$p.nativePluginLoader;abiMembersResolved=$p.abiMembersResolved;
            cellTypeCreated=$p.cellTypeCreated;designerCreated=$p.designerCreated;adapterResolved=$p.adapterResolved;
            runtimeVersion=$p.runtimeVersion;embeddedRuntimeExecuted=$p.embeddedRuntimeExecuted;embeddedHttpStarted=$p.embeddedHttpStarted;
            dependencyFiles=$p.dependencyFiles;ownFiles=$p.ownFiles;
            sdkAssemblies=@($p.sdkAssemblies | Select-Object name,version,sha256)}
    });installed=$false;liveDesignerValidated=$false}
} elseif($CompatibilityEvidence) {throw 'CompatibilityEvidence is only applicable to universal payloads.'}
$sdkReferences = @()
$requiredSdk = @('GrapeCity.Forguncy.CellTypes','GrapeCity.Forguncy.CellTypes.Design','GrapeCity.Forguncy.Plugin')
$references = @(Get-AssemblyReferences $inputFiles['plugin/ForguncyDesignerMcp.dll'].fullPath)
foreach ($name in $requiredSdk) { if (@($references | Where-Object { $_.name -ceq $name }).Count -ne 1) { throw "Missing required own-plugin SDK reference: $name" } }
foreach ($name in @($references | Where-Object { $_.name.StartsWith('GrapeCity.Forguncy.',[StringComparison]::Ordinal) } | ForEach-Object name)) {
    $reference = @($references | Where-Object { $_.name -ceq $name })
    $sdkFile = Join-Path $designerRoot ('WebSite\designerBin\'+$name+'.dll')
    $sdkIdentity = [Reflection.AssemblyName]::GetAssemblyName($sdkFile)
    if ($reference.Count -ne 1 -or $reference[0].version -ne $sdkIdentity.Version.ToString() -or $sdkIdentity.Version.ToString() -ne $version) { throw "Main plugin was not compiled against this exact SDK: $name" }
    $sdkReferences += [ordered]@{name=$name;version=$reference[0].version;installedSha256=(Get-Hash $sdkFile)}
}

$templatePath = Join-Path $sourceRoot 'ForguncyDesignerMcp\PluginConfig.json'
$templateHash = Get-Hash $templatePath
$config = Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json -ErrorAction Stop
if ([string]$config.guid -ne '8b34e735-b71d-48cb-b943-c6eb5a0f1432' -or [string]$config.pluginType -ne 'cellType' -or @($config.serverApiAssembly).Count -ne 0) { throw 'Unexpected plugin identity/type/server API template; review the packager contract.' }
$templateVersion = [string]$config.version
if (($templateVersion -replace '\.0$','') -ne ($packageVersion -replace '\.0$','')) { throw 'Plugin template version differs from compiled product.' }
$config.version = $packageVersion
$config.dependenceVersion = $version
$config.assembly = $ownPluginNames
$resourcesRoot = Join-Path $sourceRoot 'ForguncyDesignerMcp\Resources'
$resourceRecords = @(Get-SafeFiles $resourcesRoot | Sort-Object | ForEach-Object {
    Assert-NoTestArtifact ([IO.Path]::GetRelativePath($resourcesRoot,$_))
    if ([IO.Path]::GetExtension($_) -in @('.dll','.exe','.edll','.edll2')) { throw "Managed/vendor binary is not a client resource: $_" }
    [pscustomobject]@{path=('Resources/'+[IO.Path]::GetRelativePath($resourcesRoot,$_).Replace('\','/'));fullPath=$_;sha256=(Get-Hash $_)}
})
$outputBase = Join-Path $sourceRoot ('dist\'+$packageVersion)
$outputRoot = Join-Path $outputBase ($packageLabel+'-'+[Guid]::NewGuid().ToString('N'))
Get-ChildPath $outputBase ([IO.Path]::GetFileName($outputRoot)) | Out-Null
Assert-NoLink $outputBase $sourceRoot
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$pluginRoot = Join-Path $outputRoot 'plugin'
$hostRoot = Join-Path $outputRoot 'host'
[IO.Directory]::CreateDirectory($pluginRoot) | Out-Null
[IO.Directory]::CreateDirectory($hostRoot) | Out-Null
foreach ($file in $inputFiles.Values) { Copy-Checked $file.fullPath (Get-ChildPath $outputRoot $file.path) $file.sha256 }
foreach ($file in $resourceRecords) { Copy-Checked $file.fullPath (Get-ChildPath $pluginRoot $file.path) $file.sha256 }
if ((Get-Hash $templatePath) -ne $templateHash) { throw 'PluginConfig template changed while collecting client resources.' }
Write-Json (Join-Path $pluginRoot 'PluginConfig.json') $config
foreach ($asset in @($config.javascript)+@($config.css)+@($config.image)) {
    if (-not [IO.File]::Exists((Get-ChildPath $pluginRoot ([string]$asset)))) { throw "PluginConfig references a missing client asset: $asset" }
}
$license = Join-Path $sourceRoot 'LICENSE'
Copy-Checked $license (Join-Path $pluginRoot 'LICENSE.txt') (Get-Hash $license)
$notices = Join-Path $sourceRoot 'docs/THIRD-PARTY-NOTICES.md'
if (-not [IO.File]::Exists($notices)) { throw 'Third-party license notices are required before packaging.' }
Copy-Checked $notices (Join-Path $pluginRoot 'THIRD-PARTY-NOTICES.md') (Get-Hash $notices)
$pluginFileList = Get-FileManifest $pluginRoot
$pluginZip = Join-Path $outputRoot ('ForguncyDesignerMcp-'+$packageVersion+'-'+$packageLabel+'-preview.zip')
New-CheckedZip $pluginZip $pluginRoot $pluginFileList
$hostDll = Join-Path $hostRoot 'ForguncyDesignerMcp.Host.dll'
Write-Json (Join-Path $outputRoot 'mcp-config.sample.json') @{mcpServers=@{'forguncy-designer'=@{url='http://127.0.0.1:39291/mcp';headers=@{Authorization='Bearer REPLACE_WITH_TOKEN'}}}}
[IO.Directory]::CreateDirectory((Join-Path $outputRoot 'evidence')) | Out-Null
Write-Json (Join-Path $outputRoot 'evidence\payload.json') ([ordered]@{sourceGitCommit=$payload.sourceGitCommit;sourceInputSha256=$payload.sourceInputSha256;designerVersion=$payload.designerVersion;designerSha256=$payload.designerSha256;files=$payload.files;localManifestSha256=$manifestHash})
if($componentSummary){Write-Json (Join-Path $outputRoot 'evidence\component-verification.json') $componentSummary}
if($compatibilitySummary){Write-Json (Join-Path $outputRoot 'evidence\native-compatibility.json') $compatibilitySummary}
$componentText=if($componentSummary){'Source-matched isolated native component and protocol checks passed; see evidence/component-verification.json. This is not live Designer acceptance.'}else{'No source-matched component evidence was attached to this package.'}
$readme = @"
# Forguncy Designer MCP cross-version preview

Compiled/minimum Designer SDK: $version
Package mode: $mode
Supported exact Designer versions: $($expectedVersions -join ', ')
Package version from the actual main DLL FileVersion: $packageVersion

This is a preview packaging artifact. It does not claim complete module CRUD,
formal installation, cold-start discovery, or live Designer acceptance.
The six component DLLs and four embedded runtime dependencies were copied from the verified
immutable payload; no vendor DLLs, SDK runtime, startup hooks, or source project
are redistributed. No process, installer, or Designer was started by this script.

The plugin ZIP contains PluginConfig.json, the six own DLLs and four runtime dependencies, and current own
client resources. Its dependenceVersion is the minimum $version. Runtime adapters
remain restricted to the listed exact build fingerprints. Install through a
supported Designer's plugin manager when installation has been authorized.
This script does not install it.

Configure the sample URL and Authorization token for the embedded Designer endpoint.
The host/ folder is optional compatibility content.

package-manifest.json records every component's actual assembly/file/product
versions and every packaged file hash. All own components must share the same
package version; this script never relabels their DLL metadata.
The source PluginConfig template version was
$templateVersion; the generated package takes its version from the frozen DLL.

Packaging checked hashes, JSON structure, SDK reference identity and ZIP contents.
$componentText
Installation, settings changes, process launch and live business tests: not run.
"@
[IO.File]::WriteAllText((Join-Path $outputRoot 'README.md'),$readme,[Text.UTF8Encoding]::new($false))
foreach ($file in $inputFiles.Values) { if ((Get-Hash $file.fullPath) -ne $file.sha256) { throw "Payload changed during packaging: $($file.path)" } }
if ((Get-Hash $manifestPath) -ne $manifestHash -or (Get-Hash $nativeFile) -ne $profile.hash) { throw 'Payload manifest or exact SDK changed during packaging.' }
if((Get-McpSourceInputHash (Get-McpSourceInputs $sourceRoot)) -ne [string]$payload.sourceInputSha256) { throw 'Product source/resources changed during packaging; rebuild a consistent payload.' }
if((Get-McpSourceGitCommit $sourceRoot) -ne [string]$payload.sourceGitCommit){throw 'The committed source changed during packaging.'}
$packageManifest = [ordered]@{
    schemaVersion=1; preview=$true; designerVersion=$version; designerSha256=$profile.hash
    packageMode=$mode;compiledDesignerVersion=$version;minimumDesignerVersion=$version;supportedDesignerProfiles=$supportedProfiles
    packageVersion=$packageVersion; sourceTemplateVersion=$templateVersion; payloadManifestSha256=$manifestHash
    sourceInputSha256=[string]$payload.sourceInputSha256
    sourceGitCommit=[string]$payload.sourceGitCommit
    createdUtc=[DateTimeOffset]::UtcNow.ToString('O'); ownAssemblies=$ownVersions; sdkReferences=$sdkReferences
    sourceClientResources=$resourceRecords | Select-Object path,sha256
    pluginZip=[IO.Path]::GetFileName($pluginZip); pluginZipSha256=(Get-Hash $pluginZip)
    files=(Get-FileManifest $outputRoot)
    validation=[ordered]@{payloadHashes=$true;exactSdkProfile=$true;ownAssemblyVersions=$true;jsonParsed=$true;pluginZipEntriesAndHashes=$true;sourceMatchedComponentChecks=($null -ne $componentSummary);nativeUniversalCompatibility=($null -ne $compatibilitySummary)}
    installed=$false;settingsChanged=$false;processesStarted=$false;startupHookUsed=$false;liveAcceptanceExecuted=$false;allModulesCrudValidated=$false
}
Write-Json (Join-Path $outputRoot 'package-manifest.json') $packageManifest
$offlineFiles = Get-FileManifest $outputRoot
$offlineZip = Join-Path $outputRoot ('ForguncyDesignerMcp-'+$packageVersion+'-'+$packageLabel+'-preview-offline.zip')
New-CheckedZip $offlineZip $outputRoot $offlineFiles
$offlineHash = Get-Hash $offlineZip
[IO.File]::WriteAllText(($offlineZip+'.sha256'),$offlineHash+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
# Publish unique, checked top-level files first; latest is the atomic commit point.
$distRoot = Join-Path $sourceRoot 'dist'
Assert-NoLink $distRoot $sourceRoot
$releaseId = 'ForguncyDesignerMcp-'+$packageVersion+'-'+[IO.Path]::GetFileName($outputRoot)
$stablePlugin = Join-Path $distRoot ($releaseId+'-plugin.zip')
$stableOffline = Join-Path $distRoot ($releaseId+'-offline.zip')
$stableChecksum = $stableOffline+'.sha256'
$pluginHash = Get-Hash $pluginZip
Copy-Checked $pluginZip $stablePlugin $pluginHash
$pluginChecksumTemp = Join-Path $outputRoot 'plugin.zip.sha256'
[IO.File]::WriteAllText($pluginChecksumTemp,$pluginHash+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
Copy-Checked $pluginChecksumTemp ($stablePlugin+'.sha256') (Get-Hash $pluginChecksumTemp)
Copy-Checked $offlineZip $stableOffline $offlineHash
Copy-Checked ($offlineZip+'.sha256') $stableChecksum (Get-Hash ($offlineZip+'.sha256'))
$latest = [ordered]@{ schemaVersion=1; outputRoot=$outputRoot; sourceGitCommit=[string]$payload.sourceGitCommit; packageVersion=$packageVersion; pluginZip=$stablePlugin; pluginZipSha256=$pluginHash; pluginChecksum=($stablePlugin+'.sha256'); offlineZip=$stableOffline; offlineSha256=$offlineHash; offlineChecksum=$stableChecksum }
$latestPath = Join-Path $distRoot 'latest-release.json'
$latestTemp = Join-Path $distRoot ('.latest-'+[Guid]::NewGuid().ToString('N')+'.tmp')
try {
    Write-Json $latestTemp $latest
    [IO.File]::Move($latestTemp,$latestPath,$true)
} finally { if ([IO.File]::Exists($latestTemp)) { [IO.File]::Delete($latestTemp) } }
[ordered]@{ outputRoot=$outputRoot; pluginZip=$stablePlugin; offlineZip=$stableOffline; archivePluginZip=$pluginZip; archiveOfflineZip=$offlineZip; offlineSha256=$offlineHash; packageMode=$mode;minimumDesignerVersion=$version;supportedDesignerProfiles=$supportedProfiles;sourceGitCommit=[string]$payload.sourceGitCommit; packageVersion=$packageVersion; preview=$true; installed=$false; processesStarted=$false } | ConvertTo-Json -Depth 4
