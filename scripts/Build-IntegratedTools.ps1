[CmdletBinding()]
param([string]$Python = $env:SEEIN_PYTHON, [switch]$SkipDependencies, [switch]$SkipPane)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($projectRoot -match '[^\x00-\x7F]') { throw 'Native GN tools require an ASCII path. Use an ASCII checkout path, for example C:/src/SEEIN.' }
Set-Location -LiteralPath $projectRoot
. (Join-Path $PSScriptRoot 'BuildEnvironment.ps1')
if(!$Python){$Python=Get-SeeinPython}
. (Join-Path $PSScriptRoot 'Enter-ToolBuildEnvironment.ps1')
function Run-Native([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}
$lock = Get-Content vendor/tools/sources.lock.json -Raw | ConvertFrom-Json
foreach ($module in 'snow','pane') {
    $entry = $lock.$module
    $source = Join-Path $projectRoot ('.build/deps/' + $entry.directory)
    if (!(Test-Path -LiteralPath (Join-Path $source '.git'))) {
        Run-Native git @('clone','--filter=blob:none','--no-checkout',$entry.repository,$source)
        Run-Native git @('-C',$source,'checkout','--detach',$entry.commit)
    }
    $revision = & git -C $source rev-parse HEAD
    if ($revision.Trim() -ne $entry.commit) { throw "Unexpected $module source revision." }
    $patch = Join-Path $projectRoot "vendor/tools/$module.patch"
    & git -C $source apply --check $patch 2>$null
    if ($LASTEXITCODE -eq 0) { Run-Native git @('-C',$source,'apply',$patch) }
    else {
        & git -C $source apply --reverse --check $patch 2>$null
        if ($LASTEXITCODE -ne 0) { throw "The $module source differs from its pinned patch. Preserve local edits and reconcile it first." }
    }
}
Run-Native $Python @('vendor/tools/apply-integration.py')
$snow = Join-Path $projectRoot '.build/deps/snow-shot-1.6.3'
Run-Native git @('-C',$snow,'submodule','update','--init','--recursive')
$pane = Join-Path $projectRoot '.build/deps/Pane-0.1.606'
if (!$SkipPane) {
    Set-Content -LiteralPath (Join-Path $pane 'global.json') -Value '{"sdk":{"version":"10.0.404","rollForward":"latestPatch"}}' -Encoding utf8
    Push-Location -LiteralPath $pane
    try { Run-Native (Get-SeeinDotnet -Major 10) @('publish','src/Pane.App/Pane.App.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:Version=0.1.606','-p:PublishAot=false','-p:DebugType=None','-p:DebugSymbols=false','-p:NuGetAudit=false','-o',(Join-Path $projectRoot '.build/tool-payload/pane')) }
    finally { Pop-Location }
}
$env:VCPKG_ROOT = Join-Path $projectRoot '.build/tools/vcpkg'
$env:VCPKG_MAX_CONCURRENCY = '2'
$env:CARGO_BUILD_JOBS = '2'
$triplets = Join-Path $projectRoot '.build/tools/vcpkg-triplets'
New-Item -ItemType Directory -Path $triplets -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor/tools/portable-msvc.cmake'),(Join-Path $projectRoot 'vendor/tools/seein-x64-windows.cmake') -Destination $triplets
$installed = Join-Path $snow 'vcpkg_installed'
if (!$SkipDependencies) {
    Run-Native (Join-Path $env:VCPKG_ROOT 'vcpkg.exe') @('install',"--x-manifest-root=$snow","--overlay-triplets=$triplets","--overlay-ports=$snow/cmake/vcpkg-overlay-ports",'--triplet=seein-x64-windows','--host-triplet=seein-x64-windows','--x-feature=snow-shot',"--x-install-root=$installed")
}
$build = Join-Path $projectRoot '.build/tools-snow-build'
$env:CC='cl.exe'; $env:CXX='cl.exe'
New-Item -ItemType Directory -Path (Join-Path $build 'prebuilt') -Force | Out-Null
$ocrAssets = Join-Path $projectRoot '.build/tool-payload/snow/assets/ocr'
Run-Native cmake @("-DSNOW_OCR_ASSET_MANIFEST=$snow/snow_shot/packaging/snow-shot-ocr-asset-manifest.json", "-DSNOW_OCR_ARTIFACTS_DIR=$snow/artifacts", "-DSNOW_OCR_DESTINATION=$ocrAssets", '-DSNOW_OCR_PLATFORM=windows-x64', '-P', "$snow/cmake/StageSnowShotOcrAssets.cmake")
$ocrRuntime = Join-Path $build 'prebuilt/snow-ocr-process.exe'
$officialOcr = @(Get-ChildItem -LiteralPath (Join-Path $ocrAssets 'runtimes') -Filter '*.exe' -Recurse)
if ($officialOcr.Count -ne 1) { throw 'Expected one verified official OCR runtime.' }
Copy-Item -LiteralPath $officialOcr[0].FullName -Destination $ocrRuntime -Force
$env:LIBCLANG_PATH = Join-Path $env:VCPKG_ROOT 'downloads/tools/clang/clang-15.0.6/bin'
$env:PATH = $env:LIBCLANG_PATH + ';' + $env:PATH
Run-Native cmake @('-S',$snow,'-B',$build,'-G','Ninja','-DCMAKE_BUILD_TYPE=Release',"-DCMAKE_TOOLCHAIN_FILE=$env:VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake",'-DVCPKG_TARGET_TRIPLET=seein-x64-windows','-DVCPKG_HOST_TRIPLET=seein-x64-windows',"-DVCPKG_INSTALLED_DIR=$installed",'-DVCPKG_MANIFEST_INSTALL=OFF',"-DQt6_DIR=$projectRoot/.build/tools/qt/kit/lib/cmake/Qt6", "-DSNOW_LIBCLANG_BIN_DIR=$env:LIBCLANG_PATH", "-DPython3_EXECUTABLE=$Python",'-DSNOW_IMAGE_PROFILE=redistributable','-DSNOW_APPS_ENABLE_RELEASE_OPTIMIZATION=OFF','-DSNOW_APPS_BUILD_TESTS=OFF','-DSNOW_APPS_BUILD_IMAGE_VIEWER=OFF','-DSNOW_APPS_BUILD_SNOW_SHOT_MINI=OFF','-DSNOW_SHOT_ENABLE_MCP=OFF','-DSNOW_APPS_STRICT_COMPILE=OFF','-DSNOW_SHOT_ENABLE_STRICT_COMPILE=OFF')
Run-Native cmake @('-S',$snow,'-B',$build,"-DSEEIN_OCR_RUNTIME=$ocrRuntime")
Run-Native cmake @('--build',$build,'--target','snow_shot','--parallel','2')
Write-Host "Native tools built. Stage the executable with Qt deployment dependencies and the pinned OCR asset manifest before packaging."
