$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$scratch = Join-Path $root 'tmp/EAI-COST'
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$source = [System.IO.File]::ReadAllText((Join-Path $scratch 'edit.py'), $utf8)
$stringPattern = "(?:'''[\s\S]*?'''|'(?:\\.|[^'\\])*')"
$pairPattern = '\(\s*(?<old>' + $stringPattern + ')\s*,\s*(?<new>' + $stringPattern + ')\s*\),?'
$plans = [regex]::Matches($source, "edit\(p\+'(?<file>[^']+)',\s*\[(?<pairs>[\s\S]*?)\]\)")
function Decode-Literal([string] $literal) {
    if ($literal.StartsWith("'''")) { return $literal.Substring(3, $literal.Length - 6) }
    return $literal.Substring(1, $literal.Length - 2).Replace('\n', "`n").Replace('\r', "`r").Replace('\\', '\')
}
function Apply-Edits([string] $name, $pairs) {
    $path = Join-Path $root $name
    $original = [System.IO.File]::ReadAllBytes($path)
    $text = $utf8.GetString($original)
    $ending = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    foreach ($pair in $pairs) {
        $old = $pair[0].Replace("`r`n", "`n").Replace("`n", $ending)
        $new = $pair[1].Replace("`r`n", "`n").Replace("`n", $ending)
        $count = [regex]::Matches($text, [regex]::Escape($old)).Count
        if ($count -ne 1) { throw "Expected one anchor in ${name}: $($old.Substring(0, [Math]::Min(85, $old.Length))), found $count" }
        $text = $text.Replace($old, $new)
    }
    $baseline = Join-Path (Join-Path $scratch 'baseline') $name
    if (Test-Path -LiteralPath $baseline) { throw "Baseline already exists: $name" }
    if ([Convert]::ToBase64String([System.IO.File]::ReadAllBytes($path)) -ne [Convert]::ToBase64String($original)) { throw "Concurrent edit: $name" }
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($baseline)) | Out-Null
    [System.IO.File]::WriteAllBytes($baseline, $original)
    [System.IO.File]::WriteAllBytes($path, $utf8.GetBytes($text))
    Write-Output "Edited $name"
}
foreach ($plan in $plans) {
    $pairs = @([regex]::Matches($plan.Groups['pairs'].Value, $pairPattern) | ForEach-Object {
        ,@((Decode-Literal $_.Groups['old'].Value), (Decode-Literal $_.Groups['new'].Value))
    })
    Apply-Edits ('EasyLabel/src/EasyAI.Wpf/' + $plan.Groups['file'].Value) $pairs
}
$resources = @(
    @('Strings.resx', 'Offline only', 'Offline-only mode blocks agent actions, sign-in checks and price refreshes; local labeling, training and testing stay available.'),
    @('Strings.vi.resx', 'Chỉ dùng ngoại tuyến', 'Chế độ ngoại tuyến chặn tác vụ trợ lý, kiểm tra đăng nhập và cập nhật giá; gán nhãn, huấn luyện và kiểm thử cục bộ vẫn dùng được.')
)
foreach ($resource in $resources) {
    $addition = '  <data name="AgentOfflineOnly" xml:space="preserve">' + "`n    <value>$($resource[1])</value>`n  </data>`n" + '  <data name="AgentOfflineExplanation" xml:space="preserve">' + "`n    <value>$($resource[2])</value>`n  </data>`n</root>"
    Apply-Edits ('EasyLabel/src/EasyAI.Wpf/Resources/' + $resource[0]) @(,@('</root>', $addition))
}
