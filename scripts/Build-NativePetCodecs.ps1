[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cache = Join-Path $project '.build\deps\pet-ffmpeg-minimal'
$tools = Join-Path $project '.build\tools'
$bash = Join-Path $tools 'msys64\usr\bin\bash.exe'
$commit = 'a65b3bfe9dacc3b20597ef199d0afdd8bc8128e2'
$sourceHash = 'B26FF29BD0B0315B74C8D998C7293CECB74A27ACDD6D009CB993D976514F77BD'
New-Item -ItemType Directory -Path $cache, $tools -Force | Out-Null
$archive = Join-Path $cache 'FFmpeg-n8.1-source.tar.gz'
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest "https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/$commit" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive).Hash -ne $sourceHash) { throw 'FFmpeg source integrity verification failed.' }
$source = Join-Path $cache "FFmpeg-$commit"
if (!(Test-Path -LiteralPath (Join-Path $source 'configure'))) {
    & tar -xf $archive -C $cache
    if ($LASTEXITCODE) { throw 'Could not extract FFmpeg sources.' }
}
if (!(Test-Path -LiteralPath $bash)) {
    $metadata = Invoke-RestMethod 'https://api.github.com/repos/msys2/msys2-installer/releases/latest'
    $asset = $metadata.assets | Where-Object name -eq 'msys2-base-x86_64-latest.tar.xz'
    $toolArchive = Join-Path $tools 'msys2-base.tar.xz'
    Invoke-WebRequest $asset.browser_download_url -OutFile $toolArchive
    if ($asset.digest -ne ('sha256:' + (Get-FileHash $toolArchive).Hash.ToLowerInvariant())) { throw 'MSYS2 integrity verification failed.' }
    & tar -xf $toolArchive -C $tools
    if ($LASTEXITCODE) { throw 'Could not extract the build toolchain.' }
}
$drive = @('S', 'T', 'U', 'V') | Where-Object { !(Test-Path ($_ + ':\')) } | Select-Object -First 1
if (!$drive) { throw 'No free temporary build drive is available.' }
$previousSystem = $env:MSYSTEM
$mapped = $false
try {
    & subst.exe ($drive + ':') $project
    if ($LASTEXITCODE) { throw 'Could not map the Unicode project path for the native linker.' }
    $mapped = $true
    $env:MSYSTEM = 'MINGW64'
    $mappedBash = $drive + ':\.build\tools\msys64\usr\bin\bash.exe'
    & $mappedBash -lc 'pacman -Sy --noconfirm --needed mingw-w64-x86_64-gcc mingw-w64-x86_64-libvpx mingw-w64-x86_64-nasm mingw-w64-x86_64-pkgconf make diffutils' *> (Join-Path $cache 'toolchain.log')
    if ($LASTEXITCODE) { throw 'Could not prepare the native build toolchain.' }
    $vpxVersion = (& $mappedBash -lc 'pacman -Q mingw-w64-x86_64-libvpx').Trim()
    if ($vpxVersion -ne 'mingw-w64-x86_64-libvpx 1.17.0-1') {
        throw 'The libvpx build input changed. Restore pinned 1.17.0-1 or update the corresponding source, licenses and validation together.'
    }
    $version = (& $mappedBash -lc 'gcc -dumpfullversion').Trim()
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Unexpected compiler version.' }
    $prefix = $drive + ':/.build/tools/msys64/mingw64'
    $sourcePath = '/' + $drive.ToLowerInvariant() + '/.build/deps/pet-ffmpeg-minimal/FFmpeg-' + $commit
    $configure = './configure --prefix=../runtime --target-os=mingw32 --arch=x86_64 --cc=gcc --enable-shared --disable-static --disable-programs --disable-doc --disable-debug --disable-autodetect --disable-everything --enable-libvpx --enable-decoder=libvpx_vp8,libvpx_vp9,png,mjpeg,gif --enable-demuxer=matroska,mov,gif,image2 --enable-protocol=file --enable-swscale --disable-avdevice --disable-avfilter --disable-swresample --disable-network --disable-iconv --pkg-config-flags=--static'
    $linker = "$prefix/bin/gcc.exe -B$prefix/lib/gcc/x86_64-w64-mingw32/$version/ -B$prefix/lib/"
    $flags = "-L./libavutil -L./libavcodec -L./libavformat -L./libswscale -L$prefix/lib -L$prefix/x86_64-w64-mingw32/lib -L$prefix/lib/gcc/x86_64-w64-mingw32/$version -Wl,--enable-auto-image-base -static-libgcc"
    $command = "cd '$sourcePath' && $configure && make -j8 LD='$linker' LDFLAGS='$flags' && make install"
    & $mappedBash -lc $command *> (Join-Path $cache 'build.log')
    if ($LASTEXITCODE) { throw "Native codec build failed. See $cache\build.log" }
    & $mappedBash -lc 'pacman -Q mingw-w64-x86_64-gcc mingw-w64-x86_64-libvpx mingw-w64-x86_64-winpthreads' | Set-Content -LiteralPath (Join-Path $cache 'toolchain-versions.txt') -Encoding utf8
    [ordered]@{tag='n8.1';commit=$commit;sha256=$sourceHash;configuration=$configure} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $cache 'origin.json') -Encoding utf8
    Get-ChildItem -LiteralPath (Join-Path $cache 'runtime\bin') -Filter '*.dll' | Get-FileHash |
        Select-Object @{n='file';e={Split-Path -Leaf $_.Path}}, Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $cache 'binaries.json') -Encoding utf8
} finally {
    $env:MSYSTEM = $previousSystem
    if ($mapped) { & subst.exe ($drive + ':') /D }
}
