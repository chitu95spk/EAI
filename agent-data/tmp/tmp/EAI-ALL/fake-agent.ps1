# QA fake for the live agent transcript (F9). Start EasyAI with:  EasyAI.exe --fake-agent "<full path to this file>"
# The app starts this script instead of Claude Code with the usual arguments; it streams stream-json lines with small delays
# and writes proposals.json next to the MCP config (the run folder), like the real MCP propose_shapes tool would.
# Pass the word "fail" in the prompt for an error result, "hang" to wait 2 minutes (to test Cancel).
$a = @($args)
$mcpIndex = [array]::IndexOf($a, '--mcp-config')
$run = Split-Path -Parent $a[$mcpIndex + 1]
$pIndex = [array]::IndexOf($a, '-p')
$prompt = if ($pIndex -ge 0) { [string]$a[$pIndex + 1] } else { '' }
$o = [Console]::Out

function Send($line) {
    $o.WriteLine($line)
    $o.Flush()
}

Send '{"type":"system","subtype":"init","model":"fake-model"}'
Start-Sleep -Milliseconds 600
Send '{"type":"assistant","message":{"content":[{"type":"text","text":"I will look at the image first and then label the objects you asked for."}]}}'
Start-Sleep -Milliseconds 700
Send '{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"mcp__easyai__get_image_info","input":{}}]}}'
Start-Sleep -Milliseconds 500
Send '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"width 1000, height 800, 0 shapes"}]}}'
Start-Sleep -Milliseconds 600
Send '{"type":"assistant","message":{"content":[{"type":"text","text":"The picture is clear. Checking one region in detail."},{"type":"tool_use","id":"t2","name":"mcp__easyai__get_crop","input":{"x":100,"y":150,"width":400,"height":300}}]}}'
Start-Sleep -Milliseconds 700
if ($prompt -match 'hang') { Start-Sleep -Seconds 120; exit 0 }
Send '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"crop ok"}]}]}}'
Start-Sleep -Milliseconds 600
if ($prompt -match 'fail') {
    Send '{"type":"result","subtype":"error_during_execution","is_error":true,"result":"The fake agent failed on purpose.","total_cost_usd":0.004,"num_turns":2}'
    exit 1
}

Send '{"type":"assistant","message":{"content":[{"type":"text","text":"Found one object. Proposing it now."},{"type":"tool_use","id":"t3","name":"mcp__easyai__propose_shapes","input":{"count":1}}]}}'
Start-Sleep -Milliseconds 600
$p = '{"version":1,"proposals":[{"id":"auto-1","label":"OK","shapeType":"rectangle","points":[[120,170],[420,400]],"holes":[],"score":0.9,"note":"fake","source":"agent"}]}'
Set-Content -Path (Join-Path $run 'proposals.json') -Value $p
Send '{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t3","content":"1 proposal stored"}]}}'
Start-Sleep -Milliseconds 400
Send '{"type":"assistant","message":{"content":[{"type":"text","text":"Done: one rectangle proposed."}]}}'
Start-Sleep -Milliseconds 300
Send '{"type":"result","subtype":"success","is_error":false,"result":"Done: one rectangle proposed.","total_cost_usd":0.0123,"num_turns":4,"duration_ms":5200,"usage":{"input_tokens":5,"cache_creation_input_tokens":100,"cache_read_input_tokens":200,"output_tokens":50}}'
exit 0
