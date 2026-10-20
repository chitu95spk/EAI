$R = "E:\Code\EasyVision_Unisen - NoService\"
Set-Location $R
$env:TEMP = $R + "tmp\EAI-PROJ\temp"; $env:TMP = $env:TEMP
$env:EAI_LABEL_RUN = "1"; $env:EAI_LABEL_OUT = $R + "artifacts\EAI-LABEL\s3-full-eco-par3-retry-sam2"
$env:EAI_LABEL_BASELINE = $R + "artifacts\EAI-LABEL\s0-baseline"
dotnet test EasyLabel\tests\EasyLabel.SegAI.Tests -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~LabelMetricsOnGroundTruthTests|FullyQualifiedName~LabelMetricsVsBaselineTests" > artifacts\EAI-LABEL\s3-full-eco-par3-retry-sam2\score.log 2>&1
