#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [switch]$TakeOverOriginal,
    [switch]$RemoveOriginal,
    [switch]$VerifyOnly,
    [switch]$StartInBackground,
    [string]$PackageRoot = $PSScriptRoot,
    [string]$InstallParent = $env:ProgramFiles
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 has no [IO.Path]::GetRelativePath; provide a compatible helper.
function Get-RelativePath([string]$basePath, [string]$targetPath) {
    $base = [IO.Path]::GetFullPath($basePath).TrimEnd('\') + '\'
    $target = [IO.Path]::GetFullPath($targetPath)
    if ($target -eq $base.TrimEnd('\')) { return '.' }
    $baseUri = New-Object System.Uri($base)
    $targetUri = New-Object System.Uri($target)
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('/', '\')
}

$sourceRoot = (Resolve-Path -LiteralPath $PackageRoot -ErrorAction Stop).ProviderPath
$programFilesRoot = [IO.Path]::GetFullPath($env:ProgramFiles)
$programDataRoot = [IO.Path]::GetFullPath($env:ProgramData)
$installParent = [IO.Path]::GetFullPath($InstallParent)
$installRoot = [IO.Path]::GetFullPath((Join-Path $installParent 'Seein'))
$transactionRoot = [IO.Path]::GetFullPath((Join-Path $installParent '.Seein-update'))
$stageRoot = Join-Path $transactionRoot 'new'
$rollbackProgramRoot = Join-Path $transactionRoot 'previous'
$rollbackState = Join-Path $transactionRoot 'state'
$dataRoot = [IO.Path]::GetFullPath((Join-Path $programDataRoot 'Seein'))
$legacyInstall = [IO.Path]::GetFullPath((Join-Path $programFilesRoot 'JonsboPanel'))
$previousInstalls = @(
    [IO.Path]::GetFullPath((Join-Path $programFilesRoot 'Seein')),
    $legacyInstall
) | Where-Object { -not [string]::Equals($_, $installRoot, [StringComparison]::OrdinalIgnoreCase) }
$separator = [IO.Path]::DirectorySeparatorChar
$programFilesPrefix = $programFilesRoot.TrimEnd($separator) + $separator
$installPrefix = [IO.Path]::GetFullPath($installParent).TrimEnd($separator) + $separator
$programDataPrefix = $programDataRoot.TrimEnd($separator) + $separator
if (-not $installRoot.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $stageRoot.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $rollbackProgramRoot.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $legacyInstall.StartsWith($programFilesPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $dataRoot.StartsWith($programDataPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe installation path.' }
$processCatalog=Get-Content -LiteralPath (Join-Path $sourceRoot 'App/process-names.json') -Raw -Encoding UTF8|ConvertFrom-Json
$sourcePayload = Join-Path (Join-Path $sourceRoot 'App') $processCatalog.service.path
if (Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'App') -File -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue | Select-Object -First 1) {
    throw 'Application payload contains debug symbols. Deploy the verified application package; keep PDB files in its symbols archive.'
}
$sourceUninstaller = Join-Path $sourceRoot 'Uninstall.ps1'
if (-not (Test-Path -LiteralPath $sourcePayload -PathType Leaf) -or
    -not (Test-Path -LiteralPath $sourceUninstaller -PathType Leaf)) {
    throw 'This installer must be run from the root of the published Seein package.'
}

$manifestPath = Join-Path $sourceRoot 'MANIFEST.sha256'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Package MANIFEST.sha256 is missing; refusing to change the installed application.' }
$manifestEntries = @{}
foreach ($line in Get-Content -LiteralPath $manifestPath -Encoding UTF8) {
    if ($line -notmatch '^([0-9a-fA-F]{64}) \*(.+)$') { throw "Invalid manifest entry: $line" }
    $relative = $Matches[2].Replace('\', '/')
    if ($relative.StartsWith('/') -or $relative -match '(^|/)\.\.(/|$)' -or $relative.Contains(':')) { throw "Unsafe manifest path: $relative" }
    if ($manifestEntries.ContainsKey($relative)) { throw "Duplicate manifest path: $relative" }
    $manifestEntries[$relative] = $Matches[1].ToLowerInvariant()
}
if ($manifestEntries.Count -eq 0) { throw 'Package manifest is empty.' }
$payloadFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object { $_.FullName -ne $manifestPath })
if ($payloadFiles.Count -ne $manifestEntries.Count) { throw 'Package file count does not match MANIFEST.sha256.' }
foreach ($file in $payloadFiles) {
    $relative = (Get-RelativePath $sourceRoot $file.FullName).Replace('\', '/')
    if (-not $manifestEntries.ContainsKey($relative)) { throw "Unlisted package file: $relative" }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    if ($actualHash -ne $manifestEntries[$relative]) { throw "Package hash mismatch: $relative" }
}
$modular=Test-Path -LiteralPath (Join-Path $sourceRoot 'App/SEEIN.modular')
$roleModules=@{rgb='devices-rgb';capture='capture';efficiency='efficiency';index='efficiency';fps='display'}
function Resolve-PayloadProcess([string]$Root,[string]$Role){
    $module=$roleModules[$Role]
    $folder=if($modular -and $module){Join-Path $Root ('Plugins/'+$module)}else{Join-Path $Root 'App'}
    Join-Path $folder $processCatalog.$Role.path
}
foreach($role in 'main','service','display','pet','capture','efficiency','index','rgb','fps'){
    if($modular -and $roleModules[$role] -and !(Test-Path -LiteralPath (Join-Path $sourceRoot ('Plugins/'+$roleModules[$role])))){continue}
    $entry=$processCatalog.$role;$target=Resolve-PayloadProcess $sourceRoot $role
    if(!(Test-Path -LiteralPath $target -PathType Leaf)){throw "Required SEEIN process is missing: $role"}
    foreach($alias in $entry.legacy){if(Test-Path -LiteralPath (Join-Path (Split-Path $target) $alias)){throw "Legacy executable remains: $alias"}}
}
if($VerifyOnly){Write-Output "Verified SEEIN application manifest and process-name contract ($($payloadFiles.Count) files).";return}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [switch]$OnlyMissing
    )

    $sourcePath = [IO.Path]::GetFullPath($Source)
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($sourcePath)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $relative = (Get-RelativePath $sourcePath $current)
        $targetDirectory = if ($relative -eq '.') { $destinationPath } else { Join-Path $destinationPath $relative }
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $current -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
                continue
            }
            $targetFile = Join-Path $targetDirectory $item.Name
            if ($OnlyMissing -and (Test-Path -LiteralPath $targetFile)) { continue }
            Copy-Item -LiteralPath $item.FullName -Destination $targetFile -Force
        }
    }
}

function Set-TrayUserSid([string]$Sid) {
    $sidPath = Join-Path $dataRoot 'tray-user.sid'
    $sidTemp = $sidPath + '.tmp'
    Set-Content -LiteralPath $sidTemp -Value $Sid -Encoding ascii
    Move-Item -LiteralPath $sidTemp -Destination $sidPath -Force
    $sidAcl = New-Object System.Security.AccessControl.FileSecurity
    $sidAcl.SetAccessRuleProtection($true, $false)
    $sidAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('SYSTEM', 'FullControl', 'Allow')))
    $sidAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('Administrators', 'FullControl', 'Allow')))
    Set-Acl -LiteralPath $sidPath -AclObject $sidAcl
}

