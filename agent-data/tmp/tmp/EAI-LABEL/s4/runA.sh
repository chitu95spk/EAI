cd "/e/Code/EasyVision_Unisen - NoService"
B="python -I tmp/EAI-LABEL/s4/bench.py --n 6"
for i in 1 2 3 4 8; do $B --intra $i --tag A_free_i$i; done
$B --intra 1 --aff 0x1 --tag A_1core_i1
$B --intra 2 --aff 0x5 --tag A_2core_i2
$B --intra 1 --aff 0x5 --tag A_2core_i1
$B --intra 3 --aff 0x15 --tag A_3core_i3
$B --intra 2 --aff 0x5 --inter 2 --tag A_2core_i2_inter2
for L in ENABLE_BASIC ENABLE_EXTENDED ENABLE_ALL; do $B --intra 2 --aff 0x5 --level $L --tag B_2core_$L; done
