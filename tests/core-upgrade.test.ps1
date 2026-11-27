$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../scripts/Preserve-InstalledPlugins.ps1')
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('Seein-core-upgrade-'+[guid]::NewGuid().ToString('N'))
try {
    foreach($name in @('old','stage','mismatch')){New-Item -ItemType Directory -Path (Join-Path $fixture "$name/App") -Force|Out-Null;[IO.File]::WriteAllText((Join-Path $fixture "$name/App/module-publisher.pem"),'fixture-key')}
    New-Item -ItemType Directory -Path (Join-Path $fixture 'old/Plugins/devices'),(Join-Path $fixture 'old/Plugins/.transactions/private-test') -Force|Out-Null
    [IO.File]::WriteAllText((Join-Path $fixture 'old/Plugins/devices/keep.txt'),'keep')
    Copy-SeeinInstalledPlugins -InstalledRoot (Join-Path $fixture 'old') -StageRoot (Join-Path $fixture 'stage')
    if([IO.File]::ReadAllText((Join-Path $fixture 'stage/Plugins/devices/keep.txt')) -ne 'keep'){throw 'Legacy payload was not retained'}
    if(Test-Path -LiteralPath (Join-Path $fixture 'stage/Plugins/.transactions')){throw 'Old maintenance state leaked into upgrade'}
    [IO.File]::WriteAllText((Join-Path $fixture 'mismatch/App/module-publisher.pem'),'different-key')
    $rejected=$false;try{Copy-SeeinInstalledPlugins -InstalledRoot (Join-Path $fixture 'old') -StageRoot (Join-Path $fixture 'mismatch')}catch{$rejected=$true}
    if(!$rejected -or (Test-Path -LiteralPath (Join-Path $fixture 'mismatch/Plugins'))){throw 'Mismatched publisher was not rejected before copy'}
    if([IO.File]::ReadAllText((Join-Path $fixture 'old/Plugins/devices/keep.txt')) -ne 'keep'){throw 'Original payload was changed'}
    Write-Output 'PASS: Core upgrade retains installed modules, skips transactions and rejects a mismatched publisher; no installed service or user data changed.'
}
finally {
    $owned=Get-Item -LiteralPath $fixture -ErrorAction SilentlyContinue
    if($owned){if($owned.Parent.FullName.TrimEnd('\') -ine [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -or !$owned.Name.StartsWith('Seein-core-upgrade-') -or ($owned.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unsafe test cleanup'};Remove-Item -LiteralPath $owned.FullName -Recurse -Force}
}
