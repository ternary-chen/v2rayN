"""Build a clean portable Windows package; never reads the installed user's configuration."""
import argparse, hashlib, json, shutil, tempfile, zipfile
from pathlib import Path

def digest(path):
    with path.open('rb') as stream: return hashlib.file_digest(stream, 'sha256').hexdigest()

def package(publish, helper, dependencies, output, commit):
    repo = Path(__file__).resolve().parents[1]
    build = json.loads((repo / 'v2rayN/AdaptiveRouting/custom-build.json').read_text(encoding='utf-8'))
    if len(commit) != 40 or any(c not in '0123456789abcdef' for c in commit): raise ValueError('Use a full source commit')
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='learning-windows-', dir=output) as tmp:
        stage = Path(tmp)
        for p in publish.iterdir():
            if p.is_file() and (p.suffix.lower() in {'.exe', '.dll'} or p.name == 'custom-build.json'): shutil.copy2(p, stage / p.name)
        if not (stage / 'v2rayN.exe').is_file(): raise ValueError('Client executable is missing')
        if json.loads((stage / 'custom-build.json').read_text()) != build: raise ValueError('Published build identity mismatch')
        shutil.copy2(helper, stage / 'CustomUpdater.exe')
        for name in ['geoip.dat', 'geosite.dat', 'xray/xray.exe', 'xray/wintun.dll']:
            source = dependencies / name
            target = stage / 'bin' / name
            target.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(source, target)
        shutil.copy2(repo / 'LICENSE', stage / 'LICENSE')
        for name in ['docs', '.agents']: shutil.copytree(repo / name, stage / name)
        shutil.copy2(repo / 'docs/custom-updates.md', stage / '功能说明.md')
        (stage / '版本说明.txt').write_text(f"v2rayN {build['Version']} Windows x64\n提交：{commit}\n自动检查与下载，检查更新页面确认后才安装。\n首次使用请导入自己的订阅，不含作者配置。\n", encoding='utf-8')
        manifest = {'commit': commit, 'release': build['Version'], 'build': build['Build'], 'runtime': 'win-x64', 'self_contained': True,
                    'sha256': {p.relative_to(stage).as_posix(): digest(p) for p in stage.rglob('*') if p.is_file() and p.suffix.lower() in {'.exe','.dll','.dat'}}}
        (stage / 'build-info.json').write_text(json.dumps(manifest, indent=2)+'\n', encoding='utf-8')
        forbidden = {'guiconfigs','guilogs','guibackups','binconfigs','auth.json','.env','limited-learning.json'}
        if any(p.name.lower() in forbidden for p in stage.rglob('*')): raise ValueError('Private data found in staging')
        archive = output / f'v2rayN-custom-{commit[:8]}-win-x64.zip'
        with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
            for p in stage.rglob('*'):
                if p.is_file(): z.write(p,p.relative_to(stage).as_posix())
        with zipfile.ZipFile(archive) as z:
            if z.testzip(): raise ValueError('Archive verification failed')
        (output/'SHA256SUMS-windows.txt').write_text(f'{digest(archive)}  {archive.name}\n',encoding='ascii')
        print(archive)

if __name__ == '__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for name in ['publish','helper','dependencies','output']: p.add_argument('--'+name,type=Path,required=True)
    p.add_argument('--commit',required=True);a=p.parse_args()
    package(a.publish,a.helper,a.dependencies,a.output,a.commit)
