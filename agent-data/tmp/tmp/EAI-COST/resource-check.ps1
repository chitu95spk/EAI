$ErrorActionPreference = 'Stop'
$taskExpected = @('AgentBudgetStopped', 'AgentLedgerTampered', 'AgentCostEstimated') | Sort-Object
foreach ($taskName in @('Strings.resx', 'Strings.vi.resx')) {
    $taskBefore = [xml][IO.File]::ReadAllText((Join-Path (Get-Location) ('tmp/EAI-COST/session-20261017T070851898/' + $taskName)))
    $taskAfter = [xml][IO.File]::ReadAllText((Join-Path (Get-Location) ('EasyLabel/src/EasyAI.Wpf/Resources/' + $taskName)))
    $taskOld = @{}
    foreach ($taskRow in $taskBefore.root.data) { $taskOld[$taskRow.name] = $taskRow.InnerXml }
    $taskAdded = @()
    foreach ($taskRow in $taskAfter.root.data) {
        if ($taskOld.ContainsKey($taskRow.name)) {
            if ($taskOld[$taskRow.name] -cne $taskRow.InnerXml) { throw ('Existing resource changed: ' + $taskRow.name) }
            $taskOld.Remove($taskRow.name)
        } else { $taskAdded += $taskRow.name }
    }
    if ($taskOld.Count -ne 0) { throw 'Existing resource removed' }
    if ((($taskAdded | Sort-Object) -join ',') -cne ($taskExpected -join ',')) { throw 'Unexpected keys' }
    Write-Output ($taskName + ': XML valid; existing entries preserved; exactly three requested keys added')
}
$taskDesigner = [IO.File]::ReadAllText((Join-Path (Get-Location) 'EasyLabel/src/EasyAI.Wpf/Resources/Strings.Designer.cs'))
foreach ($taskKey in $taskExpected) {
    if (!$taskDesigner.Contains('public static string ' + $taskKey + ' => Get(nameof(' + $taskKey + '));')) { throw ('Missing accessor: ' + $taskKey) }
}
Write-Output 'Designer accessor style: PASS'
