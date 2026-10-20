$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=(Resolve-Path '.').Path
$src=Join-Path $root 'tmp\EAI-OKNG\data'; $dst=Join-Path $root 'tmp\EAI-OKNG\smoke-project'
if(Test-Path $dst){Remove-Item $dst -Recurse -Force}
New-Item -ItemType Directory $dst | Out-Null
Copy-Item "$src\*" $dst -Recurse
$pngs=Get-ChildItem $dst -Filter *.png | Sort-Object Name
$ng=@($pngs|?{$_.Name -match '_NG\.'}|select -First 6); $ok=@($pngs|?{$_.Name -notmatch '_NG\.'}|select -First 6)
function W($f,$flag){
 $im=[System.Drawing.Image]::FromFile($f.FullName); $w=$im.Width;$h=$im.Height;$im.Dispose()
 $o=[ordered]@{version='5.5.0';flags=@{$flag=$true};shapes=@();imagePath=$f.Name;imageData=$null;imageHeight=$h;imageWidth=$w}
 ($o|ConvertTo-Json -Depth 5)|Set-Content ([IO.Path]::ChangeExtension($f.FullName,'.json')) -Encoding utf8
}
$ok|%{W $_ 'ok'}; $ng|%{W $_ 'ng'}
"ok: "+($ok.Name -join ','); "ng: "+($ng.Name -join ',')
