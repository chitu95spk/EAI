namespace EasyLabel.Core.Datasets
{
    /// <summary>
    /// A read-only import result. The original JSON text is retained for diagnostics only;
    /// this type never writes to the caller's document or filesystem.
    /// </summary>
    public sealed class DatasetImportPreview
    {
        internal DatasetImportPreview(string sourceJson, AnnotationDocument document, DatasetValidationResult validation)
        {
            SourceJson = sourceJson ?? string.Empty;
            Document = document;
            Validation = validation;
        }

        public string SourceJson { get; private set; }

        public AnnotationDocument Document { get; private set; }

        public DatasetValidationResult Validation { get; private set; }

        public bool IsUsable
        {
            get { return Document != null && Validation != null && Validation.IsValid; }
        }
    }
}
