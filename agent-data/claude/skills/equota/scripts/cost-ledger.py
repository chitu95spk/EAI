"""Cost ledger for Claude sessions of this project. Read-only on transcripts; writes only docs/agent-lessons/cost-ledger.csv.

  record:  python cost-ledger.py --session <guid> [--note "text"]   append one row (main + subagents) for that session
  report:  python cost-ledger.py --report [--days N] [--by day|week|month|contract|model|session]
  detail:  python cost-ledger.py --detail [--days N]   full dashboard: totals, tokens, model/agent USD, days, top sessions/contracts, trend
  Every call first refreshes prices.json from the live pricing page (skip with --no-refresh; --refresh-only just refreshes).

Units are RELATIVE: input*1 + cache_write*1.25 + cache_read*0.1 + output*5, times a model ratio (haiku .33, sonnet 1,
opus 1.67). Assumed weights, not a bill. The ledger is append-only; a session recorded again adds a newer row and the
report uses the latest row per session.
"""
import argparse, csv, datetime, glob, json, os, re, sys, collections

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..'))
PROJ = os.path.join(os.path.expanduser('~'), '.claude', 'projects', re.sub(r'[:\\/ _.]', '-', ROOT))
LEDGER = os.path.join(ROOT, 'docs', 'agent-lessons', 'cost-ledger.csv')
AGENT_LEDGER = os.path.join(ROOT, 'docs', 'agent-lessons', 'agent-ledger.csv')
AFIELDS = ['recorded_at', 'session', 'agent_id', 'role', 'model', 'description', 'started', 'minutes', 'requests', 'tool_calls', 'tool_errors',
           'edits', 'code_edits', 'reads', 'repeat_reads', 'result_k_chars', 'max_result_k_chars', 'images', 'final_chars',
           'first_ctx_k', 'max_ctx_k', 'input_m', 'cache_write_m', 'cache_read_m', 'output_m',
           'in_side_usd', 'out_side_usd', 'cost_usd', 'cache_reuse_pct', 'out_usd_pct']
W = dict(inp=1.0, cw=1.25, cr=0.1, out=5.0)
MW = dict(haiku=0.33, sonnet=1.0, opus=1.67, fable=5.0)   # fable/mythos: $10/$50 vs sonnet $2/$10; counted in 'other_usd'
# Prices live in ../prices.json (USD per million tokens, with an as_of date). Every call refreshes them from the live pricing page
# (refresh_prices) unless --no-refresh is given or the network fails; each ledger row keeps the dollars computed with the prices
# valid when it was recorded, old rows are never repriced.
PRICE_FILE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'prices.json')
PRICE_URL = 'https://platform.claude.com/docs/en/about-claude/pricing.md'
PRICE_AS_OF = ''
M5 = M1 = MR = 0.0
PRICES = []


def load_prices():
    global PRICE_AS_OF, M5, M1, MR, PRICES
    pj = json.load(open(PRICE_FILE, encoding='utf-8'))
    PRICE_AS_OF = pj.get('as_of', '')
    M5, M1, MR = pj.get('cache_write_5m_multiplier', 1.25), pj.get('cache_write_1h_multiplier', 2.0), pj.get('cache_read_multiplier_default', 0.1)
    # most specific key first so 'opus-4-1' wins over 'opus-4' and 'fable-5-1' over 'fable-5'
    ms = sorted(pj['models'], key=lambda m: -len(m['key']))
    PRICES = [(m['key'], float(m['in']), float(m['out']), m.get('read'), m.get('w5'), m.get('w1')) for m in ms]
    return pj


def _money(cell):
    m = re.search(r'\$([0-9.]+)', cell)
    return float(m.group(1)) if m else None


def parse_pricing_table(md):
    """Rows of the first 'Model | Base input | 5m | 1h | cache hits | Output' table -> {key: {in,out,read,w5,w1}}."""
    out = {}
    for line in md.splitlines():
        if not line.startswith('| Claude '):
            if out and not line.startswith('|'):
                break
            continue
        c = [x.strip() for x in line.strip().strip('|').split('|')]
        if len(c) < 6:
            continue
        name = re.sub(r'\(.*?\)\)?', '', c[0])           # drop "([retired...](url))"
        name = re.sub(r'\[.*', '', name).strip().lower()
        key = re.sub(r'[ .]+', '-', name.replace('claude ', '').strip()).strip('-')
        v = [_money(x) for x in c[1:6]]
        if key and all(x is not None for x in v):
            out[key] = dict(key=key, **{'in': v[0], 'w5': v[1], 'w1': v[2], 'read': v[3], 'out': v[4]})
    return out


def refresh_prices():
    """Fetch the live pricing page and rewrite prices.json when it changed. Returns a one-line status for the user."""
    import urllib.request
    try:
        req = urllib.request.Request(PRICE_URL, headers={'User-Agent': 'Mozilla/5.0 equota'})
        md = urllib.request.urlopen(req, timeout=20).read().decode('utf-8', 'replace')
        got = parse_pricing_table(md)
    except Exception as e:
        return 'prices: NOT refreshed (%s: %s); using prices.json as_of %s' % (type(e).__name__, e, PRICE_AS_OF or 'unknown')
    fam = lambda f: any(k.startswith(f) for k in got)
    if len(got) < 5 or not (fam('opus') and fam('sonnet') and fam('haiku')):
        return 'prices: NOT refreshed (pricing page parsed to %d rows, layout changed?); using prices.json as_of %s' % (len(got), PRICE_AS_OF or 'unknown')
    pj = json.load(open(PRICE_FILE, encoding='utf-8'))
    old = {m['key']: m for m in pj['models']}
    changes = []
    for k, m in got.items():
        o = old.get(k)
        if o is None:
            changes.append('+%s' % k)
        elif any(abs((o.get(f) if o.get(f) is not None else o['in'] * 0.1 if f == 'read' else -1) - m[f]) > 1e-9 for f in ('in', 'out', 'read')):
            changes.append('%s: in/out/read %s/%s/%s -> %s/%s/%s' % (k, o.get('in'), o.get('out'), o.get('read'), m['in'], m['out'], m['read']))
    # entries the page no longer lists (or generic family fallbacks) are kept after the parsed ones
    keep = [o for k, o in old.items() if k not in got]
    pj['models'] = list(got.values()) + keep
    today = datetime.date.today().isoformat()
    if changes or pj.get('as_of') != today:
        pj['as_of'] = today
        pj['source'] = PRICE_URL + ' (fetched automatically by cost-ledger.py); first-party API list prices, USD per million tokens'
        json.dump(pj, open(PRICE_FILE, 'w', encoding='utf-8'), indent=2)
    return 'prices: REFRESHED from live pricing page (%d models, %s)' % (len(got), ('changed: ' + '; '.join(changes)) if changes else 'no price changes')


