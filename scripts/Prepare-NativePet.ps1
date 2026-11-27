[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destinationRoot = (Get-Item -LiteralPath $Destination).FullName
$cache = Join-Path $project '.build\deps\pet-ffmpeg-minimal'
if (!(Test-Path -LiteralPath (Join-Path $cache 'runtime\bin\avcodec-62.dll'))) {
    & (Join-Path $PSScriptRoot 'Build-NativePetCodecs.ps1')
}
$assets = Join-Path $destinationRoot 'pet-assets'
$codecs = Join-Path $destinationRoot 'pet-codecs'
$petArchive = Join-Path $project '.build\deps\DshPet-0.4.9\dsh-pet-win-x64.zip'
if (!(Test-Path -LiteralPath $petArchive)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $petArchive) -Force | Out-Null
    Invoke-WebRequest -UseBasicParsing 'https://github.com/PC2005-cloud/dsh-pet/releases/download/v0.4.9/dsh-pet-win-x64.zip' -OutFile $petArchive
}
if ((Get-FileHash -LiteralPath $petArchive).Hash -ne 'FEC1D27D59FC0C9A883E77EFF2FACBF5676C15FFD6011B1436351CBC34F06C1E') {
    throw 'Pinned desktop pet assets failed SHA256 verification.'
}
# Read only the original ASAR assets. The extracted JavaScript runtime is a build input.
$original = Join-Path $project '.build\deps\DshPet-0.4.9\seein-native-assets'
& {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $asar = Join-Path (Split-Path -Parent $petArchive) 'native-assets-input.asar'
    $archive = [IO.Compression.ZipFile]::OpenRead($petArchive)
    try {
        $entry = @($archive.Entries | Where-Object FullName -eq 'dsh-pet-0.4.9-win-x64/resources/app.asar')
        if ($entry.Count -ne 1) { throw 'Pinned archive has an unexpected ASAR layout.' }
        $inputStream = $entry[0].Open()
        $outputStream = [IO.File]::Create($asar)
        try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
    } finally { $archive.Dispose() }
    $extractor = @'
const fs=require('node:fs');
const path=require('node:path');
const [file,destination]=process.argv.slice(2);
const archive=fs.readFileSync(file);
const header=JSON.parse(archive.subarray(16,16+archive.readUInt32LE(12)).toString('utf8'));
const start=8+archive.readUInt32LE(4);
function extract(files,root){
 fs.mkdirSync(root,{recursive:true});
 for(const [name,item] of Object.entries(files)){
  if(path.basename(name)!==name||name==='.'||name==='..'||item.link||item.unpacked)throw Error('Invalid ASAR path');
  const target=path.join(root,name);
  if(item.files){extract(item.files,target);continue;}
  const offset=start+Number(item.offset);
  if(!Number.isSafeInteger(offset)||offset<start||offset+item.size>archive.length)throw Error('Invalid ASAR offset');
  fs.writeFileSync(target,archive.subarray(offset,offset+item.size));
 }
}
extract(header.files.assets.files,path.join(destination,'assets'));
'@
    $extractor | node - $asar $original
    if ($LASTEXITCODE) { throw 'Could not unpack the original animation assets.' }
    # Re-extract from the pinned archive on every publish, rather than trusting an edited asset cache.
}
New-Item -ItemType Directory -Path $assets, $codecs -Force | Out-Null
Copy-Item -Path (Join-Path $original 'assets\*') -Destination $assets -Recurse -Force
$petLicense = Join-Path $project '.build\deps\DshPet-0.4.9\source\dsh-pet\LICENSE'
if (!(Test-Path -LiteralPath $petLicense)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $petLicense) -Force | Out-Null
    Invoke-WebRequest 'https://raw.githubusercontent.com/PC2005-cloud/dsh-pet/4e589f6dacabbd3170e91e7a213b2a7afff1a09b/dsh-pet/LICENSE' -OutFile $petLicense
}
if ((Get-FileHash -LiteralPath $petLicense).Hash -ne '22757DC9C895B2729A2FC684D227CADFB54D5AF3DD300029926839E8C4E269CE') { throw 'Pet asset license does not match the pinned source.' }
Copy-Item -LiteralPath $petLicense -Destination (Join-Path $assets 'LICENSE-dsh-pet.txt')
foreach ($name in 'avcodec-62.dll', 'avformat-62.dll', 'avutil-60.dll', 'swscale-9.dll') {
    Copy-Item -LiteralPath (Join-Path $cache "runtime\bin\$name") -Destination $codecs
}
foreach ($name in 'libvpx-1.dll', 'libwinpthread-1.dll') {
    Copy-Item -LiteralPath (Join-Path $project ".build\tools\msys64\mingw64\bin\$name") -Destination $codecs
}
$origin = Get-Content -Raw -LiteralPath (Join-Path $cache 'origin.json') | ConvertFrom-Json
$source = Join-Path $cache ('FFmpeg-' + $origin.commit)
Copy-Item -LiteralPath (Join-Path $source 'COPYING.LGPLv2.1') -Destination (Join-Path $codecs 'FFmpeg-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $cache 'origin.json') -Destination (Join-Path $codecs 'FFmpeg-origin.json')
$vpxSource = Join-Path $cache 'libvpx-82615ff0733cd4063838c352bc2e2ab225f37ade'
if (!(Test-Path -LiteralPath (Join-Path $vpxSource 'LICENSE'))) {
    $vpxArchive = Join-Path $cache 'libvpx-1.17.0-source.tar.gz'
    if (!(Test-Path -LiteralPath $vpxArchive)) { Invoke-WebRequest 'https://codeload.github.com/webmproject/libvpx/tar.gz/82615ff0733cd4063838c352bc2e2ab225f37ade' -OutFile $vpxArchive }
    if ((Get-FileHash -LiteralPath $vpxArchive).Hash -ne '0F7644896F57CA4C4AE93109A1C9FEA1963933A62FA9E074B3D9304EEB66F5F1') { throw 'libvpx source integrity verification failed.' }
    & tar -xf $vpxArchive -C $cache
    if ($LASTEXITCODE) { throw 'Could not extract libvpx license materials.' }
}
foreach ($name in 'LICENSE', 'PATENTS', 'AUTHORS') { Copy-Item -LiteralPath (Join-Path $vpxSource $name) -Destination (Join-Path $codecs "libvpx-$name.txt") }
Copy-Item -LiteralPath (Join-Path $project '.build\tools\msys64\mingw64\share\licenses\libwinpthread\COPYING') -Destination (Join-Path $codecs 'winpthread-LICENSE.txt')
Get-ChildItem -LiteralPath $codecs -Filter '*.dll' | Get-FileHash | Select-Object @{n='file';e={Split-Path -Leaf $_.Path}}, Hash |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $codecs 'SHA256.json') -Encoding utf8
Get-ChildItem -LiteralPath $assets, $codecs -File -Recurse | Measure-Object Length -Sum | Select-Object Count, Sum
