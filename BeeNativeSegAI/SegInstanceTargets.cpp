#include "SegInstanceTargets.h"

#include <algorithm>
#include <cmath>

namespace BeeSegAI {
namespace {

struct Pt { double x, y; };

bool KnownSymmetry(int k) { return k == 2 || k == 4; }

int SymmetryOf(const std::vector<SymmetryK>& perClass, int classIndex)
{
    if (classIndex < 0 || static_cast<size_t>(classIndex) >= perClass.size()) return 1;
    return KnownSymmetry(perClass[static_cast<size_t>(classIndex)]) ? perClass[static_cast<size_t>(classIndex)] : 1;
}

void Corners(const DecodedInstance& d, Pt out[4])
{
    const double angle = d.kind == InstanceKind::Circle ? 0.0 : d.theta;
    const double ux = std::cos(angle), uy = std::sin(angle);
    const double vx = -uy, vy = ux;
    const double hw = d.w * 0.5, hh = d.h * 0.5;
    const double sx[4] = { 1, -1, -1, 1 }, sy[4] = { 1, 1, -1, -1 };
    for (int i = 0; i < 4; ++i) {
        out[i].x = d.cx + ux * hw * sx[i] + vx * hh * sy[i];
        out[i].y = d.cy + uy * hw * sx[i] + vy * hh * sy[i];
    }
}

double SignedArea(const std::vector<Pt>& poly)
{
    double sum = 0;
    for (size_t i = 0; i < poly.size(); ++i) {
        const Pt& a = poly[i];
        const Pt& b = poly[(i + 1) % poly.size()];
        sum += a.x * b.y - b.x * a.y;
    }
    return sum * 0.5;
}

// Sutherland-Hodgman: subject clipped by the convex counter-clockwise (positive SignedArea) clip polygon.
std::vector<Pt> ClipConvex(std::vector<Pt> subject, const std::vector<Pt>& clip)
{
    for (size_t e = 0; e < clip.size() && !subject.empty(); ++e) {
        const Pt a = clip[e], b = clip[(e + 1) % clip.size()];
        auto side = [&](const Pt& p) { return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x); };
        std::vector<Pt> next;
        for (size_t i = 0; i < subject.size(); ++i) {
            const Pt p = subject[i], q = subject[(i + 1) % subject.size()];
            const double sp = side(p), sq = side(q);
            if (sp >= 0) next.push_back(p);
            if ((sp >= 0) != (sq >= 0)) {
                const double t = sp / (sp - sq);
                next.push_back(Pt{ p.x + (q.x - p.x) * t, p.y + (q.y - p.y) * t });
            }
        }
        subject.swap(next);
    }
    return subject;
}

double LensArea(double r1, double r2, double d)
{
    if (d >= r1 + r2) return 0;
    if (d <= std::fabs(r1 - r2)) { const double r = std::min(r1, r2); return kInstancePi * r * r; }
    const double a1 = std::acos(std::max(-1.0, std::min(1.0, (d * d + r1 * r1 - r2 * r2) / (2 * d * r1))));
    const double a2 = std::acos(std::max(-1.0, std::min(1.0, (d * d + r2 * r2 - r1 * r1) / (2 * d * r2))));
    return r1 * r1 * (a1 - std::sin(2 * a1) * 0.5) + r2 * r2 * (a2 - std::sin(2 * a2) * 0.5);
}

} // namespace

// ---- affine --------------------------------------------------------------------------------------------------

Affine2x3 InvertAffine(const Affine2x3& a, bool* ok)
{
    Affine2x3 r;
    const double det = a.m[0] * a.m[4] - a.m[1] * a.m[3];
    if (ok) *ok = std::fabs(det) > 1e-18;
    if (std::fabs(det) <= 1e-18) return r;
    const double i00 = a.m[4] / det, i01 = -a.m[1] / det, i10 = -a.m[3] / det, i11 = a.m[0] / det;
    r.m[0] = i00; r.m[1] = i01; r.m[3] = i10; r.m[4] = i11;
    r.m[2] = -(i00 * a.m[2] + i01 * a.m[5]);
    r.m[5] = -(i10 * a.m[2] + i11 * a.m[5]);
    return r;
}

Affine2x3 ComposeAffine(const Affine2x3& first, const Affine2x3& second)
{
    Affine2x3 r;
    r.m[0] = second.m[0] * first.m[0] + second.m[1] * first.m[3];
    r.m[1] = second.m[0] * first.m[1] + second.m[1] * first.m[4];
    r.m[2] = second.m[0] * first.m[2] + second.m[1] * first.m[5] + second.m[2];
    r.m[3] = second.m[3] * first.m[0] + second.m[4] * first.m[3];
    r.m[4] = second.m[3] * first.m[1] + second.m[4] * first.m[4];
    r.m[5] = second.m[3] * first.m[2] + second.m[4] * first.m[5] + second.m[5];
    return r;
}

