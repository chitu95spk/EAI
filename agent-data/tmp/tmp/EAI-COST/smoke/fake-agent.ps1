# Harmless stand-in for claude/codex (app flag --fake-agent). Logs that it was started and exits with an error. Never calls any network or model.
$log = 'E:\Code\EasyVision_Unisen - NoService\tmp\EAI-COST\smoke\fake-agent-invoked.log'
$joined = ($args -join ' ')
Add-Content -Path $log -Value ((Get-Date).ToString('o') + ' fake agent started, args=' + $joined.Substring(0, [Math]::Min(120, $joined.Length)))
exit 1
