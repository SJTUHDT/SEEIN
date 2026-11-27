"""Collect exact corresponding source and notices; never collect runtime profiles."""
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor, as_completed
import hashlib, json, urllib.request, urllib.error, shutil, tarfile, subprocess, zipfile

root=Path(__file__).resolve().parents[1]
out=root/'artifacts/releases/v2.1.0/sources';out.mkdir(parents=True,exist_ok=True)
downloads=root/'.build/source-downloads';downloads.mkdir(parents=True,exist_ok=True)
qt=[('6.12.0','qtbase'),('6.12.0','qtsvg'),('6.12.0','qttools'),('6.12.0','qttranslations'),('6.8.3','qtbase'),('6.8.3','qt5compat')]
def request(url):
    return urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'SEEIN-Release/2.1.0'}),timeout=60)
def fetch_qt(item):
    version,module=item;name=f'{module}-everywhere-src-{version}.tar.xz'
    for area in ['official_releases','archive']:
        url=f'https://download.qt.io/{area}/qt/{version.rsplit(".",1)[0]}/{version}/submodules/{name}'
        try:
            with request(url+'.sha256') as response:expected=response.read().decode().split()[0].lower()
            break
        except urllib.error.HTTPError as error:
            if error.code!=404 or area=='archive':raise
    if len(expected)!=64:raise RuntimeError('Unexpected Qt checksum response')
    file=downloads/name
    if not file.exists():
        temporary=file.with_suffix(file.suffix+'.partial')
        with request(url) as response,temporary.open('wb') as stream:shutil.copyfileobj(response,stream,1024*1024)
        temporary.replace(file)
    with file.open('rb') as stream:actual=hashlib.file_digest(stream,'sha256').hexdigest()
    if actual!=expected:raise RuntimeError('Qt source hash mismatch: '+name)
    shutil.copy2(file,out/name)
    return {'file':name,'url':url,'sha256':expected,'license':'Upstream Qt module licenses, see archive LICENSES'}

records=[]
with ThreadPoolExecutor(max_workers=4) as pool:
    for future in as_completed([pool.submit(fetch_qt,item) for item in qt]):
        result=future.result();records.append(result);print('Verified source: '+result['file'],flush=True)

for file in (root/'.build/deps/pet-ffmpeg-minimal').iterdir():
    if file.name in {'FFmpeg-n8.1-source.tar.gz','libvpx-1.17.0-source.tar.gz','toolchain-versions.txt','origin.json'}:shutil.copy2(file,out/file.name)
shutil.copy2(root/'scripts/Build-NativePetCodecs.ps1',out/'Build-NativePetCodecs.ps1')
rgb=root/'.build/deps/OpenRGB-8e16de27858cf2be6af54a33ce003df74cb9fd3e/OpenRGB-1.1-source.zip'
with rgb.open('rb') as stream:
    if hashlib.file_digest(stream,'sha256').hexdigest()!='064fd11d18ff0cc854c2da0f0b8ee703b4a053089b0cc0cf2e8b925dde7276d8':raise RuntimeError('OpenRGB source checksum mismatch')
shutil.copy2(rgb,out/rgb.name)

# Include native dependency source downloads and patches from the fixed vcpkg
# build, excluding executable tool installers and build trees.
inputs=[]
for p in (root/'.build/tools/vcpkg/downloads').iterdir():
    if not p.is_file():continue
    if p.name.endswith(('.tar.gz','.tar.xz','.tar.bz2','.tar.zst','.patch')) or p.name.startswith(('libpng-','libwebp-','libjpeg-','openjpeg-','libyuv-')):inputs.append(p)
with tarfile.open(out/'NativeDependencySources-2.1.0.tar.gz','w:gz') as archive:
    for p in sorted(inputs):archive.add(p,arcname=p.name,recursive=False)
    vcpkg=root/'.build/tools/vcpkg';revision=json.loads((root/'vendor/tools/sources.lock.json').read_text())['vcpkg']
    target=downloads/'vcpkg-source.tar'
    with target.open('wb') as stream:subprocess.run(['git','-C',str(vcpkg),'archive','--format=tar',revision],stdout=stream,check=True)
    archive.add(target,arcname='vcpkg-source.tar',recursive=False)

(out/'SOURCE-PROVENANCE.json').write_text(json.dumps(records,indent=2),encoding='utf8')
print('Corresponding sources collected',flush=True)