Affine2x3 MakeRotationAffine(double cx, double cy, double angleDegrees, double scale)
{
    const double rad = angleDegrees * kInstancePi / 180.0;
    const double alpha = scale * std::cos(rad), beta = scale * std::sin(rad);
    Affine2x3 r;
    r.m[0] = alpha; r.m[1] = beta;  r.m[2] = (1 - alpha) * cx - beta * cy;
    r.m[3] = -beta; r.m[4] = alpha; r.m[5] = beta * cx + (1 - alpha) * cy;
    return r;
}

Affine2x3 MakeFlipXAffine(double imageWidth)
{
    Affine2x3 r;
    r.m[0] = -1; r.m[2] = imageWidth;
    return r;
}

Affine2x3 MakeLetterboxAffine(int imageWidth, int imageHeight, int side)
{
    const double scale = static_cast<double>(side) / std::max(imageWidth, imageHeight);
    const int fw = std::max(1, static_cast<int>(std::lround(imageWidth * scale)));
    const int fh = std::max(1, static_cast<int>(std::lround(imageHeight * scale)));
    Affine2x3 r;
    r.m[0] = static_cast<double>(fw) / imageWidth;
    r.m[4] = static_cast<double>(fh) / imageHeight;
    r.m[2] = (side - fw) / 2;   // integer division, same as Prepare()
    r.m[5] = (side - fh) / 2;
    return r;
}

double WrapAngle(double radians)
{
    double a = std::fmod(radians, 2 * kInstancePi);
    if (a <= -kInstancePi) a += 2 * kInstancePi;
    else if (a > kInstancePi) a -= 2 * kInstancePi;
    return a;
}

double AngleDifferenceModuloSymmetry(double a, double b, SymmetryK k)
{
    const double period = 2 * kInstancePi / (KnownSymmetry(k) ? k : 1);
    double d = std::fmod(a - b, period);
    if (d < 0) d += period;
    return std::min(d, period - d);
}

bool ApplyAffineToLabels(const Affine2x3& affine, std::vector<InstanceLabel>& labels, double relativeTolerance)
{
    const double* m = affine.m;
    // Pass 1: every label must see (nearly) equal scale along its own two axes.
    struct Mapped { double su, sv, theta; };
    std::vector<Mapped> mapped(labels.size());
    for (size_t i = 0; i < labels.size(); ++i) {
        const double ux = std::cos(labels[i].theta), uy = std::sin(labels[i].theta);
        const double ax = m[0] * ux + m[1] * uy, ay = m[3] * ux + m[4] * uy;
        const double bx = m[0] * -uy + m[1] * ux, by = m[3] * -uy + m[4] * ux;
        const double su = std::hypot(ax, ay), sv = std::hypot(bx, by);
        if (!(su > 0) || !(sv > 0) || std::fabs(su - sv) > relativeTolerance * std::max(su, sv)) return false;
        mapped[i] = Mapped{ su, sv, std::atan2(ay, ax) };
    }
    for (size_t i = 0; i < labels.size(); ++i) {
        InstanceLabel& l = labels[i];
        const double x = l.cx, y = l.cy;
        l.cx = m[0] * x + m[1] * y + m[2];
        l.cy = m[3] * x + m[4] * y + m[5];
        if (l.kind == InstanceKind::Circle) {
            const double s = 0.5 * (mapped[i].su + mapped[i].sv);
            l.w *= s; l.h *= s; l.theta = 0;
        } else {
            l.w *= mapped[i].su; l.h *= mapped[i].sv;
            l.theta = WrapAngle(mapped[i].theta);
        }
    }
    return true;
}

// ---- rendering -----------------------------------------------------------------------------------------------

int InstanceGaussianRadius(double minSideCells)
{
    const double h = minSideCells, w = minSideCells, mo = 0.7;
    if (!(h > 0)) return 0;
    const double b1 = h + w, c1 = w * h * (1 - mo) / (1 + mo);
    const double r1 = (b1 + std::sqrt(std::max(0.0, b1 * b1 - 4 * c1))) / 2;
    const double b2 = 2 * (h + w), c2 = (1 - mo) * w * h;
    const double r2 = (b2 + std::sqrt(std::max(0.0, b2 * b2 - 16 * c2))) / 2;
    const double a3 = 4 * mo, b3 = -2 * mo * (h + w), c3 = (mo - 1) * w * h;
    const double r3 = (b3 + std::sqrt(std::max(0.0, b3 * b3 - 4 * a3 * c3))) / 2;
    return std::max(0, static_cast<int>(std::min(r1, std::min(r2, r3))));
}

