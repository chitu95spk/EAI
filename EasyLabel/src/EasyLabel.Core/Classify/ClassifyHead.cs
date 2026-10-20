using System.Threading;

namespace EasyLabel.Core.Classify;

/// <summary>
/// The OK/NG head: per-feature standardisation followed by a class-balanced, L2-regularised logistic regression (the bias is not
/// penalised). Training is deterministic (no random start, fixed iteration caps): Newton steps with conjugate-gradient inner solves
/// and a backtracking line search. <see cref="PredictNgProbability"/> returns P(NG) in 0..1; the label NG is the class with label 1.
/// </summary>
public sealed class ClassifyHead
{
    /// <summary>L2 strength on the standardised weights (starting value).</summary>
    public const double DefaultLambda = 1.0;

    /// <summary>Newton iterations cap (fixed, so the fit is reproducible and bounded).</summary>
    public const int MaxNewtonIterations = 30;

    /// <summary>Conjugate-gradient iterations cap per Newton step.</summary>
    public const int MaxCgIterations = 300;

    private const double MinScale = 1e-6;

    private readonly float[] _mean;
    private readonly float[] _scale;
    private readonly float[] _weights;

    /// <summary>A head from stored parts (for example a loaded <c>.eaicls</c>). Arrays are copied.</summary>
    /// <param name="mean">Per-feature mean used for standardisation.</param>
    /// <param name="scale">Per-feature standard deviation (each value must be positive).</param>
    /// <param name="weights">Weights on the standardised features.</param>
    /// <param name="bias">Intercept.</param>
    public ClassifyHead(float[] mean, float[] scale, float[] weights, float bias)
    {
        ArgumentNullException.ThrowIfNull(mean);
        ArgumentNullException.ThrowIfNull(scale);
        ArgumentNullException.ThrowIfNull(weights);
        if (mean.Length == 0 || mean.Length != scale.Length || mean.Length != weights.Length)
            throw new ArgumentException("Mean, scale and weights need the same non-zero length.");
        if (!float.IsFinite(bias)) throw new ArgumentException("Bias must be finite.", nameof(bias));
        for (int j = 0; j < mean.Length; j++)
        {
            if (!float.IsFinite(mean[j]) || !float.IsFinite(weights[j])) throw new ArgumentException("Head values must be finite.");
            if (!float.IsFinite(scale[j]) || scale[j] <= 0f) throw new ArgumentException("Scale values must be positive and finite.");
        }

        _mean = (float[])mean.Clone();
        _scale = (float[])scale.Clone();
        _weights = (float[])weights.Clone();
        Bias = bias;
    }

    /// <summary>Number of input features.</summary>
    public int FeatureCount => _weights.Length;

    /// <summary>Per-feature mean (read only view of the stored values).</summary>
    public IReadOnlyList<float> Mean => _mean;

    /// <summary>Per-feature scale (read only view of the stored values).</summary>
    public IReadOnlyList<float> Scale => _scale;

    /// <summary>Weights on the standardised features (read only view of the stored values).</summary>
    public IReadOnlyList<float> Weights => _weights;

    /// <summary>Intercept.</summary>
    public float Bias { get; }

