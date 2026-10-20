using System.Globalization;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Batch
{
    public sealed class AugmentService
    {
        private readonly LabelMeIO _labelMeIO;
        private readonly SidecarIO _sidecarIO;
        private readonly IAugmentImageWriter _imageWriter;

        public AugmentService(IAugmentImageWriter imageWriter)
            : this(new LabelMeIO(), new SidecarIO(), imageWriter)
        {
        }

        public AugmentService(LabelMeIO labelMeIO, SidecarIO sidecarIO, IAugmentImageWriter imageWriter)
        {
            _labelMeIO = labelMeIO ?? throw new ArgumentNullException(nameof(labelMeIO));
            _sidecarIO = sidecarIO ?? throw new ArgumentNullException(nameof(sidecarIO));
            _imageWriter = imageWriter ?? throw new ArgumentNullException(nameof(imageWriter));
        }

        public AugmentResult Preview(AugmentRequest request)
        {
            return Execute(request, false);
        }

        public AugmentResult Apply(AugmentRequest request)
        {
            return Execute(request, true);
        }

        private AugmentResult Execute(AugmentRequest request, bool apply)
        {
            Validate(request);

            LabelMeDocument sourceDocument = _labelMeIO.Read(request.JsonPath);
            ValidateAgainstSourceDocument(request, sourceDocument);
            EasyLabelSidecar sourceSidecar = _sidecarIO.Read(request.SidecarPath);
            AugmentResult result = new AugmentResult();

            foreach (AugmentOutput output in CreateOutputs(request))
            {
                EnsureOutputAvailable(output);
                result.AddOutput(output);

                if (!apply)
                {
                    continue;
                }

                AugmentImageOperation imageOperation = new AugmentImageOperation
                {
                    IsClone = output.IsClone,
                    Variant = output.Variant,
                    SourcePath = request.ImagePath,
                    OutputPath = output.ImagePath,
                    ArbitraryRotationDegrees = request.ArbitraryRotationDegrees,
                    HsvHueDelta = request.HsvHueDelta,
                    HsvSaturationScale = request.HsvSaturationScale,
                    HsvValueScale = request.HsvValueScale,
                    ScaleFactor = request.ScaleFactor,
                    CropX = request.CropX,
                    CropY = request.CropY,
                    CropWidth = request.CropWidth,
                    CropHeight = request.CropHeight
                };
                _imageWriter.Write(imageOperation);

                LabelMeDocument outputDocument = CloneDocument(sourceDocument);
                outputDocument.ImagePath = Path.GetFileName(output.ImagePath);

                if (!output.IsClone && output.Variant.HasValue)
                {
                    TransformDocument(outputDocument, output.Variant.Value, sourceDocument.ImageWidth, sourceDocument.ImageHeight, request);
                }

                _labelMeIO.Write(outputDocument, output.JsonPath);
                if (File.Exists(request.SidecarPath))
                {
                    _sidecarIO.Write(CloneSidecar(sourceSidecar), output.SidecarPath);
                }
            }

            return result;
        }

        private static void Validate(AugmentRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.EffectiveRoot))
            {
                throw new ArgumentException("Project root is required.", nameof(request));
            }

            if (!Directory.Exists(request.EffectiveRoot))
            {
                throw new DirectoryNotFoundException($"Project folder does not exist: {request.EffectiveRoot}");
            }

            if (string.IsNullOrWhiteSpace(request.ImagePath) || !File.Exists(request.ImagePath))
            {
                throw new FileNotFoundException("Source image does not exist.", request.ImagePath);
            }

            if (string.IsNullOrWhiteSpace(request.JsonPath) || !File.Exists(request.JsonPath))
            {
                throw new FileNotFoundException("Source annotation does not exist.", request.JsonPath);
            }

            if (request.CloneCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Clone count cannot be negative.");
            }

            if (request.CloneCount == 0 && request.Variants.Count == 0)
            {
                throw new ArgumentException("Select at least one clone or augment variant.", nameof(request));
            }

            if (request.Variants.Contains(AugmentVariant.Scale) && request.ScaleFactor <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Scale factor must be greater than zero.");
            }

            if (request.Variants.Contains(AugmentVariant.Crop)
                && (request.CropWidth <= 0 || request.CropHeight <= 0 || request.CropX < 0 || request.CropY < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Crop rectangle must have positive size and non-negative origin.");
            }
        }

        private static void ValidateAgainstSourceDocument(AugmentRequest request, LabelMeDocument document)
        {
            if (request.Variants.Contains(AugmentVariant.Scale))
            {
                int scaledWidth = (int)Math.Round(document.ImageWidth * request.ScaleFactor, MidpointRounding.AwayFromZero);
                int scaledHeight = (int)Math.Round(document.ImageHeight * request.ScaleFactor, MidpointRounding.AwayFromZero);
                if (scaledWidth <= 0 || scaledHeight <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(request), "Scale factor creates an empty output image.");
                }
            }

            if (request.Variants.Contains(AugmentVariant.Crop)
                && (request.CropX + request.CropWidth > document.ImageWidth
                    || request.CropY + request.CropHeight > document.ImageHeight))
            {
                throw new ArgumentOutOfRangeException(nameof(request), "Crop rectangle must fit inside the source image.");
            }
        }

        private static IEnumerable<AugmentOutput> CreateOutputs(AugmentRequest request)
        {
            ProjectPaths paths = request.ResolvePaths();
            for (int i = 1; i <= request.CloneCount; i++)
            {
                yield return CreateOutput(paths, request.ImagePath, "_c" + i.ToString(CultureInfo.InvariantCulture), true, null);
            }

            foreach (AugmentVariant variant in request.Variants.Distinct())
            {
                yield return CreateOutput(paths, request.ImagePath, GetVariantSuffix(variant), false, variant);
            }
        }

        private static AugmentOutput CreateOutput(ProjectPaths paths, string sourceImagePath, string suffix, bool isClone, AugmentVariant? variant)
        {
            string directory = Path.GetDirectoryName(sourceImagePath) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(sourceImagePath);
            string extension = Path.GetExtension(sourceImagePath);
            string outputImagePath = Path.Combine(directory, name + suffix + extension);
            string outputBasePath = Path.Combine(directory, name + suffix);

            return new AugmentOutput
            {
                ImagePath = outputImagePath,
                JsonPath = outputBasePath + ".json",
                SidecarPath = paths.MetaSidecarPath(outputImagePath),
                IsClone = isClone,
                Variant = variant
            };
        }

        private static string GetVariantSuffix(AugmentVariant variant)
        {
            switch (variant)
            {
                case AugmentVariant.FlipHorizontal:
                    return "_fh";
                case AugmentVariant.FlipVertical:
                    return "_fv";
                case AugmentVariant.Rotate90Clockwise:
                    return "_r90";
                case AugmentVariant.Rotate180:
                    return "_r180";
                case AugmentVariant.Rotate270Clockwise:
                    return "_r270";
                case AugmentVariant.RotateArbitrary:
                    return "_ra";
                case AugmentVariant.HsvJitter:
                    return "_hsv";
                case AugmentVariant.Scale:
                    return "_scale";
                case AugmentVariant.Crop:
                    return "_crop";
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown augment variant.");
            }
        }

        private static void EnsureOutputAvailable(AugmentOutput output)
        {
            if (File.Exists(output.ImagePath))
            {
                throw new IOException($"Output image already exists: {output.ImagePath}");
            }

            if (File.Exists(output.JsonPath))
            {
                throw new IOException($"Output annotation already exists: {output.JsonPath}");
            }

            if (File.Exists(output.SidecarPath))
            {
                throw new IOException($"Output sidecar already exists: {output.SidecarPath}");
            }
        }

        private static LabelMeDocument CloneDocument(LabelMeDocument source)
        {
            LabelMeDocument clone = new LabelMeDocument
            {
                Version = source.Version,
                ImagePath = source.ImagePath,
                ImageData = null,
                ImageHeight = source.ImageHeight,
                ImageWidth = source.ImageWidth,
                Flags = new Dictionary<string, bool>(source.Flags)
            };

            foreach (LabelShape shape in source.Shapes)
            {
                clone.Shapes.Add(CloneShape(shape));
            }

            return clone;
        }

        private static LabelShape CloneShape(LabelShape source)
        {
            LabelShape clone = new LabelShape
            {
                Label = source.Label,
                GroupId = source.GroupId,
                ShapeType = source.ShapeType,
                Flags = new Dictionary<string, bool>(source.Flags),
                Description = source.Description,
                Direction = source.Direction
            };

            foreach (double[] point in source.Points)
            {
                clone.Points.Add(new[] { point[0], point[1] });
            }

            return clone;
        }

        private static EasyLabelSidecar CloneSidecar(EasyLabelSidecar source)
        {
            EasyLabelSidecar clone = new EasyLabelSidecar
            {
                ReviewStatus = source.ReviewStatus
            };

            foreach (ShapeProvenance provenance in source.Shapes)
            {
                clone.Shapes.Add(new ShapeProvenance
                {
                    Source = provenance.Source,
                    Confidence = provenance.Confidence,
                    Model = provenance.Model
                });
            }

            return clone;
        }

        private static void TransformDocument(LabelMeDocument document, AugmentVariant variant, int width, int height, AugmentRequest request)
        {
            foreach (LabelShape shape in document.Shapes)
            {
                for (int i = 0; i < shape.Points.Count; i++)
                {
                    shape.Points[i] = TransformPoint(shape.Points[i], variant, width, height, request);
                }

                if (shape.ShapeType == "oriented_rectangle" && shape.Points.Count >= 4)
                {
                    ReorderOrientedBox(shape);
                }
            }

            if (variant == AugmentVariant.Rotate90Clockwise || variant == AugmentVariant.Rotate270Clockwise)
            {
                document.ImageWidth = height;
                document.ImageHeight = width;
            }
            else if (variant == AugmentVariant.Scale)
            {
                document.ImageWidth = (int)Math.Round(width * request.ScaleFactor, MidpointRounding.AwayFromZero);
                document.ImageHeight = (int)Math.Round(height * request.ScaleFactor, MidpointRounding.AwayFromZero);
            }
            else if (variant == AugmentVariant.Crop)
            {
                document.ImageWidth = request.CropWidth;
                document.ImageHeight = request.CropHeight;
            }
        }

        private static double[] TransformPoint(double[] point, AugmentVariant variant, int width, int height, AugmentRequest request)
        {
            double x = point[0];
            double y = point[1];
            switch (variant)
            {
                case AugmentVariant.FlipHorizontal:
                    return new[] { width - x, y };
                case AugmentVariant.FlipVertical:
                    return new[] { x, height - y };
                case AugmentVariant.Rotate90Clockwise:
                    return new[] { height - y, x };
                case AugmentVariant.Rotate180:
                    return new[] { width - x, height - y };
                case AugmentVariant.Rotate270Clockwise:
                    return new[] { y, width - x };
                case AugmentVariant.RotateArbitrary:
                    return RotatePoint(x, y, width, height, request.ArbitraryRotationDegrees);
                case AugmentVariant.HsvJitter:
                    return new[] { x, y };
                case AugmentVariant.Scale:
                    return new[] { x * request.ScaleFactor, y * request.ScaleFactor };
                case AugmentVariant.Crop:
                    return new[] { x - request.CropX, y - request.CropY };
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown augment variant.");
            }
        }

        private static double[] RotatePoint(double x, double y, int width, int height, double degrees)
        {
            double radians = degrees * Math.PI / 180.0;
            double alpha = Math.Cos(radians);
            double beta = Math.Sin(radians);
            double centerX = width / 2.0;
            double centerY = height / 2.0;
            double translatedX = x - centerX;
            double translatedY = y - centerY;
            return new[]
            {
                alpha * translatedX + beta * translatedY + centerX,
                -beta * translatedX + alpha * translatedY + centerY
            };
        }

        private static void ReorderOrientedBox(LabelShape shape)
        {
            double[][] corners = shape.Points.Take(4).Select(point => new[] { point[0], point[1] }).ToArray();
            double[] topLeft = corners.OrderBy(point => point[0] + point[1]).First();
            double[] bottomRight = corners.OrderByDescending(point => point[0] + point[1]).First();
            double[] topRight = corners.OrderByDescending(point => point[0] - point[1]).First();
            double[] bottomLeft = corners.OrderBy(point => point[0] - point[1]).First();

            shape.Points = new List<double[]>
            {
                topLeft,
                topRight,
                bottomRight,
                bottomLeft
            };
            shape.Direction = Math.Atan2(topRight[1] - topLeft[1], topRight[0] - topLeft[0]);
        }
    }
}
