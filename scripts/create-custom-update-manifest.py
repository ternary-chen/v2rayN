"""Generate the fork update feed only after both platform archives have been verified."""
import argparse, hashlib, json, zipfile
from pathlib import Path

def inspect(path, runtime, version, number, commit):
    with zipfile.ZipFile(path) as z:
        if z.testzip(): raise ValueError('Corrupt archive: '+path.name)
        info=json.loads(z.read('build-info.json'))
        buildname='custom-build.json' if runtime=='win-x64' else 'v2rayN Learning.app/Contents/Resources/custom-build.json'
        build=json.loads(z.read(buildname))
        helper='CustomUpdater.exe' if runtime=='win-x64' else 'v2rayN Learning.app/Contents/MacOS/CustomUpdater'
        if helper not in z.namelist() or info['commit']!=commit or info['runtime']!=runtime or build!={'Schema':1,'Build':number,'Version':version}:
            raise ValueError('Archive identity/helper mismatch: '+path.name)
    with path.open('rb') as f: checksum=hashlib.file_digest(f,'sha256').hexdigest()
    return {'Runtime':runtime,'Url':f'https://github.com/ternary-chen/v2rayN/releases/download/{version}/{path.name}','Sha256':checksum,'Size':path.stat().st_size}

def generate(windows, macos, notes, output, commit):
    repo=Path(__file__).resolve().parents[1]
    build=json.loads((repo/'v2rayN/AdaptiveRouting/custom-build.json').read_text(encoding='utf-8'))
    packages=[inspect(windows,'win-x64',build['Version'],build['Build'],commit),inspect(macos,'osx-arm64',build['Version'],build['Build'],commit)]
    text=notes.read_text(encoding='utf-8')
    if len(text)>16000: raise ValueError('Release notes exceed client limit')
    manifest={'Schema':1,'Build':build['Build'],'Version':build['Version'],'Commit':commit,'Notes':text,'Packages':packages}
    output.write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(output)

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    for n in ['windows','macos','notes','output']:p.add_argument('--'+n,type=Path,required=True)
    p.add_argument('--commit',required=True);a=p.parse_args();generate(a.windows,a.macos,a.notes,a.output,a.commit)
