"""Build public release archives, source snapshots, inventories and checksums."""
from pathlib import Path
import hashlib, json, os, shutil, subprocess, zipfile, xml.etree.ElementTree as ET

root=Path(__file__).resolve().parents[1]; release=root/'artifacts/releases/v2.2.0'
assets=release/'upload';assets.mkdir(parents=True,exist_ok=True)
def sha(file):
    with file.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
def source_files():
    for directory,folders,names in os.walk(root):
        folders[:]=[n for n in folders if n not in {'.build','.git','artifacts','bin','obj','__pycache__','.vs'}]
        for name in names:yield Path(directory)/name
def zip_tree(directory,out,files=None):
    temporary=out.with_suffix(out.suffix+'.partial')
    with zipfile.ZipFile(temporary,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
        for file in sorted(files if files is not None else directory.rglob('*')):
            if file.is_file():archive.write(file,file.relative_to(directory).as_posix())
    temporary.replace(out)

licenses=release/'licenses';licenses.mkdir(exist_ok=True)
packages={}
for project in (root/'src').rglob('obj/project.assets.json'):
    data=json.loads(project.read_text(encoding='utf-8-sig'))
    for key,library in data.get('libraries',{}).items():
        if library.get('type')!='package':continue
        name,version=key.rsplit('/',1)
        if key in packages:continue
        info={'name':name,'version':version,'packageUrl':f'https://www.nuget.org/packages/{name}/{version}'}
        for folder in data.get('packageFolders',{}):
            package=Path(folder)/library.get('path',key.lower())
            nuspec=next(package.glob('*.nuspec'),None)
            if nuspec is None:continue
            metadata=ET.parse(nuspec).getroot();namespace={'n':'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'}
            license_element=next((e for e in metadata.iter() if e.tag.split('}')[-1]=='license'),None)
            if license_element is not None:
                info['license']=license_element.text;info['licenseType']=license_element.attrib.get('type')
            # Ship supplied license/notice text instead of machine-specific paths.
            for file in package.rglob('*'):
                if file.is_file() and file.stat().st_size<2_000_000 and any(t in file.name.lower() for t in ['license','notice','copying','copyright']) and file.suffix.lower() in {'.txt','.md','.html',''}:
                    relative=file.relative_to(package).as_posix().replace('/','_')
                    shutil.copy2(file,licenses/f'{name}-{version}-{relative}')
            break
        packages[key]=info

parts=[]
for profile in ['Core']:
    directory=release/f'Seein-win-x64-2.2.0-{profile}'
    if not directory.is_dir():raise RuntimeError('Missing build profile '+profile)
    # Include complete public docs and notices in every program ZIP, then refresh
    # its installer manifest so the extra files are verified too.
    for name in ['README.md','LICENSE','THIRD-PARTY-NOTICES.md']:
        shutil.copy2(root/name,directory/name)
    shutil.copytree(root/'docs',directory/'docs',dirs_exist_ok=True)
    shutil.copytree(licenses,directory/'licenses',dirs_exist_ok=True)
    manifest=directory/'MANIFEST.sha256'
    manifest.write_text(''.join(sha(file)+' *'+file.relative_to(directory).as_posix()+'\n' for file in sorted(directory.rglob('*')) if file.is_file() and file!=manifest),encoding='utf8')
    name=f'SEEIN-2.2.0-win-x64-{"Full" if profile=="PublicFull" else "Core"}.zip'
    zip_tree(directory,assets/name)
    parts.append({'profile':profile,'archive':name,'archiveBytes':(assets/name).stat().st_size,'logicalBytes':sum(f.stat().st_size for f in directory.rglob('*') if f.is_file())})

modules=[]
for directory in sorted((release/'Seein-win-x64-2.2.0-PublicFull/Plugins').iterdir()):
    if not directory.is_dir():continue
    manifest=json.loads((directory/'module.json').read_text(encoding='utf-8-sig'))
    if manifest.get('private'):raise RuntimeError('Non-public module in release')
    module_id=manifest['id'];file=release/'Plugins-PublicFull'/f'{module_id}-2.2.0.seeinplugin'
    shutil.copy2(file,assets/file.name)
    modules.append({'id':module_id,'name':manifest['name'],'version':manifest['version'],'apiVersion':manifest['apiVersion'],'logicalBytes':sum(f.stat().st_size for f in directory.rglob('*') if f.is_file()),'archiveBytes':file.stat().st_size,'fileCount':len(manifest['files'])})

for file in [release/'ToolSources.zip',release/'plugin-catalog.json',release/'plugin-catalog.sig',*(release/'sources').glob('*')]:
    if file.is_file():shutil.copy2(file,assets/file.name)
zip_tree(licenses,assets/'ThirdPartyLicenses-2.2.0.zip')
zip_tree(root,assets/'SEEIN-2.2.0-source.zip',list(source_files()))
inventory={'version':'2.2.0','platform':'win-x64','baseLicense':'GPL-3.0-only','profiles':parts,'modules':modules,'nugetPackages':sorted(packages.values(),key=lambda p:p['name'].lower()),'nativeSourceLock':json.loads((root/'vendor/tools/sources.lock.json').read_text()),'scope':'Fourteen optional public features with four shared support packages; no runtime user data.'}
(assets/'COMPONENTS.json').write_text(json.dumps(inventory,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
if (root/'docs/ACCEPTANCE.md').exists():shutil.copy2(root/'docs/ACCEPTANCE.md',assets/'ACCEPTANCE.md')
for file in assets.iterdir():
    if file.is_file() and file.stat().st_size>=2*1024**3:raise RuntimeError('Asset exceeds GitHub size limit: '+file.name)
(assets/'SHA256SUMS.txt').write_text(''.join(sha(f)+' *'+f.name+'\n' for f in sorted(assets.iterdir()) if f.is_file() and f.name!='SHA256SUMS.txt'),encoding='utf8')
print(json.dumps({'assets':len(list(assets.iterdir())),'profiles':parts,'modules':modules},ensure_ascii=False,indent=2))
