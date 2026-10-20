using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Datasets
{
    public enum CanonicalAnnotationKind
    {
        Unsupported = 0,
        Rectangle = 1,
        Polygon = 2,
        OrientedRectangle = 3
    }

    public sealed class AnnotationPoint
    {
        public AnnotationPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; private set; }

        public double Y { get; private set; }
    }

    /// <summary>
    /// A canonical projection of one source-shape array position. Invalid and unsupported
    /// source entries remain represented here so callers cannot silently lose them.
    /// </summary>
    public sealed class CanonicalAnnotation
    {
        private readonly JToken _rawShape;
        private readonly JToken _rawShapeType;
        private readonly JToken _rawGroupId;
        private readonly JToken _rawDirection;
        private readonly JToken _mask;
        private readonly JToken _flags;
        private readonly IReadOnlyList<AnnotationPoint> _points;
        private readonly IReadOnlyDictionary<string, JToken> _unknownFields;

        internal CanonicalAnnotation(
            int sourceIndex,
            CanonicalAnnotationKind kind,
            string originalShapeType,
            JToken rawShapeType,
            string label,
            long? groupId,
            JToken rawGroupId,
            string description,
            JToken flags,
            JToken mask,
            double? direction,
            JToken rawDirection,
            IList<AnnotationPoint> points,
            JToken rawShape,
            IDictionary<string, JToken> unknownFields)
        {
            SourceIndex = sourceIndex;
            Kind = kind;
            OriginalShapeType = originalShapeType;
            _rawShapeType = DatasetManifest.Clone(rawShapeType);
            Label = label;
            GroupId = groupId;
            _rawGroupId = DatasetManifest.Clone(rawGroupId);
            Description = description;
            _flags = DatasetManifest.Clone(flags);
            _mask = DatasetManifest.Clone(mask);
            Direction = direction;
            _rawDirection = DatasetManifest.Clone(rawDirection);
            _points = new ReadOnlyCollection<AnnotationPoint>(new List<AnnotationPoint>(points ?? new List<AnnotationPoint>()));
            _rawShape = DatasetManifest.Clone(rawShape);
            _unknownFields = DatasetManifest.CreateReadOnlyTokens(unknownFields);
        }

        public int SourceIndex { get; private set; }

        public CanonicalAnnotationKind Kind { get; private set; }

        public string OriginalShapeType { get; private set; }

        public JToken OriginalShapeTypeToken
        {
            get { return DatasetManifest.Clone(_rawShapeType); }
        }

        public string Label { get; private set; }

        public long? GroupId { get; private set; }

        public JToken GroupIdToken
        {
            get { return DatasetManifest.Clone(_rawGroupId); }
        }

        public string Description { get; private set; }

        public JToken Flags
        {
            get { return DatasetManifest.Clone(_flags); }
        }

        public JToken Mask
        {
            get { return DatasetManifest.Clone(_mask); }
        }

        public double? Direction { get; private set; }

        public JToken DirectionToken
        {
            get { return DatasetManifest.Clone(_rawDirection); }
        }

        public IReadOnlyList<AnnotationPoint> Points
        {
            get { return _points; }
        }

        public IReadOnlyDictionary<string, JToken> UnknownFields
        {
            get { return DatasetManifest.CloneDictionary(_unknownFields); }
        }

        public JToken RawShape
        {
            get { return DatasetManifest.Clone(_rawShape); }
        }

        public JToken RawUnsupportedPayload
        {
            get { return Kind == CanonicalAnnotationKind.Unsupported ? DatasetManifest.Clone(_rawShape) : null; }
        }
    }

    public sealed class AnnotationDocument
    {
        private readonly IReadOnlyList<CanonicalAnnotation> _annotations;

        internal AnnotationDocument(DatasetManifest manifest, IList<CanonicalAnnotation> annotations)
        {
            Manifest = manifest;
            _annotations = new ReadOnlyCollection<CanonicalAnnotation>(new List<CanonicalAnnotation>(annotations ?? new List<CanonicalAnnotation>()));
        }

        public DatasetManifest Manifest { get; private set; }

        public IReadOnlyList<CanonicalAnnotation> Annotations
        {
            get { return _annotations; }
        }
    }
}