FIELDS = ['recorded_at', 'session', 'price_as_of', 'started', 'ended', 'minutes', 'contracts', 'requests', 'agents', 'errors',
          'cost_usd', 'main_usd', 'agent_usd', 'haiku_usd', 'sonnet_usd', 'opus_usd', 'other_usd', 'unpriced_req',
          'main_units_m', 'agent_units_m', 'total_units_m', 'cache_read_m', 'cache_write_m', 'output_m',
          'haiku_units_m', 'sonnet_units_m', 'opus_units_m', 'haiku_req', 'sonnet_req', 'opus_req',
          'implementer_units_m', 'reviewer_units_m', 'explorer_units_m', 'other_agent_units_m', 'note',
          'input_m', 'implementer_usd', 'reviewer_usd', 'explorer_usd', 'other_agent_usd']


def mkey(m):
    m = (m or '').lower()
    if 'mythos' in m:
        return 'fable'
    for k in MW:
        if k in m:
            return k
    return 'sonnet'


def dollars_parts(model_id, i, cw5, cw1, cr, out):
    """(input-side USD, output-side USD) for one request, or None when the model id has no price entry."""
    m = (model_id or '').lower()
    for key, pin, pout, pread, pw5, pw1 in PRICES:
        if key in m:
            read = pread if pread is not None else pin * MR
            w5 = pw5 if pw5 is not None else pin * M5
            w1 = pw1 if pw1 is not None else pin * M1
            return (i * pin + cw5 * w5 + cw1 * w1 + cr * read) / 1e6, out * pout / 1e6
    return None


def dollars(model_id, i, cw5, cw1, cr, out):
    """USD for one request, or None when the model id has no price entry."""
    p = dollars_parts(model_id, i, cw5, cw1, cr, out)
    return None if p is None else p[0] + p[1]


def scan(path):
    s = dict(req={}, errors=0, first=None, last=None, contracts=set(), tools=collections.Counter(), res_chars=0, res_max=0,
             reads=collections.Counter(), final_chars=0, edits=0, code_edits=0, images=0, tool_err_by=collections.Counter())
    names = {}
    for line in open(path, encoding='utf-8', errors='replace'):
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
        m = o.get('message')
        if not isinstance(m, dict):
            continue
        if o.get('type') == 'assistant':
            u = m.get('usage')
            mid = m.get('id') or o.get('requestId') or o.get('uuid')
            if u and mid:
                cwt = u.get('cache_creation_input_tokens', 0) or 0
                cc = u.get('cache_creation') or {}
                c5 = cc.get('ephemeral_5m_input_tokens', 0) or 0
                c1 = cc.get('ephemeral_1h_input_tokens', 0) or 0
                if c5 + c1 != cwt:      # split missing or inconsistent: treat the whole write as 1-hour (this session class uses 1h)
                    c5, c1 = 0, cwt
                parts = dollars_parts(m.get('model'), u.get('input_tokens', 0) or 0, c5, c1, u.get('cache_read_input_tokens', 0) or 0, u.get('output_tokens', 0) or 0)
                usd = None if parts is None else parts[0] + parts[1]
                s['req'][mid] = (mkey(m.get('model')), u.get('input_tokens', 0) or 0, cwt,
                                 u.get('cache_read_input_tokens', 0) or 0, u.get('output_tokens', 0) or 0, usd,
                                 None if parts is None else parts[1])
            c = m.get('content')
            if isinstance(c, list):
                txt = sum(len(b.get('text') or '') for b in c if b.get('type') == 'text')
                if txt:
                    s['final_chars'] = txt      # the last assistant text wins = the hand-back report
                for b in c:
                    if b.get('type') == 'tool_use':
                        nm = b.get('name') or '?'
                        names[b.get('id')] = nm
                        s['tools'][nm] += 1
                        if nm in ('Edit', 'Write', 'NotebookEdit'):
                            s['edits'] += 1
                            fpe = ((b.get('input') or {}).get('file_path') or '').replace(chr(92), '/')
                            if re.search(r'\.(cs|cpp|h|hpp|c|csproj|vcxproj|xaml|resx|py|ps1)$', fpe) and not re.search(r'/(\.claude|\.agents|docs|tmp|artifacts|memory)/', fpe):
                                s['code_edits'] += 1
                        if nm == 'Read':
                            s['reads'][(b.get('input') or {}).get('file_path') or '?'] += 1
                    if b.get('type') == 'tool_use' and b.get('name') in ('Edit', 'Write'):
                        fp = (b.get('input') or {}).get('file_path') or ''
                        mm = re.search(r'task-contract\.([^\\/]+)\.toml$', fp)
                        if mm and mm.group(1) != 'template':
                            s['contracts'].add(mm.group(1))
        elif o.get('type') == 'user' and isinstance(m.get('content'), list):
            for b in m['content']:
                if b.get('type') == 'tool_result':
                    if b.get('is_error'):
                        s['errors'] += 1
                        s['tool_err_by'][names.get(b.get('tool_use_id'), '?')] += 1
                    rc = b.get('content')
                    if isinstance(rc, str):
                        n = len(rc)
                    elif isinstance(rc, list):
                        n = sum(len(x.get('text') or '') for x in rc if isinstance(x, dict))
                        s['images'] += sum(1 for x in rc if isinstance(x, dict) and x.get('type') == 'image')
                    else:
                        n = 0
                    s['res_chars'] += n
                    s['res_max'] = max(s['res_max'], n)
    return s


def units(r):
    mk, i, cw, cr, out = r[:5]
    return (i * W['inp'] + cw * W['cw'] + cr * W['cr'] + out * W['out']) * MW[mk]


