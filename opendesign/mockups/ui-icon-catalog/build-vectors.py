"""Emit only reviewed pictograms used by inventory.json; stdlib only."""
from pathlib import Path
import json
import re
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parent

def main():
    definitions = json.loads((ROOT / 'vector-definitions.json').read_text())
    inventory = json.loads((ROOT / 'inventory.json').read_text())
    target = ROOT / 'proposed'
    target.mkdir(exist_ok=True)
    seen = set()
    for entry in inventory['entries']:
        key = entry['id']
        assert re.fullmatch(r'[a-z0-9-]+', key), key
        assert key not in seen, key
        seen.add(key)
        geometry = definitions[entry.get('geometry', key)]
        assert entry['proposed'] == f'proposed/{key}.svg'
        title = escape(entry['name'])
        description = escape(entry['purpose'])
        markup = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" '
                  f'fill="none" stroke="currentColor" stroke-width="1.8" '
                  f'stroke-linecap="round" stroke-linejoin="round" role="img" '
                  f'aria-labelledby="title desc">\n<title id="title">{title}</title>\n'
                  f'<desc id="desc">{description}</desc>\n{geometry}\n</svg>\n')
        ET.fromstring(markup)
        (target / f'{key}.svg').write_text(markup)
    print(f'Generated {len(seen)} proposed UI pictograms; product files untouched.')

if __name__ == '__main__':
    main()
