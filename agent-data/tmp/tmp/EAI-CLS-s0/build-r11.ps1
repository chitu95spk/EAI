$ProgressPreference = 'SilentlyContinue'
Set-Location "E:\Code\EasyVision_Unisen - NoService"
$env:TEMP = "E:\Code\EasyVision_Unisen - NoService\tmp\EAI-CLS-s0"
$env:TMP = $env:TEMP
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" "BeeNativeSegAI\BeeNativeSegAI.vcxproj" /t:Build /p:Configuration=Release /p:Platform=x64 /p:EasyVisionIsolatedVerification=true /v:normal > artifacts\EAI\EAI-CLS\r11-commit\build.log 2>&1
Write-Host "exit $LASTEXITCODE"