def agent_row(session, agent_id, role, desc, sc):
    reqs = list(sc['req'].values())
    tot = collections.Counter(); ins = outs = 0.0; models = collections.Counter(); ctx = []
    for r in reqs:
        tot['in'] += r[1]; tot['cw'] += r[2]; tot['cr'] += r[3]; tot['out'] += r[4]; models[r[0]] += 1
        ctx.append(r[1] + r[2] + r[3])
        if r[5] is not None:
            outs += r[6] or 0.0; ins += r[5] - (r[6] or 0.0)
    mins = int((sc['last'] - sc['first']).total_seconds() / 60) if sc['first'] and sc['last'] else 0
    repeat = sum(v - 1 for v in sc['reads'].values() if v > 1)
    usd = ins + outs
    return {'recorded_at': datetime.datetime.now().strftime('%Y-%m-%d %H:%M'), 'session': session, 'agent_id': agent_id, 'role': role,
            'model': models.most_common(1)[0][0] if models else '', 'description': (desc or '')[:70].replace(chr(10), ' '),
            'started': sc['first'].strftime('%Y-%m-%d %H:%M') if sc['first'] else '', 'minutes': mins, 'requests': len(reqs),
            'tool_calls': sum(sc['tools'].values()), 'tool_errors': sc['errors'], 'edits': sc['edits'], 'code_edits': sc['code_edits'], 'reads': sc['tools']['Read'],
            'repeat_reads': repeat, 'result_k_chars': round(sc['res_chars'] / 1000, 1), 'max_result_k_chars': round(sc['res_max'] / 1000, 1),
            'images': sc['images'], 'final_chars': sc['final_chars'], 'first_ctx_k': round(ctx[0] / 1000, 1) if ctx else 0,
            'max_ctx_k': round(max(ctx) / 1000, 1) if ctx else 0, 'input_m': round(tot['in'] / 1e6, 3), 'cache_write_m': round(tot['cw'] / 1e6, 3),
            'cache_read_m': round(tot['cr'] / 1e6, 3), 'output_m': round(tot['out'] / 1e6, 4), 'in_side_usd': round(ins, 3),
            'out_side_usd': round(outs, 3), 'cost_usd': round(usd, 3),
            'cache_reuse_pct': round(100 * tot['cr'] / (tot['cr'] + tot['cw'] + tot['in']), 1) if (tot['cr'] + tot['cw'] + tot['in']) else 0,
            'out_usd_pct': round(100 * outs / usd, 1) if usd else 0}


def save_agent_rows(session, rows):
    """Upsert: replace every row of this session (so re-recording never duplicates), keep all other rows untouched."""
    old = []
    if os.path.isfile(AGENT_LEDGER):
        with open(AGENT_LEDGER, newline='', encoding='utf-8') as f:
            old = [r for r in csv.DictReader(f) if r['session'] != session]
    os.makedirs(os.path.dirname(AGENT_LEDGER), exist_ok=True)
    with open(AGENT_LEDGER, 'w', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=AFIELDS, extrasaction='ignore')
        w.writeheader()
        for r in old + rows:
            w.writerow(r)


