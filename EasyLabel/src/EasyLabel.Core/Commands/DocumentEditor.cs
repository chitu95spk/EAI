using System;
using System.Collections.Generic;
using System.Linq;
using EasyLabel.Core.Model;
using Newtonsoft.Json;

namespace EasyLabel.Core.Commands
{
    public sealed class DocumentEditor
    {
        private readonly LabelMeDocument _document;
        private readonly EasyLabelSidecar _sidecar;
        public CommandStack History { get; private set; }

        public DocumentEditor(LabelMeDocument document, EasyLabelSidecar sidecar)
        {
            _document = document ?? throw new ArgumentNullException(nameof(document));
            _sidecar = sidecar ?? throw new ArgumentNullException(nameof(sidecar));
            History = new CommandStack();
        }

        public static T Clone<T>(T value) where T : class
        {
            return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))
                ?? throw new InvalidOperationException("Could not clone edit state.");
        }

        public void Add(IEnumerable<LabelShape> shapes, IEnumerable<ShapeProvenance> provenance, string name)
        {
            List<LabelShape> added = shapes.ToList();
            List<ShapeProvenance> sources = provenance.ToList();
            if (added.Count != sources.Count) throw new ArgumentException("Shape provenance must be aligned.");
            Change(name, state =>
            {
                state.Shapes.AddRange(added.Select(Clone));
                state.Provenance.AddRange(sources.Select(Clone));
            });
        }

        public void Delete(int index)
        {
            Change("Delete shape", state =>
            {
                state.Shapes.RemoveAt(index);
                state.Provenance.RemoveAt(index);
            });
        }

        public void Replace(int index, LabelShape shape, string name)
        {
            Change(name, state => state.Shapes[index] = Clone(shape));
        }

        public void ChangeClass(int index, string label)
        {
            Change("Change class", state => state.Shapes[index].Label = label);
        }

        public void Duplicate(int index)
        {
            Change("Duplicate shape", state =>
            {
                LabelShape duplicate = Clone(state.Shapes[index]);
                foreach (double[] point in duplicate.Points)
                {
                    point[0] += 10.0;
                    point[1] += 10.0;
                }

                state.Shapes.Add(duplicate);
                state.Provenance.Add(index < state.Provenance.Count
                    ? Clone(state.Provenance[index]) : new ShapeProvenance());
            });
        }

        public void InsertVertex(int shapeIndex, int afterVertexIndex, double[] point)
        {
            if (point == null || point.Length < 2) throw new ArgumentException("A vertex requires x and y coordinates.", nameof(point));
            Change("Add vertex", state =>
            {
                LabelShape shape = state.Shapes[shapeIndex];
                if (!string.Equals(shape.ShapeType, "polygon", StringComparison.Ordinal) || shape.Points.Count < 3)
                    throw new InvalidOperationException("Vertices can only be added to polygons.");
                int insertionIndex = Math.Max(0, Math.Min(afterVertexIndex + 1, shape.Points.Count));
                shape.Points.Insert(insertionIndex, new[] { point[0], point[1] });
            });
        }

        public void RemoveVertex(int shapeIndex, int vertexIndex)
        {
            Change("Remove vertex", state =>
            {
                LabelShape shape = state.Shapes[shapeIndex];
                if (!string.Equals(shape.ShapeType, "polygon", StringComparison.Ordinal) || shape.Points.Count <= 3)
                    throw new InvalidOperationException("A polygon must retain at least three vertices.");
                shape.Points.RemoveAt(vertexIndex);
            });
        }

        /// <summary>Deletes several shapes (with their provenance) as ONE undo unit. Duplicates are ignored.</summary>
        public void DeleteMany(IEnumerable<int> indices)
        {
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            List<int> ordered = indices.Distinct().OrderByDescending(i => i).ToList();
            Change("Delete shapes", state =>
            {
                foreach (int index in ordered)
                {
                    if (index < 0 || index >= state.Shapes.Count) throw new ArgumentOutOfRangeException(nameof(indices));
                    state.Shapes.RemoveAt(index);
                    state.Provenance.RemoveAt(index);
                }
            });
        }

        /// <summary>Replaces several shapes as ONE undo unit (keyed by shape index).</summary>
        public void ReplaceMany(IReadOnlyDictionary<int, LabelShape> replacements, string name)
        {
            if (replacements == null) throw new ArgumentNullException(nameof(replacements));
            List<KeyValuePair<int, LabelShape>> items = replacements.ToList();
            Change(name, state =>
            {
                foreach (KeyValuePair<int, LabelShape> item in items)
                {
                    if (item.Key < 0 || item.Key >= state.Shapes.Count) throw new ArgumentOutOfRangeException(nameof(replacements));
                    state.Shapes[item.Key] = Clone(item.Value);
                }
            });
        }

        /// <summary>Replaces some shapes and deletes others (with their provenance) as ONE undo unit (used by merge).</summary>
        public void ReplaceAndDelete(IReadOnlyDictionary<int, LabelShape> replacements, IEnumerable<int> deletions, string name)
        {
            if (replacements == null) throw new ArgumentNullException(nameof(replacements));
            if (deletions == null) throw new ArgumentNullException(nameof(deletions));
            List<KeyValuePair<int, LabelShape>> items = replacements.ToList();
            List<int> removed = deletions.Distinct().OrderByDescending(i => i).ToList();
            Change(name, state =>
            {
                foreach (KeyValuePair<int, LabelShape> item in items)
                {
                    if (item.Key < 0 || item.Key >= state.Shapes.Count) throw new ArgumentOutOfRangeException(nameof(replacements));
                    state.Shapes[item.Key] = Clone(item.Value);
                }

                foreach (int index in removed)
                {
                    if (index < 0 || index >= state.Shapes.Count) throw new ArgumentOutOfRangeException(nameof(deletions));
                    state.Shapes.RemoveAt(index);
                    state.Provenance.RemoveAt(index);
                }
            });
        }

        /// <summary>
        /// Applies an accepted agent review as ONE undo unit: replaces the shapes at the keys of <paramref name="replacements"/>
        /// (provenance replaced too when <paramref name="replacementProvenance"/> has the key), deletes <paramref name="deletions"/>
        /// (descending, so indexes stay valid) and appends <paramref name="adds"/>. All indexes refer to the document before the call.
        /// Undo restores the whole previous state, so deleted and replaced shapes come back at their original indexes.
        /// A target may be replaced or deleted once, not both.
        /// </summary>
        public void ApplyReview(IEnumerable<LabelShape> adds, IEnumerable<ShapeProvenance> addProvenance,
            IReadOnlyDictionary<int, LabelShape> replacements, IReadOnlyDictionary<int, ShapeProvenance> replacementProvenance,
            IEnumerable<int> deletions, string name)
        {
            List<LabelShape> added = (adds ?? Enumerable.Empty<LabelShape>()).ToList();
            List<ShapeProvenance> addSources = (addProvenance ?? Enumerable.Empty<ShapeProvenance>()).ToList();
            if (added.Count != addSources.Count) throw new ArgumentException("Shape provenance must be aligned.");
            List<KeyValuePair<int, LabelShape>> items = (replacements ?? new Dictionary<int, LabelShape>()).ToList();
            List<int> removed = (deletions ?? Enumerable.Empty<int>()).Distinct().OrderByDescending(i => i).ToList();
            if (items.Any(item => removed.Contains(item.Key)))
                throw new ArgumentException("A shape can be replaced or deleted, not both.", nameof(deletions));
            Change(name, state =>
            {
                foreach (KeyValuePair<int, LabelShape> item in items)
                {
                    if (item.Key < 0 || item.Key >= state.Shapes.Count) throw new ArgumentOutOfRangeException(nameof(replacements));
                    state.Shapes[item.Key] = Clone(item.Value);
                    if (replacementProvenance != null && replacementProvenance.TryGetValue(item.Key, out ShapeProvenance source))
                        state.Provenance[item.Key] = Clone(source);
                }

                foreach (int index in removed)
                {
                    if (index < 0 || index >= state.Shapes.Count) throw new ArgumentOutOfRangeException(nameof(deletions));
                    state.Shapes.RemoveAt(index);
                    state.Provenance.RemoveAt(index);
                }

                state.Shapes.AddRange(added.Select(Clone));
                state.Provenance.AddRange(addSources.Select(Clone));
            });
        }

        private void Change(string name, Action<EditState> change)
        {
            EditState before = Capture();
            EditState after = Clone(before);
            change(after);
            if (JsonConvert.SerializeObject(before) == JsonConvert.SerializeObject(after)) return;
            after.ReviewStatus = after.Shapes.Count == 0 ? "unlabeled" : "in_progress";
            History.Execute(new ShapeEditCommand(_document, _sidecar, before, after, name));
        }

        private EditState Capture()
        {
            return new EditState
            {
                Shapes = _document.Shapes.Select(Clone).ToList(),
                Provenance = Enumerable.Range(0, _document.Shapes.Count)
                    .Select(index => index < _sidecar.Shapes.Count
                        ? Clone(_sidecar.Shapes[index]) : new ShapeProvenance()).ToList(),
                ReviewStatus = _sidecar.ReviewStatus
            };
        }

        private sealed class EditState
        {
            public List<LabelShape> Shapes { get; set; } = new List<LabelShape>();
            public List<ShapeProvenance> Provenance { get; set; } = new List<ShapeProvenance>();
            public string ReviewStatus { get; set; } = "unlabeled";
        }

        private sealed class ShapeEditCommand : ICommand
        {
            private readonly LabelMeDocument _document;
            private readonly EasyLabelSidecar _sidecar;
            private readonly EditState _before;
            private readonly EditState _after;
            public string Name { get; private set; }

            public ShapeEditCommand(LabelMeDocument document, EasyLabelSidecar sidecar,
                EditState before, EditState after, string name)
            {
                _document = document;
                _sidecar = sidecar;
                _before = before;
                _after = after;
                Name = name;
            }

            public void Execute() { Apply(_after); }
            public void Undo() { Apply(_before); }

            private void Apply(EditState snapshot)
            {
                EditState state = Clone(snapshot);
                _document.Shapes = state.Shapes;
                _sidecar.Shapes = state.Provenance;
                _sidecar.ReviewStatus = state.ReviewStatus;
            }
        }
    }
}
