# Rebuilds Models/DefectV2/mobilenetv2-12-s8s16.onnx (used by BarcodeStreakAnomaly) from the MobileNetV2 ONNX that
# Segment AI already ships (Models/DefectV2/mobilenetv2-12.onnx): the sub-graph input -> tensors 365 (stride 8,
# 32 ch) and 400 (stride 16, 64 ch), with dynamic spatial size. Needs: pip install onnx
import onnx, onnx.utils

src = r'Models\DefectV2\mobilenetv2-12.onnx'
dst = r'Models\DefectV2\mobilenetv2-12-s8s16.onnx'
onnx.utils.extract_model(src, dst, ['input'], ['365', '400'])
m = onnx.load(dst)
for d in m.graph.input[0].type.tensor_type.shape.dim[2:]:
    d.ClearField('dim_value')
    d.dim_param = 'dyn'
for o in m.graph.output:
    for d in list(o.type.tensor_type.shape.dim)[2:]:
        d.ClearField('dim_value')
        d.dim_param = 'dyn_' + o.name
del m.graph.value_info[:]
onnx.save(m, dst)
