using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using EasyLabel.Core.Datasets;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Tests
{
    public sealed class DatasetImportTests
    {
        [Fact]
        public void LabelMeSevenFixtureImportsAllShapesAndPreservesMetadata()
        {
            string source = File.ReadAllText(GetFixturePath());
            LabelMeImportAdapter adapter = new LabelMeImportAdapter();

            DatasetImportPreview preview = adapter.Import(source);

            Assert.NotNull(preview.Document);
            Assert.True(preview.Validation.IsValid);
            Assert.Equal(6, preview.Document.Annotations.Count);
            Assert.Equal(5, preview.Document.Annotations.Count(annotation => annotation.OriginalShapeType == "oriented_rectangle"));
            Assert.Equal(CanonicalAnnotationKind.OrientedRectangle, preview.Document.Annotations[0].Kind);
            Assert.Equal("oriented_rectangle", preview.Document.Annotations[0].OriginalShapeType);
            Assert.Equal("cap", preview.Document.Annotations[0].Label);
            Assert.Equal(4L, preview.Document.Annotations[0].GroupId);
            Assert.Equal("synthetic obb one", preview.Document.Annotations[0].Description);
            Assert.True((bool)preview.Document.Annotations[0].Flags["verified"]);
            Assert.Equal("oriented_rectangle", (string)preview.Document.Annotations[0].OriginalShapeTypeToken);
            Assert.Equal(4L, (long)preview.Document.Annotations[0].GroupIdToken);
            Assert.Equal(0.12d, (double)preview.Document.Annotations[0].DirectionToken);
            Assert.Equal("synthetic", (string)preview.Document.Annotations[0].UnknownFields["review_state"]);
            Assert.NotNull(preview.Document.Annotations[0].Mask);
            Assert.Equal("bitmap-reference", (string)preview.Document.Annotations[0].Mask["kind"]);
            Assert.Equal("fixture", (string)preview.Document.Manifest.UnknownFields["source_note"]);
            Assert.True((bool)preview.Document.Annotations[0].RawShape["custom_payload"]["keep"]);
        }

        [Fact]
        public void RotationAndOrientedRectangleMapToTheSameKindAndKeepPointOrder()
        {
            const string source = "{\"version\":\"5.5.0\",\"shapes\":[{\"label\":\"legacy\",\"shape_type\":\"rotation\",\"direction\":0.5,\"points\":[[4,2],[8,2],[8,5],[4,5]]},{\"label\":\"current\",\"shape_type\":\"oriented_rectangle\",\"direction\":1.5,\"points\":[[12,6],[15,7],[14,10],[11,9]]}]}";
            DatasetImportPreview preview = new LabelMeImportAdapter().Import(source);

            Assert.True(preview.Validation.IsValid);
            Assert.Equal(CanonicalAnnotationKind.OrientedRectangle, preview.Document.Annotations[0].Kind);
            Assert.Equal(CanonicalAnnotationKind.OrientedRectangle, preview.Document.Annotations[1].Kind);
            Assert.Equal("rotation", preview.Document.Annotations[0].OriginalShapeType);
            Assert.Equal("oriented_rectangle", preview.Document.Annotations[1].OriginalShapeType);
            Assert.Equal(4d, preview.Document.Annotations[0].Points[0].X);
            Assert.Equal(2d, preview.Document.Annotations[0].Points[0].Y);
            Assert.Equal(11d, preview.Document.Annotations[1].Points[3].X);
            Assert.Equal(9d, preview.Document.Annotations[1].Points[3].Y);
            Assert.Equal(1.5d, preview.Document.Annotations[1].Direction);
        }

        [Fact]
        public void ImportDoesNotMutateCallerTokenOrSourceText()
        {
            const string source = "{\"version\":\"7.0.4\",\"shapes\":[{\"label\":\"one\",\"shape_type\":\"rectangle\",\"points\":[[1,2],[3,4]]}]}";
            JObject callerToken = JObject.Parse(source);
            string before = callerToken.ToString(Newtonsoft.Json.Formatting.None);

            DatasetImportPreview preview = new LabelMeImportAdapter().Import(callerToken);
            ((JObject)preview.Document.Annotations[0].RawShape)["label"] = "changed-preview-only";

            Assert.Equal(before, callerToken.ToString(Newtonsoft.Json.Formatting.None));
            Assert.Equal(source, new LabelMeImportAdapter().Import(source).SourceJson);
        }

        [Fact]
        public void InvalidAndUnsupportedContentRemainIndexedAndReported()
        {
            const string source = "{\"version\":\"7.0.4\",\"shapes\":[{\"label\":\"flat\",\"shape_type\":\"oriented_rectangle\",\"points\":[[1,1],[2,2],[3,3],[4,4]]},{\"label\":\"ellipse\",\"shape_type\":\"circle\",\"points\":[[1,1],[2,2]]},17]}";
            DatasetImportPreview preview = new LabelMeImportAdapter().Import(source);

            Assert.Equal(3, preview.Document.Annotations.Count);
            Assert.Equal(CanonicalAnnotationKind.OrientedRectangle, preview.Document.Annotations[0].Kind);
            Assert.Equal(CanonicalAnnotationKind.Unsupported, preview.Document.Annotations[1].Kind);
            Assert.Equal(CanonicalAnnotationKind.Unsupported, preview.Document.Annotations[2].Kind);
            Assert.NotNull(preview.Document.Annotations[1].RawUnsupportedPayload);
            Assert.Contains(preview.Validation.Issues, issue => issue.SourceIndex == 0 && issue.Code == "invalid_geometry");
            Assert.Contains(preview.Validation.Issues, issue => issue.SourceIndex == 1 && issue.Code == "unsupported_shape_type");
            Assert.Contains(preview.Validation.Issues, issue => issue.SourceIndex == 2 && issue.Code == "non_object_shape");
            DatasetValidationIssue invalidGeometry = preview.Validation.Issues.Single(issue => issue.SourceIndex == 0 && issue.Code == "invalid_geometry");
            Assert.Equal(DatasetValidationSeverity.Error, invalidGeometry.Severity);
            Assert.Equal("$.shapes[0].points", invalidGeometry.JsonPath);
        }

        [Fact]
        public void ExtremeFiniteCoordinatesThatOverflowAreaAreInvalid()
        {
            const string source = "{\"version\":\"7.0.4\",\"shapes\":[{\"label\":\"extreme\",\"shape_type\":\"oriented_rectangle\",\"points\":[[1e308,1e308],[-1e308,1e308],[-1e308,-1e308],[1e308,-1e308]]}]}";
            DatasetImportPreview preview = new LabelMeImportAdapter().Import(source);

            DatasetValidationIssue issue = preview.Validation.Issues.Single(item => item.Code == "invalid_geometry");
            Assert.Equal(0, issue.SourceIndex);
            Assert.Equal(DatasetValidationSeverity.Error, issue.Severity);
            Assert.Equal("$.shapes[0].points", issue.JsonPath);
        }

        [Fact]
        public void MalformedAndInvalidEnvelopeReturnDocumentLevelValidation()
        {
            DatasetImportPreview malformed = new LabelMeImportAdapter().Import("{\"shapes\":[");
            DatasetImportPreview invalidEnvelope = new LabelMeImportAdapter().Import("{\"version\":\"7.0.4\",\"shapes\":{}}");

            Assert.Null(malformed.Document);
            Assert.Equal("malformed_json", malformed.Validation.Issues.Single().Code);
            Assert.Equal("$", malformed.Validation.Issues.Single().JsonPath);
            Assert.Null(invalidEnvelope.Document);
            Assert.Equal("invalid_envelope", invalidEnvelope.Validation.Issues.Single().Code);
            Assert.Equal("$.shapes", invalidEnvelope.Validation.Issues.Single().JsonPath);
        }

        private static string GetFixturePath([CallerFilePath] string sourceFile = "")
        {
            return Path.Combine(Path.GetDirectoryName(sourceFile), "fixtures", "labelme7_oriented_rectangle.json");
        }
    }
}
