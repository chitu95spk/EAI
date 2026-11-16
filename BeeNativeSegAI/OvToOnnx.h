// Prototype: OpenVINO ov::Model (SegAI V2 network) -> ONNX ModelProto bytes, no protobuf dependency.
// Mirrors scripts/ov2onnx.py. Runs ov::pass::ConstantFolding first, then maps 1:1:
//   Convolution->Conv, GroupConvolution->Conv(group), Add->Add, Multiply->Mul, Maximum->Max,
//   Minimum->Min, Relu->Relu, Concat->Concat, Softmax->Softmax, Interpolate(linear_onnx,half_pixel,sizes)->Resize.
// Throws std::runtime_error on any op/dtype it cannot map, so the caller can fall back to OpenVINO.
#pragma once
#include <openvino/openvino.hpp>
#include <openvino/op/ops.hpp>
#include <openvino/pass/manager.hpp>
#include <openvino/pass/constant_folding.hpp>
#include <cstdint>
#include <cstring>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

namespace segai_cuda {

class Pb {  // minimal protobuf wire writer
public:
    std::string s;
    void Varint(uint64_t v) { while (v >= 0x80) { s.push_back(char((v & 0x7F) | 0x80)); v >>= 7; } s.push_back(char(v)); }
    void Tag(int field, int wire) { Varint((uint64_t(field) << 3) | uint64_t(wire)); }
    void Int(int field, int64_t v) { Tag(field, 0); Varint(uint64_t(v)); }
    void Bytes(int field, const void* p, size_t n) { Tag(field, 2); Varint(n); s.append(static_cast<const char*>(p), n); }
    void Str(int field, const std::string& v) { Bytes(field, v.data(), v.size()); }
    void Msg(int field, const Pb& m) { Bytes(field, m.s.data(), m.s.size()); }
};

struct AttrI { std::string name; int64_t v; };
struct AttrInts { std::string name; std::vector<int64_t> v; };
struct AttrS { std::string name; std::string v; };

inline Pb Attr(const AttrI& a) { Pb p; p.Str(1, a.name); p.Int(3, a.v); p.Int(20, 2); return p; }                        // type INT=2
inline Pb Attr(const AttrS& a) { Pb p; p.Str(1, a.name); p.Str(4, a.v); p.Int(20, 3); return p; }                        // type STRING=3
inline Pb Attr(const AttrInts& a) { Pb p; p.Str(1, a.name); for (auto v : a.v) p.Int(8, v); p.Int(20, 7); return p; }    // type INTS=7

struct NodeSpec {
    std::string op, name;
    std::vector<std::string> in, out;
    std::vector<Pb> attrs;
};

inline Pb NodeMsg(const NodeSpec& n) {
    Pb p;
    for (auto& i : n.in) p.Str(1, i);
    for (auto& o : n.out) p.Str(2, o);
    p.Str(3, n.name);
    p.Str(4, n.op);
    for (auto& a : n.attrs) p.Msg(5, a);
    return p;
}

inline Pb Tensor(const std::string& name, int dataType, const std::vector<int64_t>& dims, const void* raw, size_t bytes) {
    Pb p;
    for (auto d : dims) p.Int(1, d);
    p.Int(2, dataType);  // FLOAT=1, INT64=7
    p.Str(8, name);
    p.Bytes(9, raw, bytes);
    return p;
}

inline Pb ValueInfo(const std::string& name, const std::vector<int64_t>& dims) {
    Pb shape; for (auto d : dims) { Pb dim; dim.Int(1, d); shape.Msg(1, dim); }
    Pb tt; tt.Int(1, 1); tt.Msg(2, shape);
    Pb ty; ty.Msg(1, tt);
    Pb vi; vi.Str(1, name); vi.Msg(2, ty);
    return vi;
}

inline std::vector<int64_t> ToI64(const ov::Shape& s) { return std::vector<int64_t>(s.begin(), s.end()); }
template <class T> inline std::vector<int64_t> ToI64v(const T& v) { return std::vector<int64_t>(v.begin(), v.end()); }

// Convert (folds constants on a clone; the caller's model is untouched). Returns serialized ONNX ModelProto.
inline std::string ConvertToOnnx(const std::shared_ptr<const ov::Model>& src) {
    auto m = src->clone();
    ov::pass::Manager mg;
    mg.register_pass<ov::pass::ConstantFolding>();
    mg.run_passes(m);

    auto outName = [](const ov::Output<const ov::Node>& o) { return o.get_node()->get_friendly_name() + ":" + std::to_string(o.get_index()); };
    std::vector<Pb> inits, nodes, graphIn, graphOut;
    std::set<std::string> seen;
    auto addConst = [&](const ov::Output<const ov::Node>& o, const std::string& nameOverride = {}) -> std::string {
        auto c = std::dynamic_pointer_cast<const ov::op::v0::Constant>(o.get_node_shared_ptr());
        if (!c) throw std::runtime_error(std::string("non-constant weight input after folding: ") + o.get_node()->get_type_name());
        std::string name = nameOverride.empty() ? outName(o) : nameOverride;
        if (c->get_element_type() != ov::element::f32) throw std::runtime_error("constant is not f32: " + name);
        if (seen.insert(name).second) inits.push_back(Tensor(name, 1, ToI64(c->get_shape()), c->get_data_ptr(), c->get_byte_size()));
        return name;
    };
    auto tname = [&](const ov::Input<ov::Node>& in) -> std::string {
        auto so = in.get_source_output();
        if (std::string(so.get_node()->get_type_name()) == "Constant") return addConst(so);
        return outName(so);
    };

    for (auto& op : m->get_ordered_ops()) {
        std::string t = op->get_type_name();
        if (t == "Constant" || t == "Result") continue;
        std::string o = outName(op->output(0));
        if (t == "Parameter") {
            if (op->get_output_partial_shape(0).is_dynamic()) throw std::runtime_error("dynamic input shape");
            graphIn.push_back(ValueInfo(o, ToI64(op->get_output_shape(0)))); continue;
        }
        NodeSpec n; n.name = op->get_friendly_name(); n.out = {o};
        for (auto& i : op->inputs()) { if (t == "Interpolate" && i.get_index() > 0) break; n.in.push_back(tname(i)); }  // Interpolate: only data input; sizes become Resize sizes
        if (auto c = std::dynamic_pointer_cast<ov::op::v1::Convolution>(op)) {
            n.op = "Conv";
            std::vector<int64_t> pads = ToI64v(c->get_pads_begin()); for (auto v : c->get_pads_end()) pads.push_back(int64_t(v));
            n.attrs = {Attr(AttrInts{"strides", ToI64v(c->get_strides())}), Attr(AttrInts{"dilations", ToI64v(c->get_dilations())}), Attr(AttrInts{"pads", pads})};
        } else if (auto g = std::dynamic_pointer_cast<ov::op::v1::GroupConvolution>(op)) {
            n.op = "Conv";
            auto wc = std::dynamic_pointer_cast<const ov::op::v0::Constant>(op->input_value(1).get_node_shared_ptr());
            if (!wc) throw std::runtime_error("GroupConvolution weights not constant");
            auto ws = wc->get_shape();  // [G, OC/G, IC/G, kH, kW] -> [G*OC/G, IC/G, kH, kW]
            std::vector<int64_t> d = {int64_t(ws[0] * ws[1])}; for (size_t i = 2; i < ws.size(); ++i) d.push_back(int64_t(ws[i]));
            std::string wn = tname(op->input(1)) + "_g";
            if (seen.insert(wn).second) inits.push_back(Tensor(wn, 1, d, wc->get_data_ptr(), wc->get_byte_size()));
            n.in[1] = wn;
            std::vector<int64_t> pads = ToI64v(g->get_pads_begin()); for (auto v : g->get_pads_end()) pads.push_back(int64_t(v));
            n.attrs = {Attr(AttrI{"group", int64_t(ws[0])}), Attr(AttrInts{"strides", ToI64v(g->get_strides())}),
                       Attr(AttrInts{"dilations", ToI64v(g->get_dilations())}), Attr(AttrInts{"pads", pads})};
        } else if (t == "Add") n.op = "Add";
        else if (t == "Multiply") n.op = "Mul";
        else if (t == "Maximum") n.op = "Max";
        else if (t == "Minimum") n.op = "Min";
        else if (t == "Relu") n.op = "Relu";
        else if (auto cc = std::dynamic_pointer_cast<ov::op::v0::Concat>(op)) { n.op = "Concat"; n.attrs = {Attr(AttrI{"axis", cc->get_axis()})}; }
        else if (auto s1 = std::dynamic_pointer_cast<ov::op::v1::Softmax>(op)) { n.op = "Softmax"; n.attrs = {Attr(AttrI{"axis", int64_t(s1->get_axis())})}; }
        else if (auto s8 = std::dynamic_pointer_cast<ov::op::v8::Softmax>(op)) { n.op = "Softmax"; n.attrs = {Attr(AttrI{"axis", s8->get_axis()})}; }
        else if (t == "Interpolate") {
            ov::op::util::InterpolateBase::InterpolateAttrs a;
            if (auto i4 = std::dynamic_pointer_cast<ov::op::v4::Interpolate>(op)) a = i4->get_attrs();
            else if (auto i11 = std::dynamic_pointer_cast<ov::op::v11::Interpolate>(op)) a = i11->get_attrs();
            else throw std::runtime_error("unknown Interpolate version");
            if (a.mode != ov::op::util::InterpolateBase::InterpolateMode::LINEAR_ONNX ||
                a.coordinate_transformation_mode != ov::op::util::InterpolateBase::CoordinateTransformMode::HALF_PIXEL ||
                a.shape_calculation_mode != ov::op::util::InterpolateBase::ShapeCalcMode::SIZES)
                throw std::runtime_error("Interpolate attributes not supported by ONNX Resize mapping");
            auto os = op->get_output_shape(0);
            std::vector<int64_t> sizes = ToI64(os);
            std::string sn = o + "_sizes";
            if (seen.insert(sn).second) inits.push_back(Tensor(sn, 7, {int64_t(sizes.size())}, sizes.data(), sizes.size() * 8));
            n.op = "Resize"; n.in = {n.in[0], "", "", sn};
            n.attrs = {Attr(AttrS{"mode", "linear"}), Attr(AttrS{"coordinate_transformation_mode", "half_pixel"})};
        } else throw std::runtime_error("unsupported op for ONNX conversion: " + t);
        nodes.push_back(NodeMsg(n));
    }
    auto res = m->get_results();
    if (res.size() != 1 || graphIn.size() != 1) throw std::runtime_error("expect exactly 1 input and 1 output");
    graphOut.push_back(ValueInfo(outName(res[0]->input_value(0)), ToI64(res[0]->get_input_shape(0))));

    Pb g;
    for (auto& n : nodes) g.Msg(1, n);
    g.Str(2, "segai_v2");
    for (auto& i : inits) g.Msg(5, i);
    for (auto& i : graphIn) g.Msg(11, i);
    for (auto& o : graphOut) g.Msg(12, o);
    Pb opset; opset.Str(1, ""); opset.Int(2, 17);
    Pb model; model.Int(1, 9); model.Msg(8, opset); model.Msg(7, g);
    return model.s;
}

}  // namespace segai_cuda
