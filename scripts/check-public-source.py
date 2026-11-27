"""Fail closed on private-module remnants and credentials in the public tree."""
from pathlib import Path
import re, os

root=Path(__file__).resolve().parents[1]
excluded={'.git','.build','artifacts','bin','obj','__pycache__','.vs'}
forbidden=re.compile(r'SEEIN\.PrivateModules|自动预约|seereserve|Reservation(?:View|Host|Settings|WebView|Tabs|Messages)|["\x27]reservation["\x27]',re.I)
secrets=re.compile(r'gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----')
errors=[];count=0
def files():
    for directory, folders, names in os.walk(root):
        folders[:] = [name for name in folders if name not in excluded]
        for name in names: yield Path(directory)/name
for file in files():
    relative=file.relative_to(root)
    if not file.is_file() or excluded.intersection(relative.parts):continue
    count+=1
    if file.suffix.lower() in {'.dpapi','.pfx','.authorization','.db','.sqlite','.pdb','.exe','.dll'}:errors.append(str(relative)+': runtime/private file in source tree');continue
    if file.suffix.lower() in {'.png','.jpg','.jpeg','.webp','.gif','.ico','.ttf','.woff','.woff2'}:continue
    try:text=file.read_text(encoding='utf-8-sig')
    except UnicodeDecodeError:continue
    # This checker names its own patterns, but contains no private module code.
    if relative.as_posix()!='scripts/check-public-source.py' and forbidden.search(text):errors.append(str(relative)+': excluded feature reference')
    if secrets.search(text):errors.append(str(relative)+': credential/private key pattern')
    if re.search(r'C:[\\/]Users[\\/]Administrator[\\/]',text,re.I):errors.append(str(relative)+': developer account path')
    if relative.as_posix()!='scripts/check-public-source.py' and re.search(r'C:/dafeiyu/uv/tools/astrbot/Scripts/python',text,re.I):errors.append(str(relative)+': local build executable')
if errors:raise SystemExit('\n'.join(errors))
print(f'Public source check passed: {count} files')