function Move-InstallDirectory([string]$Source, [string]$Destination) {
    # All switch paths are siblings on the install volume. Keep a locked tree intact.
    for ($attempt = 0; ; $attempt++) {
        try { [IO.Directory]::Move($Source, $Destination); return }
        catch [IO.IOException] {
            if ($attempt -ge 19 -or (Test-Path -LiteralPath $Destination)) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}

function Preserve-ReplacedProgram([string]$Source, [string]$OriginalRoot = $Source) {
    $item=Get-Item -LiteralPath $Source -Force
    if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
       (!$item.FullName.StartsWith($installPrefix,[StringComparison]::OrdinalIgnoreCase) -and
        !$item.FullName.StartsWith($programFilesPrefix,[StringComparison]::OrdinalIgnoreCase))){throw 'Unsafe replaced program path.'}
    $desktop=[Environment]::GetFolderPath('DesktopDirectory')
    $destination=Join-Path $desktop ('SEEIN-待删除-已替换程序-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $destination|Out-Null
    $manifest=@(Get-ChildItem -LiteralPath $item.FullName -Recurse -File|ForEach-Object{
        @{path=(Get-RelativePath $item.FullName $_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
    })
    # A desktop on another drive needs PowerShell's cross-volume move.
    Move-Item -LiteralPath $item.FullName -Destination (Join-Path $destination 'Program')
    @{originalRoot=$OriginalRoot;files=$manifest}|ConvertTo-Json -Depth 5|
        Set-Content -LiteralPath (Join-Path $destination '原路径与校验.json') -Encoding utf8
    Write-Host "Replaced program preserved for manual deletion: $destination"
}

# Only files that can change while the new service starts need a cold snapshot.
# These are all immediate children of the service data directory.
function Get-MutableStateFiles([string]$Root) {
    if (Test-Path -LiteralPath $Root -PathType Container) {
        Get-ChildItem -LiteralPath $Root -File -Force | Where-Object {
            $_.Name -match '^(settings\.json|categories\.json|migration\.json|modules\.json|tray-user\.sid|(?:app-usage|energy(?:-v2)?)\.db(?:-wal|-shm|-journal)?)$'
        }
    }
}

# The SID string must be wrapped in a SecurityIdentifier: FileSystemAccessRule treats a plain
# string as an account name and fails to translate "S-1-5-..." identities.
$trayUserSidValue = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$trayUserSid = New-Object System.Security.Principal.SecurityIdentifier($trayUserSidValue)

$legacyData = [IO.Path]::GetFullPath((Join-Path $programDataRoot 'JonsboPanel'))
$legacyService = Get-Service -Name 'JonsboPanelService' -ErrorAction SilentlyContinue
$seeinService = Get-Service -Name 'SeeinService' -ErrorAction SilentlyContinue
$legacyWasRunning = [bool]($legacyService -and $legacyService.Status -ne 'Stopped')
$seeinWasRunning = [bool]($seeinService -and $seeinService.Status -ne 'Stopped')
$legacyShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\JonsboPanel.lnk'
$legacyUninstall = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\JonsboPanel'
$migrationPath = Join-Path $dataRoot 'migration.json'
$originalRoot = Join-Path ${env:ProgramFiles(x86)} 'JONSBO-AIO'
$originalShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\JONSBO-AIO.lnk'
$originalUninstallKey = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\JONSBO-AIO'

$incomingRoot = Join-Path $dataRoot 'incoming'
$startMenuRoot = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startMenuShortcut = Join-Path $startMenuRoot 'Seein.lnk'
$trayTask = Get-ScheduledTask -TaskPath '\' -TaskName 'SeeinTray' -ErrorAction SilentlyContinue
$previousTrayTask = if ($trayTask) { Export-ScheduledTask -TaskPath '\' -TaskName 'SeeinTray' }
$previousShortcut = if (Test-Path -LiteralPath $startMenuShortcut) { ,([IO.File]::ReadAllBytes($startMenuShortcut)) }
$previousIncomingAcl = if (Test-Path -LiteralPath $incomingRoot) { Get-Acl -LiteralPath $incomingRoot }
$previousService = Get-CimInstance Win32_Service -Filter "Name='SeeinService'"

$oldTask = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -eq 'JONSBO-AIOPowerBoot' } | Select-Object -First 1
$oldProcess = Get-Process -Name 'JONSBO-AIO' -ErrorAction SilentlyContinue
$oldProcessWasRunning = [bool]$oldProcess
$migration = [ordered]@{
    capturedAt = [DateTimeOffset]::Now.ToString('O')
    oldTaskExisted = [bool]$oldTask
    oldTaskEnabled = [bool]($oldTask -and $oldTask.State -ne 'Disabled')
    tookOverOriginal = $false
    originalRemoved = $false
}

if ($RemoveOriginal) { $TakeOverOriginal = $true }

if (($oldTask -or $oldProcess) -and -not $TakeOverOriginal) {
    Write-Warning 'JONSBO-AIO was found and will be left running with its startup task unchanged. Use -TakeOverOriginal only when takeover is explicitly intended.'
}
$pawnServices = Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*PawnIO*' -or $_.DisplayName -like '*PawnIO*' }
if (-not $pawnServices) {
    Write-Warning 'PawnIO was not detected. Some motherboard and fan sensors may be unavailable. This installer does not install or replace a driver.'
}
$installedTrays = @(@($installRoot) + @($previousInstalls) | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'App\Seein.Tray.exe' })
$runningTrays = @(Get-Process -Name 'Seein.Tray' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $installedTrays -contains $_.Path })
$runningTrayPaths = @($runningTrays | ForEach-Object { $_.Path } | Select-Object -Unique)
$hadInstall = Test-Path -LiteralPath $installRoot
$serviceCreated = $false
$newInstalled = $false
$runtimeTouched = $false
$stateCaptured = $false
$serviceChanged = $false
$taskChanged = $false
$shortcutChanged = $false
$canClean = $false
$previousIndexService=Get-CimInstance Win32_Service -Filter "Name='Everything'" -ErrorAction SilentlyContinue
$previousIndexRegistry=Get-ItemProperty 'HKLM:/SOFTWARE/SEEIN/Dependencies' -ErrorAction SilentlyContinue
$indexManaged=$previousIndexRegistry.EverythingServiceManaged -eq 1 -or
    ($previousIndexService -and $previousIndexService.PathName.StartsWith(('"'+$installRoot+'\App\tools\everything\'),[StringComparison]::OrdinalIgnoreCase))
$indexWasRunning=$previousIndexService -and $previousIndexService.State -eq 'Running'
$indexTouched=$false
$legacyIndexClients=@(Get-CimInstance Win32_Process -Filter "Name='Everything.exe'" | Where-Object {
    $_.ParentProcessId -in @($runningTrays.Id) -and $_.CommandLine -like '*\Seein\Tools\everything\Everything.ini*'
})
# Creating a fixed transaction directory excludes another updater. A failed
# rollback is preserved here and cannot be overwritten by a retry.
New-Item -ItemType Directory -Path $transactionRoot -ErrorAction Stop | Out-Null
try {
    Copy-DirectoryContents -Source (Join-Path $sourceRoot 'App') -Destination (Join-Path $stageRoot 'App')
    if($modular -and (Test-Path -LiteralPath (Join-Path $sourceRoot 'Plugins'))){Copy-DirectoryContents -Source (Join-Path $sourceRoot 'Plugins') -Destination (Join-Path $stageRoot 'Plugins')}
    if($modular -and $hadInstall -and (Get-Content -LiteralPath (Join-Path $sourceRoot 'PROFILE') -Raw).Trim() -eq 'Core'){
        . (Join-Path $sourceRoot 'Preserve-InstalledPlugins.ps1')
        Copy-SeeinInstalledPlugins -InstalledRoot $installRoot -StageRoot $stageRoot
    }
    foreach($folder in @('docs','licenses')){if(Test-Path -LiteralPath (Join-Path $sourceRoot $folder)){Copy-DirectoryContents -Source (Join-Path $sourceRoot $folder) -Destination (Join-Path $stageRoot $folder)}}
    foreach($file in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','PROFILE')){if(Test-Path -LiteralPath (Join-Path $sourceRoot $file)){Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination $stageRoot}}
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'VERSION') -Destination $stageRoot
    Copy-Item -LiteralPath $sourceUninstaller -Destination $stageRoot
    $runtimeTouched = $true

    if ($legacyWasRunning) { Stop-Service -Name 'JonsboPanelService' -Force }
    if ($seeinWasRunning) { Stop-Service -Name 'SeeinService' -Force }
    if($indexManaged -and $indexWasRunning){Stop-Service Everything}
    # Integrated tools can refuse shutdown while recording/exporting. Let the
    # installed shell save and close them before replacing its files.
    $gracefulExit = $null
    try { $gracefulExit = [Threading.EventWaitHandle]::OpenExisting('Local\Seein.Tray.Exit') }
    catch [Threading.WaitHandleCannotBeOpenedException] { }
    if ($gracefulExit -and $runningTrays.Count -gt 0) {
        try { $gracefulExit.Set() | Out-Null }
        finally { $gracefulExit.Dispose() }
        foreach ($tray in $runningTrays) {
            if (!$tray.WaitForExit(30000)) { throw 'SEEIN is still busy. Finish recording or export, then retry installation.' }
        }
    }
    else { $runningTrays | Stop-Process -Force } # Compatibility with older shells.
    Get-Process -Name 'Seein.RgbEngine','seergb' -ErrorAction SilentlyContinue |
        Where-Object {
            $path = $_.Path
            $roots = @($installRoot) + $previousInstalls
            $roots | Where-Object { $path -eq (Join-Path $_ 'App\rgb-engine\Seein.RgbEngine.exe') -or $path -eq (Join-Path (Join-Path $_ 'App') $processCatalog.rgb.path) }
        } | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    $petExecutables = @(@($installRoot) + @($previousInstalls) | Where-Object { $_ } | ForEach-Object { Join-Path $_ 'App\dsh-pet\dsh-pet.exe' })
    Get-Process -Name 'dsh-pet' -ErrorAction SilentlyContinue | Where-Object { $petExecutables -contains $_.Path } | ForEach-Object {
        try { $_.Kill() } catch [InvalidOperationException] { if (!$_.HasExited) { throw } }
    }

    New-Item -ItemType Directory -Path $rollbackState | Out-Null
    $transactionAcl = New-Object System.Security.AccessControl.DirectorySecurity
    $transactionAcl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18','S-1-5-32-544') {
        $account = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $transactionAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($account, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $rollbackState -AclObject $transactionAcl
    foreach ($file in Get-MutableStateFiles $dataRoot) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Cannot snapshot a linked data file: $($file.FullName)" }
        Copy-Item -LiteralPath $file.FullName -Destination $rollbackState
    }
    $stateCaptured = $true
    if (Test-Path -LiteralPath $legacyData) {
        Copy-DirectoryContents -Source $legacyData -Destination $dataRoot -OnlyMissing
        Write-Host "Merged missing data files from $legacyData; current SEEIN files were kept."
    }
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $incomingRoot = Join-Path $dataRoot 'incoming'
    New-Item -ItemType Directory -Path $incomingRoot -Force | Out-Null
    $incomingAcl = New-Object System.Security.AccessControl.DirectorySecurity
    $incomingAcl.SetAccessRuleProtection($true, $false)
    $inherit = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $incomingAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($trayUserSid, 'Modify', $inherit, $propagation, 'Allow')))
    $incomingAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('SYSTEM', 'FullControl', $inherit, $propagation, 'Allow')))
    $incomingAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('Administrators', 'FullControl', $inherit, $propagation, 'Allow')))
    Set-Acl -LiteralPath $incomingRoot -AclObject $incomingAcl
    if ($TakeOverOriginal) {
        if ($oldProcess) { $oldProcess | Stop-Process -Force }
        if ($oldTask) { Disable-ScheduledTask -TaskName $oldTask.TaskName -TaskPath $oldTask.TaskPath | Out-Null }
        $migration.tookOverOriginal = $true
    }
    foreach($client in $legacyIndexClients){
        if(Get-Process -Id $client.ProcessId -ErrorAction SilentlyContinue){
            $control=Start-Process -FilePath $client.ExecutablePath -ArgumentList '-exit' -WindowStyle Hidden -PassThru
            if(!$control.WaitForExit(10000)){throw 'Legacy SEEIN file index did not close.'}
            $live=Get-Process -Id $client.ProcessId -ErrorAction SilentlyContinue
            if($live -and !$live.WaitForExit(10000)){throw 'Legacy SEEIN file index is still saving data.'}
        }
    }
    if ($hadInstall) { Move-InstallDirectory $installRoot $rollbackProgramRoot }
    Move-InstallDirectory $stageRoot $installRoot
    $newInstalled = $true
    if($modular){
        $pool=Start-Process -FilePath (Join-Path $installRoot 'App/Seein.Tray.exe') -ArgumentList '--module-pool' -WindowStyle Hidden -PassThru
        if(!$pool.WaitForExit(30000) -or $pool.ExitCode -ne 0){throw 'Shared module dependency consolidation failed.'}
    }
    Set-TrayUserSid $trayUserSid

    $serviceExe = Join-Path (Join-Path $installRoot 'App') $processCatalog.service.path
    if ($previousService) {
        & sc.exe config SeeinService 'binPath=' ('"' + $serviceExe + '"') 'start=' 'auto' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to update Windows service configuration.' }
        $serviceChanged = $true
    }
    else {
        & sc.exe create SeeinService 'binPath=' ('"' + $serviceExe + '"') 'start=' 'auto' 'DisplayName=' 'Seein Display Service' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to create Windows service.' }
        $serviceCreated = $true
    }
    $everythingInstaller=Join-Path $installRoot $(if($modular){'Plugins/efficiency/tools/everything/Install-Dependency.ps1'}else{'App/tools/everything/Install-Dependency.ps1'})
    if($indexManaged -and $previousIndexRegistry.EverythingExecutable -and
       $previousIndexRegistry.EverythingExecutable -ne (Resolve-PayloadProcess $installRoot 'index')){
        New-ItemProperty 'HKLM:/SOFTWARE/SEEIN/Dependencies' -Name LegacyEverythingExecutable -Value $previousIndexRegistry.EverythingExecutable -PropertyType String -Force|Out-Null
    }
    if(Test-Path -LiteralPath $everythingInstaller){$indexTouched=$true; & $everythingInstaller -InstallParent $installParent}
    Start-Service -Name 'SeeinService'
    if ((Get-Service -Name 'SeeinService').Status -ne 'Running') { throw 'Seein service did not start.' }

    $trayExe = Join-Path $installRoot 'App\Seein.Tray.exe'
    $taskAction = New-ScheduledTaskAction -Execute $trayExe -Argument '--background'
    $taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
    $taskSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    $taskChanged = $true
    Register-ScheduledTask -TaskName 'SeeinTray' -Action $taskAction -Trigger $taskTrigger -Principal $taskPrincipal -Settings $taskSettings -Description 'Starts the Seein tray manager at logon.' -Force | Out-Null

    New-Item -ItemType Directory -Path $startMenuRoot -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startMenuShortcut)
    $shortcut.TargetPath = $trayExe
    $shortcut.WorkingDirectory = Split-Path -Parent $trayExe
    $shortcut.IconLocation = "$trayExe,0"
    $shortcut.Description = 'Seein secondary-display manager'
    $shortcutChanged = $true
    $shortcut.Save()
    $canClean = $true
}
catch {
    if (-not $runtimeTouched) { $canClean = $true; throw }
    if($indexManaged -or ($indexTouched -and !$previousIndexService)){
        $indexService=Get-Service Everything -ErrorAction SilentlyContinue
        if($indexService -and $indexService.Status -ne 'Stopped'){Stop-Service Everything}
        if(!$previousIndexService -and $indexService){ & sc.exe delete Everything | Out-Null }
    }
    $rollbackService = Get-Service -Name 'SeeinService' -ErrorAction SilentlyContinue
    if ($rollbackService -and $rollbackService.Status -ne 'Stopped') { Stop-Service -Name 'SeeinService' -Force }
    if ($serviceCreated) { & sc.exe delete SeeinService | Out-Null }
    if ($newInstalled) { Move-InstallDirectory $installRoot $stageRoot }
    if (Test-Path -LiteralPath $rollbackProgramRoot) { Move-InstallDirectory $rollbackProgramRoot $installRoot }
    if ($stateCaptured) {
        foreach ($file in Get-MutableStateFiles $dataRoot) {
            if (!(Test-Path -LiteralPath (Join-Path $rollbackState $file.Name))) { Remove-Item -LiteralPath $file.FullName -Force }
        }
        foreach ($file in Get-MutableStateFiles $rollbackState) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $dataRoot $file.Name) -Force
        }
        if ($previousIncomingAcl) { Set-Acl -LiteralPath $incomingRoot -AclObject $previousIncomingAcl }
    }
    if ($serviceChanged) {
        $startMode = switch ($previousService.StartMode) {
            'Auto' { if ($previousService.DelayedAutoStart) { 'delayed-auto' } else { 'auto' } }
            'Disabled' { 'disabled' }
            default { 'demand' }
        }
        & sc.exe config SeeinService 'binPath=' $previousService.PathName 'start=' $startMode | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Unable to restore Windows service configuration.' }
    }
    if ($taskChanged) {
        if ($previousTrayTask) { Register-ScheduledTask -TaskName 'SeeinTray' -Xml $previousTrayTask -Force | Out-Null }
        else { Unregister-ScheduledTask -TaskName 'SeeinTray' -Confirm:$false -ErrorAction SilentlyContinue }
    }
    if ($shortcutChanged) {
        if ($null -ne $previousShortcut) { [IO.File]::WriteAllBytes($startMenuShortcut, $previousShortcut) }
        elseif (Test-Path -LiteralPath $startMenuShortcut) { Remove-Item -LiteralPath $startMenuShortcut -Force }
    }
    if($indexManaged -and $previousIndexService){
        & sc.exe config Everything 'binPath=' $previousIndexService.PathName | Out-Null
        if($indexWasRunning){Start-Service Everything}
    }
    if($indexTouched){
        foreach($name in 'EverythingExecutable','EverythingServiceManaged','LegacyEverythingExecutable'){
            $previous=if($previousIndexRegistry){$previousIndexRegistry.PSObject.Properties[$name]}else{$null}
            if($null -ne $previous){
                $type=if($name -eq 'EverythingServiceManaged'){'DWord'}else{'String'}
                New-ItemProperty 'HKLM:/SOFTWARE/SEEIN/Dependencies' -Name $name -Value $previous.Value -PropertyType $type -Force|Out-Null
            }else{
                Remove-ItemProperty 'HKLM:/SOFTWARE/SEEIN/Dependencies' -Name $name -ErrorAction SilentlyContinue
            }
        }
    }
    if ($seeinWasRunning) { Start-Service -Name 'SeeinService' }
    if ($legacyWasRunning) { Start-Service -Name 'JonsboPanelService' }
    foreach ($trayPath in $runningTrayPaths) {
        if (Test-Path -LiteralPath $trayPath -PathType Leaf) { Start-Process -FilePath $trayPath -ArgumentList '--background' -WindowStyle Hidden }
    }
    if ($TakeOverOriginal -and $oldTask -and $migration.oldTaskEnabled) {
        Enable-ScheduledTask -TaskName $oldTask.TaskName -TaskPath $oldTask.TaskPath -ErrorAction SilentlyContinue | Out-Null
    }
    if ($oldProcessWasRunning -and $oldProcess.Path) { Start-Process -FilePath $oldProcess.Path -ErrorAction SilentlyContinue }
    $canClean = $true
    throw
}
finally {
    if ($canClean) {
        $owned = Get-Item -LiteralPath $transactionRoot -Force
        if ($owned.Parent.FullName -ine $installParent -or $owned.Name -ne '.Seein-update' -or ($owned.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Unexpected update transaction path.'
        }
        if(Test-Path -LiteralPath $rollbackProgramRoot){Preserve-ReplacedProgram $rollbackProgramRoot $installRoot}
        Remove-Item -LiteralPath $owned.FullName -Recurse -Force
    } else { Write-Warning "Automatic recovery is incomplete. The rollback files are at $transactionRoot" }
}

& sc.exe description SeeinService 'Renders telemetry themes and sends JPEG frames to the SEEIN 33C3:F101 panel.' | Out-Null
& sc.exe failure SeeinService reset= 86400 actions= restart/1000/restart/5000/restart/10000 | Out-Null
if ($legacyService) { & sc.exe delete JonsboPanelService | Out-Null }
Unregister-ScheduledTask -TaskName 'JonsboPanelTray' -Confirm:$false -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $legacyShortcut) { Remove-Item -LiteralPath $legacyShortcut -Force -ErrorAction SilentlyContinue }
if (Test-Path -LiteralPath $legacyUninstall) { Remove-Item -LiteralPath $legacyUninstall -Recurse -Force -ErrorAction SilentlyContinue }
foreach ($previous in $previousInstalls) {
    if (-not (Test-Path -LiteralPath $previous)) { continue }
    Preserve-ReplacedProgram $previous
}

