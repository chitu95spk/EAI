param([string]$Mode='dml0',[int]$Runs=5,[string]$Level='ALL_DISABLED')
$root='E:\Code\EasyVision_Unisen - NoService'
$bin="$root\EasyLabel\tests\EasyLabel.Ai.Onnx.Tests\bin\Release\net9.0"
$env:PATH="$bin\runtimes\win-x64\native;$bin;$env:PATH"
Add-Type -Path "$bin\Microsoft.ML.OnnxRuntime.dll"
$o=New-Object Microsoft.ML.OnnxRuntime.SessionOptions
if($Level -eq 'ALL_DISABLED'){$o.GraphOptimizationLevel=[Microsoft.ML.OnnxRuntime.GraphOptimizationLevel]::ORT_DISABLE_ALL}
$o.EnableMemoryPattern=$false;$o.ExecutionMode=[Microsoft.ML.OnnxRuntime.ExecutionMode]::ORT_SEQUENTIAL
if($Mode -like 'dml*'){$o.AppendExecutionProvider_DML([int]$Mode.Substring(3))}
if($Mode -like 'cpu*'){$o.IntraOpNumThreads=2}
$sw=[Diagnostics.Stopwatch]::StartNew()
$s=New-Object Microsoft.ML.OnnxRuntime.InferenceSession("$env:LOCALAPPDATA\EasyAI\models\sam2\encoder.onnx",$o)
"load_s=$([math]::Round($sw.Elapsed.TotalSeconds,2)) inputs=$($s.InputMetadata.Keys -join ',')"
$name=($s.InputMetadata.Keys)[0]
foreach($k in 0,1){
 $b=[IO.File]::ReadAllBytes("$root\tmp\EAI-LABEL\s4\in$k.f32");$f=New-Object 'float[]' ($b.Length/4);[Buffer]::BlockCopy($b,0,$f,0,$b.Length)
 $t=New-Object 'Microsoft.ML.OnnxRuntime.Tensors.DenseTensor[float]' (,[int[]](1,3,1024,1024))
 for($i=0;$i -lt $f.Length;$i++){} # placeholder
 $t=[Microsoft.ML.OnnxRuntime.Tensors.DenseTensor[float]]::new([Memory[float]]$f,[int[]](1,3,1024,1024))
 $in=[Microsoft.ML.OnnxRuntime.NamedOnnxValue]::CreateFromTensor($name,$t)
 for($r=0;$r -lt $Runs;$r++){
  $sw.Restart();$res=$s.Run([Microsoft.ML.OnnxRuntime.NamedOnnxValue[]]@($in));$el=$sw.Elapsed.TotalSeconds
  $sum=0.0;foreach($v in $res){foreach($x in $v.AsEnumerable[float]()){$sum+=$x}}
  "img$k run$r enc_s=$([math]::Round($el,3)) sum=$([math]::Round($sum,2))"
 }
}
