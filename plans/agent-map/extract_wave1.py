import sys

with open('/tmp/dsh-spill-FzDXFU/session-0c1aa7ca152b/d26f9367eecb-workflow.txt', 'r', encoding='utf-8') as f:
    text = f.read()

prefix = '{\n  "results": [\n'
idx = text.find(prefix)
if idx != -1:
    content = text[idx:]
    # Extract index 0
    # Between '"index": 0,' and '"index": 1,'
    idx0 = content.find('"index": 0,')
    idx1 = content.find('"index": 1,')
    if idx0 != -1 and idx1 != -1:
        part0 = content[idx0:idx1]
        # find "result": "
        r_idx = part0.find('"result": "')
        if r_idx != -1:
            raw_res = part0[r_idx + len('"result": "'):]
            # trim trailing quotes / commas
            last_q = raw_res.rfind('"\n    }')
            if last_q != -1:
                raw_res = raw_res[:last_q]
            # unescape
            parsed = raw_res.encode('utf-8').decode('unicode_escape')
            with open('plans/agent-map/modules/H02_B061_H02.md', 'w', encoding='utf-8') as out:
                out.write(parsed)
            print("Successfully extracted H02_B061_H02.md")