if ($RemoveOriginal) {
    Get-Process -Name 'JONSBO-AIO','JONSBO-AIOPowerBoot' -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($oldTask) { Unregister-ScheduledTask -TaskName $oldTask.TaskName -TaskPath $oldTask.TaskPath -Confirm:$false }
    if (Test-Path -LiteralPath $originalShortcut) { Remove-Item -LiteralPath $originalShortcut -Force }
    if (Test-Path -LiteralPath $originalRoot) { Remove-Item -LiteralPath $originalRoot -Recurse -Force }
    if (Test-Path -LiteralPath $originalUninstallKey) { Remove-Item -LiteralPath $originalUninstallKey -Recurse -Force }
    $migration.originalRemoved = $true
}
$migration | ConvertTo-Json | Set-Content -LiteralPath $migrationPath -Encoding utf8
# Background installation keeps the user's foreground application undisturbed.
if ($StartInBackground) { Start-Process -FilePath $trayExe -ArgumentList '--background' -WindowStyle Hidden }
else { Start-Process -FilePath $trayExe }
# Windows caches shell icons; refresh so the Start menu and taskbar pick up the new artwork.
if (-not $StartInBackground) {
    try { Start-Process -FilePath 'ie4uinit.exe' -ArgumentList '-show' -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue } catch { }
    Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer') -Filter 'iconcache_*.db' -ErrorAction SilentlyContinue |
        ForEach-Object { try { Remove-Item $_.FullName -Force -ErrorAction Stop } catch { } }
}
Write-Host "Seein was installed at $installRoot"
if (($oldTask -or $oldProcess) -and -not $TakeOverOriginal) { Write-Warning 'JONSBO-AIO was detected and left enabled; it may continue to hold the panel serial port.' }

