"""Verify published archives without installing or starting the application."""
from pathlib import Path, PurePosixPath
import hashlib, json, subprocess, sys, zipfile

root=Path(__file__).resolve().parents[1]
release=root/'artifacts/releases/v2.1.0'
upload=release/'upload'

def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream,'sha256').hexdigest()

def manifest(path,base):
    result={}
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        expected,name=line.split(' *',1)
        relative=PurePosixPath(name)
        if len(expected)!=64 or relative.is_absolute() or '..' in relative.parts or ':' in name or name in result:
            raise ValueError('Unsafe or duplicate manifest entry: '+name)
        file=base.joinpath(*relative.parts)
        if digest(file)!=expected.lower():raise ValueError('Checksum mismatch: '+name)
        result[name]=expected
    actual={f.relative_to(base).as_posix() for f in base.rglob('*') if f.is_file() and f!=path}
    if set(result)!=actual:raise ValueError('Manifest file set mismatch: '+str(path))
    return result

subprocess.run([sys.executable,str(root/'scripts/check-public-source.py')],check=True)
assets=manifest(upload/'SHA256SUMS.txt',upload)
for profile in ['Core','PublicFull']:
    directory=release/f'Seein-win-x64-2.1.0-{profile}'
    files=manifest(directory/'MANIFEST.sha256',directory)
    print(f'Verified {profile}: {len(files)} installed files',flush=True)

for file in sorted(upload.iterdir()):
    if file.suffix not in {'.zip','.seeinplugin'}:continue
    with zipfile.ZipFile(file) as archive:
        names=archive.namelist()
        if len(names)!=len(set(names)):raise ValueError('Duplicate ZIP entry: '+file.name)
        for name in names:
            p=PurePosixPath(name)
            if p.is_absolute() or '..' in p.parts or ':' in name:raise ValueError('Unsafe ZIP entry: '+name)
        failure=archive.testzip()
        if failure:raise ValueError('ZIP CRC failure: '+failure)
    print('Verified ZIP: '+file.name,flush=True)

with zipfile.ZipFile(upload/'SEEIN-2.1.0-source.zip') as archive:
    for name in archive.namelist():
        if {'.build','.git','artifacts','bin','obj'}.intersection(PurePosixPath(name).parts):
            raise ValueError('Non-source directory in snapshot: '+name)
        if archive.read(name)!=(root/name).read_bytes():raise ValueError('Stale source snapshot: '+name)

inventory=json.loads((upload/'COMPONENTS.json').read_text(encoding='utf8'))
if len(inventory['modules'])!=6:raise ValueError('Unexpected module count')
print(f'PASS: {len(assets)} checksummed assets; program manifests, archive integrity and current source snapshot verified.')
