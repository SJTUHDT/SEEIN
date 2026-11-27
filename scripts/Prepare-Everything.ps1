[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'ProcessNames.ps1')
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version='1.4.1.1032'
$name="Everything-$version.x64.zip"
$hash='698df475ec44e638f66f1b6a32d28fea613cec78d3b6310e6abe53431eeb940c'
$cache=Join-Path $root ".build/deps/Everything-$version"
New-Item -ItemType Directory -Path $cache -Force|Out-Null
$archive=Join-Path $cache $name
if(!(Test-Path -LiteralPath $archive)){Invoke-WebRequest "https://www.voidtools.com/$name" -OutFile $archive}
if((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $hash){throw 'Everything archive hash mismatch.'}
$expanded=Join-Path $cache 'portable'
Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
$binary=Join-Path $expanded 'Everything.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $binary
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=voidtools'){throw 'Everything signature verification failed.'}
$target=Join-Path $Destination 'tools/everything'
New-Item -ItemType Directory -Path $target -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $root 'config/process-names.json') -Destination (Join-Path $Destination 'process-names.json') -Force
Copy-Item -LiteralPath $binary -Destination (Join-Path $target (Get-SeeinProcessName 'index')) -Force
Copy-Item -LiteralPath (Join-Path $expanded 'Everything.lng') -Destination $target -Force
$license=Join-Path $root 'vendor/tools/everything-LICENSE.txt'
Copy-Item -LiteralPath $license -Destination (Join-Path $target 'License.txt') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-EverythingDependency.ps1') -Destination (Join-Path $target 'Install-Dependency.ps1') -Force
@{Version=$version;Url="https://www.voidtools.com/$name";SHA256=$hash}|ConvertTo-Json|Set-Content (Join-Path $target 'origin.json') -Encoding utf8
