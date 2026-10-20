$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path '.').Path
$owned = @(
    'EasyLabel/src/EasyAI.Wpf/ViewModels/LabelStepViewModel.AgentChat.cs',
    'EasyLabel/src/EasyAI.Wpf/Commands/ViewModelCommandHandlers.cs',
    'EasyLabel/src/EasyAI.Wpf/Commands/WpfCommandHost.cs',
    'EasyLabel/tests/EasyLabel.Commands.Tests/Catalog/CommandCatalogSnapshotTests.cs',
    'EasyLabel/tests/EasyLabel.Commands.Tests/Catalog/commands.catalog.json'
)
$created = @(
    'EasyLabel/src/EasyLabel.Commands/AgentSlashCommandDescriptors.cs',
    'EasyLabel/src/EasyLabel.Commands/AgentSlashCatalog.cs',
    'EasyLabel/src/EasyAI.Wpf/ViewModels/MainViewModel.SlashRegistry.cs',
    'EasyLabel/src/EasyAI.Wpf/ViewModels/LabelStepViewModel.SlashRegistry.cs',
    'EasyLabel/tests/EasyLabel.Commands.Tests/Invocation/AgentSlashRegistryTests.cs'
)
$baseline = Get-Content 'tmp/EAI-CC/cc3-start/hashes.json' -Raw | ConvertFrom-Json
$unexpected = @($baseline | Where-Object { $_.Path -notin $owned } | Where-Object {
    !(Test-Path -LiteralPath $_.Path) -or (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.Hash
})
if ($unexpected.Count) { throw ('Out-of-scope baseline changed: ' + ($unexpected.Path -join ', ')) }
"PASS: $(@($baseline | Where-Object { $_.Path -notin $owned }).Count) baseline files outside the allow-list unchanged."
$before = [IO.File]::ReadAllText((Join-Path $repo 'tmp/EAI-CC/cc3-before/LabelStepViewModel.AgentChat.cs'))
$after = [IO.File]::ReadAllText((Join-Path $repo $owned[0]))
$begin = '    public bool TryHandleSlashCommand()'
$end = '    // ----- which images a run covers'
if ($before.Substring(0, $before.IndexOf($begin)) -cne $after.Substring(0, $after.IndexOf($begin))) { throw 'AgentChat prefix changed' }
if ($before.Substring($before.IndexOf($end)) -cne $after.Substring($after.IndexOf($end))) { throw 'AgentChat suffix changed' }
'PASS: AgentChat prefix and suffix outside the slash region match the supplied snapshot exactly.'
foreach ($pair in @(
    @('MainViewModel.cs', 'EasyLabel/src/EasyAI.Wpf/ViewModels/MainViewModel.cs'),
    @('AgentSlashCommands.cs', 'EasyLabel/src/EasyAI.Wpf/Services/Agent/AgentSlashCommands.cs')
)) {
    if ((Get-FileHash ('tmp/EAI-CC/cc3-before/' + $pair[0])).Hash -ne (Get-FileHash $pair[1]).Hash) { throw ($pair[0] + ' changed') }
    ('PASS: ' + $pair[0] + ' matches the supplied snapshot byte-for-byte.')
}
$oldCatalog = Get-Content 'tmp/EAI-CC/cc3-start/commands.catalog.json' -Raw | ConvertFrom-Json
$newCatalog = Get-Content $owned[4] -Raw | ConvertFrom-Json
$oldById = @{}
foreach ($command in $oldCatalog.commands) { $oldById[$command.id] = $command }
$newIds = @()
$changedIds = @()
foreach ($command in $newCatalog.commands) {
    if (!$oldById.ContainsKey($command.id)) { $newIds += $command.id; continue }
    if (($oldById[$command.id] | ConvertTo-Json -Depth 30 -Compress) -cne ($command | ConvertTo-Json -Depth 30 -Compress)) { $changedIds += $command.id }
}
if ($newIds.Count -ne 7 -or $changedIds.Count -ne 1 -or $changedIds[0] -ne 'AGT.CHECK') { throw 'Unexpected catalog delta' }
$oldCheck = $oldById['AGT.CHECK']
$newCheck = $newCatalog.commands | Where-Object id -eq 'AGT.CHECK'
$oldCheck.aliases = @('check')
if (($oldCheck | ConvertTo-Json -Depth 30 -Compress) -cne ($newCheck | ConvertTo-Json -Depth 30 -Compress)) { throw 'AGT.CHECK changed beyond alias' }
"PASS: catalog $($oldCatalog.commands.Count) -> $($newCatalog.commands.Count); added $($newIds -join ', '); AGT.CHECK alias only."
foreach ($file in ($owned + $created | Where-Object { $_ -like '*.cs' })) {
    $lines = [IO.File]::ReadAllLines((Join-Path $repo $file))
    if (@($lines | Where-Object { $_ -match '^(<<<<<<<|=======|>>>>>>>)' }).Count) { throw ('Conflict marker in ' + $file) }
    if ($file -in $created -and @($lines | Where-Object { $_ -match '\s+$' }).Count) { throw ('Trailing whitespace in ' + $file) }
    ('FILE: ' + $file + ' (' + $lines.Count + ' lines)')
}
$ErrorActionPreference = 'Continue'
git diff --check -- $owned 2> 'tmp/EAI-CC/logs/diff-check-stderr.log'
if ($LASTEXITCODE -ne 0) { throw 'Scoped git diff --check failed' }
'PASS: scoped whitespace and conflict-marker checks.'
foreach ($file in @($owned | Where-Object { $_ -like '*.cs' })) {
    "HUNKS: $file"
    git diff --unified=0 -- $file 2> 'tmp/EAI-CC/logs/diff-hunks-stderr.log' | Select-String '^@@' | ForEach-Object { $_.Line }
}
