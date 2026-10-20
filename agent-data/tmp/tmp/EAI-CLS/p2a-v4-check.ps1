# Reflection check of BeeCore SegmentAI.CheckModelFile / CheckClassTable / ClassifyRejectedModel (all private static).
param([string]$Dll = "E:\Code\EasyVision_Unisen - NoService\artifacts\verification\Release\x64\bin\BeeCore\BeeCore.dll",
      [string]$Work = "E:\Code\EasyVision_Unisen - NoService\tmp\EAI-CLS\p2a-v4-files")
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Work | Out-Null
$dir = Split-Path $Dll
[AppDomain]::CurrentDomain.add_AssemblyResolve({ param($s,$a)
  $n = ($a.Name.Split(',')[0]) + '.dll'; $p = Join-Path $dir $n
  if (Test-Path $p) { return [Reflection.Assembly]::LoadFrom($p) }; return $null })
$asm = [Reflection.Assembly]::LoadFrom($Dll)
$t = $asm.GetType('BeeCore.SegmentAI', $true)
$bf = [Reflection.BindingFlags]'NonPublic,Static'
$mCheck = $t.GetMethod('CheckModelFile', $bf)
$mTable = $t.GetMethod('CheckClassTable', $bf)
$mClass = $t.GetMethod('ClassifyRejectedModel', $bf)
$mCrc   = $t.GetMethod('FastCrc', $bf)
function Crc([byte[]]$b){ [uint32]$mCrc.Invoke($null, @($b, 0, $b.Length)) }
function Table($classes, [switch]$BreakCrc){
  $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter($ms)
  $w.Write([byte[]][char[]]'SCLS'); $w.Write([uint32]1); $w.Write([uint32]$classes.Count)
  foreach($c in $classes){ $nb=[Text.Encoding]::UTF8.GetBytes($c.n); $w.Write([byte]$c.k); $w.Write([byte]$c.e); $w.Write([uint16]$nb.Length); $w.Write($nb) }
  $w.Flush(); $body = $ms.ToArray(); $crc = Crc $body; if($BreakCrc){ $crc = $crc -bxor 1 }
  return ,($body + [BitConverter]::GetBytes([uint32]$crc))
}
function BuildFile([uint32]$ver,[uint32]$numClasses,[byte[]]$table){
  $yaml = [Text.Encoding]::UTF8.GetBytes("%YAML:1.0`n---`nnative_v2: 1`npostprocess_version: 2`nside: 224`n")
  $tl = 0; if($table){ $tl = $table.Length }
  $f = New-Object byte[] (72 + $yaml.Length + $tl)
  [byte[]](83,69,71,65,73,0,0,0) | % -Begin {$i=0} -Process { $f[$i++]=$_ }
  function Put($off,[byte[]]$b){ [Array]::Copy($b,0,$f,$off,$b.Length) }
  Put 8 ([BitConverter]::GetBytes($ver)); Put 12 ([BitConverter]::GetBytes([uint32]0x02000000))
  Put 16 ([BitConverter]::GetBytes($numClasses)); Put 20 ([BitConverter]::GetBytes([uint32]448)); Put 28 ([BitConverter]::GetBytes([uint32]1))
  Put 52 ([BitConverter]::GetBytes([uint32]$tl)); Put 56 ([BitConverter]::GetBytes([uint64]$yaml.Length))
  Put 64 ([BitConverter]::GetBytes([uint32](Crc $yaml)))
  Put 72 $yaml; if($table){ Put (72+$yaml.Length) $table }
  return ,$f
}
function K($n,$k,$e){ [pscustomobject]@{n=$n;k=$k;e=$e} }
$ok   = @((K 'Scratch' 0 1),(K 'Dent' 0 0),(K 'Tot' 1 1))
$dup  = @((K 'Scratch' 0 1),(K 'Scratch' 0 1),(K 'Tot' 1 1))
$two  = @((K 'Scratch' 0 1),(K 'Good1' 1 1),(K 'Good2' 1 1))
$cases = [ordered]@{
 'v4-valid'      = @{ bytes = BuildFile 4 4 (Table $ok);              expect = 'true' }
 'v4-bad-crc'    = @{ bytes = BuildFile 4 4 (Table $ok -BreakCrc);    expect = 'false CRC' }
 'v4-duplicate'  = @{ bytes = BuildFile 4 4 (Table $dup);             expect = 'false duplicate' }
 'v4-two-good'   = @{ bytes = BuildFile 4 4 (Table $two);             expect = 'false GOOD' }
 'v5'            = @{ bytes = BuildFile 5 4 (Table $ok);              expect = 'false version 5' }
}
$real = @{
 'v3-real' = 'E:\Code\EasyVision_Unisen - NoService\artifacts\EAI\EAI-CLS\p1\baseline-ext\train6-a\train-run.segai'
 'v2-real' = 'E:\Code\EasyVision_Unisen - NoService\artifacts\EAI\EAI-CLS\p1\s0\run\tmp\V105-tchi-validation\TCHI_LOA_Structure_V2.segai'
 'v1-real' = 'E:\Code\EasyVision_Unisen - NoService\artifacts\EAI\EAI-CLS\p1\s0\run\tmp\V105-tchi-validation\defect_20260910_161533_1d842bbb7b0147efb76149484bd31b6f.segai'
}
foreach($k in $cases.Keys){ $p = Join-Path $Work "$k.segai"; [IO.File]::WriteAllBytes($p,$cases[$k].bytes); $cases[$k].path=$p }
foreach($k in $real.Keys){ $cases[$k] = @{ path=$real[$k]; expect='true (unchanged)' } }
foreach($k in $cases.Keys){
  $p = [string]$cases[$k].path
  [object[]]$a = @($p,$null,$null); $r = $mCheck.Invoke($null,$a)
  [object[]]$b = @($p,[int]-5,'',$null,$null); $mClass.Invoke($null,$b) | Out-Null
  "{0,-14} expect={1,-18} CheckModelFile={2,-5} viCause={3} | ClassifyRejected(en)={4}" -f $k,$cases[$k].expect,$r,$a[2],$b[4]
}
# Direct CheckClassTable on the v4 files
foreach($k in 'v4-valid','v4-bad-crc','v4-duplicate','v4-two-good'){
  $all = [IO.File]::ReadAllBytes($cases[$k].path); $pl=[BitConverter]::ToUInt64($all,56); $tb=[BitConverter]::ToUInt32($all,52); $nc=[BitConverter]::ToUInt32($all,16)
  [object[]]$a = @($all,[int](72+$pl),[int]$tb,[uint32]$nc,$null,$null); $r=$mTable.Invoke($null,$a)
  "CheckClassTable {0,-14} => {1} | {2}" -f $k,$r,$a[5]
}
