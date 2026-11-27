"""Export corresponding native-tool source, never build caches or user data."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import sys
import tarfile

root = Path(__file__).resolve().parents[2]
destination = Path(sys.argv[1]).resolve()
destination.mkdir(parents=True, exist_ok=True)
lock = json.loads((root / 'vendor/tools/sources.lock.json').read_text(encoding='utf-8-sig'))
for module in ('snow', 'pane'):
    source = root / '.build/deps' / lock[module]['directory']
    revision = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
    if revision != lock[module]['commit']:
        raise RuntimeError(f'Unexpected source revision: {module}')
    files = subprocess.check_output(['git', '-C', str(source), 'ls-files', '--recurse-submodules', '-z']).decode('utf-8').split('\0')
    additions = ['src/Pane.App/App.Seein.cs', 'src/Pane.App/Hosting/SeeinSearchAlignment.cs',
                 'src/Pane.App/SeeinSearchSites.cs'] if module == 'pane' else [
        'snow_shot/include/snow_shot/seeinsecret.h', 'snow_shot/include/snow_shot/seeinshell.h',
        'snow_shot/include/snow_shot/seeinexternal.h', 'snow_shot/src/app/seeinexternal.cpp',
        'snow_shot/src/app/seeinsecret.cpp', 'snow_shot/src/app/seein_bridge.inc']
    with tarfile.open(destination / f'{module}-seein-source.tar.gz', 'w:gz') as archive:
        for relative in sorted(set(files + additions)):
            if not relative:
                continue
            path = source / relative
            if path.is_file():
                archive.add(path, arcname=lock[module]['directory'] + '/' + relative, recursive=False)
    print(f'Exported {module} source at {revision}')

recipes = destination / 'SEEIN-integration'
recipes.mkdir(exist_ok=True)
shutil.copytree(root / 'vendor/tools', recipes / 'vendor/tools', dirs_exist_ok=True,
                ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
for filename in ('BuildEnvironment.ps1', 'Prepare-PublicDependencies.ps1', 'Build-IntegratedTools.ps1', 'Enter-ToolBuildEnvironment.ps1', 'Stage-IntegratedTools.ps1', 'Prepare-IntegratedTools.ps1', 'Prepare-Everything.ps1', 'Install-EverythingDependency.ps1', 'ProcessNames.ps1'):
    (recipes / 'scripts').mkdir(exist_ok=True)
    shutil.copy2(root / 'scripts' / filename, recipes / 'scripts' / filename)
shutil.copy2(root / 'docs/小工具集成与构建.md', recipes / 'README.md')
(recipes / 'config').mkdir(exist_ok=True)
shutil.copy2(root / 'config/process-names.json', recipes / 'config/process-names.json')

downloads = root / '.build/tools/vcpkg/downloads'
for pattern in ('ffmpeg-ffmpeg-n9.0.tar.gz', 'videolan-x264-*.tar.gz', 'multicoreware-x265_git-*.tar.gz', 'strukturag-libheif-*.tar.gz'):
    for path in downloads.glob(pattern):
        shutil.copy2(path, destination / path.name)
vcpkg = root / '.build/tools/vcpkg'
with (destination / 'vcpkg-source.tar').open('wb') as stream:
    subprocess.run(['git', '-C', str(vcpkg), 'archive', '--format=tar', lock['vcpkg']], stdout=stream, check=True)
hashes = []
for path in sorted(destination.rglob('*')):
    if path.is_file() and path.name != 'SOURCES.sha256':
        hashes.append(hashlib.file_digest(path.open('rb'), 'sha256').hexdigest() + ' *' + path.relative_to(destination).as_posix())
(destination / 'SOURCES.sha256').write_text('\n'.join(hashes) + '\n', encoding='utf-8')
