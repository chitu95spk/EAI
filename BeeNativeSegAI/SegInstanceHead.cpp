#include "SegInstanceHead.h"

#include <opencv2/core.hpp>
#include <opencv2/core/utility.hpp>

#include <algorithm>
#include <cmath>
#include <numeric>
#include <stdexcept>

namespace BeeSegAI {
namespace {

float Sigmoid(float z) { return 1.f / (1.f + std::exp(-z)); }

bool AllFinite(const cv::Mat& m)
{
    if (m.empty()) return true;
    return cv::checkRange(m, true, nullptr, -1e30, 1e30); // quiet = true: report through the return value, never throw
}


// P3b: the batch products run on row chunks across the cores (fixed chunking, so the result is deterministic for a given thread count
// only through the fixed partial-sum order below, never through scheduling).
constexpr int kGemmChunkRows = 512;

// dst (n x cols) = a (n x k) * b (k x cols), chunked over rows of a.
void ParallelProduct(const cv::Mat& a, const cv::Mat& b, cv::Mat& dst)
{
    dst.create(a.rows, b.cols, CV_32F);
    const int chunks = (a.rows + kGemmChunkRows - 1) / kGemmChunkRows;
    cv::parallel_for_(cv::Range(0, chunks), [&](const cv::Range& range) {
        for (int chunk = range.start; chunk < range.end; ++chunk) {
            const int begin = chunk * kGemmChunkRows, end = std::min(a.rows, begin + kGemmChunkRows);
            cv::Mat part = dst.rowRange(begin, end);
            cv::gemm(a.rowRange(begin, end), b, 1., cv::noArray(), 0., part);
        }
    });
}

// dst (a.cols x b.cols) = a^T * b, partial sums per chunk of rows added in chunk order.
void ParallelTransposeProduct(const cv::Mat& a, const cv::Mat& b, cv::Mat& dst)
{
    const int chunks = (a.rows + kGemmChunkRows - 1) / kGemmChunkRows;
    std::vector<cv::Mat> partial(static_cast<size_t>(chunks));
    cv::parallel_for_(cv::Range(0, chunks), [&](const cv::Range& range) {
        for (int chunk = range.start; chunk < range.end; ++chunk) {
            const int begin = chunk * kGemmChunkRows, end = std::min(a.rows, begin + kGemmChunkRows);
            cv::gemm(a.rowRange(begin, end), b.rowRange(begin, end), 1., cv::noArray(), 0., partial[static_cast<size_t>(chunk)], cv::GEMM_1_T);
        }
    });
    dst = partial[0].clone();
    for (int chunk = 1; chunk < chunks; ++chunk) dst += partial[static_cast<size_t>(chunk)];
}

} // namespace

bool InstanceHeadWeights::Valid(std::string* reason) const
{
    auto fail = [&](const char* text) { if (reason) *reason = text; return false; };
    if (classes < 1 || classes > 32) return fail("class count outside 1..32");
    const int outputs = Outputs();
    if (w1.type() != CV_32F || w1.rows != kInstanceFeatureChannels || w1.cols != kInstanceHidden || !w1.isContinuous()) return fail("w1 must be 448 x 128 float");
    if (b1.type() != CV_32F || b1.rows != 1 || b1.cols != kInstanceHidden || !b1.isContinuous()) return fail("b1 must be 1 x 128 float");
    if (w2.type() != CV_32F || w2.rows != kInstanceHidden || w2.cols != outputs || !w2.isContinuous()) return fail("w2 must be 128 x (classes + 7) float");
    if (b2.type() != CV_32F || b2.rows != 1 || b2.cols != outputs || !b2.isContinuous()) return fail("b2 must be 1 x (classes + 7) float");
    if (!AllFinite(w1) || !AllFinite(b1) || !AllFinite(w2) || !AllFinite(b2)) return fail("non-finite weight");
    if (static_cast<int>(symmetry.size()) != classes) return fail("symmetry size differs from the class count");
    for (int k : symmetry) if (k != 1 && k != 2 && k != 4) return fail("symmetry values must be 1, 2 or 4");
    return true;
}

void GatherInstanceRows(const cv::Mat& featureRows, const InstanceTargets& targets, std::mt19937& random, int maxRows,
                        InstanceTrainRows& out)
{
    out = InstanceTrainRows();
    const int cells = targets.grid * targets.grid;
    const int classes = targets.numClasses;
    if (classes <= 0 || maxRows <= 0 || featureRows.type() != CV_32F || featureRows.rows != cells || featureRows.cols != kInstanceFeatureChannels)
        throw std::runtime_error("instance rows: unexpected feature grid");
    if (targets.heat.size() != static_cast<size_t>(classes) * cells || targets.reg.size() != static_cast<size_t>(kInstanceRegChannels) * cells ||
        targets.regMask.size() != static_cast<size_t>(cells))
        throw std::runtime_error("instance rows: unexpected target size");

    std::vector<int> core, halo, others;
    double positives = 0;
    for (int cell = 0; cell < cells; ++cell) {
        bool peak = false, inHalo = false;
        for (int c = 0; c < classes; ++c) {
            const float h = targets.heat[static_cast<size_t>(c) * cells + cell];
            if (h >= 0.9999f) { peak = true; positives += 1; }
            else if (h >= 0.05f) inHalo = true;
        }
        if (peak || targets.regMask[cell] > 0.f) core.push_back(cell);
        else if (inHalo) halo.push_back(cell);
        else others.push_back(cell);
    }

    std::vector<int> selected;
    std::vector<float> weights;
    // Object rows first; they always carry weight 1 (a huge object count is truncated randomly, which only loses a few regression cells).
    std::shuffle(core.begin(), core.end(), random);
    if (static_cast<int>(core.size()) > maxRows) core.resize(static_cast<size_t>(maxRows));
    for (int cell : core) { selected.push_back(cell); weights.push_back(1.f); }
    int room = maxRows - static_cast<int>(selected.size());

    // Gaussian halo: at most half of the remaining room, reweighted when truncated.
    std::shuffle(halo.begin(), halo.end(), random);
    const int haloTotal = static_cast<int>(halo.size());
    const int haloKeep = std::min(haloTotal, room / 2);
    const float haloWeight = haloKeep > 0 ? static_cast<float>(haloTotal) / static_cast<float>(haloKeep) : 1.f;
    for (int i = 0; i < haloKeep; ++i) { selected.push_back(halo[static_cast<size_t>(i)]); weights.push_back(haloWeight); }
    room -= haloKeep;

    std::shuffle(others.begin(), others.end(), random);
    const int otherTotal = static_cast<int>(others.size());
    const int otherKeep = std::min(otherTotal, std::max(0, room));
    const float otherWeight = otherKeep > 0 ? static_cast<float>(otherTotal) / static_cast<float>(otherKeep) : 1.f;
    for (int i = 0; i < otherKeep; ++i) { selected.push_back(others[static_cast<size_t>(i)]); weights.push_back(otherWeight); }

    const int rows = static_cast<int>(selected.size());
    out.positives = positives;
    if (rows == 0) return;
    out.features.create(rows, kInstanceFeatureChannels, CV_32F);
    out.heat.create(rows, classes, CV_32F);
    out.reg.create(rows, kInstanceRegChannels, CV_32F);
    out.regMask.create(rows, 1, CV_32F);
    out.weight.create(rows, 1, CV_32F);
    for (int r = 0; r < rows; ++r) {
        const int cell = selected[static_cast<size_t>(r)];
        featureRows.row(cell).copyTo(out.features.row(r));
        float* heat = out.heat.ptr<float>(r);
        for (int c = 0; c < classes; ++c) heat[c] = targets.heat[static_cast<size_t>(c) * cells + cell];
        float* reg = out.reg.ptr<float>(r);
        for (int ch = 0; ch < kInstanceRegChannels; ++ch) reg[ch] = targets.reg[static_cast<size_t>(ch) * cells + cell];
        out.regMask.at<float>(r, 0) = targets.regMask[cell];
        out.weight.at<float>(r, 0) = weights[static_cast<size_t>(r)];
    }
}

void ConcatInstanceRows(std::vector<InstanceTrainRows>& parts, InstanceTrainRows& out)
{
    out = InstanceTrainRows();
    int total = 0, classes = 0;
    for (const InstanceTrainRows& part : parts) {
        out.positives += part.positives;
        out.dropped += part.dropped;
        if (part.Rows() == 0) continue;
        if (classes == 0) classes = part.heat.cols;
        else if (classes != part.heat.cols) throw std::runtime_error("instance rows: class count differs between parts");
        total += part.Rows();
    }
    if (total == 0) return;
    out.features.create(total, kInstanceFeatureChannels, CV_32F);
    out.heat.create(total, classes, CV_32F);
    out.reg.create(total, kInstanceRegChannels, CV_32F);
    out.regMask.create(total, 1, CV_32F);
    out.weight.create(total, 1, CV_32F);
    int offset = 0;
    for (InstanceTrainRows& part : parts) {
        const int count = part.Rows();
        if (count > 0) {
            part.features.copyTo(out.features.rowRange(offset, offset + count));
            part.heat.copyTo(out.heat.rowRange(offset, offset + count));
            part.reg.copyTo(out.reg.rowRange(offset, offset + count));
            part.regMask.copyTo(out.regMask.rowRange(offset, offset + count));
            part.weight.copyTo(out.weight.rowRange(offset, offset + count));
            offset += count;
        }
        part = InstanceTrainRows();
    }
}

bool TrainInstanceHead(InstanceTrainRows& data, int classes, const std::vector<int>& symmetry, const InstanceTrainOptions& options,
                       const std::function<bool()>& cancelled, const std::function<void(int)>& progress,
                       InstanceHeadWeights& out, InstanceTrainReport& report, std::string& error)
{
    try {
        const int N = data.features.rows, F = kInstanceFeatureChannels, H = kInstanceHidden, C = classes, R = kInstanceRegChannels, O = C + R;
        if (C < 1 || C > 32 || static_cast<int>(symmetry.size()) != C) { error = "instance head: invalid class count or symmetry list"; return false; }
        if (N <= 0 || data.features.type() != CV_32F || data.features.cols != F || data.heat.rows != N || data.heat.cols != C ||
            data.reg.rows != N || data.reg.cols != R || data.regMask.rows != N || data.weight.rows != N) {
            error = "instance head: no usable training rows"; return false;
        }
        if (!(data.positives > 0)) { error = "instance head: no positive (centre) cell in the training data"; return false; }
        if (options.epochs < 1 || options.epochs > 2000 || !std::isfinite(options.learningRate) || options.learningRate <= 0.f || options.learningRate > 1.f) {
            error = "instance head: invalid epochs or learning rate"; return false;
        }

        // Standardisation statistics in double (single pass), applied in place.
        std::vector<double> sum(static_cast<size_t>(F), 0.), squares(static_cast<size_t>(F), 0.);
        for (int r = 0; r < N; ++r) {
            const float* p = data.features.ptr<float>(r);
            for (int f = 0; f < F; ++f) { sum[static_cast<size_t>(f)] += p[f]; squares[static_cast<size_t>(f)] += static_cast<double>(p[f]) * p[f]; }
        }
        std::vector<float> mean(static_cast<size_t>(F)), sd(static_cast<size_t>(F));
        for (int f = 0; f < F; ++f) {
            const double m = sum[static_cast<size_t>(f)] / N;
            const double variance = std::max(0., squares[static_cast<size_t>(f)] / N - m * m);
            mean[static_cast<size_t>(f)] = static_cast<float>(m);
            sd[static_cast<size_t>(f)] = std::max(static_cast<float>(std::sqrt(variance)), .1f);
        }
        for (int r = 0; r < N; ++r) {
            float* p = data.features.ptr<float>(r);
            for (int f = 0; f < F; ++f) p[f] = (p[f] - mean[static_cast<size_t>(f)]) / sd[static_cast<size_t>(f)];
        }

        cv::RNG rng(options.seed);
        cv::Mat w1(F, H, CV_32F), b1 = cv::Mat::zeros(1, H, CV_32F), w2(H, O, CV_32F), b2 = cv::Mat::zeros(1, O, CV_32F);
        rng.fill(w1, cv::RNG::UNIFORM, -1.f / std::sqrt(float(F)), 1.f / std::sqrt(float(F)));
        rng.fill(w2, cv::RNG::UNIFORM, -.5f / std::sqrt(float(H)), .5f / std::sqrt(float(H)));
        for (int c = 0; c < C; ++c) b2.at<float>(0, c) = -2.19f;   // CenterNet prior: sigmoid = 0.1 at start
        b2.at<float>(0, C + 2) = 1.f; b2.at<float>(0, C + 3) = 1.f; // log(w / 8): about 2.7 cells
        b2.at<float>(0, C + 5) = 0.f;

        cv::Mat m1 = cv::Mat::zeros(w1.size(), CV_32F), v1 = m1.clone(), mb1 = cv::Mat::zeros(b1.size(), CV_32F), vb1 = mb1.clone();
        cv::Mat m2 = cv::Mat::zeros(w2.size(), CV_32F), v2 = m2.clone(), mb2 = cv::Mat::zeros(b2.size(), CV_32F), vb2 = mb2.clone();
        auto adam = [](cv::Mat& weights, cv::Mat& first, cv::Mat& second, const cv::Mat& gradient, float rate, int t) {
            const float c1 = 1.f - std::pow(.9f, static_cast<float>(t)), c2 = 1.f - std::pow(.999f, static_cast<float>(t));
            const int total = static_cast<int>(weights.total());
            float* w = weights.ptr<float>(); float* m = first.ptr<float>(); float* v = second.ptr<float>(); const float* g = gradient.ptr<float>();
            for (int i = 0; i < total; ++i) {
                m[i] = .9f * m[i] + .1f * g[i];
                v[i] = .999f * v[i] + .001f * g[i] * g[i];
                w[i] -= rate * (m[i] / c1) / (std::sqrt(v[i] / c2) + 1e-8f);
            }
        };

        std::vector<int> order(static_cast<size_t>(N));
        std::iota(order.begin(), order.end(), 0);
        std::seed_seq orderSeed{ options.seed, 7u };
        std::mt19937 shuffler(orderSeed);
        std::mt19937 dropper(options.seed * 2654435761u + 13u);
        const int batch = std::min(N, std::max(256, options.batchRows));
        const int stepsPerEpoch = (N + batch - 1) / batch;
        const int totalSteps = options.epochs * stepsPerEpoch;
        int step = 0;
        report = InstanceTrainReport();
        report.rows = N; report.positives = data.positives;

        for (int epoch = 1; epoch <= options.epochs; ++epoch) {
            std::shuffle(order.begin(), order.end(), shuffler);
            double lossSum = 0, focalSum = 0, regSum = 0;
            int batches = 0;
            for (int begin = 0; begin < N; begin += batch) {
                if (cancelled && cancelled()) { error = "Training cancelled"; return false; }
                const int n = std::min(batch, N - begin);
                cv::Mat x(n, F, CV_32F);
                for (int i = 0; i < n; ++i) data.features.row(order[static_cast<size_t>(begin + i)]).copyTo(x.row(i));
                if (options.inputDropout > 0.f) {
                    const float keepScale = 1.f / (1.f - options.inputDropout);
                    const uint32_t threshold = static_cast<uint32_t>(options.inputDropout * 4294967295.0);
                    for (int i = 0; i < n; ++i) { float* p = x.ptr<float>(i); for (int f = 0; f < F; ++f) p[f] = dropper() < threshold ? 0.f : p[f] * keepScale; }
                }

                cv::Mat hidden;
                ParallelProduct(x, w1, hidden);
                for (int i = 0; i < n; ++i) {
                    float* h = hidden.ptr<float>(i); const float* b = b1.ptr<float>(0);
                    for (int j = 0; j < H; ++j) { const float v = h[j] + b[j]; h[j] = v > 0.f ? v : 0.f; }
                }
                cv::Mat hiddenMask = cv::Mat::ones(n, H, CV_32F);
                if (options.hiddenDropout > 0.f) {
                    const float keepScale = 1.f / (1.f - options.hiddenDropout);
                    const uint32_t threshold = static_cast<uint32_t>(options.hiddenDropout * 4294967295.0);
                    for (int i = 0; i < n; ++i) {
                        float* h = hidden.ptr<float>(i); float* m = hiddenMask.ptr<float>(i);
                        for (int j = 0; j < H; ++j) { m[j] = dropper() < threshold ? 0.f : keepScale; h[j] *= m[j]; }
                    }
                }
                cv::Mat outputs;
                ParallelProduct(hidden, w2, outputs);
                for (int i = 0; i < n; ++i) {
                    float* o = outputs.ptr<float>(i); const float* b = b2.ptr<float>(0);
                    for (int j = 0; j < O; ++j) o[j] += b[j];
                }

                // Normalisers of this batch: positive peaks and regression-masked cells.
                double peaks = 0, masked = 0;
                for (int i = 0; i < n; ++i) {
                    const int row = order[static_cast<size_t>(begin + i)];
                    const float* t = data.heat.ptr<float>(row);
                    for (int c = 0; c < C; ++c) if (t[c] >= 0.9999f) peaks += 1;
                    if (data.regMask.at<float>(row, 0) > 0.5f) masked += 1;
                }
                const float normPositive = static_cast<float>(std::max(1., peaks)), normReg = static_cast<float>(std::max(1., masked));

                cv::Mat dOut = cv::Mat::zeros(n, O, CV_32F);
                double focal = 0, regression = 0;
                for (int i = 0; i < n; ++i) {
                    const int row = order[static_cast<size_t>(begin + i)];
                    const float* o = outputs.ptr<float>(i);
                    float* d = dOut.ptr<float>(i);
                    const float* t = data.heat.ptr<float>(row);
                    const float weight = data.weight.at<float>(row, 0);
                    for (int c = 0; c < C; ++c) {
                        const float p = std::min(1.f - 1e-4f, std::max(1e-4f, Sigmoid(o[c])));
                        float dz;
                        if (t[c] >= 0.9999f) {
                            const float omp = 1.f - p, lp = std::log(p);
                            focal += static_cast<double>(-omp * omp * lp) * weight;
                            dz = 2.f * p * omp * omp * lp - omp * omp * omp;
                        } else {
                            const float omt = 1.f - t[c], tt = omt * omt * omt * omt, lq = std::log(1.f - p);
                            focal += static_cast<double>(-tt * p * p * lq) * weight;
                            dz = -tt * (2.f * p * p * (1.f - p) * lq - p * p * p);
                        }
                        d[c] = dz * weight / normPositive;
                    }
                    if (data.regMask.at<float>(row, 0) > 0.5f) {
                        const float* target = data.reg.ptr<float>(row);
                        const bool circle = target[6] > 0.5f;
                        for (int ch = 0; ch < R; ++ch) {
                            if (circle && (ch == 4 || ch == 5)) continue; // the angle of a circle is meaningless
                            const float diff = o[C + ch] - target[ch];
                            regression += std::fabs(diff);
                            d[C + ch] = (diff > 0.f ? 1.f : (diff < 0.f ? -1.f : 0.f)) / normReg;
                        }
                    }
                }
                const double batchFocal = focal / normPositive, batchReg = regression / normReg, batchLoss = batchFocal + batchReg;
                if (!std::isfinite(batchLoss)) { error = "instance head: non-finite loss; reduce the learning rate or check the training input"; return false; }
                lossSum += batchLoss; focalSum += batchFocal; regSum += batchReg; ++batches;

                cv::Mat gradW2, gradHidden, gradW1;
                ParallelTransposeProduct(hidden, dOut, gradW2);
                cv::Mat gradB2 = cv::Mat::zeros(1, O, CV_32F);
                for (int i = 0; i < n; ++i) { const float* d = dOut.ptr<float>(i); float* g = gradB2.ptr<float>(0); for (int j = 0; j < O; ++j) g[j] += d[j]; }
                { const cv::Mat w2t = w2.t(); ParallelProduct(dOut, w2t, gradHidden); }
                cv::Mat gradB1 = cv::Mat::zeros(1, H, CV_32F);
                for (int i = 0; i < n; ++i) {
                    const float* h = hidden.ptr<float>(i); float* g = gradHidden.ptr<float>(i); float* gb = gradB1.ptr<float>(0);
                    const float* m = hiddenMask.ptr<float>(i);
                    for (int j = 0; j < H; ++j) { if (h[j] <= 0.f) g[j] = 0.f; else g[j] *= m[j]; gb[j] += g[j]; }
                }
                ParallelTransposeProduct(x, gradHidden, gradW1);

                ++step;
                const float rate = options.learningRate * (.1f + .9f * static_cast<float>(1. + std::cos(CV_PI * step / totalSteps)) / 2.f);
                if (options.weightDecay > 0.f) { w1 *= 1.f - rate * options.weightDecay; w2 *= 1.f - rate * options.weightDecay; }
                adam(w1, m1, v1, gradW1, rate, step); adam(b1, mb1, vb1, gradB1, rate, step);
                adam(w2, m2, v2, gradW2, rate, step); adam(b2, mb2, vb2, gradB2, rate, step);
            }
            if (epoch == 1) report.firstLoss = lossSum / std::max(1, batches);
            report.lastLoss = lossSum / std::max(1, batches);
            report.focalLast = focalSum / std::max(1, batches);
            report.regLast = regSum / std::max(1, batches);
            report.epochs = epoch; report.steps = step;
            if (progress) progress(epoch);
        }

        // Fold the standardisation into the first layer: w1' = w1 / sd, b1' = b1 - sum_f mean_f * w1'_f.
        out = InstanceHeadWeights();
        out.classes = C;
        out.w1 = cv::Mat(F, H, CV_32F);
        out.b1 = cv::Mat(1, H, CV_32F);
        for (int j = 0; j < H; ++j) {
            double bias = b1.at<float>(0, j);
            for (int f = 0; f < F; ++f) {
                const float folded = w1.at<float>(f, j) / sd[static_cast<size_t>(f)];
                out.w1.at<float>(f, j) = folded;
                bias -= static_cast<double>(mean[static_cast<size_t>(f)]) * folded;
            }
            out.b1.at<float>(0, j) = static_cast<float>(bias);
        }
        out.w2 = w2.clone();
        out.b2 = b2.clone();
        out.symmetry = symmetry;
        std::string reason;
        if (!out.Valid(&reason)) { error = "instance head: trained weights rejected (" + reason + ")"; return false; }
        return true;
    } catch (const std::exception& e) {
        error = std::string("instance head: ") + e.what();
        return false;
    }
}

void EvaluateInstanceHeadRow(const InstanceHeadWeights& weights, const float* features, float* outputs)
{
    const int F = kInstanceFeatureChannels, H = kInstanceHidden, O = weights.Outputs();
    float hidden[kInstanceHidden];
    for (int j = 0; j < H; ++j) hidden[j] = weights.b1.at<float>(0, j);
    for (int f = 0; f < F; ++f) {
        const float v = features[f];
        const float* row = weights.w1.ptr<float>(f);
        for (int j = 0; j < H; ++j) hidden[j] += v * row[j];
    }
    for (int j = 0; j < O; ++j) outputs[j] = weights.b2.at<float>(0, j);
    for (int j = 0; j < H; ++j) {
        const float h = hidden[j] > 0.f ? hidden[j] : 0.f;
        if (h == 0.f) continue;
        const float* row = weights.w2.ptr<float>(j);
        for (int k = 0; k < O; ++k) outputs[k] += h * row[k];
    }
}

} // namespace BeeSegAI
