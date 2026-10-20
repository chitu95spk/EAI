#pragma once

// EAI-POS P4 quality refinement of the instance head (SEGAI_InferPredictInstances quality = 1). Plain C++ + OpenCV (no OpenVINO),
// like SegInstanceHead, so the maths can be unit-tested without model files.
//
// Pieces
//   * Class templates, captured at train time (SegV2Model::AddInstances): one S x S patch per class, sampled in the object's own
//     box frame (a square of side L = max(w, h) rotated by theta, blurred to the patch pixel size when L > S), normalised (zero mean,
//     unit norm) and averaged over the labelled RotatedBox instances of the class. Circles do not contribute. The templates are the
//     optional .segai keys instance_template_size / instance_templates; a file without them has no templates.
//   * Box refine: the symmetry NCC picks the rotation phase theta + m * 360/k that best matches the template, then cv::findTransformECC
//     (MOTION_EUCLIDEAN) gives the sub-pixel translation and rotation of the patch. Two rounds; the second resamples at the first
//     refined pose. Centre and angle are refined; w and h are kept from the head.
//   * Circle refine: radial caliper on 72 rays around the head centre (maximum |gradient| with a parabolic sub-pixel peak in the
//     head radius band 0.7..1.3 r), algebraic circle fit with one outlier pass. No template is used.
//
// Failure (no template for the class, patch leaves the image, flat patch, NCC or ECC correlation too low, correction too large,
// too few edges, fit residual too large) leaves the instance untouched and returns false; the caller reports refined = 0, i.e. the
// head result. Coordinates follow the head convention; templates and inference use the same sampling, so a fixed 0.5 px label
// offset cancels in the correction.

#include "SegInstanceTargets.h"

#include <opencv2/core.hpp>

#include <string>
#include <vector>

namespace BeeSegAI {

constexpr int kInstanceTemplateSize = 32; // S: patch pixels per side of a class template

// Class templates: rows = classes, cols = S * S, CV_32F, zero mean and unit norm per row (a zero row = class without template).
struct InstanceTemplateSet {
    int size = 0;   // S; 0 = no templates
    cv::Mat data;
    bool Empty() const { return size <= 0 || data.empty(); }
    // Shape, type and finite values for `classes` rows; the reason goes to *reason when given.
    bool Valid(int classes, std::string* reason = nullptr) const;
};

// Gray CV_32F image (0..255) of the square of side `side` centred on (cx, cy) and rotated by theta, sampled into size x size
// (bilinear; Gaussian blur first when side > size). Returns false when the square leaves the image or a value is not finite.
bool SampleInstancePatch(const cv::Mat& gray, double cx, double cy, double side, double theta, int size, cv::Mat& patch);

// Side of the sampled square for a box of width x height (kPatchMargin x max side), shared by the templates and the refinement.
double InstancePatchSide(double width, double height);

// Zero mean and unit norm in place. Returns false for a flat patch (norm below 0.5 gray levels over the whole patch).
bool NormalizePatch(cv::Mat& patch);

// Adds the normalised patch of one RotatedBox label (coordinates in `gray`) to sum.row(label.classIndex) and counts it.
// symmetry = angle symmetry k of the class (1, 2 or 4): the label angle is first reduced to the canonical phase [-pi/k, pi/k], so
// the symmetric phases of one object do not ghost in the average. Circles and invalid samples are skipped (returns false, nothing changes).
bool AccumulateInstanceTemplate(const cv::Mat& gray, const InstanceLabel& label, int size, int symmetry, cv::Mat& sum, std::vector<int>& counts);

// Averages the accumulated rows into set (size = S; a row without samples stays zero).
void FinalizeInstanceTemplates(const cv::Mat& sum, const std::vector<int>& counts, InstanceTemplateSet& set);

// Refines one decoded instance in the coordinates of `gray` (CV_32F). symmetry = angle symmetry k of its class (1, 2 or 4). On success
// the centre and angle (box) or the centre, radius and w = h = 2r (circle) are replaced and d.refined = 1; on failure d is unchanged
// and d.refined = 0. Circles do not need templates; boxes need the template row of d.classIndex.
bool RefineInstance(const cv::Mat& gray, const InstanceTemplateSet& templates, int symmetry, DecodedInstance& d);

// Same as RefineInstance, and appends one line per ECC round (pose, correlation, correction) to trace for diagnostics.
bool RefineInstanceTraced(const cv::Mat& gray, const InstanceTemplateSet& templates, int symmetry, DecodedInstance& d,
                          std::vector<std::string>& trace);

} // namespace BeeSegAI
