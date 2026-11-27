[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'BuildEnvironment.ps1')
$dotnet = Get-SeeinDotnet

& $dotnet restore (Join-Path $projectRoot 'Seein.sln')
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
& $dotnet build (Join-Path $projectRoot 'Seein.sln') -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $dotnet test (Join-Path $projectRoot 'tests\Seein.Tests\Seein.Tests.csproj') -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }

