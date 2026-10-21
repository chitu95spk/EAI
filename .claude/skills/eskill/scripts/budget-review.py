import json, os, re, sys, glob, datetime, collections

import argparse
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass
ap =argparse.ArgumentParser(description='Relative token-cost review of Claude sessions for this project (read-only).')
ap.add_argument('--days', type=int, default=7, help='sessions modified in the last N days (default 7)')
ap.add_argument('--session', default='', help='only this session id (full guid); prints one brief line for the contract [budget] actuals')
args = ap.parse_args()
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..'))
PROJ = os.path.join(os.path.expanduser('~'), '.claude', 'projects', re.sub(r'[:\\/ _.]', '-', ROOT))
SINCE = datetime.datetime.now() - datetime.timedelta(days=args.days)
if args.session:
    SINCE = datetime.datetime(2000, 1, 1)
OUT = os.path.join(ROOT, 'artifacts', 'budget-review', datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))
if not args.session:
    os.makedirs(OUT, exist_ok=True)

# relative price weights (input = 1). Assumption, not a bill: cache-write 1.25, cache-read 0.1, output 5.
W = dict(inp=1.0, cw=1.25, cr=0.1, out=5.0)
# relative model price vs sonnet (assumption): haiku ~0.33, opus ~1.67 (list-price ratios), unknown = 1.
MW = dict(haiku=0.33, sonnet=1.0, opus=1.67, fable=1.0)

def model_key(m):
    m = (m or '').lower()
    for k in MW:
        if k in m:
            return k
    return 'other'

def scan(path, is_sub=False):
    s = dict(path=path, req={}, tools=collections.Counter(), errors=0, big=[], reads=collections.Counter(),
             results=0, first=None, last=None, title='', contracts=set(), big_total=0, bashcmds=[], mdreads=0)
    tool_name, tool_desc = {}, {}
    try:
        f = open(path, encoding='utf-8')
    except Exception:
        return s
    for line in f:
        if len(line) < 20:
            continue
        try:
            o = json.loads(line)
        except Exception:
            continue
        ts = o.get('timestamp')
        if ts:
            try:
                t = datetime.datetime.fromisoformat(ts.replace('Z', '+00:00')).astimezone().replace(tzinfo=None)
                s['first'] = s['first'] or t
                s['last'] = t
            except Exception:
                pass
        if o.get('aiTitle'):
            s['title'] = o['aiTitle']
        m = o.get('message')
        if not isinstance(m, dict):
            continue
        if o.get('type') == 'assistant':
            u = m.get('usage')
            mid = m.get('id') or o.get('requestId') or o.get('uuid')
            if u and mid:
                s['req'][mid] = (model_key(m.get('model')), u.get('input_tokens', 0) or 0,
                                 u.get('cache_creation_input_tokens', 0) or 0,
                                 u.get('cache_read_input_tokens', 0) or 0, u.get('output_tokens', 0) or 0)
            c = m.get('content')
            if isinstance(c, list):
                for b in c:
                    if b.get('type') == 'tool_use':
                        n = b.get('name'); inp = b.get('input') or {}
                        tool_name[b.get('id')] = n
                        s['tools'][n] += 1
                        d = inp.get('file_path') or inp.get('command') or inp.get('pattern') or inp.get('description') or ''
                        tool_desc[b.get('id')] = str(d)
                        if n == 'Read' and inp.get('file_path'):
                            fp = inp['file_path']
                            s['reads'][fp] += 1
                            if re.search(r'(AGENTS|CLAUDE|CODEX_HISTORY|\.codex-session)\.md$', fp) and not inp.get('limit'):
                                s['mdreads'] += 1
                        if n in ('Edit', 'Write') and inp.get('file_path'):
                            mm = re.search(r'task-contract\.([^\\/]+)\.toml$', inp['file_path'])
                            if mm and mm.group(1) != 'template':
                                s['contracts'].add(mm.group(1))
                        if n == 'Bash' and inp.get('command'):
                            s['bashcmds'].append(str(inp['command'])[:90])
        elif o.get('type') == 'user' and isinstance(m.get('content'), list):
            for b in m['content']:
                if b.get('type') != 'tool_result':
                    continue
                s['results'] += 1
                cont = b.get('content')
                txt = cont if isinstance(cont, str) else json.dumps(cont)
                if b.get('is_error'):
                    s['errors'] += 1
                if len(txt) > 8000:
                    s['big'].append((len(txt), tool_name.get(b.get('tool_use_id'), '?'), tool_desc.get(b.get('tool_use_id'), '')[:80]))
                    s['big_total'] += len(txt)
    return s

def totals(s):
    t = collections.Counter()
    for (mk, i, cw, cr, out) in s['req'].values():
        t['inp'] += i; t['cw'] += cw; t['cr'] += cr; t['out'] += out
        t['units_' + mk] += (i * W['inp'] + cw * W['cw'] + cr * W['cr'] + out * W['out']) * MW.get(mk, 1.0)
        t['n_' + mk] += 1
    t['units'] = sum(v for k, v in t.items() if k.startswith('units_'))
    t['requests'] = len(s['req'])
    return t

sessions = []
for p in glob.glob(os.path.join(PROJ, '*.jsonl')):
    mt = datetime.datetime.fromtimestamp(os.path.getmtime(p))
    if mt < SINCE:
        continue
    sid = os.path.basename(p)[:-6]
    if args.session and sid != args.session:
        continue
    main = scan(p)
    subs = []
    sd = os.path.join(PROJ, sid, 'subagents')
    if os.path.isdir(sd):
        for sp in glob.glob(os.path.join(sd, 'agent-*.jsonl')):
            meta = {}
            try:
                meta = json.load(open(sp[:-6] + '.meta.json', encoding='utf-8'))
            except Exception:
                pass
            sub = scan(sp, True)
            sub['agentType'] = meta.get('agentType', '?')
            sub['desc'] = meta.get('description', '')
            subs.append(sub)
    sessions.append(dict(sid=sid, main=main, subs=subs, mtime=mt))

sessions.sort(key=lambda x: x['mtime'], reverse=True)
rows = []
agent_roll = collections.defaultdict(lambda: collections.Counter())
agent_list = []
for se in sessions:
    tm = totals(se['main'])
    ts = collections.Counter()
    for sub in se['subs']:
        t = totals(sub)
        ts.update(t)
        key = sub['agentType'] + '/' + (max(('haiku', 'sonnet', 'opus', 'other'), key=lambda k: t['n_' + k]) if t['requests'] else '?')
        agent_roll[key].update({'agents': 1, 'units': t['units'], 'cr': t['cr'], 'cw': t['cw'], 'out': t['out'], 'requests': t['requests'], 'errors': sub['errors'], 'tools': sum(sub['tools'].values())})
        agent_list.append((t['units'], se['sid'][:8], sub['agentType'], sub['desc'][:60], t['requests'], sum(sub['tools'].values()), sub['errors'], t['n_opus'], t['n_sonnet'], t['n_haiku']))
    mins = ((se['main']['last'] - se['main']['first']).total_seconds() / 60) if se['main']['first'] and se['main']['last'] else 0
    reps = [(c, f) for f, c in se['main']['reads'].items() if c >= 3]
    rows.append(dict(sid=se['sid'][:8], date=se['mtime'].strftime('%m-%d %H:%M'), title=(se['main']['title'] or '')[:55],
                     contracts=','.join(sorted(se['main']['contracts']))[:30], mins=int(mins), req=tm['requests'],
                     main_units=tm['units'], sub_units=ts['units'], n_sub=len(se['subs']), cr=tm['cr'], cw=tm['cw'], out=tm['out'],
                     tools=sum(se['main']['tools'].values()), err=se['main']['errors'] + sum(x['errors'] for x in se['subs']),
                     big=len(se['main']['big']), big_chars=se['main']['big_total'], mdreads=se['main']['mdreads'] + sum(x['mdreads'] for x in se['subs']),
                     reps=len(reps), opus=tm['n_opus'] + ts['n_opus'], sonnet=tm['n_sonnet'] + ts['n_sonnet'], haiku=tm['n_haiku'] + ts['n_haiku'],
                     bigs=se['main']['big'], rep_list=reps))