def record(session, note, quiet=False, agents_only=False):
    p = os.path.join(PROJ, session + '.jsonl')
    if not os.path.isfile(p):
        print('transcript not found: ' + p)
        return 1
    main = scan(p)
    arows = [agent_row(session, 'main', 'main', 'main session', main)]
    by_model = collections.Counter(); req_model = collections.Counter(); role = collections.Counter()
    tot = collections.Counter(); agents = 0; errors = main['errors']; role_usd = collections.Counter()
    mu = 0.0
    usd_main = usd_agent = 0.0; unpriced = 0; usd_model = collections.Counter()
    for r in main['req'].values():
        u = units(r); mu += u; by_model[r[0]] += u; req_model[r[0]] += 1
        if r[5] is None:
            unpriced += 1
        else:
            usd_main += r[5]; usd_model[r[0]] += r[5]
        tot['cr'] += r[3]; tot['cw'] += r[2]; tot['out'] += r[4]; tot['in'] += r[1]
    au = 0.0
    sd = os.path.join(PROJ, session, 'subagents')
    for sp in glob.glob(os.path.join(sd, 'agent-*.jsonl')) if os.path.isdir(sd) else []:
        agents += 1
        meta = {}
        try:
            meta = json.load(open(sp[:-6] + '.meta.json', encoding='utf-8'))
        except Exception:
            pass
        sub = scan(sp); errors += sub['errors']
        at = meta.get('agentType', '')
        arows.append(agent_row(session, os.path.basename(sp)[6:-6], at.replace('easyvision-', '') or 'unknown', meta.get('description', ''), sub))
        key = 'implementer' if 'implementer' in at else 'reviewer' if 'reviewer' in at else 'explorer' if 'explorer' in at else 'other'
        for r in sub['req'].values():
            u = units(r); au += u; by_model[r[0]] += u; req_model[r[0]] += 1; role[key] += u
            if r[5] is None:
                unpriced += 1
            else:
                usd_agent += r[5]; usd_model[r[0]] += r[5]; role_usd[key] += r[5]
            tot['cr'] += r[3]; tot['cw'] += r[2]; tot['out'] += r[4]; tot['in'] += r[1]
    mins = int((main['last'] - main['first']).total_seconds() / 60) if main['first'] and main['last'] else 0
    row = {
        'recorded_at': datetime.datetime.now().strftime('%Y-%m-%d %H:%M'), 'session': session, 'price_as_of': PRICE_AS_OF,
        'started': main['first'].strftime('%Y-%m-%d %H:%M') if main['first'] else '', 'ended': main['last'].strftime('%Y-%m-%d %H:%M') if main['last'] else '',
        'minutes': mins, 'contracts': ';'.join(sorted(main['contracts'])), 'requests': len(main['req']), 'agents': agents, 'errors': errors,
        'cost_usd': round(usd_main + usd_agent, 2), 'main_usd': round(usd_main, 2), 'agent_usd': round(usd_agent, 2),
        'haiku_usd': round(usd_model['haiku'], 2), 'sonnet_usd': round(usd_model['sonnet'], 2), 'opus_usd': round(usd_model['opus'], 2),
        'other_usd': round(sum(v for k, v in usd_model.items() if k not in ('haiku', 'sonnet', 'opus')), 2), 'unpriced_req': unpriced,
        'main_units_m': round(mu / 1e6, 3), 'agent_units_m': round(au / 1e6, 3), 'total_units_m': round((mu + au) / 1e6, 3),
        'cache_read_m': round(tot['cr'] / 1e6, 2), 'cache_write_m': round(tot['cw'] / 1e6, 2), 'output_m': round(tot['out'] / 1e6, 3),
        'haiku_units_m': round(by_model['haiku'] / 1e6, 3), 'sonnet_units_m': round(by_model['sonnet'] / 1e6, 3), 'opus_units_m': round(by_model['opus'] / 1e6, 3),
        'haiku_req': req_model['haiku'], 'sonnet_req': req_model['sonnet'], 'opus_req': req_model['opus'],
        'implementer_units_m': round(role['implementer'] / 1e6, 3), 'reviewer_units_m': round(role['reviewer'] / 1e6, 3),
        'explorer_units_m': round(role['explorer'] / 1e6, 3), 'other_agent_units_m': round(role['other'] / 1e6, 3), 'note': note,
        'input_m': round(tot['in'] / 1e6, 3), 'implementer_usd': round(role_usd['implementer'], 2), 'reviewer_usd': round(role_usd['reviewer'], 2),
        'explorer_usd': round(role_usd['explorer'], 2), 'other_agent_usd': round(role_usd['other'], 2)}
    if agents_only:
        save_agent_rows(session, arows)
        return 0
    os.makedirs(os.path.dirname(LEDGER), exist_ok=True)
    new = not os.path.isfile(LEDGER)
    if not new:     # older ledger without the USD columns: rewrite with the new header, keeping every row (new cells blank)
        with open(LEDGER, newline='', encoding='utf-8') as f:
            old_rows = list(csv.DictReader(f))
            old_header = f.name and (old_rows[0].keys() if old_rows else [])
        if list(old_header) != FIELDS:
            with open(LEDGER, 'w', newline='', encoding='utf-8') as f:
                w = csv.DictWriter(f, fieldnames=FIELDS, extrasaction='ignore')
                w.writeheader()
                for orow in old_rows:
                    w.writerow(orow)
    with open(LEDGER, 'a', newline='', encoding='utf-8') as f:
        w = csv.DictWriter(f, fieldnames=FIELDS)
        if new:
            w.writeheader()
        w.writerow(row)
    save_agent_rows(session, arows)
    if quiet:
        return 0
    print('recorded %s: total %.2f M units (main %.2f + agents %d = %.2f), %d requests, %d errors, %d min, contracts: %s'
          % (session[:8], row['total_units_m'], row['main_units_m'], agents, row['agent_units_m'], row['requests'], errors, mins, row['contracts'] or 'none'))
    print('by model (M units): haiku %.2f, sonnet %.2f, opus %.2f. Relative units.' % (by_model['haiku'] / 1e6, by_model['sonnet'] / 1e6, by_model['opus'] / 1e6))
    print('cost USD (list price, estimate): $%.2f = main $%.2f + agents $%.2f | haiku $%.2f, sonnet $%.2f, opus $%.2f%s'
          % (row['cost_usd'], row['main_usd'], row['agent_usd'], row['haiku_usd'], row['sonnet_usd'], row['opus_usd'],
             (' | %d requests had no price entry and are NOT in the $ total' % unpriced) if unpriced else ''))
    print('ledger: %s | per-agent log: %s (%d rows for this session)' % (os.path.relpath(LEDGER, ROOT), os.path.relpath(AGENT_LEDGER, ROOT), len(arows)))
    return 0


def report(days, by):
    if not os.path.isfile(LEDGER):
        print('no ledger yet: record a session first (--session <guid>)')
        return 1
    latest = {}
    for r in csv.DictReader(open(LEDGER, encoding='utf-8')):
        latest[r['session']] = r
    since = datetime.datetime.now() - datetime.timedelta(days=days) if days else datetime.datetime(2000, 1, 1)
    rows = []
    for r in latest.values():
        try:
            end = datetime.datetime.strptime(r['ended'] or r['recorded_at'], '%Y-%m-%d %H:%M')
        except Exception:
            continue
        if end >= since:
            r['_end'] = end
            rows.append(r)
    if not rows:
        print('no recorded sessions in the period')
        return 0
    f = lambda r, k: float(r.get(k) or 0)
    tot = sum(f(r, 'total_units_m') for r in rows)
    print('== COST LEDGER: %d sessions, %.1f M units total (last %s), relative units not a bill ==' % (len(rows), tot, ('%d days' % days) if days else 'all time'))
    groups = collections.defaultdict(list)
    for r in rows:
        if by == 'day':
            k = r['_end'].strftime('%Y-%m-%d')
        elif by == 'week':
            y, w, _ = r['_end'].isocalendar(); k = '%d-W%02d' % (y, w)
        elif by == 'month':
            k = r['_end'].strftime('%Y-%m')
        elif by == 'contract':
            for c in (r['contracts'].split(';') if r['contracts'] else ['(no contract)']):
                groups[c].append(r)
            continue
        elif by == 'model':
            k = 'all'
        else:
            k = r['session'][:8]
        groups[k].append(r)
    if by == 'model':
        for m in ('haiku', 'sonnet', 'opus'):
            u = sum(f(r, m + '_units_m') for r in rows); q = sum(f(r, m + '_req') for r in rows)
            print('  %-8s %8.1f M units (%4.1f%%), %6d requests, $%.2f' % (m, u, 100 * u / tot if tot else 0, q, sum(f(r, m + '_usd') for r in rows)))
        for rl in ('implementer', 'reviewer', 'explorer', 'other_agent'):
            print('  agents/%-12s %8.1f M units' % (rl, sum(f(r, rl + '_units_m') for r in rows)))
        return 0
    usd_total = sum(f(r, 'cost_usd') for r in rows)
    print('  total USD in period: $%.2f (rows recorded before the $ column existed count as $0; estimate at list prices)' % usd_total)
    print('  %-16s %5s %10s %9s %9s %7s %7s' % (by, 'sess', 'USD', 'total M', 'agents M', 'reqs', 'errors'))
    for k, g in sorted(groups.items(), key=lambda kv: (kv[0] if by in ('day', 'week', 'month') else -sum(f(r, 'total_units_m') for r in kv[1])), reverse=(by in ('day', 'week', 'month'))):
        print('  %-16s %5d %10.2f %9.2f %9.2f %7d %7d' % (k[:16], len(g), sum(f(r, 'cost_usd') for r in g), sum(f(r, 'total_units_m') for r in g), sum(f(r, 'agent_units_m') for r in g),
                                                         sum(f(r, 'requests') for r in g), sum(f(r, 'errors') for r in g)))
    if by == 'contract':
        print('  note: a session that edited several contracts is counted in each of them, so the column can exceed the total.')
    return 0


