[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Batch,
    [Parameter(Mandatory = $true)]
    [string[]]$Images,
    [string[]]$ReferencePaths,
    [ValidateRange(0.01, 1000.0)]
    [double]$MaxUsd = 1.0,
    [ValidateSet('claude', 'codex')]
    [string]$Tool = 'codex'
)

$ErrorActionPreference = 'Stop'
$root = 'E:\Code\EasyVision_Unisen - NoService'
$sourceProject = Join-Path $root 'tmp\EAI-LOGO\project'
$outRoot = Join-Path $root 'artifacts\EAI-LOGO'
$outDir = Join-Path $outRoot $Batch
$appDir = Join-Path $root 'EasyLabel\src\EasyAI.Wpf\bin\Release\net9.0-windows'
$testDll = Join-Path $root 'EasyLabel\tests\EasyLabel.SegAI.Tests\bin\Release\net9.0-windows\EasyLabel.SegAI.Tests.dll'
$references = @(
    (Join-Path $root 'tmp\EAI-LOGO\references\NhanL1 (4)-NG-examples.jpg'),
    (Join-Path $root 'tmp\EAI-LOGO\references\NhanL1 (8)-NG-examples.jpg'),
    (Join-Path $root 'tmp\EAI-LOGO\references\NhanL1 (11)-NG-examples.jpg'),
    (Join-Path $root 'tmp\EAI-LOGO\references\label-policy.txt')
)
if ($null -ne $ReferencePaths -and $ReferencePaths.Count -gt 0) {
    $references = @($ReferencePaths)
}

if ([string]::IsNullOrWhiteSpace($Batch) -or $Batch -match '[\\/:*?"<>|]') {
    throw 'Batch must be a non-empty folder name without path separators or wildcard characters.'
}
if (-not (Test-Path -LiteralPath $sourceProject -PathType Container)) { throw "Project is missing: $sourceProject" }
if (-not (Test-Path -LiteralPath $appDir -PathType Container)) { throw "Built app directory is missing: $appDir" }
if (-not (Test-Path -LiteralPath $testDll -PathType Leaf)) { throw "Built test DLL is missing: $testDll" }
if (Test-Path -LiteralPath $outDir) { throw "Refusing to reuse an existing output folder: $outDir" }
foreach ($reference in $references) { if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Reference is missing: $reference" } }

$normalized = @()
foreach ($image in $Images) {
    foreach ($piece in $image -split ',') {
        $rel = $piece.Trim()
        if ([string]::IsNullOrWhiteSpace($rel)) { continue }
        if ([System.IO.Path]::IsPathRooted($rel) -or $rel.Contains('..') -or [System.IO.Path]::GetExtension($rel) -notmatch '^(?i)\.jpe?g$|^(?i)\.png$') {
            throw "Image must be a project-relative JPEG or PNG name: $rel"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $sourceProject $rel) -PathType Leaf)) { throw "Image is missing from the immutable source project: $rel" }
        $normalized += $rel
    }
}
if ($normalized.Count -eq 0) { throw 'At least one project-relative image is required.' }
if (($normalized | Select-Object -Unique).Count -ne $normalized.Count) { throw 'Duplicate image names are not allowed.' }

New-Item -ItemType Directory -Path $outDir | Out-Null
$tempDir = Join-Path $outDir 'temp'
New-Item -ItemType Directory -Path $tempDir | Out-Null

$prompt = @'
Find only visible defect regions on the product label or bottle: curled label, bulged label, wrinkled label, torn label, scratched label, or dented bottle. Propose one tight oriented_rectangle, class NG, for each spatially distinct confirmed damaged material. Box the full connected damaged band or warped area, including faint continuation that belongs to the same defect. Use high-resolution crops and the supplied examples, crop-context metadata, and policy to verify every region. A crop coordinate is local: convert it to original-image coordinates using that crop's stated origin and actual X/Y scale before proposing. Never mix an original-image center with crop width/height. For generated elongated boxes only, write the four points long-edge-first and in a consistent winding. Preserve every existing human label unchanged. Do not box the complete product, bottle, label, tray, printed logo, background, shadow, reflection, or uncertain area. Filenames are not evidence. If no defect is visible, return no proposal.
'@.Trim()

