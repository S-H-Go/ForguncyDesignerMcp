# Build provenance for the six product projects; tests and generated outputs
# do not form part of the distributable product input snapshot.
function Get-McpSourceInputs([string]$SourceRoot) {
    $roots=@('ForguncyDesignerMcp','ForguncyDesignerMcp.Core','ForguncyDesignerMcp.Adapters.V11',
        'ForguncyDesignerMcp.Adapters.V12','ForguncyDesignerMcp.Host','ForguncyDesignerMcp.PlatformRuntime')
    $paths=@(foreach($name in $roots) {
        Get-ChildItem -LiteralPath (Join-Path $SourceRoot $name) -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.git|\.vs)[\\/]' } |
            ForEach-Object FullName
    })
    $paths+=@('SourceInputs.ps1','Build-IntegrationPayload.ps1','Build-CrossVersionPackage.ps1') |
        ForEach-Object {Join-Path $SourceRoot ('scripts\'+$_)}
    foreach($name in @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','global.json','NuGet.Config','LICENSE','docs/THIRD-PARTY-NOTICES.md')) {
        $file=Join-Path $SourceRoot $name
        if([IO.File]::Exists($file)){$paths+=$file}
    }
    $records=@($paths | Sort-Object -Unique | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($SourceRoot,$_).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
    })
    Get-McpCanonicalSourceInputs $records
}
function Get-McpCanonicalSourceInputs([object[]]$Inputs) {
    $ordered=[Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($item in $Inputs) {
        if($item -is [System.Collections.IDictionary]) { $path=[string]$item['path']; $sha=[string]$item['sha256'] }
        else { $path=[string]$item.path; $sha=[string]$item.sha256 }
        if([string]::IsNullOrWhiteSpace($path) -or [string]::IsNullOrWhiteSpace($sha) -or $ordered.ContainsKey($path)) { throw "Invalid or duplicate source input path: $path" }
        $ordered.Add($path,$sha)
    }
    foreach($entry in $ordered.GetEnumerator()) { [ordered]@{path=$entry.Key;sha256=$entry.Value} }
}
function Get-McpSourceInputHash([object[]]$Inputs) {
    $canonical=@(Get-McpCanonicalSourceInputs $Inputs) | ConvertTo-Json -Depth 4 -Compress
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
}
function Get-McpSourceGitCommit([string]$SourceRoot) {
    $gitRoot=(& git -C $SourceRoot rev-parse --show-toplevel 2>$null)
    if($LASTEXITCODE -ne 0 -or -not $gitRoot -or
       -not [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([string]$gitRoot)).Equals(
           [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($SourceRoot)),[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Product provenance requires the independent plugin repository, not its parent business repository.'
    }
    $changes=@(& git -C $SourceRoot status --porcelain=v1 --untracked-files=normal)
    if($LASTEXITCODE -ne 0 -or $changes.Count -ne 0){throw 'Commit the reviewed source before integrated verification or packaging.'}
    $commit=(& git -C $SourceRoot rev-parse HEAD).Trim()
    if($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$'){throw 'A committed product source baseline is required.'}
    $commit
}
