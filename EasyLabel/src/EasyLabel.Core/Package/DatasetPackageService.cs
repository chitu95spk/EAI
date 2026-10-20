using System.IO.Compression;
using System.Security.Cryptography;
using EasyLabel.Core.Convert;
using EasyLabel.Core.Io;
using Newtonsoft.Json;

namespace EasyLabel.Core.Package
{
    public sealed class DatasetPackageService
    {
        private static readonly DateTimeOffset FixedZipTimestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented
        };

        private readonly UltralyticsExportService _exportService;

        public DatasetPackageService()
            : this(new UltralyticsExportService())
        {
        }

        public DatasetPackageService(UltralyticsExportService exportService)
        {
            _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
        }

        public DatasetPackageResult Package(DatasetPackageRequest request)
        {
            ValidatePackageRequest(request);

            string projectRoot = Path.GetFullPath(request.ProjectRoot);
            string packagePath = Path.GetFullPath(request.PackagePath);
            string packageDirectory = Path.GetDirectoryName(packagePath) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(packageDirectory);

            string workspace = CreateTemporaryWorkspace();
            string exportRoot = Path.Combine(workspace, "dataset");
            string tempPackagePath = Path.Combine(packageDirectory, "." + Path.GetFileName(packagePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                UltralyticsExportResult exportResult = _exportService.Export(new UltralyticsExportRequest
                {
                    ProjectRoot = projectRoot,
                    Paths = request.Paths,
                    OutputDirectory = exportRoot,
                    LabelFormat = request.LabelFormat,
                    TrainRatio = request.TrainRatio,
                    ValRatio = request.ValRatio,
                    TestRatio = request.TestRatio,
                    Seed = request.Seed,
                    StratifyByClass = request.StratifyByClass
                });

                CopyClasses(request.Paths ?? ProjectPaths.Legacy(projectRoot), exportRoot);
                NormalizeDataYaml(exportResult.DataYamlPath);
                WritePackageMetadata(exportRoot, request, projectRoot, exportResult);

                DatasetPackageManifest manifest = CreateManifest(exportRoot, request, projectRoot, exportResult);
                string manifestPath = Path.Combine(exportRoot, "manifest.json");
                AtomicFile.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, JsonSettings) + Environment.NewLine);

                CreateZip(exportRoot, tempPackagePath);
                ReplacePackage(tempPackagePath, packagePath);

                return new DatasetPackageResult
                {
                    PackagePath = packagePath,
                    FileCount = manifest.Files.Count + 1,
                    TotalBytes = manifest.Files.Sum(file => file.Size) + new FileInfo(manifestPath).Length
                };
            }
            finally
            {
                TryDeleteFile(tempPackagePath);
                TryDeleteDirectory(workspace);
            }
        }

        public DatasetPackageRestoreResult OpenPackage(string packagePath, string restoreDirectory)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            {
                throw new FileNotFoundException("Dataset package does not exist.", packagePath);
            }

            if (string.IsNullOrWhiteSpace(restoreDirectory))
            {
                throw new ArgumentException("Restore folder is required.", nameof(restoreDirectory));
            }

            string fullRestoreDirectory = Path.GetFullPath(restoreDirectory);
            Directory.CreateDirectory(fullRestoreDirectory);

            using (ZipArchive archive = ZipFile.OpenRead(Path.GetFullPath(packagePath)))
            {
                foreach (ZipArchiveEntry entry in archive.Entries.OrderBy(item => item.FullName, StringComparer.Ordinal))
                {
                    ExtractEntry(entry, fullRestoreDirectory);
                }
            }

            return VerifyManifest(fullRestoreDirectory);
        }

        public DatasetPackageRestoreResult VerifyManifest(string datasetRoot)
        {
            if (string.IsNullOrWhiteSpace(datasetRoot))
            {
                throw new ArgumentException("Dataset folder is required.", nameof(datasetRoot));
            }

            string fullRoot = Path.GetFullPath(datasetRoot);
            string manifestPath = Path.Combine(fullRoot, "manifest.json");
            DatasetPackageRestoreResult result = new DatasetPackageRestoreResult
            {
                RestoreDirectory = fullRoot,
                ManifestPath = manifestPath
            };

            if (!File.Exists(manifestPath))
            {
                result.Errors.Add("manifest.json is missing.");
                result.ManifestValid = false;
                return result;
            }

            DatasetPackageManifest? manifest = JsonConvert.DeserializeObject<DatasetPackageManifest>(File.ReadAllText(manifestPath));
            if (manifest == null)
            {
                result.Errors.Add("manifest.json could not be read.");
                result.ManifestValid = false;
                return result;
            }

            foreach (DatasetPackageManifestFile entry in manifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                result.FileCount++;
                if (!TryGetSafeChildPath(fullRoot, entry.Path, out string filePath))
                {
                    result.Errors.Add("Manifest path escapes the dataset folder: " + entry.Path);
                    continue;
                }

                if (!File.Exists(filePath))
                {
                    result.Errors.Add("Missing file: " + entry.Path);
                    continue;
                }

                FileInfo info = new FileInfo(filePath);
                if (info.Length != entry.Size)
                {
                    result.Errors.Add("Size mismatch: " + entry.Path);
                    continue;
                }

                string actualHash = ComputeSha256(filePath);
                if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.Errors.Add("SHA-256 mismatch: " + entry.Path);
                }
            }

            result.ManifestValid = result.Errors.Count == 0;
            return result;
        }

        private static void ValidatePackageRequest(DatasetPackageRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.ProjectRoot) || !Directory.Exists(request.ProjectRoot))
            {
                throw new DirectoryNotFoundException("Project folder does not exist: " + request.ProjectRoot);
            }

            if (string.IsNullOrWhiteSpace(request.PackagePath))
            {
                throw new ArgumentException("Package path is required.", nameof(request));
            }

            string extension = Path.GetExtension(request.PackagePath);
            if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Dataset packages must use the .zip extension.", nameof(request));
            }
        }

        private static void CopyClasses(ProjectPaths paths, string exportRoot)
        {
            string sourcePath = paths.ClassesFile;
            string targetPath = Path.Combine(exportRoot, "classes.txt");
            File.Copy(sourcePath, targetPath, false);
        }

        private static void NormalizeDataYaml(string dataYamlPath)
        {
            List<string> lines = File.ReadAllLines(dataYamlPath).ToList();
            if (lines.Count == 0)
            {
                lines.Add("path: '.'");
            }
            else
            {
                lines[0] = "path: '.'";
            }

            AtomicFile.WriteAllText(dataYamlPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }

        private static void WritePackageMetadata(string exportRoot, DatasetPackageRequest request, string projectRoot, UltralyticsExportResult exportResult)
        {
            DatasetPackageSourceSummary summary = CreateSourceSummary(request, projectRoot, exportResult);
            AtomicFile.WriteAllText(
                Path.Combine(exportRoot, "split-config.json"),
                JsonConvert.SerializeObject(summary, JsonSettings) + Environment.NewLine);

            string augmentConfig = string.IsNullOrWhiteSpace(request.AugmentConfigJson)
                ? "{}"
                : request.AugmentConfigJson.Trim();
            AtomicFile.WriteAllText(Path.Combine(exportRoot, "augment-config.json"), augmentConfig + Environment.NewLine);

            string changelog = string.IsNullOrWhiteSpace(request.Changelog)
                ? "Packaged dataset snapshot."
                : request.Changelog.Trim();
            AtomicFile.WriteAllText(Path.Combine(exportRoot, "CHANGELOG.txt"), changelog + Environment.NewLine);
        }

        private static DatasetPackageManifest CreateManifest(
            string exportRoot,
            DatasetPackageRequest request,
            string projectRoot,
            UltralyticsExportResult exportResult)
        {
            DatasetPackageManifest manifest = new DatasetPackageManifest
            {
                CreatedAtUtc = request.CreatedAtUtc ?? DateTime.UtcNow,
                SourceSummary = CreateSourceSummary(request, projectRoot, exportResult)
            };

            foreach (string path in EnumeratePackageFiles(exportRoot))
            {
                manifest.Files.Add(CreateManifestFile(exportRoot, path));
            }

            return manifest;
        }

        private static DatasetPackageSourceSummary CreateSourceSummary(DatasetPackageRequest request, string projectRoot, UltralyticsExportResult exportResult)
        {
            DatasetPackageSourceSummary summary = new DatasetPackageSourceSummary
            {
                SourceFolderName = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                LabelFormat = request.LabelFormat,
                TrainRatio = request.TrainRatio,
                ValRatio = request.ValRatio,
                TestRatio = request.TestRatio,
                Seed = request.Seed,
                StratifyByClass = request.StratifyByClass,
                TotalImages = exportResult.TotalImages,
                TotalLabels = exportResult.TotalLabels
            };

            foreach (UltralyticsExportSplitResult split in exportResult.Splits)
            {
                summary.Splits.Add(new DatasetPackageSplitSummary
                {
                    Name = split.Name,
                    Images = split.ImageCount,
                    Labels = split.LabelCount
                });
            }

            return summary;
        }

        private static IEnumerable<string> EnumeratePackageFiles(string root)
        {
            return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => ToRelativePath(root, path), StringComparer.Ordinal);
        }

        private static DatasetPackageManifestFile CreateManifestFile(string root, string path)
        {
            FileInfo info = new FileInfo(path);
            return new DatasetPackageManifestFile
            {
                Path = ToRelativePath(root, path),
                Size = info.Length,
                Sha256 = ComputeSha256(path)
            };
        }

        private static void CreateZip(string sourceRoot, string packagePath)
        {
            using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                foreach (string directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories)
                             .OrderBy(path => ToRelativePath(sourceRoot, path), StringComparer.Ordinal))
                {
                    string entryName = ToRelativePath(sourceRoot, directory).TrimEnd('/') + "/";
                    ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    entry.LastWriteTime = FixedZipTimestamp;
                }

                foreach (string path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                             .OrderBy(path => ToRelativePath(sourceRoot, path), StringComparer.Ordinal))
                {
                    string entryName = ToRelativePath(sourceRoot, path);
                    ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    entry.LastWriteTime = FixedZipTimestamp;

                    using (Stream entryStream = entry.Open())
                    using (FileStream fileStream = File.OpenRead(path))
                    {
                        fileStream.CopyTo(entryStream);
                    }
                }
            }
        }

        private static void ExtractEntry(ZipArchiveEntry entry, string restoreDirectory)
        {
            if (!TryGetSafeChildPath(restoreDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar), out string targetPath))
            {
                throw new InvalidDataException("Package entry escapes the restore folder: " + entry.FullName);
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                return;
            }

            string? directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            entry.ExtractToFile(targetPath, true);
            File.SetLastWriteTimeUtc(targetPath, entry.LastWriteTime.UtcDateTime);
        }

        private static void ReplacePackage(string tempPath, string packagePath)
        {
            if (File.Exists(packagePath))
            {
                File.Replace(tempPath, packagePath, null);
            }
            else
            {
                File.Move(tempPath, packagePath);
            }
        }

        private static string CreateTemporaryWorkspace()
        {
            string root = Path.Combine(Path.GetTempPath(), "EasyLabel", "DatasetPackage", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static bool TryGetSafeChildPath(string root, string relativePath, out string fullPath)
        {
            fullPath = string.Empty;
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            {
                return false;
            }

            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }

        private static string ToRelativePath(string root, string path)
        {
            return Path.GetRelativePath(root, path).Replace('\\', '/');
        }

        private static string ComputeSha256(string path)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string packageTempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EasyLabel", "DatasetPackage"))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(packageTempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
