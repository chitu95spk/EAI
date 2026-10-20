#!/bin/bash
# IoU variants on the first 6 GT images (all NG-named). Budget: B <= 0.6 USD, A <= 1.2 USD.
R="/e/Code/EasyVision_Unisen - NoService"
RW="$(cd "$R" && pwd -W)"
export TEMP="$RW/tmp/EAI-LABEL/tmp" TMP="$RW/tmp/EAI-LABEL/tmp" EAI_LABEL_RUN=1
export EAI_LABEL_PROJECT="$RW/tmp/EAI-LABEL/s0/project" EAI_LABEL_APPDIR="$RW/artifacts/EAI-OKNG/b5/out" EAI_LABEL_PARALLEL=3
IMGS=$(ls /e/EAI_TEST/*.json | head -6 | xargs -n1 basename | sed 's/\.json$/.png/' | paste -sd, -)
export EAI_LABEL_IMAGES="$IMGS"
cd "$R/EasyLabel"
run() { # name effort prompt maxusd
  export EAI_LABEL_OUT="$RW/artifacts/EAI-LABEL/iou-$1" EAI_LABEL_EFFORT="$2" EAI_LABEL_MAX_USD="$4"
  if [ -n "$3" ]; then export EAI_LABEL_PROMPT="$3"; else unset EAI_LABEL_PROMPT; fi
  mkdir -p "$EAI_LABEL_OUT"
  dotnet test tests/EasyLabel.SegAI.Tests -c Release -p:Platform=x64 --no-build --filter FullyQualifiedName~MeasureAgentPromptBatchLabelling > "$EAI_LABEL_OUT/run.log" 2>&1
  dotnet test tests/EasyLabel.SegAI.Tests -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~LabelMetricsOnGroundTruthTests" > "$EAI_LABEL_OUT/score.log" 2>&1
}
run eco-tight Economy "Label every fan impeller part on the tray with an oriented_rectangle, class OK. Fit each box tightly to the visible part outline, with no margin." 0.6
run normal Normal "" 1.2
echo ALLDONE > "$RW/artifacts/EAI-LABEL/iou-done.txt"
