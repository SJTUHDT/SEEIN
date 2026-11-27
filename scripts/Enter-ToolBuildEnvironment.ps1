$ErrorActionPreference = 'Stop'
$toolRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../.build/tools'))
$msvc = Join-Path $toolRoot 'msvc/kit/Contents/VC/Tools/MSVC/14.51.36231'
$sdk = Join-Path $toolRoot 'windows-sdk/microsoft.windows.sdk.cpp/c'
$sdkLib = Join-Path $toolRoot 'windows-sdk/microsoft.windows.sdk.cpp.x64/c'
$sdkVersion = '10.0.28000.0'
if(!(Test-Path -LiteralPath (Join-Path $msvc 'bin/Hostx64/x64/cl.exe')) -and $env:VCToolsInstallDir -and (Get-Command cl.exe -ErrorAction SilentlyContinue)){
    $env:SEEIN_PORTABLE_MSVC='1'
    return
}
if (!(Test-Path -LiteralPath (Join-Path $msvc 'bin/Hostx64/x64/cl.exe'))) { throw 'Portable MSVC is not prepared.' }
$env:PATH = (Join-Path $msvc 'bin/Hostx64/x64') + ';' + (Join-Path $sdk "bin/$sdkVersion/x64") + ';' + (Join-Path $toolRoot 'python-build/cmake/data/bin') + ';' + (Join-Path $toolRoot 'python-build/bin') + ';' + $env:PATH
$env:PATH = (Join-Path $toolRoot 'msvc/kit/Contents/VC/Redist/MSVC/14.51.36231/debug_nonredist/x64/Microsoft.VC145.DebugCRT') + ';' + (Join-Path $sdk "bin/$sdkVersion/x64/ucrt") + ';' + $env:PATH
$env:INCLUDE = (Join-Path $msvc 'include') + ';' + ((@('ucrt','shared','um','winrt','cppwinrt') | ForEach-Object { Join-Path $sdk "Include/$sdkVersion/$_" }) -join ';')
$env:LIB = (Join-Path $msvc 'lib/x64') + ';' + (Join-Path $msvc 'lib/onecore/x64') + ';' + (Join-Path $sdkLib 'ucrt/x64') + ';' + (Join-Path $sdkLib 'um/x64')
$env:VCToolsInstallDir = $msvc + '\'
$env:WindowsSdkDir = $sdk + '\'
$env:WindowsSDKVersion = $sdkVersion + '\'
$env:SEEIN_PORTABLE_MSVC = '1'
$env:_LINK_ = ''
$env:CARGO_HOME = Join-Path $toolRoot 'rust/cargo'
$env:RUSTUP_HOME = Join-Path $toolRoot 'rust/rustup'
$env:PATH = (Join-Path $env:CARGO_HOME 'bin') + ';' + $env:PATH
$env:PYTHONPATH = Join-Path $toolRoot 'python-build'
$gitBin = Join-Path $toolRoot 'git/cmd'
if (Test-Path -LiteralPath (Join-Path $gitBin 'git.exe')) { $env:PATH = $gitBin + ';' + $env:PATH }
$pythonBin = if($env:SEEIN_PYTHON){Split-Path $env:SEEIN_PYTHON}else{''}
if (Test-Path -LiteralPath (Join-Path $pythonBin 'python.exe')) { $env:PATH = $pythonBin + ';' + $env:PATH }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Remove-Item Env:CL -ErrorAction SilentlyContinue
