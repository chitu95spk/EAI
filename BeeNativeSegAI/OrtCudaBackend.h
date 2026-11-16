// NVIDIA CUDA backend for SegAI V2: runs the feature+head graph with ONNX Runtime (CUDA execution provider).
// Header-only and included by exactly ONE translation unit (SegV2Core.cpp) because ORT_API_MANUAL_INIT must agree everywhere.
//
// onnxruntime.dll is NOT linked: it is loaded on first use from the folder of BeeNativeSegAI.dll. Machines without ORT/CUDA load
// BeeNativeSegAI.dll normally and CPU / Intel GPU keep working; selecting CUDA there throws a descriptive std::runtime_error
// that the caller turns into a visible fallback to CPU.
//
// Traps handled here (see docs/segai-tune/CUDA_BACKEND.md):
//  * C:\Windows\System32\onnxruntime.dll shadows the bundled one (0xC000007B) -> always load the copy next to this DLL by full path.
//  * A missing cublasLt64_13.dll etc. makes the CUDA EP fall back to CPU SILENTLY -> required runtime DLLs are pre-loaded and named
//    in the error, and the session is created with session.disable_cpu_ep_fallback=1 so a CPU-placed node is an error, never a silent slowdown.
#pragma once
#define ORT_API_MANUAL_INIT
#include "OvToOnnx.h"
#include <onnxruntime_cxx_api.h>
#include <openvino/opsets/opset13.hpp>
#include <windows.h>
#include <iostream>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>
#include <chrono>

namespace segai_cuda {

inline std::wstring ModuleFolder() {
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&ModuleFolder), &module)) return L"";
    std::wstring path(1024, L'\0');
    const DWORD n = GetModuleFileNameW(module, path.data(), DWORD(path.size()));
    path.resize(n);
    const size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"" : path.substr(0, slash);
}

inline std::string Narrow(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), int(w.size()), nullptr, 0, nullptr, nullptr);
    std::string s(size_t(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), int(w.size()), s.data(), n, nullptr, nullptr);
    return s;
}