def _latest_rows():
    latest = {}
    for r in csv.DictReader(open(LEDGER, encoding='utf-8')):
        latest[r['session']] = r
    out = []
    for r in latest.values():
        try:
            r['_end'] = datetime.datetime.strptime(r['ended'] or r['recorded_at'], '%Y-%m-%d %H:%M')
        except Exception:
            continue
        out.append(r)
    return out


def _sum(rows, k):
    return sum(float(r.get(k) or 0) for r in rows)


def _bar(x, tot, width=24):
    n = int(round(width * x / tot)) if tot else 0
    return '#' * n + '.' * (width - n)


def detail(days):
    """Full statistics dashboard for the last `days` days (0 = all time)."""
    if not os.path.isfile(LEDGER):
        print('no ledger yet: record a session first (--session <guid>)')
        return 1
    now = datetime.datetime.now()
    allrows = _latest_rows()
    since = now - datetime.timedelta(days=days) if days else datetime.datetime(2000, 1, 1)
    rows = [r for r in allrows if r['_end'] >= since]
    if not rows:
        print('no recorded sessions in the period')
        return 0
    first, last = min(r['_end'] for r in rows), max(r['_end'] for r in rows)
    span = days if days else max(1, (now.date() - first.date()).days + 1)
    usd, units = _sum(rows, 'cost_usd'), _sum(rows, 'total_units_m')
    reqs, mins, errs, agents = _sum(rows, 'requests'), _sum(rows, 'minutes'), _sum(rows, 'errors'), _sum(rows, 'agents')
    print('== EV-QUOTA DETAIL: %s (%s .. %s) ==' % ((('last %d days' % days) if days else 'all time'), first.strftime('%Y-%m-%d'), last.strftime('%Y-%m-%d')))
    print('ledger USD uses the list prices valid when each session was recorded (price_as_of %s .. %s)' % (
        min((r.get('price_as_of') or '?') for r in rows), max((r.get('price_as_of') or '?') for r in rows)))
    print('')
    print('-- TOTALS --')
    print('  sessions %d | USD $%.2f | %.2f M units | requests %d | subagents %d | tool errors %d | wall time %.1f h' % (len(rows), usd, units, reqs, agents, errs, mins / 60))
    print('  per day (span %d d): $%.2f, %.2f M units | per session: $%.2f, %.0f min | per request: $%.4f | per hour: $%.2f' % (
        span, usd / span, units / span, usd / len(rows), mins / len(rows), usd / reqs if reqs else 0, usd / (mins / 60) if mins else 0))
    print('  errors per 100 requests %.1f | agents per session %.1f | requests without a price entry %d' % (100 * errs / reqs if reqs else 0, agents / len(rows), _sum(rows, 'unpriced_req')))
    print('')
    print('-- TOKENS (M) --')
    cr, cw, out, inp = _sum(rows, 'cache_read_m'), _sum(rows, 'cache_write_m'), _sum(rows, 'output_m'), _sum(rows, 'input_m')
    allt = cr + cw + out + inp
    print('  cache read %.2f | cache write %.2f | output %.3f | fresh input %.3f%s' % (cr, cw, out, inp, '' if inp else ' (input_m is recorded only from the new column on)'))
    print('  cache reuse read/(read+write): %.1f%% | output share of all tokens: %.2f%%' % (100 * cr / (cr + cw) if cr + cw else 0, 100 * out / allt if allt else 0))
    print('')
    print('-- BY MODEL --')
    print('  %-8s %10s %6s %10s %8s %9s' % ('model', 'USD', 'USD%', 'M units', 'reqs', '$/req'))
    for m in ('haiku', 'sonnet', 'opus'):
        u, q, d = _sum(rows, m + '_units_m'), _sum(rows, m + '_req'), _sum(rows, m + '_usd')
        print('  %-8s %10.2f %5.1f%% %10.2f %8d %9.4f  %s' % (m, d, 100 * d / usd if usd else 0, u, q, d / q if q else 0, _bar(d, usd)))
    if _sum(rows, 'other_usd'):
        print('  %-8s %10.2f %5.1f%%' % ('other', _sum(rows, 'other_usd'), 100 * _sum(rows, 'other_usd') / usd if usd else 0))
    print('')
    print('-- MAIN vs SUBAGENTS --')
    mu, au = _sum(rows, 'main_usd'), _sum(rows, 'agent_usd')
    print('  main $%.2f (%.1f%%) | subagents $%.2f (%.1f%%)' % (mu, 100 * mu / usd if usd else 0, au, 100 * au / usd if usd else 0))
    for rl in ('implementer', 'reviewer', 'explorer', 'other_agent'):
        print('  agents/%-12s $%8.2f  %7.2f M units' % (rl, _sum(rows, rl + '_usd'), _sum(rows, rl + '_units_m')))
    print('')
    print('-- BY DAY (newest first, up to 14) --')
    byday = collections.defaultdict(list)
    for r in rows:
        byday[r['_end'].strftime('%Y-%m-%d')].append(r)
    top = max(_sum(g, 'cost_usd') for g in byday.values())
    print('  %-10s %5s %9s %8s %7s %6s' % ('day', 'sess', 'USD', 'M units', 'reqs', 'errs'))
    for k in sorted(byday, reverse=True)[:14]:
        g = byday[k]
        print('  %-10s %5d %9.2f %8.2f %7d %6d  %s' % (k, len(g), _sum(g, 'cost_usd'), _sum(g, 'total_units_m'), _sum(g, 'requests'), _sum(g, 'errors'), _bar(_sum(g, 'cost_usd'), top, 20)))
    best = max(byday.items(), key=lambda kv: _sum(kv[1], 'cost_usd'))
    print('  top day: %s $%.2f (%d sessions)' % (best[0], _sum(best[1], 'cost_usd'), len(best[1])))
    print('')
    print('-- TOP 5 SESSIONS BY USD --')
    for r in sorted(rows, key=lambda r: -float(r.get('cost_usd') or 0))[:5]:
        print('  %s %s $%7.2f %6.2f M %4d req %3d min agents %s  %s%s' % (r['session'][:8], r['ended'][:10], float(r.get('cost_usd') or 0), float(r.get('total_units_m') or 0),
              float(r.get('requests') or 0), float(r.get('minutes') or 0), r.get('agents') or 0, r.get('contracts') or '(no contract)', ('  # ' + r['note']) if r.get('note') else ''))
    print('')
    print('-- TOP 5 CONTRACTS BY USD (a session counts in each contract it edited) --')
    byc = collections.defaultdict(list)
    for r in rows:
        for c in (r['contracts'].split(';') if r.get('contracts') else ['(no contract)']):
            byc[c].append(r)
    for c, g in sorted(byc.items(), key=lambda kv: -_sum(kv[1], 'cost_usd'))[:5]:
        print('  %-40s %3d sess  $%8.2f  %7.2f M units' % (c[:40], len(g), _sum(g, 'cost_usd'), _sum(g, 'total_units_m')))
    if days:
        prev = [r for r in allrows if since - datetime.timedelta(days=days) <= r['_end'] < since]
        print('')
        print('-- TREND vs previous %d days --' % days)
        if not prev:
            print('  no recorded sessions in the previous window (the ledger starts later); nothing to compare')
        else:
            for lab, k in (('USD', 'cost_usd'), ('M units', 'total_units_m'), ('requests', 'requests'), ('errors', 'errors'), ('sessions', None)):
                x, y = (len(rows), len(prev)) if k is None else (_sum(rows, k), _sum(prev, k))
                print('  %-9s %10.2f vs %10.2f  %s' % (lab, x, y, ('%+.0f%%' % (100 * (x - y) / y)) if y else 'n/a'))
    print('')
    print('Estimate at API list prices, not a subscription bill. Thinking tokens are not counted. A column that did not exist when a row was recorded counts as 0 for it.')
    return 0


