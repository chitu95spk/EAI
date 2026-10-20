#!/bin/bash
# Usage: bash tmp/EAI-ALL/verify.sh <artifacts-subdir e.g. EAI-AGENT/s2>
# Builds EasyAI.Wpf (isolated OutDir) and runs the 4 EasyLabel test projects; prints only summaries.
cd "e:/Code/EasyVision_Unisen - NoService" || exit 1
tag="$1"; d="artifacts/$tag"; mkdir -p "$d"
export MSYS_NO_PATHCONV=1 TEMP='E:\Code\EasyVision_Unisen - NoService\tmp\EAI-PROJ\temp' TMP='E:\Code\EasyVision_Unisen - NoService\tmp\EAI-PROJ\temp'
win="E:\\Code\\EasyVision_Unisen - NoService\\artifacts\\${tag//\//\\}\\out\\\\"
"/c/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe" EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -t:Build -p:Configuration=Release -p:Platform=x64 "-p:OutDir=$win" -v:minimal > "$d/build.log" 2>&1
echo "build exit $?"
grep -E ": error " "$d/build.log" | sort -u | head -15 | cut -c1-320
for p in SegAI Core Ai.Mcp Ai.Onnx Camera; do
  dotnet test EasyLabel/tests/EasyLabel.$p.Tests -c Release -p:Platform=x64 > "$d/test-$p.log" 2>&1
  echo "== $p"
  grep -E "^(Passed|Failed)!|  Failed |: error " "$d/test-$p.log" | sort -u | head -8 | cut -c1-260
done
