# Sweep: every network story (NetworkStoryHopTable) -> its state machine's closure (<>8__N) and hoisted-local (<x>5__N)
# fields the hops do not carry, and whether each is written before the story's first hop (MoveTo / first await).
import re, os, glob, sys, collections
root = os.path.expanduser('~/.se2-dev/game-code/Decompiled')
meta = re.compile(r'new NetworkStoryHopMetadata\("([^"]+)", "([^"]+)", new string\[\d+\][^,]*, new string\[\d+\][^,]*, new NetworkStoryHop\[\d+\]\s*\{(.*?)\}\)', re.S)
hop = re.compile(r'new NetworkStoryHop\((-?\d+), NetworkStoryHopKind\.(\w+), new string\[\d+\](?: \{([^}]*)\})?\)')
stories = []
for f in glob.glob(root + '/*/Keen/VRage/Multiplayer/NetworkStories/NetworkStoryHopTable.cs'):
    t = open(f, encoding='utf-8', errors='replace').read()
    for chunk in t.split('new NetworkStoryHopMetadata(')[1:]:
        m = re.match(r'"([^"]+)", "([^"]+)"', chunk)
        if not m: continue
        hops = [(int(h[0]), h[1], [x.strip().strip('"') for x in h[2].split(',')] if h[2] else []) for h in hop.findall(chunk)]
        stories.append((m.group(1), m.group(2), hops))
# index source files by type full name
files = {}
for f in glob.glob(root + '/**/*.cs', recursive=True):
    ns = None
    files.setdefault(os.path.basename(f)[:-3], []).append(f)
out = collections.Counter(); rows = []
for typ, method, hops in stories:
    cls = typ.split('.')[-1].split('+')[-1]
    cands = files.get(cls, [])
    src = None
    for c in cands:
        s = open(c, encoding='utf-8', errors='replace').read()
        if 'namespace ' + '.'.join(typ.split('.')[:-1]) in s or len(cands) == 1: src = s; break
    if src is None: out['no source'] += 1; continue
    sm = re.search(r'(?:struct|class) (_003C' + re.escape(method) + r'_003Ed__\d+) : IAsyncStateMachine\s*\{', src)
    if not sm: out['no state machine'] += 1; continue
    body = src[sm.end():]
    end = body.find('\n\t}\n')
    body = body[:end]
    fields = re.findall(r'(?:public|private) [^\s(]+(?:<[^>]*>)? (_003C\w*?_003E(?:8|5)__\d+);', body)
    carried = set(sum((h[2] for h in hops), []))
    mv = body.find('private void MoveNext()')
    code = body[mv:]
    first_hop = min([i for i in [code.find('MoveTo.'), code.find('GetAwaiter()')] if i >= 0] or [len(code)])
    for fld in fields:
        plain = re.sub(r'_003C(\w*?)_003E\d__\d+', r'\1', fld)
        if plain in carried: continue
        kind = 'closure' if '_003E8__' in fld else 'local'
        before = re.search(re.escape(fld) + r'(\.\w+)? = ', code[:first_hop]) is not None
        used_after = fld in code[first_hop:]
        rows.append((typ.split('.')[-1], method, kind, plain or fld, 'set before first hop' if before else 'set after', 'used after' if used_after else '-', [h[1] for h in hops]))
    out['stories'] += 1
print(dict(out), 'flagged', len(rows))
for r in sorted(rows):
    if r[4] == 'set before first hop' and r[5] == 'used after': print('RISK', *r)
print('--- other uncarried (set after the hop: safe)')
for r in sorted(rows):
    if not (r[4] == 'set before first hop' and r[5] == 'used after'): print('     ', *r[:6])