$env:EAI_LABEL_RUN = '1'
$env:EAI_LABEL_APPDIR = $appDir
$env:EAI_LABEL_PROJECT = $sourceProject
$env:EAI_LABEL_OUT = $outDir
$env:EAI_LABEL_CLASSES = 'NG'
$env:EAI_LABEL_IMAGES = [string]::Join(',', $normalized)
$env:EAI_LABEL_REFERENCES = [string]::Join(';', $references)
$env:EAI_LABEL_PROMPT = $prompt
$env:EAI_LABEL_MAX_USD = $MaxUsd.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$env:EAI_LABEL_PARALLEL = '1'
$env:EAI_LABEL_EXPERIENCE = '0'
$env:EAI_LABEL_ACCURACY = 'Normal'
$env:EAI_LABEL_EFFORT = 'Normal'
$env:EAI_LABEL_TOOL = $Tool
$env:EAI_LABEL_SAM2_REFINE = '0'
$env:EAI_LABEL_RETRY_NORMAL = '0'
$env:TEMP = $tempDir
$env:TMP = $tempDir

$evidence = [ordered]@{
    utc = [DateTime]::UtcNow.ToString('o')
    batch = $Batch
    source_project = $sourceProject
    output = $outDir
    test_dll = $testDll
    app_dir = $appDir
    test_dll_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $testDll).Hash
    app_segai_dll = (Join-Path $appDir 'EasyLabel.SegAI.dll')
    app_segai_dll_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $appDir 'EasyLabel.SegAI.dll')).Hash
    app_easyai_dll = (Join-Path $appDir 'EasyAI.dll')
    app_easyai_dll_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $appDir 'EasyAI.dll')).Hash
    images = $normalized
    references = $references
    prompt = $prompt
    tool_requested = $Tool
    levers = [ordered]@{ classes = 'NG'; sam2_refine = 0; parallel = 1; experience = 0; accuracy = 'Normal'; effort = 'Normal'; max_usd = $MaxUsd }
    environment = [ordered]@{ TEMP = $env:TEMP; TMP = $env:TMP }
    command = @('dotnet', 'vstest', $testDll, '--TestCaseFilter:FullyQualifiedName=EasyLabel.SegAI.Tests.BootstrapBaselineTests.MeasureAgentPromptBatchLabelling')
}
$evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outDir 'launch-evidence.json') -Encoding utf8

$log = Join-Path $outDir 'vstest.log'
& dotnet vstest $testDll '--TestCaseFilter:FullyQualifiedName=EasyLabel.SegAI.Tests.BootstrapBaselineTests.MeasureAgentPromptBatchLabelling' *> $log
$exit = $LASTEXITCODE
if ($exit -ne 0) { throw "Pilot failed with exit code $exit. See $log" }
$summary = Join-Path $outDir 'summary.json'
$csv = Join-Path $outDir 'per-image.csv'
if (-not (Test-Path -LiteralPath $summary -PathType Leaf) -or -not (Test-Path -LiteralPath $csv -PathType Leaf)) {
    throw "The test did not complete a batch (summary.json/per-image.csv missing). Inspect $log"
}
if ((Select-String -LiteralPath $log -Pattern '^\s*Skipped:\s*[1-9][0-9]*\b' -Quiet) -or (Select-String -LiteralPath $log -Pattern 'SKIPPED \(nothing proven\)' -Quiet)) {
    throw "The test runner reported a skipped test; the batch is not complete. Inspect $log"
}
$rows = @(Import-Csv -LiteralPath $csv)
if ($rows.Count -ne $normalized.Count) {
    throw "The completed batch has $($rows.Count) rows but planned $($normalized.Count) images. Inspect $csv and $log"
}
$summaryData = Get-Content -Raw -LiteralPath $summary | ConvertFrom-Json
if ($summaryData.images_planned -ne $normalized.Count -or $summaryData.images_done -ne $normalized.Count -or $summaryData.images_succeeded -ne $normalized.Count -or $summaryData.not_processed -ne 0 -or $summaryData.stopped_by -ne 'Done') {
    throw "Batch summary is incomplete or stopped early. Inspect $summary"
}
if (@($rows | Where-Object { $_.outcome -ne 'Success' -or -not [string]::IsNullOrWhiteSpace($_.error) }).Count -ne 0) {
    throw "One or more planned images did not succeed. Inspect $csv"
}
Write-Host "Completed label pilot: $outDir ($($rows.Count)/$($normalized.Count) images)"
