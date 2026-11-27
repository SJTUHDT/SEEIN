#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$PayloadDirectory=$PSScriptRoot,[string]$InstallParent=$env:ProgramFiles)
$ErrorActionPreference='Stop'
$appRoot=[IO.Path]::GetFullPath((Join-Path $PayloadDirectory '../..'))
$names=Get-Content -LiteralPath (Join-Path $appRoot 'process-names.json') -Raw|ConvertFrom-Json
$provider=[IO.Path]::GetFullPath((Join-Path $appRoot $names.index.path))
$signature=Get-AuthenticodeSignature -LiteralPath $provider
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=voidtools'){throw 'SEEIN file index does not have a valid voidtools signature.'}
$key='HKLM:/SOFTWARE/SEEIN/Dependencies'
$managed=(Get-ItemProperty -LiteralPath $key -Name EverythingServiceManaged -ErrorAction SilentlyContinue).EverythingServiceManaged -eq 1
$service=Get-Service Everything -ErrorAction SilentlyContinue
if($service){
    $image=(Get-CimInstance Win32_Service -Filter "Name='Everything'").PathName
    $managed=$managed -or $image.StartsWith(('"'+$provider+'"'),[StringComparison]::OrdinalIgnoreCase)
    if($managed){
        if($service.Status -ne 'Stopped'){Stop-Service Everything}
        & sc.exe config Everything 'binPath=' ('"'+$provider+'" -svc') 'start=' 'auto' 'DisplayName=' 'SEEIN File Index' | Out-Null
        if($LASTEXITCODE -ne 0){throw 'Could not migrate the SEEIN-owned index service.'}
    }
    # A separately installed Everything service is reused without taking ownership.
} else {
    $process=Start-Process -FilePath $provider -ArgumentList '-install-service' -WindowStyle Hidden -PassThru -Wait
    if($process.ExitCode -ne 0 -or !(Get-Service Everything -ErrorAction SilentlyContinue)){throw 'SEEIN index service installation failed.'}
    $managed=$true
}
if($managed){Set-Service Everything -StartupType Automatic -DisplayName 'SEEIN File Index'}
Start-Service Everything
(Get-Service Everything).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
New-Item -Path $key -Force|Out-Null
New-ItemProperty -Path $key -Name EverythingExecutable -Value $provider -PropertyType String -Force|Out-Null
New-ItemProperty -Path $key -Name EverythingServiceManaged -Value ([int]$managed) -PropertyType DWord -Force|Out-Null
Write-Output "SEEIN file index is ready: $provider (managed service: $managed)"