    /// <summary>
    /// Fits the head. <paramref name="isNg"/> gives the label of each feature row (true = NG). Both classes must be present.
    /// Class weights are n / (2 * n_class), so the two classes weigh the same whatever their counts.
    /// </summary>
    /// <exception cref="ArgumentException">Empty or ragged input, a single class, or a non-positive lambda.</exception>
    /// <exception cref="OperationCanceledException">Cancelled between Newton steps.</exception>
    public static ClassifyHead Train(IReadOnlyList<float[]> features, IReadOnlyList<bool> isNg, double lambda = DefaultLambda, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(isNg);
        int n = features.Count;
        if (n == 0 || isNg.Count != n) throw new ArgumentException("Features and labels must be non-empty and the same length.");
        if (!(lambda > 0.0) || !double.IsFinite(lambda)) throw new ArgumentOutOfRangeException(nameof(lambda), "Lambda must be positive.");

        int d = features[0]?.Length ?? 0;
        if (d == 0) throw new ArgumentException("Feature rows must be non-empty.", nameof(features));
        int ngCount = 0;
        for (int i = 0; i < n; i++)
        {
            float[] row = features[i] ?? throw new ArgumentException("Feature rows must not be null.", nameof(features));
            if (row.Length != d) throw new ArgumentException("Feature rows must have the same length.", nameof(features));
            for (int j = 0; j < d; j++)
                if (!float.IsFinite(row[j])) throw new ArgumentException("Feature values must be finite.", nameof(features));
            if (isNg[i]) ngCount++;
        }

        int okCount = n - ngCount;
        if (okCount == 0 || ngCount == 0) throw new ArgumentException("Both OK and NG rows are needed.", nameof(isNg));

        // Standardise with the float values that are stored, so training and prediction see the same numbers.
        var mean = new float[d];
        var scale = new float[d];
        var sum = new double[d];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < d; j++) sum[j] += features[i][j];
        for (int j = 0; j < d; j++) mean[j] = (float)(sum[j] / n);

