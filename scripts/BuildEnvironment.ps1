$ErrorActionPreference='Stop'
$script:SeeinProject=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Get-SeeinDotnet {
    param([int]$Major=10)
    $override=if($Major -eq 10){$env:SEEIN_DOTNET10}else{$env:SEEIN_DOTNET}
    if($override -and (Test-Path -LiteralPath $override)){return $override}
    $local=Join-Path $script:SeeinProject $(if($Major -eq 10){'.build/tools/dotnet10/dotnet.exe'}else{'.build/dotnet/dotnet.exe'})
    if(Test-Path -LiteralPath $local){return $local}
    return (Get-Command dotnet -ErrorAction Stop).Source
}
function Get-SeeinPython {
    if($env:SEEIN_PYTHON -and (Test-Path -LiteralPath $env:SEEIN_PYTHON)){return $env:SEEIN_PYTHON}
    $command=Get-Command python -ErrorAction Stop
    if($command.Source -like '*WindowsApps*'){throw 'Install Python 3.11+ or set SEEIN_PYTHON to its executable.'}
    return $command.Source
}
function Initialize-SeeinDependencies {
    param([switch]$ForPublish)
    if(!$ForPublish){return}
    # A caller-provided immutable cache is optional. Only named third-party input
    # directories are linked, never an application installation or user data.
    if($env:SEEIN_DEPENDENCY_CACHE){
        $cache=[IO.Path]::GetFullPath($env:SEEIN_DEPENDENCY_CACHE)
        foreach($folder in @('deps','tools','tool-payload')){
            $source=Join-Path $cache $folder;$destination=Join-Path $script:SeeinProject ('.build/'+$folder)
            if((Test-Path -LiteralPath $source) -and !(Test-Path -LiteralPath $destination)){
                New-Item -ItemType Directory -Path (Split-Path $destination) -Force|Out-Null
                New-Item -ItemType Junction -Path $destination -Target $source|Out-Null
            }
        }
    }
    & (Join-Path $PSScriptRoot 'Prepare-PublicDependencies.ps1')
}