# Tool-call budgets per role (from references/routing-claude.md "Cost controls"); main has none.
CALL_BUDGET = dict(explorer=25, algorithm=20, profiler=20, architect=15, implementer=40, verifier=15, reviewer=25)
CHEAP_ROLES = ('explorer', 'profiler')


def _agent_rows(days):
    if not os.path.isfile(AGENT_LEDGER):
        return []
    since = datetime.datetime.now() - datetime.timedelta(days=days) if days else datetime.datetime(2000, 1, 1)
    out = []
    for r in csv.DictReader(open(AGENT_LEDGER, encoding='utf-8')):
        try:
            if datetime.datetime.strptime(r['started'] or r['recorded_at'], '%Y-%m-%d %H:%M') >= since:
                out.append(r)
        except Exception:
            pass
    return out


def _pct(vals, q):
    v = sorted(vals)
    return v[min(len(v) - 1, int(q * len(v)))] if v else 0


def agent_flags(r):
    g = lambda k: float(r.get(k) or 0)
    role, model, fl = r['role'], r['model'], []
    if role == 'main':
        return fl
    b = CALL_BUDGET.get(role)
    if b and g('tool_calls') > 1.5 * b:      # 1.5x grace: the cap is a brief instruction, not enforced by the harness
        fl.append('OVERRUN(%d>%d calls)' % (g('tool_calls'), b))
    if g('first_ctx_k') > 30:
        fl.append('COLD-START(%.0fk ctx on request 1)' % g('first_ctx_k'))
    if g('repeat_reads') >= 8:
        fl.append('REREAD(%d repeat reads)' % g('repeat_reads'))
    if g('max_result_k_chars') >= 40:
        fl.append('BIG-RESULT(%.0fk chars in one tool result)' % g('max_result_k_chars'))
    if g('requests') > 5 and g('cache_reuse_pct') < 85:
        fl.append('LOW-CACHE(%.0f%% reuse)' % g('cache_reuse_pct'))
    if g('tool_calls') >= 5 and g('tool_errors') / g('tool_calls') > 0.2:
        fl.append('ERRORS(%d of %d calls)' % (g('tool_errors'), g('tool_calls')))
    if g('cost_usd') > 0.05 and g('final_chars') < 60:
        fl.append('NO-REPORT(%d chars handed back, $%.2f spent)' % (g('final_chars'), g('cost_usd')))
    if role == 'implementer' and g('edits') == 0 and g('cost_usd') > 0.05:
        fl.append('IMPLEMENTER-NO-EDIT($%.2f, 0 edits)' % g('cost_usd'))
    if role in CHEAP_ROLES and model in ('sonnet', 'opus', 'fable'):
        fl.append('TIER(%s on a haiku role)' % model)
    if role in ('Explore', 'general-purpose', 'Plan', 'claude') and model in ('opus', 'fable') and 'TIER' not in ''.join(fl):
        fl.append('TIER(%s on generic agent)' % model)
    return fl


RULE_HINT = {
    'OVERRUN': 'brief states a hard call budget; lower it or split the task (routing-claude.md Cost controls)',
    'COLD-START': 'large fixed context on request 1: pass a digest instead of CLAUDE.md/contract, trim the agent file',
    'REREAD': 'read each file once and keep notes; give exact path:line ranges in the brief',
    'BIG-RESULT': 'cap tool output in the brief (head/limit/line ranges); no whole-file reads of large files',
    'LOW-CACHE': 'idle gaps or changing prefixes break the cache: one agent per task, batch its steps',
    'ERRORS': 'brief lacked a constraint (tool missing, wrong path): fix the brief template for that role',
    'NO-REPORT': 'agent ended without the required hand-back: enforce the report format',
    'IMPLEMENTER-NO-EDIT': 'exploration disguised as implementation or ambiguous brief: send an explorer first',
    'TIER': 'model override defeated the tier table: pass model explicitly per the table before every Agent call',
}


