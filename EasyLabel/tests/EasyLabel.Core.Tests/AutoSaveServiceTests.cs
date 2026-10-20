using System;
using System.IO;
using System.Threading.Tasks;
using EasyLabel.App.Shell;
using EasyLabel.Core.Commands;
using EasyLabel.Core.Model;
using Xunit;

namespace EasyLabel.Core.Tests
{
    public sealed class AutoSaveServiceTests
    {
        [Fact]
        public async Task RecoverySurvivesNewServiceAndKeepsSameNamesSeparate()
        {
            string root = Path.Combine(Path.GetTempPath(), "EasyLabel", Guid.NewGuid().ToString("N"));
            string first = Path.Combine(root, "a", "image.png");
            string second = Path.Combine(root, "b", "image.png");
            AutoSaveService service = new AutoSaveService();
            LabelMeDocument document = new LabelMeDocument { ImageWidth = 400, ImageHeight = 300 };
            EasyLabelSidecar sidecar = new EasyLabelSidecar();
            new DocumentEditor(document, sidecar).Add(new[] { new LabelShape { Label = "model" } },
                new[] { new ShapeProvenance { Source = "model", Confidence = .9, Model = "test.pt" } }, "Detect");
            await service.SaveAsync(root, first, document, sidecar);
            await service.SaveAsync(root, second, new LabelMeDocument(), new EasyLabelSidecar());
            AutoSaveService reopened = new AutoSaveService();
            var recovery = reopened.ReadNewer(root, first, Path.ChangeExtension(first, ".json"));
            Assert.NotNull(recovery);
            Assert.Equal("model", recovery.Document.Shapes[0].Label);
            Assert.Equal(.9, recovery.Sidecar.Shapes[0].Confidence);
            var other = reopened.ReadNewer(root, second, Path.ChangeExtension(second, ".json"));
            Assert.NotNull(other);
            Assert.Empty(other.Document.Shapes);
            Assert.Throws<ArgumentException>(() => service.GetPath(root, Path.Combine(root, "..", "outside.png")));
        }

        [Fact]
        public async Task ClearWaitsForPendingSaveAndOlderRecoveryIsIgnored()
        {
            string root = Path.Combine(Path.GetTempPath(), "EasyLabel", Guid.NewGuid().ToString("N"));
            string image = Path.Combine(root, "image.png");
            string label = Path.ChangeExtension(image, ".json");
            AutoSaveService service = new AutoSaveService();
            Task save = service.SaveAsync(root, image, new LabelMeDocument(), new EasyLabelSidecar());
            Task clear = service.ClearAsync(root, image);
            await Task.WhenAll(save, clear);
            Assert.Null(service.ReadNewer(root, image, label));
            await service.SaveAsync(root, image, new LabelMeDocument(), new EasyLabelSidecar());
            File.WriteAllText(label, "{}");
            File.SetLastWriteTimeUtc(label, DateTime.UtcNow.AddMinutes(1));
            Assert.Null(service.ReadNewer(root, image, label));
        }
    }
}
