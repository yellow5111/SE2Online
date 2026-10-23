# Replicated signals with a component-typed argument whose component type never appears in a client composition.
import re, os, glob, collections
D = os.path.expanduser('~/.se2-dev/game-code/Decompiled'); C = os.path.expanduser('~/.se2-dev/game-code/Content')
fwd = re.compile(r'private static void (\w+?)_ForwardOutOfProcess\(ref byte context, ([^)]*)\)')
sigs = []
for f in glob.glob(D + '/*/Game2/Simulation/Replication/CB/SignalTransportForwarders.cs') + glob.glob(D + '/*/**/Replication/CB/SignalTransportForwarders.cs', recursive=True):
    for m in fwd.finditer(open(f, encoding='utf-8', errors='replace').read()):
        parts, depth, cur = [], 0, ''
        for ch in m.group(2):
            if ch == '<': depth += 1
            if ch == '>': depth -= 1
            if ch == ',' and depth == 0: parts.append(cur); cur = ''
            else: cur += ch
        parts.append(cur)
        params = [p.strip().replace('in ', '', 1).rsplit(' ', 1) for p in parts if p.strip()]
        params = [p for p in params if len(p) == 2]
        sigs.append((m.group(1), params))
sigs = list({s[0]: s for s in sigs}.values())
# component classes by short name
comp = {}
for f in glob.glob(D + '/**/*.cs', recursive=True):
    for m in re.finditer(r'public (?:sealed |abstract )?class (\w+) : (\w+)', open(f, encoding='utf-8', errors='replace').read()):
        comp.setdefault(m.group(1), m.group(2))
def is_component(t, depth=0):
    if depth > 12 or t not in comp: return False
    b = comp[t]
    return b in ('Component', 'GameComponent', 'SessionComponent', 'EntityComponent') or is_component(b, depth + 1)
defs = {}
for f in glob.glob(C + '/**/*.def', recursive=True):
    defs[os.path.basename(f)[:-4]] = set(re.findall(r'"Type": "\w+:[\w.]+\.(\w+)"', open(f, encoding='utf-8', errors='replace').read()))
pairs = []
for n in defs:
    for a, b in (('_ServerComposition', '_ClientComposition'), ('Server', 'Client')):
        if n.endswith(a) and n[:-len(a)] + b in defs: pairs.append((n, n[:-len(a)] + b))
server_only = collections.defaultdict(list)
for sv, cl in pairs:
    for t in defs[sv] - defs[cl]: server_only[t].append(sv)
rows = []
for name, params in sigs:
    for t, arg in params[1:]:
        base = re.sub(r'<.*', '', t).rstrip('?')
        if is_component(base) and base in server_only:
            rows.append((name, base, arg, len(server_only[base]), server_only[base][:3]))
print(len(sigs), 'replicated signals,', len(pairs), 'server/client composition pairs;', len(rows), 'signal arguments naming a component some server composition has and its client composition lacks')
for r in rows: print(*r)
odd = collections.Counter()
for name, params in sigs:
    for t, arg in params[1:]:
        base = re.sub(r'<.*', '', t).rstrip('?')
        if base == 'Component' or (re.match(r'I[A-Z]', base) and base not in ('IdentityId', 'InventoryItem')): odd[base] += 1
print('interface/base-typed arguments:', odd.most_common(20))
impl = collections.defaultdict(set)
for f in glob.glob(D + '/Game2.Simulation/**/*.cs', recursive=True) + glob.glob(D + '/VRage*/**/*.cs', recursive=True):
    for m in re.finditer(r'public (?:sealed |abstract )?class (\w+) : ([^\n{]+)', open(f, encoding='utf-8', errors='replace').read()):
        for i in ('IDiscoverableProvider', 'ISeat', 'IBlockPhysicsColliderProvider', 'IControllable'):
            if re.search(r'\b' + i + r'\b', m.group(2)): impl[i].add(m.group(1))
for i, cs in impl.items():
    risky = {c: server_only[c][:2] for c in cs if c in server_only}
    print(i, 'implemented by', sorted(cs)[:8], '| server-only in some pair:', risky)
# By composition key (what a component reference actually carries), not by type.
kv = {}
for f in glob.glob(C + '/**/*.def', recursive=True):
    t = open(f, encoding='utf-8', errors='replace').read()
    tags = dict(re.findall(r'"\$Key": "([^"]+)",\s*"\$Value": "([0-9a-f-]{36})"', t))
    types = dict(re.findall(r'"Key": "([0-9a-f-]{36})",\s*"Value": \{[^{}]*?"Type": "\w+:[\w.]+\.(\w+)"', t, re.S))
    kv[os.path.basename(f)[:-4]] = {k: types.get(g, '?') for k, g in tags.items()}
srv_only_types = collections.defaultdict(list)
for sv, cl in pairs:
    for key, typ in kv.get(sv, {}).items():
        if key not in kv.get(cl, {}): srv_only_types[typ].append(sv + ':' + key)
hits = []
for name, params in sigs:
    for t, arg in params[1:]:
        base = re.sub(r'<.*', '', t).rstrip('?')
        cands = {base} | impl.get(base, set())
        for c in cands:
            if c in srv_only_types: hits.append((name, base, c, srv_only_types[c][:3]))
print('by key:', len(hits))
for h in hits: print(*h)
