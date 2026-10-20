cd "E:/Code/EasyVision_Unisen - NoService"
S=artifacts/EAI/EAI-CLS/p1/s3
export TEMP="$PWD/tmp/EAI-CLS-s3/temp" TMP="$PWD/tmp/EAI-CLS-s3/temp" DOTNET_CLI_HOME="$PWD/tmp/EAI-CLS-s2c/dotnet-home" DOTNET_CLI_TELEMETRY_OPTOUT=1
P=EasyLabel/tests/EasyLabel.SegAI.Tests/EasyLabel.SegAI.Tests.csproj
dotnet build $P -c Release -p:Platform=x64 > $S/build-parity-tests.log 2>&1; echo "parity-build exit=$?" >> $S/exitcodes.txt
EASYAI_PARITY_OUT=$PWD/$S/parity EASYAI_SEGAI_DIR=$PWD/$S/run dotnet test $P -c Release -p:Platform=x64 --no-build --filter FullyQualifiedName~ParityDumpTests > $S/parity-test.log 2>&1; echo "parity-test exit=$?" >> $S/exitcodes.txt
echo done > $S/parity-done.txt