def audit(days):
    rows = _agent_rows(days)
    if not rows:
        print('no per-agent rows yet: run --backfill (history) or record a session first')
        return 1
    g = lambda r, k: float(r.get(k) or 0)
    sessions = collections.defaultdict(list)
    for r in rows:
        sessions[r['session']].append(r)
    subs = [r for r in rows if r['role'] != 'main']
    tot = sum(g(r, 'cost_usd') for r in rows)
    print('== EV-QUOTA AGENT AUDIT: %d sessions, %d agent runs, $%.2f (%s) ==' % (len(sessions), len(subs), tot, ('last %d days' % days) if days else 'all time'))
    print('Input side = fresh input + cache write + cache read; output side = generated tokens. Agents are mostly input-side by nature:')
    print('judge input by cold-start, re-reads and result size, and output by what the agent handed back or wrote.')
    print('')
    print('-- BY ROLE (cost, input vs output, per-run medians) --')
    print('  %-12s %4s %8s %7s %7s %5s %6s %6s %6s %6s %6s %5s' % ('role', 'runs', 'USD', 'med$', 'p90$', 'out%', 'medReq', 'ctx0k', 'reuse%', 'reread', 'err%', 'flag'))
    byrole = collections.defaultdict(list)
    for r in rows:
        byrole[r['role']].append(r)
    for role, g_ in sorted(byrole.items(), key=lambda kv: -sum(g(x, 'cost_usd') for x in kv[1])):
        usd = sum(g(x, 'cost_usd') for x in g_)
        outu = sum(g(x, 'out_side_usd') for x in g_)
        calls = sum(g(x, 'tool_calls') for x in g_)
        fl = sum(1 for x in g_ if agent_flags(x))
        print('  %-12s %4d %8.2f %7.3f %7.3f %4.1f%% %6.0f %6.1f %5.0f%% %6.1f %5.1f%% %5d' % (
            role[:12], len(g_), usd, _pct([g(x, 'cost_usd') for x in g_], .5), _pct([g(x, 'cost_usd') for x in g_], .9),
            100 * outu / usd if usd else 0, _pct([g(x, 'requests') for x in g_], .5), _pct([g(x, 'first_ctx_k') for x in g_], .5),
            sum(g(x, 'cache_reuse_pct') for x in g_) / len(g_), sum(g(x, 'repeat_reads') for x in g_) / len(g_),
            100 * sum(g(x, 'tool_errors') for x in g_) / calls if calls else 0, fl))
    print('')
    mains = byrole.get('main', [])
    if mains:
        mu_ = sum(g(x, 'cost_usd') for x in mains)
        print('-- ROOT SESSION (the cost that matters most) --')
        print('  root = %.0f%% of all USD | input side %.0f%% / output side %.0f%% | median %.0f requests/session, ctx on request 1 = %.0fk, cache reuse %.0f%%' % (
            100 * mu_ / tot if tot else 0, 100 - 100 * sum(g(x, 'out_side_usd') for x in mains) / mu_ if mu_ else 0, 100 * sum(g(x, 'out_side_usd') for x in mains) / mu_ if mu_ else 0,
            _pct([g(x, 'requests') for x in mains], .5), _pct([g(x, 'first_ctx_k') for x in mains], .5), sum(g(x, 'cache_reuse_pct') for x in mains) / len(mains)))
        print('  root product-code edits: %d | root result volume %.0fk chars | sessions over 150 requests: %d (each later request re-reads the whole context)' % (
            sum(g(x, 'code_edits') for x in mains), sum(g(x, 'result_k_chars') for x in mains), sum(1 for x in mains if g(x, 'requests') > 150)))
        print('')
    print('-- PRODUCTIVITY (what did the spend buy?) --')
    for role in ('implementer', 'explorer', 'reviewer', 'verifier', 'architect', 'algorithm', 'profiler'):
        g_ = byrole.get(role)
        if not g_:
            continue
        usd = sum(g(x, 'cost_usd') for x in g_)
        if role == 'implementer':
            e = sum(g(x, 'edits') for x in g_)
            print('  implementer: $%.2f for %d Edit/Write calls = $%.3f per edit; %d of %d runs made no edit' % (usd, e, usd / e if e else 0, sum(1 for x in g_ if not g(x, 'edits')), len(g_)))
        else:
            c = sum(g(x, 'final_chars') for x in g_) / 1000
            print('  %-11s: $%.2f for %.0fk chars of hand-back = $%.3f per 1k chars; avg %.0f tool calls per run' % (role, usd, c, usd / c if c else 0, sum(g(x, 'tool_calls') for x in g_) / len(g_)))
    print('')
    nflags = collections.Counter(); flagrows = []
    for r in subs:
        f = agent_flags(r)
        for x in f:
            nflags[x.split('(')[0]] += 1
        if f:
            flagrows.append((g(r, 'cost_usd'), r, f))
    print('-- SESSION SHAPE --')
    over = under = 0; lines = []
    for sid, g_ in sessions.items():
        main = [x for x in g_ if x['role'] == 'main']
        ag = [x for x in g_ if x['role'] != 'main']
        if not main:
            continue
        m = main[0]
        au, mu = sum(g(x, 'cost_usd') for x in ag), g(m, 'cost_usd')
        if ag and (au + mu) < 1.0 and g(m, 'code_edits') == 0:
            over += 1
            lines.append('  OVER-DELEGATED  %s  $%.2f total, %d agents ($%.2f) on a small session' % (sid[:8], au + mu, len(ag), au))
        if not ag and g(m, 'code_edits') >= 5 and mu > 2:
            under += 1
            lines.append('  UNDER-DELEGATED %s  $%.2f main, %d product-code edits by root, no agents' % (sid[:8], mu, g(m, 'code_edits')))
    for l in lines[:10]:
        print(l)
    print('  over-delegated sessions: %d | under-delegated (root wrote many edits): %d' % (over, under))
    print('')
    print('-- FLAG COUNTS (agent runs) --')
    for k, n in nflags.most_common():
        print('  %-20s %4d of %d runs (%.0f%%)  -> %s' % (k, n, len(subs), 100 * n / len(subs), RULE_HINT.get(k, '')))
    print('')
    print('-- 10 COSTLIEST FLAGGED RUNS --')
    for usd, r, f in sorted(flagrows, key=lambda t: -t[0])[:10]:
        print('  $%6.2f %s %-12s %-6s %s | %s | %s' % (usd, r['session'][:8], r['role'][:12], r['model'], r['agent_id'][:8], '; '.join(f), r['description'][:50]))
    print('')
    print('Flags are heuristics from transcripts, not verdicts. Thinking tokens are not counted. Evidence for route-rule proposals in eskill.')
    return 0


