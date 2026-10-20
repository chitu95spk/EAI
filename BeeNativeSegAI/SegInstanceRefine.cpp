#include "SegInstanceRefine.h"

#include <opencv2/core.hpp>
#include <opencv2/imgproc.hpp>
#include <opencv2/video/tracking.hpp>

#include <algorithm>
#include <cmath>
#include <sstream>
#include <vector>

namespace BeeSegAI {
namespace {

// Acceptance limits. A refinement that fails any of them is discarded (the head result is kept with refined = 0).
constexpr int kRefineRounds = 4;                 // ECC rounds, each resampling the patch at the refined pose (early stop when converged)
constexpr double kPatchMargin = 1.3;             // patch side / max(w, h): keeps the object edges inside the patch for ECC
constexpr double kRefineMinNcc = 0.5;            // symmetry NCC of the chosen phase
constexpr double kRefineMinEcc = 0.6;            // ECC correlation of the best round (its cc belongs to the corrected pose it returns)
constexpr double kRefineMaxShiftFraction = 0.25; // |centre correction| <= 0.25 * side
constexpr double kRefineMaxAngle = 0.26;         // |angle correction| <= 15 degrees
constexpr double kFlatNorm = 0.5;                // patch norm (gray levels) below which a patch is flat
constexpr int kCaliperRays = 72;
constexpr double kCaliperMinContrast = 8.0;      // gray levels per pixel of the radial derivative
constexpr double kCaliperMaxRms = 0.5;           // px, residual of the circle fit over the kept edges

// Bilinear sample of a CV_32F plane; false when (x, y) lies outside [0, cols-1] x [0, rows-1].
bool SampleBilinear(const cv::Mat& img, double x, double y, float& value)
{
    if (img.cols < 2 || img.rows < 2) return false;
    if (!(x >= 0.0 && y >= 0.0 && x <= img.cols - 1 && y <= img.rows - 1)) return false;
    int x0 = static_cast<int>(std::floor(x));
    int y0 = static_cast<int>(std::floor(y));
    if (x0 > img.cols - 2) x0 = img.cols - 2;
    if (y0 > img.rows - 2) y0 = img.rows - 2;
    const double fx = x - x0;
    const double fy = y - y0;
    const float* r0 = img.ptr<float>(y0);
    const float* r1 = img.ptr<float>(y0 + 1);
    value = static_cast<float>((1.0 - fx) * (1.0 - fy) * r0[x0] + fx * (1.0 - fy) * r0[x0 + 1] +
                               (1.0 - fx) * fy * r1[x0] + fx * fy * r1[x0 + 1]);
    return true;
}

// Patch pixel (u, v) -> image point. Patch centre ((S-1)/2, (S-1)/2) maps to (cx, cy); one patch pixel is side / S image pixels.
struct PatchFrame {
    double cx = 0, cy = 0, s = 1, c = 0, ca = 1, sa = 0;
    double X(double u, double v) const { return cx + ca * ((u - c) * s) - sa * ((v - c) * s); }
    double Y(double u, double v) const { return cy + sa * ((u - c) * s) + ca * ((v - c) * s); }
};

PatchFrame MakePatchFrame(double cx, double cy, double side, double theta, int size)
{
    PatchFrame f;
    f.cx = cx;
    f.cy = cy;
    f.s = side / size;
    f.c = 0.5 * (size - 1);
    f.ca = std::cos(theta);
    f.sa = std::sin(theta);
    return f;
}

// Least-squares circle through the points (algebraic fit, centred coordinates). False when degenerate.
bool FitCircle(const std::vector<cv::Point2d>& pts, double& cx, double& cy, double& r)
{
    if (pts.size() < 3) return false;
    const double n = static_cast<double>(pts.size());
    double mx = 0, my = 0;
    for (const cv::Point2d& p : pts) { mx += p.x; my += p.y; }
    mx /= n;
    my /= n;
    double suu = 0, suv = 0, svv = 0, su = 0, sv = 0, suz = 0, svz = 0, sz = 0;
    for (const cv::Point2d& p : pts) {
        const double u = p.x - mx;
        const double v = p.y - my;
        const double z = u * u + v * v;
        suu += u * u;
        suv += u * v;
        svv += v * v;
        su += u;
        sv += v;
        suz += u * z;
        svz += v * z;
        sz += z;
    }
    // z = A u + B v + C with A = 2a, B = 2b, C = r^2 - a^2 - b^2 (centre (mx + a, my + b)).
    cv::Mat M = (cv::Mat_<double>(3, 3) << suu, suv, su, suv, svv, sv, su, sv, n);
    cv::Mat rhs = (cv::Mat_<double>(3, 1) << suz, svz, sz);
    cv::Mat sol;
    if (!cv::solve(M, rhs, sol, cv::DECOMP_LU)) return false;
    const double a = 0.5 * sol.at<double>(0);
    const double b = 0.5 * sol.at<double>(1);
    const double r2 = sol.at<double>(2) + a * a + b * b;
    if (!(r2 > 0.0) || !std::isfinite(r2)) return false;
    cx = mx + a;
    cy = my + b;
    r = std::sqrt(r2);
    return std::isfinite(cx) && std::isfinite(cy);
}

// Box refine: symmetry NCC for the phase, then two ECC rounds (translation and rotation of the patch).
bool RefineBox(const cv::Mat& gray, const InstanceTemplateSet& templates, int symmetry, DecodedInstance& d, std::vector<std::string>* trace)
{
    if (templates.Empty() || d.classIndex < 0 || d.classIndex >= templates.data.rows) return false;
    const int size = templates.size;
    if (templates.data.type() != CV_32FC1 || templates.data.cols != size * size) return false;
    cv::Mat tmpl = templates.data.row(d.classIndex).clone().reshape(1, size);
    if (cv::norm(tmpl, cv::NORM_L2) < 0.5) return false; // class without template
    const double side = std::max(d.w, d.h);
    if (!(side >= 8.0) || !std::isfinite(side)) return false;
    const double patchSide = InstancePatchSide(d.w, d.h); // square the patch samples (kPatchMargin x the box)
    const int k = (symmetry == 2 || symmetry == 4) ? symmetry : 1;
    const double period = 2.0 * kInstancePi / k;

    // 1) Symmetry phase: the rotation theta + m * period whose patch correlates best with the template.
    double bestNcc = -2.0;
    double bestTheta = d.theta;
    cv::Mat patch;
    for (int m = 0; m < k; ++m) {
        const double theta = d.theta + m * period;
        if (!SampleInstancePatch(gray, d.cx, d.cy, patchSide, theta, size, patch) || !NormalizePatch(patch)) continue;
        const double ncc = patch.dot(tmpl);
        if (ncc > bestNcc) {
            bestNcc = ncc;
            bestTheta = theta;
        }
    }
    if (trace) {
        std::ostringstream os;
        os << "phase: k=" << k << " ncc=" << bestNcc << " theta_deg=" << bestTheta * 180.0 / kInstancePi;
        trace->push_back(os.str());
    }
    if (!(bestNcc >= kRefineMinNcc)) return false;

    // 2) ECC, up to kRefineRounds rounds. warp maps template coordinates to patch coordinates (the patch is resampled at the
    // refined pose each round); the loop stops once a round moves the pose by less than 0.01 patch pixel and 1e-4 rad.
    // Each round evaluates ECC at the current pose, applies that round's full correction (gain 1), and records the POST-correction
    // pose together with that round's cc: the cc returned by findTransformECC is the correlation of the warped patch, so it belongs
    // to the corrected pose. The best round (highest cc) is the result. Early stop when a correction is below 0.01 patch px and 1e-4
    // rad; the stop round's corrected pose is still recorded.
    double cx = d.cx;
    double cy = d.cy;
    double theta = bestTheta;
    double bestCc = -2.0;
    double bestCx = cx;
    double bestCy = cy;
    double bestAngle = theta;
    for (int round = 0; round < kRefineRounds; ++round) {
        if (!SampleInstancePatch(gray, cx, cy, patchSide, theta, size, patch)) break;
        cv::Mat warp = cv::Mat::eye(2, 3, CV_32F);
        double cc = -1.0;
        try {
            cc = cv::findTransformECC(tmpl, patch, warp, cv::MOTION_EUCLIDEAN,
                                      cv::TermCriteria(cv::TermCriteria::COUNT + cv::TermCriteria::EPS, 60, 1e-5), cv::noArray());
        } catch (const cv::Exception&) {
            break;
        }
        if (!std::isfinite(cc)) break;
        const double w00 = warp.at<float>(0, 0), w01 = warp.at<float>(0, 1), w02 = warp.at<float>(0, 2);
        const double w10 = warp.at<float>(1, 0), w11 = warp.at<float>(1, 1), w12 = warp.at<float>(1, 2);
        if (!std::isfinite(w00 + w01 + w02 + w10 + w11 + w12)) break;
        // Template centre -> patch point W(c); its offset from the centre, in image pixels, moves the pose centre.
        const PatchFrame f = MakePatchFrame(cx, cy, patchSide, theta, size);
        const double ox = (w00 + w01) * f.c + w02 - f.c;
        const double oy = (w10 + w11) * f.c + w12 - f.c;
        const double dx = ox * f.s;
        const double dy = oy * f.s;
        const double dphi = std::atan2(w10, w00);
        cx += f.ca * dx - f.sa * dy;
        cy += f.sa * dx + f.ca * dy;
        theta += dphi;
        if (cc > bestCc) {
            bestCc = cc;
            bestCx = cx;
            bestCy = cy;
            bestAngle = theta;
        }
        if (trace) {
            std::ostringstream os;
            os << "round " << round << ": cc=" << cc << " patch_shift=(" << ox << "," << oy << ") dphi_deg=" << dphi * 180.0 / kInstancePi
               << " -> corrected pose=(" << cx << "," << cy << ") theta_deg=" << theta * 180.0 / kInstancePi;
            trace->push_back(os.str());
        }
        if (std::hypot(ox, oy) < 0.01 && std::fabs(dphi) < 1e-4) break;
    }
    if (!(bestCc >= kRefineMinEcc)) return false;
    cx = bestCx;
    cy = bestCy;
    theta = bestAngle;

    // 3) Plausibility: the correction must stay near the head estimate.
    const double shift = std::hypot(cx - d.cx, cy - d.cy);
    if (!std::isfinite(shift) || shift > kRefineMaxShiftFraction * side) return false;
    if (!(std::fabs(WrapAngle(theta - bestTheta)) <= kRefineMaxAngle)) return false;

    // Canonical representative for symmetric classes: [-pi/k, pi/k], as the decoder produces.
    double out = WrapAngle(theta);
    if (k > 1) out -= period * std::round(out / period);
    d.cx = cx;
    d.cy = cy;
    d.theta = out;
    d.refined = 1;
    return true;
}

// Circle refine: radial caliper on rays around the head centre, then a circle fit with one outlier pass.
bool RefineCircle(const cv::Mat& gray, DecodedInstance& d)
{
    const double r0 = 0.25 * (d.w + d.h); // circle: w = h = 2r
    if (!(r0 >= 3.0) || !std::isfinite(r0)) return false;
    const double lo = 0.7 * r0;
    const double hi = 1.3 * r0;
    const double step = 0.25;
    const int count = static_cast<int>(std::ceil((hi - lo) / step)) + 1;
    std::vector<double> rho(static_cast<size_t>(count));
    for (int j = 0; j < count; ++j) rho[static_cast<size_t>(j)] = lo + j * step;

    std::vector<cv::Point2d> edges;
    edges.reserve(kCaliperRays);
    std::vector<double> mag(static_cast<size_t>(count), 0.0);
    for (int ray = 0; ray < kCaliperRays; ++ray) {
        const double a = 2.0 * kInstancePi * ray / kCaliperRays;
        const double ca = std::cos(a);
        const double sa = std::sin(a);
        for (int j = 0; j < count; ++j) {
            const double r = rho[static_cast<size_t>(j)];
            float outer = 0.f, inner = 0.f;
            const bool okOuter = SampleBilinear(gray, d.cx + ca * (r + 0.5), d.cy + sa * (r + 0.5), outer);
            const bool okInner = SampleBilinear(gray, d.cx + ca * (r - 0.5), d.cy + sa * (r - 0.5), inner);
            mag[static_cast<size_t>(j)] = (okOuter && okInner) ? static_cast<double>(outer) - static_cast<double>(inner) : 0.0;
        }
        int best = -1;
        double bestMag = 0.0;
        for (int j = 1; j + 1 < count; ++j) {
            const double m = std::fabs(mag[static_cast<size_t>(j)]);
            if (m > bestMag) { bestMag = m; best = j; }
        }
        if (best < 0 || bestMag < kCaliperMinContrast) continue;
        const double m0 = std::fabs(mag[static_cast<size_t>(best - 1)]);
        const double m1 = bestMag;
        const double m2 = std::fabs(mag[static_cast<size_t>(best + 1)]);
        double offset = 0.0;
        const double denom = m0 - 2.0 * m1 + m2;
        if (denom < 0.0) offset = std::max(-0.5, std::min(0.5, 0.5 * (m0 - m2) / denom));
        const double radius = rho[static_cast<size_t>(best)] + offset * step;
        edges.push_back(cv::Point2d(d.cx + ca * radius, d.cy + sa * radius));
    }
    if (static_cast<double>(edges.size()) < 0.6 * kCaliperRays) return false;

    double cx = 0.0, cy = 0.0, r = 0.0;
    if (!FitCircle(edges, cx, cy, r)) return false;
    std::vector<cv::Point2d> kept;
    kept.reserve(edges.size());
    double sq0 = 0.0;
    for (const cv::Point2d& p : edges) {
        const double e = std::hypot(p.x - cx, p.y - cy) - r;
        sq0 += e * e;
    }
    const double rms0 = std::sqrt(sq0 / static_cast<double>(edges.size()));
    for (const cv::Point2d& p : edges) {
        const double e = std::fabs(std::hypot(p.x - cx, p.y - cy) - r);
        if (e <= std::max(1.0, 3.0 * rms0)) kept.push_back(p);
    }
    if (static_cast<double>(kept.size()) < 0.6 * kCaliperRays) return false;
    if (kept.size() < edges.size() && !FitCircle(kept, cx, cy, r)) return false;
    double sq = 0.0;
    for (const cv::Point2d& p : kept) {
        const double e = std::hypot(p.x - cx, p.y - cy) - r;
        sq += e * e;
    }
    const double rms = std::sqrt(sq / static_cast<double>(kept.size()));
    if (!(rms <= kCaliperMaxRms)) return false;
    if (std::fabs(cx - d.cx) > 0.3 * r0 || std::fabs(cy - d.cy) > 0.3 * r0) return false;
    if (!(r >= lo && r <= hi)) return false;
    d.cx = cx;
    d.cy = cy;
    d.radius = r;
    d.w = 2.0 * r;
    d.h = 2.0 * r;
    d.theta = 0.0;
    d.refined = 1;
    return true;
}

} // namespace

bool InstanceTemplateSet::Valid(int classes, std::string* reason) const
{
    auto fail = [reason](const std::string& text) {
        if (reason) *reason = text;
        return false;
    };
    if (size < 8 || size > 128) return fail("template size is outside 8..128");
    if (data.type() != CV_32FC1 || data.rows != classes || data.cols != size * size)
        return fail("template matrix shape differs from the class count and the template size");
    if (!cv::checkRange(data, true, nullptr, -1e30, 1e30)) return fail("template values are not finite");
    return true;
}

bool SampleInstancePatch(const cv::Mat& gray, double cx, double cy, double side, double theta, int size, cv::Mat& patch)
{
    if (gray.empty() || gray.type() != CV_32FC1 || size < 4 || size > 256) return false;
    if (!std::isfinite(cx) || !std::isfinite(cy) || !std::isfinite(side) || !std::isfinite(theta) || !(side > 0.0)) return false;
    const PatchFrame f = MakePatchFrame(cx, cy, side, theta, size);
    const double last = size - 1;
    const double xs[4] = { f.X(0, 0), f.X(last, 0), f.X(0, last), f.X(last, last) };
    const double ys[4] = { f.Y(0, 0), f.Y(last, 0), f.Y(0, last), f.Y(last, last) };
    const double xmin = *std::min_element(xs, xs + 4);
    const double xmax = *std::max_element(xs, xs + 4);
    const double ymin = *std::min_element(ys, ys + 4);
    const double ymax = *std::max_element(ys, ys + 4);
    if (xmin < 0.0 || ymin < 0.0 || xmax > gray.cols - 1 || ymax > gray.rows - 1) return false;

    cv::Mat blurred;
    const cv::Mat* source = &gray;
    double ox = 0.0;
    double oy = 0.0;
    if (f.s > 1.0) {
        // Anti-alias to the patch pixel size on a crop around the square (the crop keeps the blur local and cheap).
        const double sigma = 0.5 * f.s;
        const int margin = static_cast<int>(std::ceil(3.0 * sigma)) + 2;
        const int x0 = std::max(0, static_cast<int>(std::floor(xmin)) - margin);
        const int y0 = std::max(0, static_cast<int>(std::floor(ymin)) - margin);
        const int x1 = std::min(gray.cols, static_cast<int>(std::ceil(xmax)) + margin + 1);
        const int y1 = std::min(gray.rows, static_cast<int>(std::ceil(ymax)) + margin + 1);
        cv::GaussianBlur(gray(cv::Rect(x0, y0, x1 - x0, y1 - y0)), blurred, cv::Size(0, 0), sigma, sigma, cv::BORDER_REPLICATE);
        source = &blurred;
        ox = x0;
        oy = y0;
    }
    patch.create(size, size, CV_32FC1);
    for (int i = 0; i < size; ++i) {
        float* row = patch.ptr<float>(i);
        for (int j = 0; j < size; ++j) {
            float v = 0.f;
            if (!SampleBilinear(*source, f.X(j, i) - ox, f.Y(j, i) - oy, v)) return false;
            row[j] = v;
        }
    }
    return true;
}

double InstancePatchSide(double width, double height)
{
    return kPatchMargin * std::max(width, height);
}

bool NormalizePatch(cv::Mat& patch)
{
    if (patch.empty() || patch.type() != CV_32FC1) return false;
    const cv::Scalar mean = cv::mean(patch);
    patch = patch - cv::Scalar::all(mean[0]);
    const double norm = cv::norm(patch, cv::NORM_L2);
    if (!(norm > kFlatNorm) || !std::isfinite(norm)) return false;
    patch = patch * (1.0 / norm);
    return true;
}

bool AccumulateInstanceTemplate(const cv::Mat& gray, const InstanceLabel& label, int size, int symmetry, cv::Mat& sum, std::vector<int>& counts)
{
    if (sum.empty() || sum.type() != CV_32FC1 || sum.cols != size * size) return false;
    if (label.classIndex < 0 || label.classIndex >= sum.rows || counts.size() != static_cast<size_t>(sum.rows)) return false;
    if (label.kind != InstanceKind::RotatedBox) return false; // circles have no oriented template
    // Canonical phase for the class symmetry: theta modulo 2*pi/k into [-pi/k, pi/k] (k = 1 keeps the full direction).
    const int k = (symmetry == 2 || symmetry == 4) ? symmetry : 1;
    const double period = 2.0 * kInstancePi / k;
    double theta = WrapAngle(label.theta);
    theta -= period * std::round(theta / period);
    cv::Mat patch;
    if (!SampleInstancePatch(gray, label.cx, label.cy, InstancePatchSide(label.w, label.h), theta, size, patch)) return false;
    if (!NormalizePatch(patch)) return false;
    cv::Mat row = sum.row(label.classIndex);
    row += patch.reshape(1, 1);
    counts[static_cast<size_t>(label.classIndex)] += 1;
    return true;
}

void FinalizeInstanceTemplates(const cv::Mat& sum, const std::vector<int>& counts, InstanceTemplateSet& set)
{
    set = InstanceTemplateSet();
    if (sum.empty() || sum.type() != CV_32FC1 || counts.size() != static_cast<size_t>(sum.rows)) return;
    const int size = static_cast<int>(std::sqrt(static_cast<double>(sum.cols)) + 0.5);
    if (size * size != sum.cols) return;
    set.size = size;
    set.data = cv::Mat::zeros(sum.rows, sum.cols, CV_32FC1);
    for (int c = 0; c < sum.rows; ++c) {
        const int n = counts[static_cast<size_t>(c)];
        if (n <= 0) continue;
        cv::Mat avg = sum.row(c) * (1.0 / n);
        const double norm = cv::norm(avg, cv::NORM_L2);
        if (!(norm > 1e-6) || !std::isfinite(norm)) continue;
        cv::Mat unit = avg * (1.0 / norm);
        unit.copyTo(set.data.row(c));
    }
}

bool RefineInstanceTraced(const cv::Mat& gray, const InstanceTemplateSet& templates, int symmetry, DecodedInstance& d,
                          std::vector<std::string>& trace)
{
    d.refined = 0;
    if (gray.empty() || gray.type() != CV_32FC1) return false;
    if (!std::isfinite(d.cx) || !std::isfinite(d.cy) || !std::isfinite(d.w) || !std::isfinite(d.h) || !std::isfinite(d.theta)) return false;
    if (d.kind == InstanceKind::Circle) return RefineCircle(gray, d);
    return RefineBox(gray, templates, symmetry, d, &trace);
}

bool RefineInstance(const cv::Mat& gray, const InstanceTemplateSet& templates, int symmetry, DecodedInstance& d)
{
    d.refined = 0;
    if (gray.empty() || gray.type() != CV_32FC1) return false;
    if (!std::isfinite(d.cx) || !std::isfinite(d.cy) || !std::isfinite(d.w) || !std::isfinite(d.h) || !std::isfinite(d.theta)) return false;
    if (d.kind == InstanceKind::Circle) return RefineCircle(gray, d);
    return RefineBox(gray, templates, symmetry, d, nullptr);
}

} // namespace BeeSegAI
