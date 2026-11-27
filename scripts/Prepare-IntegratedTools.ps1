[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination, [Parameter(Mandatory)][string]$SourceDestination,
      [string]$Python = $env:SEEIN_PYTHON)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'BuildEnvironment.ps1')
if(!$Python){$Python=Get-SeeinPython}
. (Join-Path $PSScriptRoot 'ProcessNames.ps1')
$payload = Join-Path $projectRoot '.build/tool-payload'
& (Join-Path $PSScriptRoot 'Prepare-Everything.ps1') -Destination $Destination
foreach ($module in 'snow','pane') {
    $source = Join-Path $payload $module
    $exe = Get-SeeinProcessName $(if ($module -eq 'snow') { 'capture' } else { 'efficiency' })
    foreach ($required in $exe,'RUNTIME.sha256','licenses') {
        if (!(Test-Path -LiteralPath (Join-Path $source $required))) { throw "Native tool $module is not staged: $required" }
    }
    foreach ($line in Get-Content -LiteralPath (Join-Path $source 'RUNTIME.sha256')) {
        if ($line -notmatch '^([0-9a-fA-F]{64}) \*(.+)$') { throw 'Invalid tool runtime manifest.' }
        $hash=$Matches[1]; $relative=$Matches[2]
        $file=[IO.Path]::GetFullPath((Join-Path $source $relative))
        if (!$file.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid tool runtime path.' }
        if (!(Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file).Hash -ne $hash) { throw "Tool runtime hash mismatch: $module/$relative" }
    }
    $target=Join-Path $Destination "tools/$module"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
    if (Get-ChildItem -LiteralPath $target -Recurse -File | Where-Object Extension -In '.pdb','.obj','.lib','.exp','.pyc') { throw "Development files found in tool payload: $module" }
}
& $Python (Join-Path $projectRoot 'vendor/tools/export-sources.py') $SourceDestination
if ($LASTEXITCODE -ne 0) { throw 'Could not export corresponding tool source.' }