def backfill(days, rebuild=False):
    """Add per-agent rows (and missing session rows) for every transcript of this project not yet in the agent log."""
    have = set()
    if os.path.isfile(AGENT_LEDGER):
        have = {r['session'] for r in csv.DictReader(open(AGENT_LEDGER, encoding='utf-8'))}
    have_sess = set()
    if os.path.isfile(LEDGER):
        have_sess = {r['session'] for r in csv.DictReader(open(LEDGER, encoding='utf-8'))}
    since = datetime.datetime.now() - datetime.timedelta(days=days) if days else datetime.datetime(2000, 1, 1)
    n = 0
    for p in sorted(glob.glob(os.path.join(PROJ, '*.jsonl')), key=os.path.getmtime):
        sid = os.path.basename(p)[:-6]
        if (sid in have and not rebuild) or datetime.datetime.fromtimestamp(os.path.getmtime(p)) < since:
            continue
        record(sid, 'backfill', quiet=True, agents_only=(sid in have_sess))
        n += 1
    print('backfill: %d sessions added to the agent log (%d already there)' % (n, len(have)))
    return 0


def compare(session):
    """Latest row of `session` versus the most recent row of ANY other session recorded before it."""
    if not os.path.isfile(LEDGER):
        print('no ledger yet')
        return 1
    rows = list(csv.DictReader(open(LEDGER, encoding='utf-8')))
    mine = [r for r in rows if r['session'] == session]
    if not mine:
        print('this session is not recorded yet; record it first')
        return 1
    cur = mine[-1]
    prev = None
    for r in rows:
        if r['session'] != session and r['recorded_at'] <= cur['recorded_at']:
            prev = r      # rows are appended in time order, so the last match is the most recent
    f = lambda r, k: float(r.get(k) or 0)
    print('== THIS RUN vs PREVIOUS RECORDED RUN ==')
    print('this : %s  ended %s  $%.2f  %.2f M units  %d req  %d agents  %d errors' % (cur['session'][:8], cur['ended'], f(cur, 'cost_usd'), f(cur, 'total_units_m'), f(cur, 'requests'), f(cur, 'agents'), f(cur, 'errors')))
    if not prev:
        print('prev : none (no earlier session in the ledger), nothing to compare')
        return 0
    print('prev : %s  ended %s  $%.2f  %.2f M units  %d req  %d agents  %d errors' % (prev['session'][:8], prev['ended'], f(prev, 'cost_usd'), f(prev, 'total_units_m'), f(prev, 'requests'), f(prev, 'agents'), f(prev, 'errors')))
    for label, k in (('USD', 'cost_usd'), ('M units', 'total_units_m'), ('requests', 'requests'), ('errors', 'errors')):
        a, b = f(cur, k), f(prev, k)
        pct = ('%+.0f%%' % (100 * (a - b) / b)) if b else 'n/a'
        print('delta %-9s %+.2f (%s)' % (label, a - b, pct))
    if f(prev, 'cost_usd') == 0:
        print('note: the previous row has no USD (recorded before the $ column existed); compare units instead')
    return 0


ap = argparse.ArgumentParser()
ap.add_argument('--compare', action='store_true')
ap.add_argument('--session', default='')
ap.add_argument('--note', default='')
ap.add_argument('--report', action='store_true')
ap.add_argument('--detail', action='store_true', help='full statistics dashboard (use with --days N; 0 = all time)')
ap.add_argument('--days', type=int, default=0)
ap.add_argument('--by', default='week', choices=['day', 'week', 'month', 'contract', 'model', 'session'])
ap.add_argument('--audit', action='store_true', help='per-agent efficiency audit: input vs output, overruns, flags, rule hints')
ap.add_argument('--backfill', action='store_true', help='add per-agent rows for all past sessions not yet logged (use --days to limit)')
ap.add_argument('--rebuild', action='store_true', help='with --backfill: re-scan sessions already in the agent log (after a metric change)')
ap.add_argument('--no-refresh', action='store_true', help='skip the automatic live price refresh')
ap.add_argument('--refresh-only', action='store_true', help='only refresh prices.json and exit')
a = ap.parse_args()
load_prices()
print('prices: refresh skipped (--no-refresh)' if a.no_refresh else refresh_prices())
load_prices()
try:
    age = (datetime.datetime.now() - datetime.datetime.strptime(PRICE_AS_OF, '%Y-%m-%d')).days
except Exception:
    age = 9999
print('prices: as_of %s, %d days old%s' % (PRICE_AS_OF or 'unknown', age, ' - STALE: live refresh failed, dollar figures use old prices' if age > 1 else ''))
if a.refresh_only:
    sys.exit(0)
if a.backfill:
    sys.exit(backfill(a.days, a.rebuild))
if a.session:
    rc = record(a.session, a.note or ('check: ' + ('audit' if a.audit else 'detail' if a.detail else 'report' if a.report else 'record')))
    if rc == 0 and (a.detail or a.audit or a.report):    # every check is logged first, then the requested view is printed
        print('')
        sys.exit(detail(a.days) if a.detail else audit(a.days) if a.audit else report(a.days, a.by))
    if rc == 0 and a.compare:
        print('')
        rc = compare(a.session)
    sys.exit(rc)
if a.audit:
    sys.exit(audit(a.days))
if a.detail:
    sys.exit(detail(a.days))
if a.report:
    sys.exit(report(a.days, a.by))
ap.print_help()
