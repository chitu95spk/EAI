import sys, json
def send(o): sys.stdout.write(json.dumps(o) + "\n"); sys.stdout.flush()
for line in sys.stdin:
    line = line.strip()
    if not line: continue
    m = json.loads(line)
    mid = m.get("id"); method = m.get("method")
    if method == "initialize":
        send({"jsonrpc":"2.0","id":mid,"result":{"protocolVersion":m["params"].get("protocolVersion","2024-11-05"),"capabilities":{"tools":{}},"serverInfo":{"name":"easyai-spike","version":"0.1"}}})
    elif method == "tools/list":
        send({"jsonrpc":"2.0","id":mid,"result":{"tools":[{"name":"ping","description":"Returns the secret word. Call it to get the word.","inputSchema":{"type":"object","properties":{},"required":[]}}]}})
    elif method == "tools/call":
        send({"jsonrpc":"2.0","id":mid,"result":{"content":[{"type":"text","text":"SECRET=pomegranate-42"}]}})
    elif mid is not None:
        send({"jsonrpc":"2.0","id":mid,"error":{"code":-32601,"message":"no"}})