inline bool FileExists(const std::wstring& path) {
    const DWORD a = GetFileAttributesW(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

// Loads onnxruntime.dll (the one next to BeeNativeSegAI.dll) and the CUDA runtime it needs. Throws with a user-readable reason.
// Success is cached; failure is retried (the user may copy the missing files and press the button again).
inline void EnsureOrtLoaded() {
    static std::mutex lock;
    static bool loaded = false;
    std::lock_guard<std::mutex> guard(lock);
    if (loaded) return;
    const std::wstring folder = ModuleFolder();
    if (folder.empty()) throw std::runtime_error("CUDA: cannot locate the BeeNativeSegAI.dll folder");
    const std::wstring ort = folder + L"\\onnxruntime.dll";
    if (!FileExists(ort)) throw std::runtime_error("CUDA: onnxruntime.dll not found next to BeeNativeSegAI.dll (" + Narrow(folder) + ")");
    if (!FileExists(folder + L"\\onnxruntime_providers_cuda.dll") || !FileExists(folder + L"\\onnxruntime_providers_shared.dll"))
        throw std::runtime_error("CUDA: onnxruntime_providers_cuda.dll / onnxruntime_providers_shared.dll not found next to BeeNativeSegAI.dll (" + Narrow(folder) + ")");
    // Dependencies of the provider (cuBLAS, cuDNN, ...) are searched in this folder first.
    // AddDllDirectory is per-process but additive (only used by LOAD_LIBRARY_SEARCH_USER_DIRS loaders) and its result is checked.
    // SetDllDirectoryW is process-wide and replaces the host's choice, so it is only applied when the host has NOT set one
    // (GetDllDirectoryW empty); cuDNN lazily loads cudnn_engines_*.dll with the legacy search order and needs this folder there.
    // Failures are not fatal (the explicit full-path loads below may still succeed) but are kept and appended to any later error.
    std::string searchNote;
    if (!AddDllDirectory(folder.c_str())) searchNote += " AddDllDirectory failed (Win32 error " + std::to_string(GetLastError()) + ").";
    const DWORD hostDirLen = GetDllDirectoryW(0, nullptr);
    if (hostDirLen > 1) {
        // The host already called SetDllDirectory: keep it, do not override a process-wide setting.
        searchNote += " Host DLL directory kept (SetDllDirectory not changed).";
    } else if (!SetDllDirectoryW(folder.c_str())) {
        searchNote += " SetDllDirectoryW failed (Win32 error " + std::to_string(GetLastError()) + ").";
    }
    if (!searchNote.empty()) std::cerr << "SegAI CUDA:" << searchNote << std::endl;
    HMODULE ortModule = LoadLibraryExW(ort.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!ortModule) throw std::runtime_error("CUDA: cannot load " + Narrow(ort) + " (Win32 error " + std::to_string(GetLastError()) + ")" + searchNote);
    using GetApiBaseFn = const OrtApiBase* (ORT_API_CALL*)();
    auto getBase = reinterpret_cast<GetApiBaseFn>(GetProcAddress(ortModule, "OrtGetApiBase"));
    if (!getBase) throw std::runtime_error("CUDA: onnxruntime.dll has no OrtGetApiBase");
    const OrtApi* api = getBase()->GetApi(ORT_API_VERSION);
    if (!api) throw std::runtime_error("CUDA: onnxruntime.dll is older than the API this build needs (ORT API " + std::to_string(ORT_API_VERSION) + ")");
    Ort::InitApi(api);
    // Pre-load the CUDA runtime so a missing file is named instead of the CUDA EP degrading to CPU without an error.
    static const wchar_t* const required[] = {L"cudart64_13.dll", L"cublas64_13.dll", L"cublasLt64_13.dll", L"cudnn64_9.dll"};
    for (const wchar_t* name : required) {
        const std::wstring full = folder + L"\\" + name;
        HMODULE m = FileExists(full) ? LoadLibraryExW(full.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH) : LoadLibraryW(name);
        if (!m) throw std::runtime_error("CUDA runtime file missing or not loadable: " + Narrow(name) + " (put it next to BeeNativeSegAI.dll or install the CUDA 13 runtime)" + searchNote);
    }
    loaded = true;
}

class OrtCudaBackend {
public:
    // deviceId: CUDA ordinal. useTf32=false keeps results within ~3e-6 of FP32 OpenVINO CPU (TF32 gives ~1e-2 drift).
    OrtCudaBackend(const std::shared_ptr<const ov::Model>& model, int deviceId = 0, bool useTf32 = false) {
        EnsureOrtLoaded();
        env_ = std::make_unique<Ort::Env>(ORT_LOGGING_LEVEL_WARNING, "SegAICuda");
        const auto convertBegin = std::chrono::steady_clock::now();
        onnx_ = ConvertToOnnx(model);
        const auto converted = std::chrono::steady_clock::now();
        Ort::SessionOptions so;
        so.SetGraphOptimizationLevel(GraphOptimizationLevel::ORT_ENABLE_ALL);
        // Explicit opt-in diagnostic trace; normally absent, so no per-Run profiling overhead.
#pragma warning(suppress : 4996)
        const wchar_t* profilePrefix = _wgetenv(L"EASYAI_SEGAI_ORT_PROFILE");
        if (profilePrefix && *profilePrefix) so.EnableProfiling(profilePrefix);
        so.AddConfigEntry("session.disable_cpu_ep_fallback", "1");   // CUDA EP not usable -> CreateSession throws instead of silently running on CPU
        Ort::CUDAProviderOptions cuda;
        cuda.Update({{"device_id", std::to_string(deviceId)}, {"cudnn_conv_algo_search", "EXHAUSTIVE"}, {"use_tf32", useTf32 ? "1" : "0"}});
        so.AppendExecutionProvider_CUDA_V2(*cuda);   // throws if the CUDA provider/DLLs cannot be loaded
        session_ = std::make_unique<Ort::Session>(*env_, onnx_.data(), onnx_.size(), so);
        if (profilePrefix && *profilePrefix) {
            const auto sessionReady = std::chrono::steady_clock::now();
            std::cerr << "SEGAI_CUDA_SETUP convert_ms=" << std::chrono::duration<double, std::milli>(converted - convertBegin).count()
                      << " session_ms=" << std::chrono::duration<double, std::milli>(sessionReady - converted).count() << std::endl;
        }
        Ort::AllocatorWithDefaultOptions a;
        inName_ = session_->GetInputNameAllocated(0, a).get();
        outName_ = session_->GetOutputNameAllocated(0, a).get();
        inShape_ = session_->GetInputTypeInfo(0).GetTensorTypeAndShapeInfo().GetShape();
        outShape_ = session_->GetOutputTypeInfo(0).GetTensorTypeAndShapeInfo().GetShape();
        onnx_.clear(); onnx_.shrink_to_fit();
    }
    ~OrtCudaBackend() { session_.reset(); env_.reset(); }
    const std::vector<int64_t>& InputShape() const { return inShape_; }
    const std::vector<int64_t>& OutputShape() const { return outShape_; }

    // x: planar RGB float32 NCHW normalized exactly as the OpenVINO path; returns [1,classes,H/8,W/8] floats.
    std::vector<float> Run(const float* x) {
        Ort::MemoryInfo mi = Ort::MemoryInfo::CreateCpu(OrtArenaAllocator, OrtMemTypeDefault);
        size_t n = 1; for (auto d : inShape_) n *= size_t(d);
        Ort::Value in = Ort::Value::CreateTensor<float>(mi, const_cast<float*>(x), n, inShape_.data(), inShape_.size());
        const char* inN[] = {inName_.c_str()}; const char* outN[] = {outName_.c_str()};
        auto out = session_->Run(Ort::RunOptions{nullptr}, inN, &in, 1, outN, 1);
        size_t m = 1; for (auto d : outShape_) m *= size_t(d);
        const float* p = out[0].GetTensorData<float>();
        return std::vector<float>(p, p + m);
    }

private:
    std::unique_ptr<Ort::Env> env_;
    std::unique_ptr<Ort::Session> session_;
    std::string onnx_, inName_, outName_;
    std::vector<int64_t> inShape_, outShape_;
};

// True when a real CUDA session can be created on device 0 (loads ORT + CUDA runtime, builds a 1-node graph). Result of a
// success is cached; failures are re-tested (cheap, they fail while loading). `reason` is filled on failure.
inline bool ProbeCuda(std::string& reason) {
    static std::mutex lock;
    static bool ok = false;
    std::lock_guard<std::mutex> guard(lock);
    if (ok) return true;
    try {
        auto input = std::make_shared<ov::opset13::Parameter>(ov::element::f32, ov::Shape{1, 3, 8, 8});
        auto relu = std::make_shared<ov::opset13::Relu>(input);
        auto result = std::make_shared<ov::opset13::Result>(relu);
        auto model = std::make_shared<ov::Model>(ov::ResultVector{result}, ov::ParameterVector{input}, "CudaProbe");
        OrtCudaBackend probe(model, 0, false);
        std::vector<float> in(3 * 8 * 8, -1.f);
        const std::vector<float> out = probe.Run(in.data());
        if (out.size() != in.size()) throw std::runtime_error("CUDA probe returned an unexpected output size");
        ok = true; reason.clear();
        return true;
    } catch (const std::exception& e) { reason = e.what(); }
    catch (...) { reason = "unknown CUDA error"; }
    return false;
}

}  // namespace segai_cuda
