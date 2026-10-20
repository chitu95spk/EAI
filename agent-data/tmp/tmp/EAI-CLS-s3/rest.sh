cd "E:/Code/EasyVision_Unisen - NoService"
S=artifacts/EAI/EAI-CLS/p1/s3
export TEMP="$PWD/tmp/EAI-CLS-s3/temp" TMP="$PWD/tmp/EAI-CLS-s3/temp"
R=$S/run/SegAITest.exe
run(){ n=$1; shift; echo "$R $*" >> $S/commands.txt; s=$(date +%s); "$R" "$@" > $S/$n.log 2>&1; echo "$n exit=$? elapsed=$(( $(date +%s)-s ))s" >> $S/exitcodes.txt; }
run t7 --class-train $S/t7out - -
run t8 --class-multi $S/t8out
M1=artifacts/V133/native-checks/ng-only/model.segai
M2=artifacts/V133/native-checks/ng-and-ok/model.segai
run infer-ng-only-r1 --baseline-ext $M1 tmp/EAI/EAI-3/fixture-src $S/infer-ng-only-r1
run infer-ng-only-r2 --baseline-ext $M1 tmp/EAI/EAI-3/fixture-src $S/infer-ng-only-r2
run infer-ng-and-ok-r1 --baseline-ext $M2 tmp/EAI/EAI-3/fixture-src $S/infer-ng-and-ok-r1
run infer-ng-and-ok-r2 --baseline-ext $M2 tmp/EAI/EAI-3/fixture-src $S/infer-ng-and-ok-r2
run train-a --baseline-ext $M1 tmp/EAI/EAI-3/fixture-src $S/train-a train
run train-b --baseline-ext $M1 tmp/EAI/EAI-3/fixture-src $S/train-b train
run ft-new-1 --class-train-finetune $S/ft-new-1 $S/run/Models/DefectV2/mobilenetv2-12.onnx artifacts/EAI/EAI-CLS/p1/s0/run/tmp/SEG-LIFECYCLE-r11/ft/run/shared/mobilenetv2-12.taps.pt - n3
echo done > $S/rest-done.txt
