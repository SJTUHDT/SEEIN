function Copy-SeeinInstalledPlugins {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$InstalledRoot,[Parameter(Mandatory)][string]$StageRoot)
    $source=Join-Path ([IO.Path]::GetFullPath($InstalledRoot)) 'Plugins'
    if(!(Test-Path -LiteralPath $source)){return}
    $oldKey=Join-Path $InstalledRoot 'App/module-publisher.pem';$newKey=Join-Path $StageRoot 'App/module-publisher.pem'
    if(!(Test-Path -LiteralPath $oldKey) -or [IO.File]::ReadAllText($oldKey).Trim() -cne [IO.File]::ReadAllText($newKey).Trim()){
        throw 'Existing plugins and incoming Core use different publisher keys. Use a matching distribution; the current installation was preserved.'
    }
    $folders=@(Get-ChildItem -LiteralPath $source -Directory -Force | Where-Object { !$_.Name.StartsWith('.') })
    foreach($folder in $folders){
        $tree=@($folder)+@(Get-ChildItem -LiteralPath $folder.FullName -Recurse -Force)
        if($tree | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }){throw 'Existing plugin tree contains links; refusing an unsafe copy.'}
        $destination=Join-Path (Join-Path $StageRoot 'Plugins') $folder.Name
        if(Test-Path -LiteralPath $destination){throw 'Core upgrade cannot overwrite a staged plugin.'}
    }
    New-Item -ItemType Directory -Path (Join-Path $StageRoot 'Plugins') -Force|Out-Null
    foreach($folder in $folders){Copy-Item -LiteralPath $folder.FullName -Destination (Join-Path $StageRoot 'Plugins') -Recurse}
}