if args.session:
    if not rows:
        print('session not found: ' + args.session); sys.exit(1)
    r = rows[0]
    print('session %s: main %.2f M units, agents %d (%.2f M units), requests %d, tool errors %d, wall-clock %d min, '
          'opus/sonnet/haiku requests %d/%d/%d. Relative units (input 1, cache-write 1.25, cache-read 0.1, output 5, model ratio haiku .33 sonnet 1 opus 1.67), not a bill.'
          % (r['sid'], r['main_units'] / 1e6, r['n_sub'], r['sub_units'] / 1e6, r['req'], r['err'], r['mins'], r['opus'], r['sonnet'], r['haiku']))
    sys.exit(0)
tot_main = sum(r['main_units'] for r in rows); tot_sub = sum(r['sub_units'] for r in rows)
lines = []
A = lines.append
A('# Budget review - sessions since 2026-09-28 (relative cost units)')
A('')
A('Units = (input*1 + cache-write*1.25 + cache-read*0.1 + output*5) * model price ratio (haiku 0.33, sonnet 1, opus 1.67). These are RELATIVE, assumed weights, not a bill. Subagent usage comes from each agent transcript under <session>/subagents.')
A('')
A('Sessions: %d | main-context units: %.1f M | subagent units: %.1f M | subagents: %d' % (len(rows), tot_main / 1e6, tot_sub / 1e6, sum(r['n_sub'] for r in rows)))
A('')
A('## Per session (top 20 by total units)')
A('| session | date | topic | contract edited | min | requests | main M | agents | agent M | cache-read M | errors | big results | big chars k | AGENTS/CLAUDE whole reads |')
A('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
for r in sorted(rows, key=lambda r: r['main_units'] + r['sub_units'], reverse=True)[:20]:
    A('| %s | %s | %s | %s | %d | %d | %.2f | %d | %.2f | %.1f | %d | %d | %d | %d |' % (r['sid'], r['date'], r['title'], r['contracts'], r['mins'], r['req'], r['main_units'] / 1e6, r['n_sub'], r['sub_units'] / 1e6, r['cr'] / 1e6, r['err'], r['big'], r['big_chars'] / 1000, r['mdreads']))
A('')
A('## Subagents by role/model')
A('| role/model | agents | requests | tool calls | units M | avg units M | errors |')
A('|---|---|---|---|---|---|---|')
for k, v in sorted(agent_roll.items(), key=lambda kv: kv[1]['units'], reverse=True):
    A('| %s | %d | %d | %d | %.2f | %.2f | %d |' % (k, v['agents'], v['requests'], v['tools'], v['units'] / 1e6, v['units'] / v['agents'] / 1e6, v['errors']))
A('')
A('## 12 most expensive single agents')
A('| units M | session | role | task | requests | tools | errors | opus/sonnet/haiku requests |')
A('|---|---|---|---|---|---|---|---|')
for a in sorted(agent_list, reverse=True)[:12]:
    A('| %.2f | %s | %s | %s | %d | %d | %d | %d/%d/%d |' % (a[0] / 1e6, a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9]))
A('')
A('## Largest tool results in main contexts (each is re-sent on every later request)')
allbig = sorted([(b[0], r['sid'], b[1], b[2]) for r in rows for b in r['bigs']], reverse=True)[:12]
for b in allbig:
    A('- %6d chars | %s | %s | %s' % (b[0], b[1], b[2], b[3]))
A('')
A('## Files read 3+ times in one session (main context)')
for r in rows:
    for c, f in sorted(r['rep_list'], reverse=True)[:3]:
        A('- %s | %dx | %s' % (r['sid'], c, f[-90:]))
A('')
open(os.path.join(OUT, 'report.md'), 'w', encoding='utf-8').write('\n'.join(lines))
print('\n'.join(lines[:60]))
print('... full report: artifacts/budget-review/20261003/report.md')

