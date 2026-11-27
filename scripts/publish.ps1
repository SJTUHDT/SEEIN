[CmdletBinding()]
param([ValidateSet('Core','PublicFull')][string]$Profile='PublicFull',[string]$Version='2.1.0',[switch]$Force)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Publish-Modular.ps1') -Profile $Profile -Version $Version -Force:$Force
if($LASTEXITCODE){throw 'Public publishing failed'}
