using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Model;
using Newtonsoft.Json;

namespace EasyLabel.SegAI
{
    public sealed partial class SegAiSession
    {
        /// <summary>Discovers annotation classes using the production LabelMe reader; annotation bytes are never written.</summary>
        public IReadOnlyList<string> SynchronizeClassTableFromAnnotations(out IReadOnlyList<string> skipped)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in EnumerateAnnotationJsons().OrderBy(p => p, StringComparer.Ordinal))
                {
                    LabelMeDocument document;
                    try { document = _labelMe.Read(path); }
                    catch (JsonException ex)
                    {
                        throw new InvalidDataException("The LabelMe JSON is not valid: " + path + " (" + ex.Message + ")", ex);
                    }
                    if (document.Shapes == null) continue;
                    foreach (LabelShape shape in document.Shapes)
                    {
                        if (shape == null || string.IsNullOrWhiteSpace(shape.Label)) continue;
                        string name = shape.Label.Trim();
                        if (seen.Add(name)) names.Add(name);
                    }
                }

                SegAiClassTable current = _map.Table;
                var extended = ClassTableSyncRules.Extend(current, names, out skipped);
                if (extended == null) return Array.Empty<string>();
                string[] added = extended.Classes.Skip(current.Classes.Count).Select(c => c.Name).ToArray();
                string advancedPath = Path.Combine(_store.EasyAiDir, "advanced.json");
                bool hadAdvanced = File.Exists(advancedPath);
                bool hadClasses = File.Exists(Paths.ClassesFile);
                byte[] advanced = hadAdvanced ? File.ReadAllBytes(advancedPath) : Array.Empty<byte>();
                byte[] classes = hadClasses ? File.ReadAllBytes(Paths.ClassesFile) : Array.Empty<byte>();
                SegClassMap previous = _map;
                try
                {
                    // Append-only changes produce no rename plans, so SaveClassTable never writes annotations here.
                    SaveClassTable(extended);
                }
                catch (Exception saveError) when (saveError is IOException || saveError is UnauthorizedAccessException
                    || saveError is ArgumentException || saveError is JsonException || saveError is InvalidOperationException)
                {
                    _map = previous;
                    var failures = new List<Exception> { saveError };
                    try { RestoreClassSyncFile(advancedPath, hadAdvanced, advanced); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { failures.Add(ex); }
                    try { RestoreClassSyncFile(Paths.ClassesFile, hadClasses, classes); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { failures.Add(ex); }
                    if (failures.Count > 1)
                        throw new IOException("Class table save failed and its files could not be fully restored.", new AggregateException(failures));
                    throw;
                }
                return added;
            }
        }

        private static void RestoreClassSyncFile(string path, bool existed, byte[] before)
        {
            if (!existed)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(before))
            {
                string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Class table path has no directory.");
                string temporary = Path.Combine(directory, ".class-sync-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temporary, before);
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }
    }
}
