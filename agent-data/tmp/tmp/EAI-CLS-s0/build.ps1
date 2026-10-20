$ProgressPreference = 'SilentlyContinue'
Set-Location "E:\Code\EasyVision_Unisen - NoService"
$env:TEMP = "E:\Code\EasyVision_Unisen - NoService\tmp\EAI-CLS-s0"
$env:TMP = "E:\Code\EasyVision_Unisen - NoService\tmp\EAI-CLS-s0"

& "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" "BeeNativeSegAI\test\SegAITest.vcxproj" /t:Build /p:Configuration=Release /p:Platform=x64 /p:EasyVisionIsolatedVerification=true /v:minimal

Write-Host "Build exit code: $LASTEXITCODE"