        var sq = new double[d];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < d; j++)
            {
                double diff = features[i][j] - (double)mean[j];
                sq[j] += diff * diff;
            }

        for (int j = 0; j < d; j++)
        {
            double sd = Math.Sqrt(sq[j] / n);
            scale[j] = sd < MinScale ? 1f : (float)sd;
        }

        var z = new double[n * d];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < d; j++) z[i * d + j] = (features[i][j] - (double)mean[j]) / scale[j];

        var label = new double[n];
        var rowWeight = new double[n];
        double wOk = n / (2.0 * okCount);
        double wNg = n / (2.0 * ngCount);
        for (int i = 0; i < n; i++)
        {
            label[i] = isNg[i] ? 1.0 : 0.0;
            rowWeight[i] = isNg[i] ? wNg : wOk;
        }

        var theta = new double[d + 1];
        for (int iteration = 0; iteration < MaxNewtonIterations; iteration++)
        {
            ct.ThrowIfCancellationRequested();
            double[] margin = Margins(z, n, d, theta);
            var prob = new double[n];
            var gradient = new double[d + 1];
            var curvature = new double[n];
            for (int i = 0; i < n; i++)
            {
                prob[i] = Sigmoid(margin[i]);
                double residual = rowWeight[i] * (prob[i] - label[i]);
                curvature[i] = Math.Max(rowWeight[i] * prob[i] * (1.0 - prob[i]), 1e-12);
                for (int j = 0; j < d; j++) gradient[j] += residual * z[i * d + j];
                gradient[d] += residual;
            }

            for (int j = 0; j < d; j++) gradient[j] += lambda * theta[j];
            double gradientNorm = Math.Sqrt(gradient.Sum(g => g * g));
            if (gradientNorm < 1e-6 * n) break;

            var rhs = new double[d + 1];
            for (int k = 0; k <= d; k++) rhs[k] = -gradient[k];
            double[] step = ConjugateGradient(z, n, d, curvature, lambda, rhs);

            double f0 = Objective(margin, label, rowWeight, theta, d, lambda);
            double slope = 0.0;
            for (int k = 0; k <= d; k++) slope += gradient[k] * step[k];
            double factor = 1.0;
            double[]? accepted = null;
            double acceptedValue = f0;
            while (factor >= 1e-8)
            {
                var trial = new double[d + 1];
                for (int k = 0; k <= d; k++) trial[k] = theta[k] + factor * step[k];
                double ft = Objective(Margins(z, n, d, trial), label, rowWeight, trial, d, lambda);
                if (ft <= f0 + 1e-4 * factor * slope)
                {
                    accepted = trial;
                    acceptedValue = ft;
                    break;
                }

                factor *= 0.5;
            }

            if (accepted == null || acceptedValue > f0) break;
            theta = accepted;
        }

        var weights = new float[d];
        for (int j = 0; j < d; j++) weights[j] = (float)theta[j];
        return new ClassifyHead(mean, scale, weights, (float)theta[d]);
    }

    /// <summary>P(NG) of one feature row (0..1). Throws when the length differs from <see cref="FeatureCount"/>.</summary>
    public double PredictNgProbability(float[] feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (feature.Length != FeatureCount) throw new ArgumentException("Feature length does not match the head.", nameof(feature));
        double value = Bias;
        for (int j = 0; j < _weights.Length; j++)
        {
            if (!float.IsFinite(feature[j])) throw new ArgumentException("Feature values must be finite.", nameof(feature));
            value += _weights[j] * ((feature[j] - (double)_mean[j]) / _scale[j]);
        }

        return Sigmoid(value);
    }

    private static double[] Margins(double[] z, int n, int d, double[] theta)
    {
        var margin = new double[n];
        for (int i = 0; i < n; i++)
        {
            double value = theta[d];
            int row = i * d;
            for (int j = 0; j < d; j++) value += z[row + j] * theta[j];
            margin[i] = value;
        }

        return margin;
    }

    private static double Objective(double[] margin, double[] label, double[] rowWeight, double[] theta, int d, double lambda)
    {
        double total = 0.0;
        for (int i = 0; i < margin.Length; i++) total += rowWeight[i] * (SoftPlus(margin[i]) - label[i] * margin[i]);
        double penalty = 0.0;
        for (int j = 0; j < d; j++) penalty += theta[j] * theta[j];
        return total + 0.5 * lambda * penalty;
    }

    // Solves H x = rhs with H = [Z' diag(c) Z + lambda I, Z' c; c' Z, sum(c)], by conjugate gradients from x = 0.
    private static double[] ConjugateGradient(double[] z, int n, int d, double[] curvature, double lambda, double[] rhs)
    {
        var x = new double[d + 1];
        var r = (double[])rhs.Clone();
        var p = (double[])rhs.Clone();
        double rhsNorm = Math.Sqrt(r.Sum(v => v * v));
        if (rhsNorm == 0.0) return x;
        double rs = rhsNorm * rhsNorm;
        for (int k = 0; k < MaxCgIterations; k++)
        {
            double[] hp = HessianVector(z, n, d, curvature, lambda, p);
            double pHp = 0.0;
            for (int j = 0; j <= d; j++) pHp += p[j] * hp[j];
            if (pHp <= 0.0) break;
            double alpha = rs / pHp;
            for (int j = 0; j <= d; j++)
            {
                x[j] += alpha * p[j];
                r[j] -= alpha * hp[j];
            }

            double rsNew = r.Sum(v => v * v);
            if (Math.Sqrt(rsNew) <= 1e-6 * rhsNorm) break;
            double beta = rsNew / rs;
            for (int j = 0; j <= d; j++) p[j] = r[j] + beta * p[j];
            rs = rsNew;
        }

        return x;
    }

    private static double[] HessianVector(double[] z, int n, int d, double[] curvature, double lambda, double[] v)
    {
        var u = new double[n];
        for (int i = 0; i < n; i++)
        {
            double value = v[d];
            int row = i * d;
            for (int j = 0; j < d; j++) value += z[row + j] * v[j];
            u[i] = curvature[i] * value;
        }

        var result = new double[d + 1];
        for (int i = 0; i < n; i++)
        {
            int row = i * d;
            for (int j = 0; j < d; j++) result[j] += z[row + j] * u[i];
            result[d] += u[i];
        }

        for (int j = 0; j < d; j++) result[j] += lambda * v[j];
        return result;
    }

    private static double Sigmoid(double x)
    {
        if (x >= 0) return 1.0 / (1.0 + Math.Exp(-x));
        double e = Math.Exp(x);
        return e / (1.0 + e);
    }

    private static double SoftPlus(double x)
    {
        return x > 0 ? x + Math.Log(1.0 + Math.Exp(-x)) : Math.Log(1.0 + Math.Exp(x));
    }
}
