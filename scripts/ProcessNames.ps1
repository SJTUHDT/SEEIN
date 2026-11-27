function Get-SeeinProcessCatalog {
    $source=Join-Path $PSScriptRoot '../config/process-names.json'
    if(!(Test-Path -LiteralPath $source)){throw 'SEEIN process-name catalog is missing.'}
    Get-Content -LiteralPath $source -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Get-SeeinProcessName([string]$Role) {
    [IO.Path]::GetFileName((Get-SeeinProcessCatalog).$Role.path)
}
function Convert-SeeinProcessPayload([string]$AppRoot) {
    $root=[IO.Path]::GetFullPath($AppRoot)
    $catalog=Get-SeeinProcessCatalog
    # The display host shares the tray assembly but has a separate real image.
    Copy-Item -LiteralPath (Join-Path $root $catalog.main.path) -Destination (Join-Path $root $catalog.display.path) -Force
    foreach($property in (Get-SeeinProcessCatalog).PSObject.Properties){
        $entry=$property.Value
        $target=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
        if(!$target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe process payload path.'}
        foreach($alias in $entry.legacy){
            $source=Join-Path (Split-Path $target) $alias
            if(Test-Path -LiteralPath $source){
                if(Test-Path -LiteralPath $target){
                    if((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash){throw "Conflicting process payloads: $source"}
                }
                Move-Item -LiteralPath $source -Destination $target -Force
            }
        }
    }
}
