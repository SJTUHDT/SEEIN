[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Fetch([string]$Url,[string]$Path,[string]$Sha256){
    New-Item -ItemType Directory -Path (Split-Path $Path) -Force|Out-Null
    if(!(Test-Path -LiteralPath $Path)){Invoke-WebRequest -Uri $Url -OutFile $Path}
    if((Get-FileHash -LiteralPath $Path).Hash -ne $Sha256){throw "Dependency checksum mismatch: $Path"}
}
$rgb=Join-Path $root '.build/deps/OpenRGB-8e16de27858cf2be6af54a33ce003df74cb9fd3e'
$archive=Join-Path $rgb 'OpenRGB_1.1_Windows_64_8e16de2.zip'
# The extracted executable is pinned too; prepare from the official archive when absent.
$engine=Join-Path $root '.build/runtime/openrgb'
if(!(Test-Path -LiteralPath (Join-Path $engine 'Seein.RgbEngine.exe'))){
    New-Item -ItemType Directory -Path $rgb,$engine -Force|Out-Null
    Fetch 'https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.1/OpenRGB_1.1_Windows_64_8e16de2.zip' $archive '63291726C6FC84EB479EF2EE752E97E1187F6CE9C49FD27EEE40DED588A8172A'
    $unpack=Join-Path $rgb 'public-portable';Expand-Archive -LiteralPath $archive -DestinationPath $unpack -Force
    $binary=@(Get-ChildItem -LiteralPath $unpack -Recurse -File -Filter 'OpenRGB.exe')
    if($binary.Count -ne 1 -or (Get-FileHash -LiteralPath $binary[0].FullName).Hash -ne 'E26AE71162795A8ABCFB90C05C1572A00BA06CA63B886ACA57392A0138963B1E'){throw 'OpenRGB executable checksum mismatch'}
    Get-ChildItem -LiteralPath $binary[0].DirectoryName|Copy-Item -Destination $engine -Recurse -Force
    Move-Item -LiteralPath (Join-Path $engine 'OpenRGB.exe') -Destination (Join-Path $engine 'Seein.RgbEngine.exe')
}
$fps=Join-Path $root '.build/deps/PresentMon-2.6.0/PresentMon.exe'
Fetch 'https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe' $fps 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'
Fetch 'https://raw.githubusercontent.com/GameTechDev/PresentMon/v2.6.0/LICENSE.txt' (Join-Path (Split-Path $fps) 'LICENSE.txt') '4C949341B1893C8C6AD82F7FB4EEDF622CD1FD9C22A9AF8F19B2DAC19D1947B6'
Fetch 'https://codeload.github.com/CalcProgrammer1/OpenRGB/zip/8e16de27858cf2be6af54a33ce003df74cb9fd3e' (Join-Path $rgb 'OpenRGB-1.1-source.zip') '064FD11D18FF0CC854C2DA0F0B8EE703B4A053089B0CC0CF2E8B925DDE7276D8'
foreach($module in @('snow','pane')){if(!(Test-Path -LiteralPath (Join-Path $root ('.build/tool-payload/'+$module+'/RUNTIME.sha256')))){throw "Missing verified $module payload. Run scripts/Build-IntegratedTools.ps1 and scripts/Stage-IntegratedTools.ps1 first; see docs/BUILDING.md."}}
