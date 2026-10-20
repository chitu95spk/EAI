$R = "E:\Code\EasyVision_Unisen - NoService\"
Set-Location $R
$env:TEMP = $R + "tmp\EAI-PROJ\temp"; $env:TMP = $env:TEMP
$env:EAI_LABEL_RUN = "1"; $env:EAI_LABEL_PROJECT = $R + "tmp\EAI-LABEL\s0\project"; $env:EAI_LABEL_APPDIR = $R + "artifacts\EAI-OKNG\b5\out"
$env:EAI_LABEL_OUT = $R + "artifacts\EAI-LABEL\s3-full-eco-par3-retry-sam2"; $env:EAI_LABEL_MAX_USD = "3.2"; $env:EAI_LABEL_PARALLEL = "3"
$env:EAI_LABEL_EFFORT = "Economy"; $env:EAI_LABEL_EXPERIENCE = "0"; $env:EAI_LABEL_RETRY_NORMAL = "1"; $env:EAI_LABEL_SAM2_REFINE = "1"
dotnet test EasyLabel\tests\EasyLabel.SegAI.Tests -c Release -p:Platform=x64 --no-build --filter FullyQualifiedName~MeasureAgentPromptBatchLabelling --logger "console;verbosity=detailed" > tmp\EAI-LABEL\s3full-run.log 2>&1
