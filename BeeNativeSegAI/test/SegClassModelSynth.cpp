#include "SegClassModelSynth.h"

#include "../SegClassTable.h"

#include <openvino/openvino.hpp>
#include <openvino/opsets/opset13.hpp>
#include <openvino/pass/serialize.hpp>
#include <opencv2/core.hpp>

#include <cstdint>
#include <cstring>
#include <fstream>
#include <iterator>
#include <memory>
#include <sstream>

namespace {

uint32_t Read32(const std::vector<unsigned char>& bytes, size_t offset)
{
    return static_cast<uint32_t>(bytes[offset]) | (static_cast<uint32_t>(bytes[offset + 1]) << 8) |
           (static_cast<uint32_t>(bytes[offset + 2]) << 16) | (static_cast<uint32_t>(bytes[offset + 3]) << 24);
}

void Write32(std::vector<unsigned char>& bytes, size_t offset, uint32_t value)
{
    for (size_t index = 0; index < 4; ++index) bytes[offset + index] = static_cast<unsigned char>((value >> (8 * index)) & 0xFFu);
}

std::string TypeName(const std::shared_ptr<ov::Node>& node)
{
    return std::string(node->get_type_name());
}

} // namespace

bool SynthesizeClassModel(const std::filesystem::path& sourcePath, const std::filesystem::path& destinationPath,
                          const std::vector<SynthChannel>& channels, const std::vector<unsigned char>& tableBytes,
                          int postprocessVersion, std::string& error)
{
    try {
        const size_t classCount = channels.size();
        if (classCount < 2 || classCount > 33) {
            error = "channel count must be 2..33";
            return false;
        }
        for (const SynthChannel& channel : channels) {
            if (channel.sourceChannel < 0 || channel.sourceChannel > 2) {
                error = "source channel must be 0, 1 or 2";
                return false;
            }
        }

        std::ifstream input(sourcePath, std::ios::binary);
        if (!input) {
            error = "cannot open the source model " + sourcePath.u8string();
            return false;
        }
        const std::vector<unsigned char> file((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
        if (file.size() < 72 || std::memcmp(file.data(), "SEGAI\0\0", 8) != 0) {
            error = "the source is not a .segai file";
            return false;
        }
        const uint32_t sourceVersion = Read32(file, 8);
        const uint64_t payloadSize = static_cast<uint64_t>(Read32(file, 56)) | (static_cast<uint64_t>(Read32(file, 60)) << 32);
        const uint64_t sourceTableBytes = sourceVersion == 4 ? Read32(file, 52) : 0;
        if ((sourceVersion != 3 && sourceVersion != 4) || Read32(file, 16) != 3 || 72 + payloadSize + sourceTableBytes != file.size()) {
            error = "the source must be a version 3 model, or a version 4 model with two classes";
            return false;
        }

        const std::string payload(reinterpret_cast<const char*>(file.data()) + 72, static_cast<size_t>(payloadSize));
        cv::FileStorage source(payload, cv::FileStorage::READ | cv::FileStorage::MEMORY);
        int nativeV2 = 0, side = 0, priorBalanced = 0;
        double minimumFraction = 0;
        source["native_v2"] >> nativeV2;
        source["side"] >> side;
        source["minimum_fraction"] >> minimumFraction;
        const bool hasPrior = !source["prior_balanced"].empty();
        if (hasPrior) source["prior_balanced"] >> priorBalanced;
        cv::Mat xmlMat, binMat;
        source["network_xml"] >> xmlMat;
        source["network_bin"] >> binMat;
        if (xmlMat.empty() || binMat.empty() || xmlMat.type() != CV_8U || binMat.type() != CV_8U) {
            error = "the source payload has no network";
            return false;
        }

        ov::Core core;
        ov::Tensor weights(ov::element::u8, {binMat.total()}, binMat.data);
        std::shared_ptr<ov::Model> model = core.read_model(std::string(reinterpret_cast<char*>(xmlMat.data), xmlMat.total()), weights);

        // Final graph of a trained model: Conv(hidden -> 3) + bias -> Softmax(axis 1) [-> Multiply(class mask)] -> Result.
        std::shared_ptr<ov::Node> node = model->get_results().at(0)->input_value(0).get_node_shared_ptr();
        if (TypeName(node) == "Multiply") node = node->input_value(0).get_node_shared_ptr();
        if (TypeName(node) != "Softmax") {
            error = "unexpected network tail: " + TypeName(node) + " instead of Softmax";
            return false;
        }
        std::shared_ptr<ov::Node> add = node->input_value(0).get_node_shared_ptr();
        if (TypeName(add) != "Add") {
            error = "unexpected network tail: " + TypeName(add) + " instead of Add";
            return false;
        }
        std::shared_ptr<ov::Node> convolution = add->input_value(0).get_node_shared_ptr();
        if (TypeName(convolution) != "Convolution") {
            error = "unexpected network tail: " + TypeName(convolution) + " instead of Convolution";
            return false;
        }
        auto biasConstant = ov::as_type_ptr<ov::op::v0::Constant>(add->input_value(1).get_node_shared_ptr());
        auto kernelConstant = ov::as_type_ptr<ov::op::v0::Constant>(convolution->input_value(1).get_node_shared_ptr());
        if (!biasConstant || !kernelConstant) {
            error = "the head weights are not constants";
            return false;
        }
        const ov::Shape kernelShape = kernelConstant->get_shape();
        if (kernelShape.size() != 4 || kernelShape[0] != 3 || kernelShape[2] != 1 || kernelShape[3] != 1) {
            error = "unexpected head kernel shape";
            return false;
        }
        const size_t hidden = kernelShape[1];
        const std::vector<float> kernel = kernelConstant->cast_vector<float>();
        const std::vector<float> bias = biasConstant->cast_vector<float>();
        if (kernel.size() != 3 * hidden || bias.size() != 3) {
            error = "unexpected head weight count";
            return false;
        }

        std::vector<float> newKernel(classCount * hidden), newBias(classCount);
        for (size_t channel = 0; channel < classCount; ++channel) {
            const size_t from = static_cast<size_t>(channels[channel].sourceChannel);
            for (size_t j = 0; j < hidden; ++j) newKernel[channel * hidden + j] = kernel[from * hidden + j];
            newBias[channel] = bias[from] + channels[channel].biasShift;
        }
        auto newConvolution = std::make_shared<ov::opset13::Convolution>(
            convolution->input_value(0),
            ov::opset13::Constant::create(ov::element::f32, ov::Shape{classCount, hidden, 1, 1}, newKernel),
            ov::Strides{1, 1}, ov::CoordinateDiff{0, 0}, ov::CoordinateDiff{0, 0}, ov::Strides{1, 1});
        auto newAdd = std::make_shared<ov::opset13::Add>(
            newConvolution, ov::opset13::Constant::create(ov::element::f32, ov::Shape{1, classCount, 1, 1}, newBias));
        ov::Output<ov::Node> probabilities = std::make_shared<ov::opset13::Softmax>(newAdd, 1);
        auto newModel = std::make_shared<ov::Model>(ov::OutputVector{probabilities}, model->get_parameters(), "NativeDefectV2_Prototype");

        std::ostringstream xml, bin;
        ov::pass::Serialize serializer(xml, bin);
        serializer.run_on_model(newModel);
        const std::string x = xml.str(), b = bin.str();

        cv::FileStorage storage("v2.yml", cv::FileStorage::WRITE | cv::FileStorage::MEMORY | cv::FileStorage::BASE64);
        storage << "native_v2" << nativeV2 << "side" << side << "minimum_fraction" << minimumFraction;
        storage << "postprocess_version" << postprocessVersion;
        if (hasPrior) storage << "prior_balanced" << priorBalanced;
        storage << "network_xml" << cv::Mat(1, static_cast<int>(x.size()), CV_8U, const_cast<char*>(x.data()));
        storage << "network_bin" << cv::Mat(1, static_cast<int>(b.size()), CV_8U, const_cast<char*>(b.data()));
        const std::string newPayload = storage.releaseAndGetString();

        std::vector<unsigned char> header(file.begin(), file.begin() + 72);
        Write32(header, 8, 4);
        Write32(header, 16, static_cast<uint32_t>(classCount));
        Write32(header, 52, static_cast<uint32_t>(tableBytes.size()));
        Write32(header, 56, static_cast<uint32_t>(newPayload.size()));
        Write32(header, 60, static_cast<uint32_t>(static_cast<uint64_t>(newPayload.size()) >> 32));
        Write32(header, 64, BeeSegAI::ClassTableCrc32(reinterpret_cast<const unsigned char*>(newPayload.data()), newPayload.size()));

        if (!destinationPath.parent_path().empty()) std::filesystem::create_directories(destinationPath.parent_path());
        std::ofstream output(destinationPath, std::ios::binary | std::ios::trunc);
        output.write(reinterpret_cast<const char*>(header.data()), static_cast<std::streamsize>(header.size()));
        output.write(newPayload.data(), static_cast<std::streamsize>(newPayload.size()));
        output.write(reinterpret_cast<const char*>(tableBytes.data()), static_cast<std::streamsize>(tableBytes.size()));
        if (!output.good()) {
            error = "cannot write " + destinationPath.u8string();
            return false;
        }
        return true;
    } catch (const std::exception& e) {
        error = e.what();
        return false;
    }
}
