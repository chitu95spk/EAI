param([string]$Name,[int]$Par,[string]$Sam2="1",[string]$Effort="Economy",[string]$Retry="1",[string]$Prompt="Mark all five metal fan assemblies in the center with tight oriented_rectangle boxes labeled OK.",[string]$MaxUsd="1.6")
$R = "E:\Code\EasyVision_Unisen - NoService\"
Set-Location $R
$env:TEMP = $R + "tmp\EAI-LABEL\speed\temp"; $env:TMP = $env:TEMP
$env:EAI_LABEL_RUN = "1"; $env:EAI_LABEL_PROJECT = $R + "tmp\EAI-LABEL\speed\data"; $env:EAI_LABEL_APPDIR = $R + "artifacts\EAI-OKNG\b5\out"
$env:EAI_LABEL_OUT = $R + "artifacts\EAI-LABEL\speed\" + $Name; $env:EAI_LABEL_MAX_USD = $MaxUsd; $env:EAI_LABEL_PARALLEL = "$Par"
$env:EAI_LABEL_EFFORT = $Effort; $env:EAI_LABEL_EXPERIENCE = "0"; $env:EAI_LABEL_RETRY_NORMAL = $Retry; $env:EAI_LABEL_SAM2_REFINE = $Sam2
$env:EAI_LABEL_PROMPT = $Prompt
dotnet test EasyLabel\tests\EasyLabel.SegAI.Tests -c Release -p:Platform=x64 --no-build --filter FullyQualifiedName~MeasureAgentPromptBatchLabelling --logger "console;verbosity=detailed" > "tmp\EAI-LABEL\speed\$Name.log" 2>&1
