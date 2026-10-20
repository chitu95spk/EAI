using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EasyLabel.Core;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using Newtonsoft.Json;

namespace EasyLabel.App.Shell
{
    public sealed class AutoSaveService
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public sealed class Recovery
        {
            public LabelMeDocument Document { get; set; } = new LabelMeDocument();
            public EasyLabelSidecar Sidecar { get; set; } = new EasyLabelSidecar();
        }

        public string GetPath(string projectRoot, string imagePath)
        {
            return GetPath(ProjectPaths.Legacy(projectRoot), imagePath);
        }

        public string GetPath(ProjectPaths paths, string imagePath)
        {
            string relative = Path.GetRelativePath(paths.ImageRoot, Path.GetFullPath(imagePath));
            if (Path.IsPathRooted(relative) || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Recovery image must belong to the project.");
            return paths.RecoveryPath(relative);
        }

        public async Task SaveAsync(string root, string imagePath, LabelMeDocument document, EasyLabelSidecar sidecar)
        {
            await SaveAsync(ProjectPaths.Legacy(root), imagePath, document, sidecar);
        }

        public async Task SaveAsync(ProjectPaths paths, string imagePath, LabelMeDocument document, EasyLabelSidecar sidecar)
        {
            string path = GetPath(paths, imagePath);
            string json = JsonConvert.SerializeObject(new Recovery { Document = document, Sidecar = sidecar });
            await _gate.WaitAsync();
            try { await Task.Run(() => AtomicFile.WriteAllText(path, json)); }
            finally { _gate.Release(); }
        }

        [return: System.Diagnostics.CodeAnalysis.MaybeNull]
        public Recovery ReadNewer(string root, string imagePath, string labelPath)
        {
            return ReadNewer(ProjectPaths.Legacy(root), imagePath, labelPath);
        }

        [return: System.Diagnostics.CodeAnalysis.MaybeNull]
        public Recovery ReadNewer(ProjectPaths paths, string imagePath, string labelPath)
        {
            string path = GetPath(paths, imagePath);
            if (!File.Exists(path) || (File.Exists(labelPath)
                && File.GetLastWriteTimeUtc(path) <= File.GetLastWriteTimeUtc(labelPath))) return null;
            return JsonConvert.DeserializeObject<Recovery>(File.ReadAllText(path))
                ?? throw new IOException("The recovery file is empty.");
        }

        public async Task ClearAsync(string root, string imagePath)
        {
            await ClearAsync(ProjectPaths.Legacy(root), imagePath);
        }

        public async Task ClearAsync(ProjectPaths paths, string imagePath)
        {
            string path = GetPath(paths, imagePath);
            await _gate.WaitAsync();
            try { await Task.Run(() => File.Delete(path)); }
            finally { _gate.Release(); }
        }
    }
}
