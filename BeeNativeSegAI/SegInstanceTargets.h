#pragma once

// EAI-POS P2: instance-target geometry for the Position (oriented box / circle) head.
// Plain C++ on purpose (no OpenCV, OpenVINO or pch) so the maths can be unit-tested without model files.
//
// Conventions
//   * Image coordinates: x right, y down, continuous (a pixel i spans [i, i+1)), the same as the label points.
//   * theta = atan2(TR - TL) in radians, range (-pi, pi]; w is the side along TL->TR, h the side along TL->BL.
//   * Circle: w = h = 2r, theta = 0 (ignored).
//   * Network grid: Grid x Grid cells of Stride input pixels (80 x 8 for the 640 input). Cell (gx, gy) covers
//     [gx*Stride, (gx+1)*Stride) in the network input frame.
//
// Rendered target planes (all row-major, index = plane * Grid * Grid + gy * Grid + gx):
//   heat    [C]  CenterNet Gaussian per class, 1.0 exactly at the centre cell, sigma = (2r+1)/6 cells.
//   reg     [7]  channel 0 offset x = cx/Stride - gx      (centre cell in [0,1), 3x3 neighbours in [-1,2))
//                channel 1 offset y = cy/Stride - gy
//                channel 2 log(w / Stride)
//                channel 3 log(h / Stride)
//                channel 4 sin(k * theta)   (circle: 0)
//                channel 5 cos(k * theta)   (circle: 1)
//                channel 6 is-circle flag (1 circle, 0 rotated box); angle channels carry no loss for circles
//   regMask [1]  1 where reg is valid (object centre cell and its 3x3 neighbours inside the grid), else 0.
//                There is no separate heat-ignore plane: the heat plane is dense and the mask is the only ignore layout.
// Where two objects claim the same regression cell, the one with the larger Gaussian weight at that cell wins
// (ties: lower label index), so the result is deterministic.
// The centre cell is shared by all classes: when two labels (of any classes) have their centre in the same grid cell, the second
// one is dropped and counted in droppedLabels (two objects whose centres are less than one cell apart cannot be separated).

#include <cstdint>
#include <vector>

namespace BeeSegAI {

constexpr int kInstanceGrid = 80;
constexpr int kInstanceStride = 8;
constexpr int kInstanceRegChannels = 7;
constexpr double kInstancePi = 3.14159265358979323846;

enum class InstanceKind : int { RotatedBox = 0, Circle = 1 };

struct InstanceLabel {
    InstanceKind kind = InstanceKind::RotatedBox;
    double cx = 0, cy = 0;
    double w = 0, h = 0;       // circle: w = h = 2r
    double theta = 0;          // radians, (-pi, pi]
    int classIndex = 0;        // 0-based class of the instance head (0..C-1)
};

// Angle ambiguity of a class: k = 1 (360 deg, full direction), 2 (180 deg), 4 (90 deg).
using SymmetryK = int;

// Row-major 2x3 affine: x' = m[0]*x + m[1]*y + m[2], y' = m[3]*x + m[4]*y + m[5]. Maps source -> destination
// exactly like cv::warpAffine without WARP_INVERSE_MAP.
struct Affine2x3 {
    double m[6] = { 1, 0, 0, 0, 1, 0 };
};

Affine2x3 InvertAffine(const Affine2x3& a, bool* ok = nullptr);
Affine2x3 ComposeAffine(const Affine2x3& first, const Affine2x3& second); // apply first, then second
// Same matrix as cv::getRotationMatrix2D(center, angleDegrees, scale) (positive angle = counter-clockwise on screen).
Affine2x3 MakeRotationAffine(double cx, double cy, double angleDegrees, double scale);
Affine2x3 MakeFlipXAffine(double imageWidth);                             // x' = W - x
// SegV2Core Prepare(): scale = side / max(w,h), resized to round(w*scale) x round(h*scale), centred with integer
// left/top offsets, so the true map is x' = x*(frameW/W) + left. Slightly anisotropic (< 1 pixel over the image).
Affine2x3 MakeLetterboxAffine(int imageWidth, int imageHeight, int side);

// Wraps to (-pi, pi].
double WrapAngle(double radians);
// Smallest absolute angle difference modulo a 2*pi/k symmetry, in [0, pi/k].
double AngleDifferenceModuloSymmetry(double a, double b, SymmetryK k);

// Maps labels through a similarity (rotation, uniform scale, translation, optional mirror). The centre goes through the
// affine, w and h scale by the transformed length of their own axis, theta becomes atan2 of the transformed w axis
// (so mirrors reflect the angle), circles stay circles with w = h = 2*meanScale*r. Anisotropic maps are not supported:
// when the two axis scales differ by more than relativeTolerance the function returns false and leaves labels untouched.
bool ApplyAffineToLabels(const Affine2x3& affine, std::vector<InstanceLabel>& labels, double relativeTolerance = 0.01);

struct InstanceTargets {
    int grid = kInstanceGrid;
    int stride = kInstanceStride;
    int numClasses = 0;
    std::vector<float> heat;     // numClasses * grid * grid
    std::vector<float> reg;      // kInstanceRegChannels * grid * grid
    std::vector<float> regMask;  // grid * grid
    int droppedLabels = 0;       // centre outside the grid, invalid size, bad class, or its centre cell already owned
};

// CenterNet gaussian_radius (min_overlap 0.7) for a square object of side minSideCells; integer radius >= 0.
int InstanceGaussianRadius(double minSideCells);

// symmetryPerClass: size numClasses (or empty = all k = 1); values other than 1, 2, 4 are treated as 1.
void RenderInstanceTargets(const std::vector<InstanceLabel>& labels, int numClasses, const std::vector<SymmetryK>& symmetryPerClass,
                           InstanceTargets& out, int grid = kInstanceGrid, int stride = kInstanceStride);

struct DecodedInstance {
    InstanceKind kind = InstanceKind::RotatedBox;
    double cx = 0, cy = 0;     // network input pixels
    double w = 0, h = 0;       // circle: w = h = 2r
    double radius = 0;         // circle: (w + h) / 4; box: 0
    double theta = 0;          // radians; for k > 1 the canonical representative in [-pi/k, pi/k]
    float score = 0;
    int classIndex = 0;
    int refined = 0;           // EAI-POS P4: 1 when the quality refinement replaced this instance; 0 = head result (also the fallback)
};

struct DecodeOptions {
    float scoreThreshold = 0.3f;
    float nmsIou = 0.5f;
    int maxInstances = 100;
    int grid = kInstanceGrid;
    int stride = kInstanceStride;
};

// heat holds scores in [0,1] (apply the sigmoid first); reg uses the raw channel layout above. Peaks are cells >= all
// eight neighbours (plateaus: only the first cell in raster order survives), then rotated NMS per class. Output order:
// score descending, then classIndex, cy, cx ascending. The angle k for class c is symmetryPerClass[c] (empty = 1).
void DecodeInstances(const float* heat, const float* reg, int numClasses, const std::vector<SymmetryK>& symmetryPerClass,
                     const DecodeOptions& options, std::vector<DecodedInstance>& out);

// Intersection-over-union of two instances (rotated rectangles via polygon clipping, circle pairs analytic). A circle compared with
// a rotated box is treated as its axis-aligned bounding square (w = h = 2r), which overestimates the overlap of the pair; that is
// only relevant for the NMS of a class that mixes both kinds and errs on the side of suppressing a duplicate.
double InstanceIou(const DecodedInstance& a, const DecodedInstance& b);

} // namespace BeeSegAI
