"""Merge independently collected source inventories without dropping consumers."""
from pathlib import Path
import json

HERE = Path(__file__).resolve().parent

def main():
    merged = {}
    appendix = []
    for filename in ('desktop-inventory.json', 'android-inventory.json'):
        data = json.loads((HERE / filename).read_text())
        entries = data if isinstance(data, list) else data['entries']
        if filename.startswith('desktop'):
            def split(original_id, new_id, predicate, name, purpose):
                original = next(e for e in entries if e['id'] == original_id)
                selected = [s for s in original['sources'] if predicate(s)]
                original['sources'] = [s for s in original['sources'] if s not in selected]
                import copy
                added = copy.deepcopy(original)
                added.update(id=new_id, name=name, purpose=purpose, sources=selected, proposed=f'proposed/{new_id}.svg')
                added['original']['variants'] = []
                added['note'] = 'Выделено по назначению и реальным потребителям, независимо от совпадения исходного символа.'
                entries.append(added)
            split('refresh','apply',lambda s: 'Apply' in s['context'], 'Применить настройки', 'Применяет изменённые настройки')
            split('play','search-tab',lambda s: 'FcTabSearch' in s['context'], 'Вкладка поиска', 'Открывает поиск публичных конфигураций')
            split('verified','find-working',lambda s: 'FcDeepVerify' in s['context'], 'Найти рабочие конфигурации', 'Запускает глубокую проверку публичных серверов')
            for e in entries:
                screen_overrides = {'apply':['Приложения','Сеть — нижняя панель'], 'refresh':['Подписки'], 'play':['Главное окно — нижняя панель','Обход DPI','Telegram-прокси'], 'search-tab':['Публичные — вкладка поиска'], 'find-working':['Публичные — поиск'], 'verified':['Публичные — поиск и сохранённые']}
                if e['id'] in screen_overrides: e['screens']=screen_overrides[e['id']]
                if e['id']=='verified': e['interactive']=False
                if e['id']=='pending': e['id']='untested'; e['geometry']='pending'; e['proposed']='proposed/untested.svg'
                if e['id']=='refresh': e['name']='Обновить данные'; e['purpose']='Обновляет подписку или перечитывает журнал'
                if e['id']=='play':
                    e['purpose']='Запускает VPN или выбранный инструмент'
                    e['original']['variants']=['▶ Запустить VPN','▶ Start VPN','Path M8,5 L8,19 L19,12 z']
                if e['id']=='refresh': e['original']['variants']=[]
                if e['id']=='verified':
                    e['original']['variants']=['— ✓✓','N ms ✓✓']
                    e['note']='Пассивный результат глубокой проверки; кнопка запуска показана отдельно.'
        for entry in entries:
            entry['screens'] = [('Android / ' if filename.startswith('android') else 'Desktop / ') + s for s in entry.get('screens', [])]
            if filename.startswith('android') and entry['id'] == 'flag':
                country = [s for s in entry['sources'] if 'FreeConfigs' in s['file']]
                appendix.append({'id':'android-country-flags','name':'Флаги стран Android — сохранить','sources':country,'note':'Региональные индикаторы страны не заменяются общей пиктограммой.'})
                entry['sources'] = [s for s in entry['sources'] if s not in country]
                entry['name'] = 'Выбор конфигурации'
                entry['purpose'] = 'Обозначает строку выбора конфигурации'
                entry['screens'] = ['Android / Простой режим']
                entry['interactive'] = True
                entry['original']['variants'] = []
            if entry['id'] == 'unknown-status':
                appendix.append(entry)
                continue
            if not entry.get('screens') or entry['id'] in ('source-only', 'country-flags', 'framework-controls'):
                appendix.append(entry)
                continue
            key = entry['id']
            if key == 'apply':
                entry['purpose'] = 'Применяет изменённые настройки; поведение зависит от платформы'
            aliases = {'search-tab':'search', 'increase':'add'}
            if key in aliases:
                entry['geometry'] = aliases[key]
            if key == 'camera':
                entry['geometry'] = 'scan'
            if key not in merged:
                merged[key] = entry
                continue
            target = merged[key]
            target['screens'] = sorted(set(target['screens'] + entry['screens']))
            target['interactive'] = target['interactive'] or entry['interactive']
            known = {(s['file'],s['line'],s['context']) for s in target['sources']}
            for source in entry['sources']:
                marker = (source['file'],source['line'],source['context'])
                if marker not in known:
                    target['sources'].append(source)
                    known.add(marker)
            variants = target['original'].setdefault('variants', [])
            for variant in [entry['original'].get('symbol'), *entry['original'].get('variants', [])]:
                if variant and variant not in variants and variant != target['original'].get('symbol'):
                    variants.append(variant)
            if entry.get('note') and entry['note'] not in target.get('note', ''):
                target['note'] = target.get('note', '') + '\n' + entry['note']
    original_shapes = {
        'play': '<path d="M8,5 L8,19 L19,12 z" fill="currentColor"/>',
        'stop': '<rect x="7" y="7" width="10" height="10" rx="2" fill="currentColor"/>',
        'radio-on': '<circle cx="12" cy="12" r="5.25" fill="currentColor" stroke="currentColor" stroke-width="1.5"/><circle cx="12" cy="12" r="2" fill="white"/>',
        'radio-off': '<circle cx="12" cy="12" r="5.25" fill="none" stroke="currentColor" stroke-width="1.5"/>',
        'switch': '<rect x="0" y="3" width="32" height="18" rx="9" fill="currentColor"/><circle cx="23" cy="12" r="7" fill="white"/>'
    }
    for entry in merged.values():
        if entry['id'] in original_shapes:
            viewbox = '0 0 32 24' if entry['id']=='switch' else '0 0 24 24'
            entry['original']['svg'] = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{viewbox}">{original_shapes[entry["id"]]}</svg>'
            entry['note'] = entry.get('note','') + '\nСлева показана собственная Desktop-геометрия; текстовые и платформенные варианты перечислены в источниках.'
        entry['sources'].sort(key=lambda source:(source['file'],source['line']))
        entry['screens'] = sorted(set(entry['screens']))
    output = {
        'base': 'b7ce0e4f',
        'status': 'proposal-only-no-product-integration',
        'coverage': [
            'Реестр охватывает явные пиктограммы исходников Desktop и Android, включая символы в локализованных подписях и XAML-фигуры.',
            'Логотип, маскот, app/tray icons, launcher и сторонние значки приложений исключены и не изменены.',
            'Флаги стран сохраняются как метаданные страны. Глобус относится только к неизвестной стране, не заменяет флаги.',
            'Нативные шаблоны контролов и ресурсы Android не выданы за собственные SVG: указаны отдельно. Текстовая навигация не получает выдуманных значков.',
            'Исходные варианты могут отличаться между платформами; все найденные места использования перечислены в карточках. Динамический пользовательский текст не имеет конечного набора символов.',
            'CLI-символы описаны отдельно в plans/ui-pictogram-cli-appendix-2026-09-07.md; терминал не переводится на SVG.'
        ],
        'entries': sorted(merged.values(), key=lambda e:e['id']),
        'appendix': appendix
    }
    (HERE / 'inventory.json').write_text(json.dumps(output,ensure_ascii=False,indent=2)+'\n')
    print(f'Merged {len(merged)} semantic groups; {len(appendix)} appendix records.')

if __name__ == '__main__':
    main()
