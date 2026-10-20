using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Datasets
{
    /// <summary>
    /// Imports LabelMe documents into the additive v1 canonical view. It intentionally does
    /// not call the legacy LabelMe IO implementation because previewing must not rewrite data.
    /// </summary>
    public sealed class LabelMeImportAdapter
    {
        private const double GeometryEpsilon = 0.000001d;

        public DatasetImportPreview Import(string sourceJson)
        {
            DatasetValidationResult validation = new DatasetValidationResult();
            string json = sourceJson ?? string.Empty;
            JToken root;

            try
            {
                root = Parse(json);
            }
            catch (JsonException exception)
            {
                validation.AddDocumentIssue("malformed_json", "$", exception.Message);
                return new DatasetImportPreview(json, null, validation);
            }

            return ImportParsed(json, root, validation);
        }

        public DatasetImportPreview Import(JToken source)
        {
            DatasetValidationResult validation = new DatasetValidationResult();
            if (source == null)
            {
                validation.AddDocumentIssue("invalid_document", "$", "The LabelMe root token is required.");
                return new DatasetImportPreview(string.Empty, null, validation);
            }

            JToken detached = source.DeepClone();
            return ImportParsed(detached.ToString(Formatting.None), detached, validation);
        }

        private static JToken Parse(string json)
        {
            using (StringReader textReader = new StringReader(json))
            using (JsonTextReader reader = new JsonTextReader(textReader))
            {
                reader.DateParseHandling = DateParseHandling.None;
                JsonLoadSettings settings = new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    LineInfoHandling = LineInfoHandling.Load
                };
                JToken root = JToken.Load(reader, settings);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment)
                    {
                        throw new JsonReaderException("Additional JSON content follows the LabelMe document.");
                    }
                }

                return root;
            }
        }

        private static DatasetImportPreview ImportParsed(string sourceJson, JToken root, DatasetValidationResult validation)
        {
            JObject rootObject = root as JObject;
            if (rootObject == null)
            {
                validation.AddDocumentIssue("invalid_envelope", "$", "The LabelMe root must be a JSON object.");
                return new DatasetImportPreview(sourceJson, null, validation);
            }

            JToken shapesToken;
            if (!rootObject.TryGetValue("shapes", StringComparison.Ordinal, out shapesToken) || !(shapesToken is JArray))
            {
                validation.AddDocumentIssue("invalid_envelope", "$.shapes", "The LabelMe document must contain a shapes array.");
                return new DatasetImportPreview(sourceJson, null, validation);
            }

            JArray shapes = (JArray)shapesToken;
            DatasetManifest manifest = CreateManifest(rootObject);
            List<CanonicalAnnotation> annotations = new List<CanonicalAnnotation>(shapes.Count);
            for (int index = 0; index < shapes.Count; index++)
            {
                annotations.Add(CreateAnnotation(shapes[index], index, validation));
            }

            return new DatasetImportPreview(sourceJson, new AnnotationDocument(manifest, annotations), validation);
        }

        private static DatasetManifest CreateManifest(JObject root)
        {
            Dictionary<string, JToken> unknown = CollectUnknown(root, new[]
            {
                "version", "flags", "shapes", "imagePath", "imageData", "imageHeight", "imageWidth"
            });

            return new DatasetManifest(
                "LabelMe",
                GetString(root["version"]),
                GetString(root["imagePath"]),
                root["imageData"],
                GetNullableInt(root["imageHeight"]),
                GetNullableInt(root["imageWidth"]),
                root["flags"],
                root,
                unknown);
        }

        private static CanonicalAnnotation CreateAnnotation(JToken source, int index, DatasetValidationResult validation)
        {
            JObject shape = source as JObject;
            if (shape == null)
            {
                validation.AddShapeIssue("non_object_shape", index, ShapePath(index), "The shapes entry must be a JSON object.");
                return new CanonicalAnnotation(index, CanonicalAnnotationKind.Unsupported, null, null, null, null, null, null, null, null, null, null, new List<AnnotationPoint>(), source, new Dictionary<string, JToken>());
            }

            JToken shapeTypeToken = shape["shape_type"];
            string shapeType = GetString(shapeTypeToken);
            CanonicalAnnotationKind kind = ToKind(shapeType);
            Dictionary<string, JToken> unknown = CollectUnknown(shape, new[]
            {
                "label", "points", "group_id", "shape_type", "flags", "description", "direction", "mask"
            });

            if (kind == CanonicalAnnotationKind.Unsupported)
            {
                validation.AddShapeIssue("unsupported_shape_type", index, ShapePropertyPath(index, "shape_type"), "Unsupported LabelMe shape_type: " + (shapeType ?? "<missing>") + ".");
            }

            List<AnnotationPoint> points = ValidatePoints(shape["points"], kind, index, validation);
            ValidateDirection(shape["direction"], index, validation);

            return new CanonicalAnnotation(
                index,
                kind,
                shapeType,
                shapeTypeToken,
                GetString(shape["label"]),
                GetNullableLong(shape["group_id"]),
                shape["group_id"],
                GetString(shape["description"]),
                shape["flags"],
                shape["mask"],
                GetNullableFiniteDouble(shape["direction"]),
                shape["direction"],
                points,
                shape,
                unknown);
        }

        private static List<AnnotationPoint> ValidatePoints(JToken pointsToken, CanonicalAnnotationKind kind, int index, DatasetValidationResult validation)
        {
            List<AnnotationPoint> points = new List<AnnotationPoint>();
            if (kind == CanonicalAnnotationKind.Unsupported)
            {
                return points;
            }

            JArray rawPoints = pointsToken as JArray;
            if (rawPoints == null)
            {
                validation.AddShapeIssue("invalid_points", index, ShapePropertyPath(index, "points"), "The points property must be an array.");
                return points;
            }

            bool valid = true;
            for (int pointIndex = 0; pointIndex < rawPoints.Count; pointIndex++)
            {
                JArray rawPoint = rawPoints[pointIndex] as JArray;
                if (rawPoint == null || rawPoint.Count != 2)
                {
                    validation.AddShapeIssue("invalid_points", index, PointPath(index, pointIndex), "Point " + pointIndex.ToString(CultureInfo.InvariantCulture) + " must contain exactly two coordinates.");
                    valid = false;
                    continue;
                }

                double x;
                double y;
                if (!TryGetFiniteDouble(rawPoint[0], out x) || !TryGetFiniteDouble(rawPoint[1], out y))
                {
                    validation.AddShapeIssue("invalid_points", index, PointPath(index, pointIndex), "Point " + pointIndex.ToString(CultureInfo.InvariantCulture) + " must contain finite numeric coordinates.");
                    valid = false;
                    continue;
                }

                points.Add(new AnnotationPoint(x, y));
            }

            if (!valid)
            {
                return new List<AnnotationPoint>();
            }

            if (kind == CanonicalAnnotationKind.Rectangle)
            {
                double width = points.Count == 2 ? Math.Abs(points[0].X - points[1].X) : 0d;
                double height = points.Count == 2 ? Math.Abs(points[0].Y - points[1].Y) : 0d;
                if (points.Count != 2 || !IsFinite(width) || !IsFinite(height) || width <= GeometryEpsilon || height <= GeometryEpsilon)
                {
                    validation.AddShapeIssue("invalid_geometry", index, ShapePropertyPath(index, "points"), "A rectangle requires two points with non-zero extents.");
                }
            }
            else if (kind == CanonicalAnnotationKind.OrientedRectangle)
            {
                double area;
                if (points.Count != 4 || !TryGetTranslatedShoelaceArea(points, out area) || Math.Abs(area) <= GeometryEpsilon)
                {
                    validation.AddShapeIssue("invalid_geometry", index, ShapePropertyPath(index, "points"), "An oriented rectangle requires four non-degenerate corners.");
                }
            }
            else if (kind == CanonicalAnnotationKind.Polygon)
            {
                double area;
                if (points.Count < 3 || !TryGetTranslatedShoelaceArea(points, out area) || Math.Abs(area) <= GeometryEpsilon)
                {
                    validation.AddShapeIssue("invalid_geometry", index, ShapePropertyPath(index, "points"), "A polygon requires at least three non-degenerate points.");
                }
            }

            return points;
        }

        private static void ValidateDirection(JToken direction, int index, DatasetValidationResult validation)
        {
            if (direction == null || direction.Type == JTokenType.Null)
            {
                return;
            }

            double ignored;
            if (!TryGetFiniteDouble(direction, out ignored))
            {
                validation.AddShapeIssue("invalid_direction", index, ShapePropertyPath(index, "direction"), "The direction property must be a finite numeric value when present.");
            }
        }

        private static CanonicalAnnotationKind ToKind(string shapeType)
        {
            if (string.Equals(shapeType, "rectangle", StringComparison.Ordinal))
            {
                return CanonicalAnnotationKind.Rectangle;
            }

            if (string.Equals(shapeType, "polygon", StringComparison.Ordinal))
            {
                return CanonicalAnnotationKind.Polygon;
            }

            if (string.Equals(shapeType, "oriented_rectangle", StringComparison.Ordinal) || string.Equals(shapeType, "rotation", StringComparison.Ordinal))
            {
                return CanonicalAnnotationKind.OrientedRectangle;
            }

            return CanonicalAnnotationKind.Unsupported;
        }

        private static Dictionary<string, JToken> CollectUnknown(JObject source, string[] knownNames)
        {
            HashSet<string> known = new HashSet<string>(knownNames, StringComparer.Ordinal);
            Dictionary<string, JToken> unknown = new Dictionary<string, JToken>(StringComparer.Ordinal);
            foreach (JProperty property in source.Properties())
            {
                if (!known.Contains(property.Name))
                {
                    unknown.Add(property.Name, property.Value);
                }
            }

            return unknown;
        }

        private static string GetString(JToken token)
        {
            return token != null && token.Type == JTokenType.String ? (string)token : null;
        }

        private static int? GetNullableInt(JToken token)
        {
            int value;
            return TryGetInt(token, out value) ? (int?)value : null;
        }

        private static long? GetNullableLong(JToken token)
        {
            long value;
            return TryGetLong(token, out value) ? (long?)value : null;
        }

        private static double? GetNullableFiniteDouble(JToken token)
        {
            double value;
            return TryGetFiniteDouble(token, out value) ? (double?)value : null;
        }

        private static bool TryGetInt(JToken token, out int value)
        {
            value = 0;
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
            {
                return false;
            }

            try
            {
                value = token.Value<int>();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryGetLong(JToken token, out long value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer)
            {
                return false;
            }

            try
            {
                value = token.Value<long>();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryGetFiniteDouble(JToken token, out double value)
        {
            value = 0d;
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
            {
                return false;
            }

            try
            {
                value = token.Value<double>();
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ShapePath(int index)
        {
            return "$.shapes[" + index.ToString(CultureInfo.InvariantCulture) + "]";
        }

        private static string ShapePropertyPath(int index, string propertyName)
        {
            return ShapePath(index) + "." + propertyName;
        }

        private static string PointPath(int shapeIndex, int pointIndex)
        {
            return ShapePropertyPath(shapeIndex, "points") + "[" + pointIndex.ToString(CultureInfo.InvariantCulture) + "]";
        }

        private static bool TryGetTranslatedShoelaceArea(IList<AnnotationPoint> points, out double area)
        {
            area = 0d;
            double originX = points[0].X;
            double originY = points[0].Y;
            if (!IsFinite(originX) || !IsFinite(originY))
            {
                return false;
            }

            double twiceArea = 0d;
            for (int index = 0; index < points.Count; index++)
            {
                AnnotationPoint current = points[index];
                AnnotationPoint next = points[(index + 1) % points.Count];
                double currentX = current.X - originX;
                double currentY = current.Y - originY;
                double nextX = next.X - originX;
                double nextY = next.Y - originY;
                if (!IsFinite(currentX) || !IsFinite(currentY) || !IsFinite(nextX) || !IsFinite(nextY))
                {
                    return false;
                }

                double firstProduct = currentX * nextY;
                double secondProduct = nextX * currentY;
                if (!IsFinite(firstProduct) || !IsFinite(secondProduct))
                {
                    return false;
                }

                double crossProduct = firstProduct - secondProduct;
                if (!IsFinite(crossProduct))
                {
                    return false;
                }

                twiceArea += crossProduct;
                if (!IsFinite(twiceArea))
                {
                    return false;
                }
            }

            area = twiceArea / 2d;
            return IsFinite(area) && IsFinite(Math.Abs(area));
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
