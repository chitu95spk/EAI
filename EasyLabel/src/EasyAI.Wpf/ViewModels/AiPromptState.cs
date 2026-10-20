using EasyLabel.Ai.Onnx;

namespace EasyAI.Desktop.ViewModels;

/// <summary>
/// UI-free prompt of the AI-Points / AI-Box tools: the clicks collected so far (positive, or negative with Shift) or the last dragged
/// box, in IMAGE pixels. Points and a box are exclusive prompts. No WPF types, so it is unit tested from EasyLabel.SegAI.Tests (linked file).
/// </summary>
public sealed class AiPromptState
{
    /// <summary>A dragged box with a side shorter than this (image pixels) is a click, not a prompt.</summary>
    public const float MinBoxSide = 4f;

    private readonly List<AiPoint> _points = new List<AiPoint>();

    public IReadOnlyList<AiPoint> Points => _points;

    /// <summary>The box prompt, or null.</summary>
    public AiBox? Box { get; private set; }

    public bool HasPoints => _points.Count > 0;

    public bool IsEmpty => _points.Count == 0 && Box == null;

    /// <summary>
    /// Adds a click (negative = background, Shift+click). A click outside the image is ignored (false). A box prompt is dropped:
    /// from now on the prompt is the points.
    /// </summary>
    public bool AddPoint(float x, float y, bool positive, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || float.IsNaN(x) || float.IsNaN(y) || x < 0f || y < 0f || x > imageWidth || y > imageHeight)
        {
            return false;
        }

        Box = null;
        _points.Add(new AiPoint(x, y, positive));
        return true;
    }

    /// <summary>
    /// Sets the dragged box (corners in any order, clamped into the image) as the prompt and drops the points. False, and nothing
    /// changes, when the box is smaller than <see cref="MinBoxSide"/> on a side.
    /// </summary>
    public bool SetBox(float x1, float y1, float x2, float y2, int imageWidth, int imageHeight)
    {
        if (!TryNormalizeBox(x1, y1, x2, y2, imageWidth, imageHeight, out AiBox box))
        {
            return false;
        }

        _points.Clear();
        Box = box;
        return true;
    }

    /// <summary>Orders the corners, clamps them into the image and rejects a box thinner than <see cref="MinBoxSide"/>.</summary>
    public static bool TryNormalizeBox(float x1, float y1, float x2, float y2, int imageWidth, int imageHeight, out AiBox box)
    {
        box = default;
        if (imageWidth <= 0 || imageHeight <= 0 || float.IsNaN(x1) || float.IsNaN(y1) || float.IsNaN(x2) || float.IsNaN(y2))
        {
            return false;
        }

        float left = Math.Min(Math.Max(Math.Min(x1, x2), 0f), imageWidth);
        float right = Math.Min(Math.Max(Math.Max(x1, x2), 0f), imageWidth);
        float top = Math.Min(Math.Max(Math.Min(y1, y2), 0f), imageHeight);
        float bottom = Math.Min(Math.Max(Math.Max(y1, y2), 0f), imageHeight);
        if (right - left < MinBoxSide || bottom - top < MinBoxSide)
        {
            return false;
        }

        box = new AiBox(left, top, right, bottom);
        return true;
    }

    /// <summary>Enter: the clicks so far are final (the next click starts a new object). False when there were no clicks.</summary>
    public bool Commit()
    {
        if (_points.Count == 0)
        {
            return false;
        }

        _points.Clear();
        return true;
    }

    /// <summary>Esc: drops the clicks and the box. False when there was nothing to drop.</summary>
    public bool Clear()
    {
        bool had = !IsEmpty;
        _points.Clear();
        Box = null;
        return had;
    }

    /// <summary>The prompt to send to a backend (all clicks so far, or the box), or null when empty.</summary>
    public AiPrompt? ToPrompt()
    {
        if (_points.Count > 0)
        {
            return new AiPrompt(_points.ToArray());
        }

        return Box.HasValue ? new AiPrompt(null, Box) : null;
    }
}

/// <summary>A prompt ready to run: all clicks so far (<see cref="IsBox"/> false) or a dragged box.</summary>
public sealed class AiPromptEventArgs : EventArgs
{
    public AiPromptEventArgs(AiPrompt prompt, bool isBox)
    {
        Prompt = prompt;
        IsBox = isBox;
    }

    public AiPrompt Prompt { get; }

    public bool IsBox { get; }
}

/// <summary>Generation counter: a result is applied only while the generation it started with is still the current one.</summary>
public sealed class AiRunTracker
{
    private int _generation;

    /// <summary>Starts a run; every earlier run becomes stale.</summary>
    public int Begin()
    {
        return ++_generation;
    }

    /// <summary>The image, project or prompt changed: every run in flight becomes stale.</summary>
    public void Invalidate()
    {
        _generation++;
    }

    public bool IsCurrent(int generation)
    {
        return generation == _generation;
    }
}