void RenderInstanceTargets(const std::vector<InstanceLabel>& labels, int numClasses, const std::vector<SymmetryK>& symmetryPerClass,
                           InstanceTargets& out, int grid, int stride)
{
    const size_t g2 = static_cast<size_t>(grid) * grid;
    out.grid = grid; out.stride = stride; out.numClasses = numClasses; out.droppedLabels = 0;
    out.heat.assign(static_cast<size_t>(std::max(0, numClasses)) * g2, 0.f);
    out.reg.assign(static_cast<size_t>(kInstanceRegChannels) * g2, 0.f);
    out.regMask.assign(g2, 0.f);
    std::vector<float> best(g2, -1.f);
    std::vector<unsigned char> centreOwned(g2, 0);

    for (size_t li = 0; li < labels.size(); ++li) {
        const InstanceLabel& l = labels[li];
        const bool finite = std::isfinite(l.cx) && std::isfinite(l.cy) && std::isfinite(l.w) && std::isfinite(l.h) && std::isfinite(l.theta);
        const double w = l.kind == InstanceKind::Circle ? 0.5 * (l.w + l.h) : l.w;
        const double h = l.kind == InstanceKind::Circle ? w : l.h;
        if (!finite || l.classIndex < 0 || l.classIndex >= numClasses || !(w > 0) || !(h > 0)) { ++out.droppedLabels; continue; }
        const int gx = static_cast<int>(std::floor(l.cx / stride)), gy = static_cast<int>(std::floor(l.cy / stride));
        if (gx < 0 || gy < 0 || gx >= grid || gy >= grid || centreOwned[static_cast<size_t>(gy) * grid + gx]) { ++out.droppedLabels; continue; }
        centreOwned[static_cast<size_t>(gy) * grid + gx] = 1;

        const int radius = InstanceGaussianRadius(std::min(w, h) / stride);
        const double sigma = (2 * radius + 1) / 6.0;
        auto weight = [&](int dx, int dy) { return std::exp(-(dx * dx + dy * dy) / (2 * sigma * sigma)); };

        float* heat = out.heat.data() + static_cast<size_t>(l.classIndex) * g2;
        for (int dy = -radius; dy <= radius; ++dy) {
            for (int dx = -radius; dx <= radius; ++dx) {
                const int x = gx + dx, y = gy + dy;
                if (x < 0 || y < 0 || x >= grid || y >= grid) continue;
                float& cell = heat[static_cast<size_t>(y) * grid + x];
                cell = std::max(cell, static_cast<float>(weight(dx, dy)));
            }
        }
        heat[static_cast<size_t>(gy) * grid + gx] = 1.f;

        const int k = SymmetryOf(symmetryPerClass, l.classIndex);
        const bool circle = l.kind == InstanceKind::Circle;
        const float s = circle ? 0.f : static_cast<float>(std::sin(k * l.theta));
        const float c = circle ? 1.f : static_cast<float>(std::cos(k * l.theta));
        const float logW = static_cast<float>(std::log(w / stride)), logH = static_cast<float>(std::log(h / stride));
        for (int dy = -1; dy <= 1; ++dy) {
            for (int dx = -1; dx <= 1; ++dx) {
                const int x = gx + dx, y = gy + dy;
                if (x < 0 || y < 0 || x >= grid || y >= grid) continue;
                const size_t cell = static_cast<size_t>(y) * grid + x;
                const float priority = (dx == 0 && dy == 0) ? 2.f : static_cast<float>(weight(dx, dy));
                if (priority <= best[cell]) continue;
                best[cell] = priority;
                out.regMask[cell] = 1.f;
                out.reg[0 * g2 + cell] = static_cast<float>(l.cx / stride - x);
                out.reg[1 * g2 + cell] = static_cast<float>(l.cy / stride - y);
                out.reg[2 * g2 + cell] = logW;
                out.reg[3 * g2 + cell] = logH;
                out.reg[4 * g2 + cell] = s;
                out.reg[5 * g2 + cell] = c;
                out.reg[6 * g2 + cell] = circle ? 1.f : 0.f;
            }
        }
    }
}

// ---- decoding ------------------------------------------------------------------------------------------------

