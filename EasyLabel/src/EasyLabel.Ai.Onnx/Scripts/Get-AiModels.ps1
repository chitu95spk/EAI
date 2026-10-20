<#
.SYNOPSIS
  Prepares the model folders for the EasyAI Auto-label AI backends (SAM2 and YOLO-World). USER-RUN ONLY.

.DESCRIPTION
  This script creates %LOCALAPPDATA%\EasyAI\models\sam2 and %LOCALAPPDATA%\EasyAI\models\yolo-world, prints the license
  notices, and prints the exact manual steps and pinned download URL. It downloads NOTHING by itself and it refuses to touch
  any path outside %LOCALAPPDATA%\EasyAI\models. Agents must never run it.

  Licenses
    SAM2 (Meta, facebookresearch/sam2): Apache-2.0.
    Ultralytics YOLO-World: AGPL-3.0. This script only prints download/export instructions; you export the model yourself and
    you are responsible for the AGPL-3.0 obligations of using and distributing it.

  Manual step A - SAM2.1 tiny to ONNX (encoder + decoder), run in a Python environment you control:
    pip install samexporter torch onnx onnxruntime
    python -m samexporter.export_encoder --checkpoint <models>\sam2\sam2.1_hiera_tiny.pt --output_path <models>\sam2\sam2.1_tiny.encoder.onnx --model_type sam2.1_hiera_tiny
    python -m samexporter.export_decoder --checkpoint <models>\sam2\sam2.1_hiera_tiny.pt --output_path <models>\sam2\sam2.1_tiny.decoder.onnx --model_type sam2.1_hiera_tiny
  The folder must end up with exactly one *encoder*.onnx and one *decoder*.onnx. EasyAI reads the tensor names from the files and
  refuses a pair whose interface it does not recognise. (The exact samexporter module names can differ between versions: UNVERIFIED.)

  Manual step B - YOLO-World with a FIXED class list, run in a Python environment you control:
    pip install ultralytics
    python -c "from ultralytics import YOLOWorld; m = YOLOWorld('yolov8s-worldv2.pt'); m.set_classes(['screw','scratch','dent']); m.export(format='onnx', nms=False, imgsz=640)"
  Copy the produced .onnx into <models>\yolo-world (exactly one .onnx there). The class names are stored in the model metadata
  ('names'); if your export lacks them, put one class per line in <model-name>.classes.txt next to the .onnx. The class list is
  fixed at export time: to detect other words you export again.
#>
[CmdletBinding()]
param(
    [string]$ModelsRoot = (Join-Path $env:LOCALAPPDATA 'EasyAI\models')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'EasyAI\models')).TrimEnd('\') + '\'
$root = [System.IO.Path]::GetFullPath($ModelsRoot).TrimEnd('\') + '\'
if (-not $root.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write outside '$allowedRoot' (got '$root')."
}

foreach ($name in 'sam2', 'yolo-world') {
    $folder = [System.IO.Path]::GetFullPath((Join-Path $root $name))
    if (-not $folder.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to create '$folder'."
    }
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    Write-Host "Folder ready: $folder"
}

Write-Host ''
Write-Host 'LICENSES'
Write-Host '  SAM2 (Meta): Apache-2.0.'
Write-Host '  Ultralytics YOLO-World: AGPL-3.0 (download/export only; you carry the AGPL-3.0 obligations).'
Write-Host ''
Write-Host 'SAM2.1 tiny checkpoint (download it yourself; this script does not):'
Write-Host '  https://dl.fbaipublicfiles.com/segment_anything_2/092824/sam2.1_hiera_tiny.pt   [UNVERIFIED URL: check it on https://github.com/facebookresearch/sam2]'
Write-Host '  Save it as sam2\sam2.1_hiera_tiny.pt, then follow manual step A in the header of this script.'
Write-Host ''
Write-Host 'YOLO-World: export with Ultralytics (manual step B in the header of this script).'
Write-Host '  Weights name used in the example: yolov8s-worldv2.pt   [UNVERIFIED: check the current Ultralytics model list]'
Write-Host ''
Write-Host "When both folders hold their .onnx files, restart EasyAI: Settings > AI models should show them as found."
