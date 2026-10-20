using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Datasets
{
    /// <summary>
    /// Describes a source annotation document without normalizing or rewriting it.
    /// </summary>
    public sealed class DatasetManifest
    {
        public const int CurrentSchemaVersion = 1;

        private readonly JObject _rawDocument;
        private readonly JToken _imageData;
        private readonly JToken _flags;
        private readonly IReadOnlyDictionary<string, JToken> _unknownFields;

        internal DatasetManifest(
            string sourceFormat,
            string labelMeVersion,
            string imagePath,
            JToken imageData,
            int? imageHeight,
            int? imageWidth,
            JToken flags,
            JObject rawDocument,
            IDictionary<string, JToken> unknownFields)
        {
            SchemaVersion = CurrentSchemaVersion;
            SourceFormat = sourceFormat;
            LabelMeVersion = labelMeVersion;
            ImagePath = imagePath;
            _imageData = Clone(imageData);
            ImageHeight = imageHeight;
            ImageWidth = imageWidth;
            _flags = Clone(flags);
            _rawDocument = rawDocument == null ? null : (JObject)rawDocument.DeepClone();
            _unknownFields = CreateReadOnlyTokens(unknownFields);
        }

        public int SchemaVersion { get; private set; }

        public string SourceFormat { get; private set; }

        public string LabelMeVersion { get; private set; }

        public string ImagePath { get; private set; }

        public JToken ImageData
        {
            get { return Clone(_imageData); }
        }

        public int? ImageHeight { get; private set; }

        public int? ImageWidth { get; private set; }

        public JToken Flags
        {
            get { return Clone(_flags); }
        }

        public IReadOnlyDictionary<string, JToken> UnknownFields
        {
            get { return CloneDictionary(_unknownFields); }
        }

        public JObject RawDocument
        {
            get { return _rawDocument == null ? null : (JObject)_rawDocument.DeepClone(); }
        }

        internal static IReadOnlyDictionary<string, JToken> CreateReadOnlyTokens(IDictionary<string, JToken> values)
        {
            Dictionary<string, JToken> copy = new Dictionary<string, JToken>(StringComparer.Ordinal);
            if (values != null)
            {
                foreach (KeyValuePair<string, JToken> value in values)
                {
                    copy.Add(value.Key, Clone(value.Value));
                }
            }

            return new ReadOnlyDictionary<string, JToken>(copy);
        }

        internal static IReadOnlyDictionary<string, JToken> CloneDictionary(IReadOnlyDictionary<string, JToken> values)
        {
            Dictionary<string, JToken> copy = new Dictionary<string, JToken>(StringComparer.Ordinal);
            if (values != null)
            {
                foreach (KeyValuePair<string, JToken> value in values)
                {
                    copy.Add(value.Key, Clone(value.Value));
                }
            }

            return new ReadOnlyDictionary<string, JToken>(copy);
        }

        internal static JToken Clone(JToken value)
        {
            return value == null ? null : value.DeepClone();
        }
    }
}
