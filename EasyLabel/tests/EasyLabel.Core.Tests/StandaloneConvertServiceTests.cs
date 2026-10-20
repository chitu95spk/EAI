using EasyLabel.Core.Convert;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Tests
{
    public sealed class StandaloneConvertServiceTests
    {
        [Fact]
        public void ConvertsLabelMeJsonToYoloTextFolder()
        {
            string input = CreateInputFolder();
            string output = CreateOutputFolder();
            StandaloneConvertService service = new StandaloneConvertService(new FakeImageInfoReader());

            StandaloneConvertResult result = service.Convert(new StandaloneConvertRequest
            {
                InputDirectory = input,
                OutputDirectory = output,
                SourceFormat = LabelConvertFormat.LabelMeJson,
                TargetFormat = LabelConvertFormat.YoloDetect
            });

            Assert.Equal(1, result.FilesScanned);
            Assert.Equal(1, result.FilesWritten);
            Assert.True(File.Exists(Path.Combine(output, "sample.txt")));
            Assert.Equal("box", File.ReadAllLines(Path.Combine(output, "classes.txt"))[0]);
            string line = File.ReadAllLines(Path.Combine(output, "sample.txt"))[0];
            Assert.StartsWith("0 ", line, StringComparison.Ordinal);
        }

        [Fact]
        public void ConvertsYoloTextToLabelMeJsonWithInjectedImageSize()
        {
            string input = Path.Combine(Path.GetTempPath(), "EasyLabel", "StandaloneConvert", Guid.NewGuid().ToString("N"));
            string output = CreateOutputFolder();
            Directory.CreateDirectory(input);
            File.WriteAllLines(Path.Combine(input, "classes.txt"), new[] { "box" });
            File.WriteAllText(Path.Combine(input, "sample.txt"), "0 0.2 0.3 0.1 0.2");
            StandaloneConvertService service = new StandaloneConvertService(new FakeImageInfoReader(100, 80));

            StandaloneConvertResult result = service.Convert(new StandaloneConvertRequest
            {
                InputDirectory = input,
                OutputDirectory = output,
                SourceFormat = LabelConvertFormat.YoloDetect,
                TargetFormat = LabelConvertFormat.LabelMeJson
            });

            Assert.Equal(1, result.FilesScanned);
            Assert.Equal(1, result.FilesWritten);
            LabelMeDocument document = new LabelMeIO().Read(Path.Combine(output, "sample.json"));
            Assert.Equal("sample.jpg", document.ImagePath);
            Assert.Equal("rectangle", document.Shapes[0].ShapeType);
            Assert.Equal(15, document.Shapes[0].Points[0][0], 6);
            Assert.Equal(16, document.Shapes[0].Points[0][1], 6);
        }

        [Fact]
        public void ConvertsPascalVocToLabelMeJson()
        {
            string input = Path.Combine(Path.GetTempPath(), "EasyLabel", "StandaloneConvert", Guid.NewGuid().ToString("N"));
            string output = CreateOutputFolder();
            Directory.CreateDirectory(input);
            File.WriteAllLines(Path.Combine(input, "classes.txt"), new[] { "box" });
            File.WriteAllText(
                Path.Combine(input, "sample.xml"),
                "<annotation><filename>sample.jpg</filename><size><width>100</width><height>80</height><depth>3</depth></size><object><name>box</name><bndbox><xmin>1</xmin><ymin>2</ymin><xmax>11</xmax><ymax>22</ymax></bndbox></object></annotation>");
            StandaloneConvertService service = new StandaloneConvertService(new FakeImageInfoReader());

            StandaloneConvertResult result = service.Convert(new StandaloneConvertRequest
            {
                InputDirectory = input,
                OutputDirectory = output,
                SourceFormat = LabelConvertFormat.PascalVoc,
                TargetFormat = LabelConvertFormat.LabelMeJson
            });

            Assert.Equal(1, result.FilesScanned);
            Assert.Equal(1, result.FilesWritten);
            LabelMeDocument document = new LabelMeIO().Read(Path.Combine(output, "sample.json"));
            Assert.Equal("sample.jpg", document.ImagePath);
            Assert.Equal(100, document.ImageWidth);
            Assert.Equal(80, document.ImageHeight);
            Assert.Equal(11, document.Shapes[0].Points[1][0], 6);
        }

        private static string CreateInputFolder()
        {
            string input = Path.Combine(Path.GetTempPath(), "EasyLabel", "StandaloneConvert", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(input);
            File.WriteAllLines(Path.Combine(input, "classes.txt"), new[] { "box" });
            LabelMeDocument document = new LabelMeDocument
            {
                ImagePath = "sample.jpg",
                ImageWidth = 100,
                ImageHeight = 80
            };
            document.Shapes.Add(new LabelShape
            {
                Label = "box",
                ShapeType = "rectangle",
                Points =
                {
                    new[] { 10.0, 20.0 },
                    new[] { 30.0, 50.0 }
                }
            });
            new LabelMeIO().Write(document, Path.Combine(input, "sample.json"));
            return input;
        }

        private static string CreateOutputFolder()
        {
            string output = Path.Combine(Path.GetTempPath(), "EasyLabel", "StandaloneConvert", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            return output;
        }

        private sealed class FakeImageInfoReader : IConvertImageInfoReader
        {
            private readonly int _width;
            private readonly int _height;

            public FakeImageInfoReader()
                : this(0, 0)
            {
            }

            public FakeImageInfoReader(int width, int height)
            {
                _width = width;
                _height = height;
            }

            public bool TryRead(string annotationPath, out ImageInfo imageInfo)
            {
                imageInfo = new ImageInfo(Path.ChangeExtension(annotationPath, ".jpg"), _width, _height);
                return _width > 0 && _height > 0;
            }
        }
    }
}
