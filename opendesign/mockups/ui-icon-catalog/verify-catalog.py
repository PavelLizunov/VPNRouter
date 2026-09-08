"""Check catalog coverage contracts, source references and vector safety."""
from pathlib import Path
from html.parser import HTMLParser
import hashlib
import json
import re
import subprocess
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]

class Links(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = []
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        for key in ('src', 'href'):
            if key in attrs:
                self.links.append(attrs[key])

def main():
    inventory = json.loads((HERE / 'inventory.json').read_text())
    assert inventory['entries'], 'Empty inventory'
    ids = set()
    refs = 0
    for e in inventory['entries']:
        assert re.fullmatch(r'[a-z0-9-]+', e['id'])
        assert e['id'] not in ids, e['id']
        ids.add(e['id'])
        for key in ('name', 'purpose', 'category', 'screens', 'sources'):
            assert e[key], (e['id'], key)
        assert isinstance(e['interactive'], bool)
        assert any(e['original'].get(k) for k in ('symbol', 'svg', 'label'))
        assert e['proposed'] == f"proposed/{e['id']}.svg"
        for source in e['sources']:
            path = (ROOT / source['file']).resolve()
            assert path.is_relative_to(ROOT), path
            lines = path.read_text().splitlines()
            assert 1 <= source['line'] <= len(lines), source
            assert source['context'], source
            if source.get('excerpt'):
                assert source['excerpt'] in lines[source['line']-1], source
            refs += 1
        data = (HERE / e['proposed']).read_text()
        tree = ET.fromstring(data)
        assert tree.attrib['viewBox'] == '0 0 24 24'
        assert tree.attrib['stroke'] == 'currentColor'
        assert tree.attrib['stroke-width'] == '1.8'
        assert '<title' in data and '<desc' in data
        for child in tree.iter():
            assert child.tag.split('}')[-1] in {'svg','title','desc','path','circle','ellipse','rect','line','polyline','polygon'}, child.tag
            for key, value in child.attrib.items():
                assert not key.lower().startswith('on') and 'href' not in key
                if key in ('fill', 'stroke'):
                    assert value in ('none', 'currentColor'), value
        assert not re.search(r'url\(|<text|<image|<script|gradient', data, re.I)
    by_id = {e['id']:e for e in inventory['entries']}
    for key, expected in {'search-tab':['Desktop / Публичные — вкладка поиска'], 'refresh':['Desktop / Подписки'], 'apply':['Desktop / Приложения','Desktop / Сеть — нижняя панель']}.items():
        assert sorted(s for s in by_id[key]['screens'] if s.startswith('Desktop / ')) == sorted(expected), key
    for key in ('play','stop','radio-on','radio-off','switch'):
        assert by_id[key]['original'].get('svg'), key
    html = (HERE / 'icon-catalog.html').read_text()
    parser = Links()
    parser.feed(html)
    for link in parser.links:
        assert not re.match(r'\w+:|//', link), link
        assert (HERE / link).exists(), link
    for control in ('search','screen','purpose','size','theme','state','reset'):
        assert f'id="{control}"' in html
    assert set(ids) == {p.stem for p in (HERE / 'proposed').glob('*.svg')}
    changed = subprocess.check_output(['git','diff','b7ce0e4f','--name-only'], cwd=ROOT, text=True).splitlines()
    assert all(p.startswith(('plans/', 'opendesign/')) for p in changed), changed
    print(f'PASS: {len(ids)} vectors, {refs} valid source locations, local links, controls, safe monochrome SVG, no product diff')
    digest = hashlib.sha256()
    for path in sorted(HERE.rglob('*')):
        if path.is_file():
            digest.update(path.relative_to(HERE).as_posix().encode())
            digest.update(path.read_bytes())
    print('Catalog SHA256:', digest.hexdigest())

if __name__ == '__main__':
    main()
