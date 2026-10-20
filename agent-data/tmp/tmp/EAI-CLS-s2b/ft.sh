cd "E:/Code/EasyVision_Unisen - NoService"
S=artifacts/EAI/EAI-CLS/p1/s2b
export TEMP="$PWD/tmp/EAI-CLS-s2b/temp" TMP="$PWD/tmp/EAI-CLS-s2b/temp"
for job in head-3 head-4 head-5 new-3 new-4 new-5; do
  case $job in head*) R=$S/run-head/SegAITest.exe;; *) R=$S/run/SegAITest.exe;; esac
  echo "$R --class-train-finetune $S/ft-$job artifacts/EAI/EAI-CLS/p1/s2b/run/Models/DefectV2/mobilenetv2-12.onnx artifacts/EAI/EAI-CLS/p1/s0/run/tmp/SEG-LIFECYCLE-r11/ft/run/shared/mobilenetv2-12.taps.pt - n3" >> $S/commands.txt
  "$R" --class-train-finetune $S/ft-$job artifacts/EAI/EAI-CLS/p1/s2b/run/Models/DefectV2/mobilenetv2-12.onnx artifacts/EAI/EAI-CLS/p1/s0/run/tmp/SEG-LIFECYCLE-r11/ft/run/shared/mobilenetv2-12.taps.pt - n3 > $S/ft-$job.log 2>&1
  echo "ft-$job exit=$?" >> $S/exitcodes.txt
done
echo done >> $S/ft-done2.txt
