import os, sys, json, subprocess, time, shutil
from pathlib import Path
ROOT = Path(__file__).resolve().parents[3]
BASE = ROOT / 'tmp/EAI-ONL-MODEL'
OUT = BASE / 'codex'
os.environ['TEMP'] = os.environ['TMP'] = str(BASE / 'temp')
os.environ['EASYAI_SEGAI_DIR'] = str(ROOT / 'tmp/EAI-ONL3/native-stage')
os.environ['DOTNET_CLI_HOME'] = str(OUT / 'dotnet-home')
os.environ['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
os.environ['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
os.environ['DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE'] = '1'
label, *command = sys.argv[1:]
free = shutil.disk_usage(ROOT).free / 2**30
if free < 10:
    raise SystemExit('STOP: E free < 10 GiB')
entry = {'label': label, 'command': command, 'cwd': str(ROOT), 'start_utc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'free_gib_before': free, 'environment': {k: os.environ[k] for k in ['TEMP','TMP','EASYAI_SEGAI_DIR','DOTNET_CLI_HOME']}}
with (OUT / (label + '.log')).open('x', encoding='utf-8') as log:
    child = subprocess.Popen(command, cwd=ROOT, env=os.environ, stdout=log, stderr=subprocess.STDOUT)
    while child.poll() is None:
        time.sleep(5)
        free = shutil.disk_usage(ROOT).free / 2**30
        if free < 10:
            child.terminate()
            entry['stopped'] = 'E free < 10 GiB'
            break
    entry['exit_code'] = child.wait()
entry['end_utc'] = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
entry['free_gib_after'] = shutil.disk_usage(ROOT).free / 2**30
with (OUT / 'commands.jsonl').open('a', encoding='utf-8') as log:
    log.write(json.dumps(entry) + '\n')
print(json.dumps(entry), flush=True)
sys.exit(entry['exit_code'])
