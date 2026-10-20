using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>Where one processed image of a finished batch run stands in the chat review list.</summary>
public enum AgentImageReviewState
{
    /// <summary>Waiting for a decision: its proposals are still on the image layer.</summary>
    Pending,

    /// <summary>Its selected proposals were accepted through the canvas accept path. Final: it cannot be selected again.</summary>
    Accepted,

    /// <summary>Its proposals were dropped through the canvas reject path. Final: it cannot be selected again.</summary>
    Discarded,
}

/// <summary>The words of the review list. The defaults are English; the view model fills them from the resources.</summary>
public sealed class AgentBatchReviewLabels
{
    /// <summary>Format with {0} = the proposal count.</summary>
    public string ProposedFormat { get; set; } = "{0} proposed";

    public string Accepted { get; set; } = "Accepted";

    public string Discarded { get; set; } = "Discarded";
}

/// <summary>One processed image of a batch run: its proposal count, its state and whether the user selected it.</summary>
public sealed class AgentBatchReviewRow : INotifyPropertyChanged
{
    private readonly AgentBatchReviewLabels _labels;
    private AgentImageReviewState _state = AgentImageReviewState.Pending;
    private bool _selected;

    public AgentBatchReviewRow(string rel, int proposed, AgentBatchReviewLabels? labels)
    {
        Rel = rel ?? string.Empty;
        Proposed = proposed < 0 ? 0 : proposed;
        _labels = labels ?? new AgentBatchReviewLabels();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The image path relative to the project (the same text the chat history shows).</summary>
    public string Rel { get; }

    /// <summary>Number of proposals the agent made for this image in the run.</summary>
    public int Proposed { get; }

    public AgentImageReviewState State
    {
        get => _state;
        internal set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            Raise(nameof(State));
            Raise(nameof(Selectable));
            Raise(nameof(StatusText));
        }
    }

    /// <summary>True while the image can still be accepted or discarded and has at least one proposal.</summary>
    public bool Selectable => _state == AgentImageReviewState.Pending && Proposed > 0;

    /// <summary>The check of the row. It can only be true while <see cref="Selectable"/>.</summary>
    public bool Selected
    {
        get => _selected;
        set
        {
            bool next = value && Selectable;
            if (_selected == next)
            {
                return;
            }

            _selected = next;
            Raise(nameof(Selected));
        }
    }

    /// <summary>"N proposed" while pending, otherwise the final state.</summary>
    public string StatusText
    {
        get
        {
            switch (_state)
            {
                case AgentImageReviewState.Accepted: return _labels.Accepted;
                case AgentImageReviewState.Discarded: return _labels.Discarded;
                default: return string.Format(CultureInfo.CurrentCulture, _labels.ProposedFormat, Proposed);
            }
        }
    }

    /// <summary>Moves a pending row to its final state and clears its check.</summary>
    internal void Resolve(AgentImageReviewState state)
    {
        Selected = false;
        State = state;
    }

    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Result of one <see cref="AgentBatchReviewApply.RunAsync"/>: the images that reached their final state and the images that could not be opened.</summary>
public sealed class AgentBatchReviewResult
{
    public AgentBatchReviewResult(IReadOnlyList<string> resolved, IReadOnlyList<string> openFailed)
    {
        Resolved = resolved;
        OpenFailed = openFailed;
    }

    public IReadOnlyList<string> Resolved { get; }

    public IReadOnlyList<string> OpenFailed { get; }
}

/// <summary>The rows of the last batch run, in run order. No WPF type: the view model mirrors <see cref="Rows"/> into its collection.</summary>
public sealed class AgentBatchReview
{
    private readonly List<AgentBatchReviewRow> _rows = new List<AgentBatchReviewRow>();
    private readonly AgentBatchReviewLabels? _labels;

    public AgentBatchReview(AgentBatchReviewLabels? labels)
    {
        _labels = labels;
    }

    public IReadOnlyList<AgentBatchReviewRow> Rows => _rows;

    public int Count => _rows.Count;

    /// <summary>True when at least one row is selected (the Accept / Discard commands need it).</summary>
    public bool CanApply => _rows.Any(r => r.Selected && r.Selectable);

    /// <summary>True when at least one row can be selected (Select all / none needs it).</summary>
    public bool CanSelectAny => _rows.Any(r => r.Selectable);

    /// <summary>Adds the image of a successful run; a second entry for the same image replaces the first.</summary>
    public AgentBatchReviewRow Add(string rel, int proposed)
    {
        AgentBatchReviewRow row = new AgentBatchReviewRow(rel, proposed, _labels);
        int at = _rows.FindIndex(r => string.Equals(r.Rel, row.Rel, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            _rows[at] = row;
        }
        else
        {
            _rows.Add(row);
        }

        return row;
    }

    public void Clear()
    {
        _rows.Clear();
    }

    public AgentBatchReviewRow? Find(string rel)
    {
        return _rows.FirstOrDefault(r => string.Equals(r.Rel, rel, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Select all selectable rows, or clear every check when all of them are already selected.</summary>
    public void ToggleSelectAll()
    {
        bool allSelected = _rows.Where(r => r.Selectable).All(r => r.Selected);
        foreach (AgentBatchReviewRow row in _rows.Where(r => r.Selectable))
        {
            row.Selected = !allSelected;
        }
    }

    /// <summary>The selected, still pending images in run order (a snapshot: later changes do not alter it).</summary>
    public IReadOnlyList<string> SelectedRels()
    {
        return _rows.Where(r => r.Selected && r.Selectable).Select(r => r.Rel).ToList();
    }

    /// <summary>Moves a pending image to its final state. Accepted or discarded images are never changed again.</summary>
    public void Resolve(string rel, AgentImageReviewState state)
    {
        AgentBatchReviewRow? row = Find(rel);
        if (row == null || row.State != AgentImageReviewState.Pending || state == AgentImageReviewState.Pending)
        {
            return;
        }

        row.Resolve(state);
    }
}

/// <summary>
/// Applies the selected images one by one. <c>openImage</c> shows the image on the canvas and returns false when it cannot be shown; the
/// loop then stops (the remaining images stay pending). <c>applyToOpenImage</c> runs the canvas accept (or reject) of the open image and
/// returns how many suggestions it applied. An image is final only when it had an effect (count &gt; 0); otherwise it stays pending.
/// After an accept with an effect, <c>saveOpenImage</c> (when given) writes the open image before the next one is opened.
/// </summary>
public static class AgentBatchReviewApply
{
    public static async Task<AgentBatchReviewResult> RunAsync(
        AgentBatchReview review,
        bool accept,
        Func<string, Task<bool>> openImage,
        Func<string, bool, int> applyToOpenImage,
        Func<Task>? saveOpenImage = null)
    {
        List<string> resolved = new List<string>();
        List<string> openFailed = new List<string>();
        foreach (string rel in review.SelectedRels())
        {
            if (!await openImage(rel))
            {
                openFailed.Add(rel);
                break;
            }

            int count = applyToOpenImage(rel, accept);
            if (count <= 0)
            {
                continue;
            }

            if (accept && saveOpenImage != null)
            {
                await saveOpenImage();
            }

            review.Resolve(rel, accept ? AgentImageReviewState.Accepted : AgentImageReviewState.Discarded);
            resolved.Add(rel);
        }

        return new AgentBatchReviewResult(resolved, openFailed);
    }
}
