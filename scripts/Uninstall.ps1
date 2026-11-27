#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$RemoveData, [string]$InstallParent = $env:ProgramFiles)

$ErrorActionPreference = 'Stop'
$installParent = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'App/Seein.Tray.exe')) { Split-Path $PSScriptRoot -Parent } else { [IO.Path]::GetFullPath($InstallParent) }
$installRoot = [IO.Path]::GetFullPath((Join-Path $installParent 'Seein'))
$previousInstalls = @([IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Seein'))) |
    Where-Object { -not [string]::Equals($_, $installRoot, [StringComparison]::OrdinalIgnoreCase) }
$dataRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'Seein'))
$migrationPath = Join-Path $dataRoot 'migration.json'

$installedTrays = @(@($installRoot) + @($previousInstalls) | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'App\Seein.Tray.exe' })
foreach($tray in @(Get-Process -Name 'Seein.Tray' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $installedTrays -contains $_.Path })){
    $control=Start-Process -FilePath $tray.Path -ArgumentList '--exit' -WindowStyle Hidden -PassThru
    if(!$control.WaitForExit(10000) -or !$tray.WaitForExit(10000)){throw 'Finish the active recording/export and exit SEEIN before uninstalling.'}
}
$indexProvider=Join-Path $installRoot 'App/tools/everything/seeeverything.exe'
if(!(Test-Path -LiteralPath $indexProvider)){$indexProvider=Join-Path $installRoot 'Plugins/efficiency/tools/everything/seeeverything.exe'}
if(Test-Path -LiteralPath $indexProvider){
    $control=Start-Process -FilePath $indexProvider -ArgumentList '-instance SEEIN -exit' -WindowStyle Hidden -PassThru
    if(!$control.WaitForExit(10000)){throw 'SEEIN file index is still closing.'}
}
if((Get-ItemProperty 'HKLM:/SOFTWARE/SEEIN/Dependencies' -ErrorAction SilentlyContinue).EverythingServiceManaged -eq 1){
    $indexService=Get-CimInstance Win32_Service -Filter "Name='Everything'" -ErrorAction SilentlyContinue
    if($indexService -and $indexService.PathName.StartsWith(('"'+$indexProvider+'"'),[StringComparison]::OrdinalIgnoreCase)){
        Stop-Service Everything
        & sc.exe delete Everything|Out-Null
        Remove-ItemProperty 'HKLM:/SOFTWARE/SEEIN/Dependencies' -Name EverythingExecutable,EverythingServiceManaged -ErrorAction SilentlyContinue
    }
}
Unregister-ScheduledTask -TaskName 'SeeinTray' -Confirm:$false -ErrorAction SilentlyContinue
$service = Get-Service -Name 'SeeinService' -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name 'SeeinService' -Force }
    & sc.exe delete SeeinService | Out-Null
}
Get-Process -Name 'Seein.RgbEngine','seergb' -ErrorAction SilentlyContinue |
    Where-Object {
        $path = $_.Path
        $roots = @($installRoot) + $previousInstalls
        $roots | Where-Object { $path -eq (Join-Path $_ 'App\rgb-engine\Seein.RgbEngine.exe') -or $path -eq (Join-Path $_ 'App\rgb-engine\seergb.exe') -or $path -eq (Join-Path $_ 'Plugins\devices\rgb-engine\seergb.exe') }
    } | Stop-Process -Force

if (Test-Path -LiteralPath $migrationPath) {
    $migration = Get-Content -LiteralPath $migrationPath -Raw | ConvertFrom-Json
    if ($migration.oldTaskExisted -and $migration.oldTaskEnabled) {
        Enable-ScheduledTask -TaskName 'JONSBO-AIOPowerBoot' -ErrorAction SilentlyContinue | Out-Null
        Write-Host 'Restored the enabled state of JONSBO-AIOPowerBoot.'
    }
}

$programFilesPrefix = [IO.Path]::GetFullPath($env:ProgramFiles) + [IO.Path]::DirectorySeparatorChar
$installPrefix = [IO.Path]::GetFullPath($installParent) + [IO.Path]::DirectorySeparatorChar
if (Test-Path -LiteralPath $installRoot) {
    if (-not $installRoot.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe uninstall path.' }
    Remove-Item -LiteralPath $installRoot -Recurse -Force
}
foreach ($previous in $previousInstalls) {
    if (-not (Test-Path -LiteralPath $previous)) { continue }
    if (-not $previous.StartsWith($programFilesPrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
    Remove-Item -LiteralPath $previous -Recurse -Force -ErrorAction SilentlyContinue
}
if ($RemoveData -and (Test-Path -LiteralPath $dataRoot)) {
    $programDataPrefix = [IO.Path]::GetFullPath($env:ProgramData) + [IO.Path]::DirectorySeparatorChar
    if (-not $dataRoot.StartsWith($programDataPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe data path.' }
    Remove-Item -LiteralPath $dataRoot -Recurse -Force
    Write-Host 'Application data, themes, energy history, and logs were removed.'
} else {
    Write-Host "Application data remains at $dataRoot. Use -RemoveData to delete it too."
}
Write-Host 'Seein was uninstalled.'