double InstanceIou(const DecodedInstance& a, const DecodedInstance& b)
{
    if (a.kind == InstanceKind::Circle && b.kind == InstanceKind::Circle) {
        const double r1 = (a.w + a.h) * 0.25, r2 = (b.w + b.h) * 0.25;
        const double inter = LensArea(r1, r2, std::hypot(a.cx - b.cx, a.cy - b.cy));
        const double uni = kInstancePi * (r1 * r1 + r2 * r2) - inter;
        return uni > 0 ? inter / uni : 0.0;
    }
    Pt ca[4], cb[4];
    Corners(a, ca); Corners(b, cb);
    std::vector<Pt> pa(ca, ca + 4), pb(cb, cb + 4);
    if (SignedArea(pa) < 0) std::reverse(pa.begin(), pa.end());
    if (SignedArea(pb) < 0) std::reverse(pb.begin(), pb.end());
    const double areaA = std::fabs(SignedArea(pa)), areaB = std::fabs(SignedArea(pb));
    const std::vector<Pt> clipped = ClipConvex(pa, pb);
    const double inter = clipped.size() >= 3 ? std::fabs(SignedArea(clipped)) : 0.0;
    const double uni = areaA + areaB - inter;
    return uni > 0 ? inter / uni : 0.0;
}

void DecodeInstances(const float* heat, const float* reg, int numClasses, const std::vector<SymmetryK>& symmetryPerClass,
                     const DecodeOptions& options, std::vector<DecodedInstance>& out)
{
    out.clear();
    if (!heat || !reg || numClasses <= 0 || options.grid <= 0) return;
    const int grid = options.grid;
    const size_t g2 = static_cast<size_t>(grid) * grid;
    std::vector<DecodedInstance> candidates;

    for (int c = 0; c < numClasses; ++c) {
        const float* plane = heat + static_cast<size_t>(c) * g2;
        const int k = SymmetryOf(symmetryPerClass, c);
        for (int gy = 0; gy < grid; ++gy) {
            for (int gx = 0; gx < grid; ++gx) {
                const size_t cell = static_cast<size_t>(gy) * grid + gx;
                const float v = plane[cell];
                if (!(v >= options.scoreThreshold)) continue;
                bool peak = true;
                for (int dy = -1; dy <= 1 && peak; ++dy) {
                    for (int dx = -1; dx <= 1; ++dx) {
                        if (!dx && !dy) continue;
                        const int x = gx + dx, y = gy + dy;
                        if (x < 0 || y < 0 || x >= grid || y >= grid) continue;
                        const size_t n = static_cast<size_t>(y) * grid + x;
                        if (plane[n] > v || (plane[n] == v && n < cell)) { peak = false; break; }
                    }
                }
                if (!peak) continue;
                // A network that produced NaN / infinity in a regression channel of this cell must not yield an instance.
                bool finiteReg = true;
                for (int ch = 0; ch < kInstanceRegChannels; ++ch) if (!std::isfinite(reg[static_cast<size_t>(ch) * g2 + cell])) finiteReg = false;
                if (!finiteReg) continue;
                auto clampLog = [](float x) { return static_cast<double>(std::max(-10.f, std::min(10.f, x))); };
                DecodedInstance d;
                d.classIndex = c; d.score = v;
                d.cx = (gx + static_cast<double>(reg[0 * g2 + cell])) * options.stride;
                d.cy = (gy + static_cast<double>(reg[1 * g2 + cell])) * options.stride;
                d.w = std::exp(clampLog(reg[2 * g2 + cell])) * options.stride;
                d.h = std::exp(clampLog(reg[3 * g2 + cell])) * options.stride;
                if (reg[6 * g2 + cell] > 0.5f) {
                    d.kind = InstanceKind::Circle;
                    d.radius = (d.w + d.h) * 0.25;
                    d.theta = 0;
                } else {
                    d.theta = std::atan2(static_cast<double>(reg[4 * g2 + cell]), static_cast<double>(reg[5 * g2 + cell])) / k;
                }
                candidates.push_back(d);
            }
        }
    }

    std::sort(candidates.begin(), candidates.end(), [](const DecodedInstance& a, const DecodedInstance& b) {
        if (a.score != b.score) return a.score > b.score;
        if (a.classIndex != b.classIndex) return a.classIndex < b.classIndex;
        if (a.cy != b.cy) return a.cy < b.cy;
        return a.cx < b.cx;
    });

    for (const DecodedInstance& cand : candidates) {
        if (options.maxInstances > 0 && static_cast<int>(out.size()) >= options.maxInstances) break;
        bool suppressed = false;
        for (const DecodedInstance& kept : out) {
            if (kept.classIndex == cand.classIndex && InstanceIou(kept, cand) > options.nmsIou) { suppressed = true; break; }
        }
        if (!suppressed) out.push_back(cand);
    }
}

} // namespace BeeSegAI