# ---- extra aggregates -------------------------------------------------------------------------------------------
comp = collections.Counter(); modelu = collections.Counter(); bucket = collections.Counter()
for se in sessions:
    for part in [se['main']] + se['subs']:
        for (mk, i, cw, cr, out) in part['req'].values():
            f = MW.get(mk, 1.0)
            comp['input'] += i * W['inp'] * f; comp['cache_write'] += cw * W['cw'] * f
            comp['cache_read'] += cr * W['cr'] * f; comp['output'] += out * W['out'] * f
            modelu[mk] += (i * W['inp'] + cw * W['cw'] + cr * W['cr'] + out * W['out']) * f
tot = sum(comp.values())
x = []
x.append('## Cost composition (all sessions)')
for k, v in comp.items():
    x.append('- %s: %.1f%% (%.1f M units)' % (k, 100 * v / tot, v / 1e6))
x.append('## By model')
for k, v in modelu.most_common():
    x.append('- %s: %.1f%% (%.1f M)' % (k, 100 * v / tot, v / 1e6))
longs = [r for r in rows if r['req'] >= 250]
x.append('## Long sessions (>=250 requests): %d sessions = %.1f%% of all units' % (len(longs), 100 * sum(r['main_units'] + r['sub_units'] for r in longs) / tot))
top2 = sorted(rows, key=lambda r: r['main_units'] + r['sub_units'], reverse=True)[:2]
x.append('## Top 2 sessions share: %.1f%%' % (100 * sum(r['main_units'] + r['sub_units'] for r in top2) / tot))
ve = [(v['errors'], v['agents']) for k, v in agent_roll.items() if k.startswith('easyvision-verifier')]
x.append('## Verifier errors/agent: %d errors over %d agents' % (sum(e for e, _ in ve), sum(a for _, a in ve)))
bigall = sum(r['big_chars'] for r in rows)
x.append('## Big tool results (>8k chars) total: %.1f M chars across %d results' % (bigall / 1e6, sum(r['big'] for r in rows)))
mdr = sum(r['mdreads'] for r in rows)
x.append('## Whole reads of AGENTS/CLAUDE/HISTORY (no limit): %d' % mdr)
# work units by agent role share
roles = collections.Counter()
for k, v in agent_roll.items():
    roles[k.split('/')[0]] += v['units']
x.append('## Subagent units by role')
for k, v in roles.most_common(8):
    x.append('- %s: %.1f M' % (k, v / 1e6))
open(os.path.join(OUT, 'report.md'), 'a', encoding='utf-8').write('\n' + '\n'.join(x) + '\n')
print('\n'.join(x))

# ---- main-context model split and per-contract view ------------------------------------------------------------
mm = collections.Counter(); ms = collections.Counter()
for se in sessions:
    for (mk, i, cw, cr, out) in se['main']['req'].values():
        mm[mk] += (i * W['inp'] + cw * W['cw'] + cr * W['cr'] + out * W['out']) * MW.get(mk, 1.0)
    for sub in se['subs']:
        for (mk, i, cw, cr, out) in sub['req'].values():
            ms[mk] += (i * W['inp'] + cw * W['cw'] + cr * W['cr'] + out * W['out']) * MW.get(mk, 1.0)
y = ['## Main-context units by model: ' + ', '.join('%s %.1f M' % (k, v / 1e6) for k, v in mm.most_common()),
     '## Subagent units by model: ' + ', '.join('%s %.1f M' % (k, v / 1e6) for k, v in ms.most_common())]
# sessions that edited exactly one contract = clean per-contract cost
y.append('## Sessions that edited exactly one contract (clean per-contract cost, units M = main + agents)')
for r in sorted(rows, key=lambda r: r['main_units'] + r['sub_units'], reverse=True):
    if r['contracts'] and ',' not in r['contracts']:
        y.append('- %s: %.1f M (%s, %d min, %d requests, %d agents, %d errors)' % (r['contracts'], (r['main_units'] + r['sub_units']) / 1e6, r['sid'], r['mins'], r['req'], r['n_sub'], r['err']))
open(os.path.join(OUT, 'report.md'), 'a', encoding='utf-8').write('\n' + '\n'.join(y) + '\n')
print('\n'.join(y))
