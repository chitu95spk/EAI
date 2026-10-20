using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EasyLabel.Core.Datasets
{
    public enum DatasetValidationSeverity
    {
        Error = 0
    }

    public sealed class DatasetValidationIssue
    {
        internal DatasetValidationIssue(string code, DatasetValidationSeverity severity, int? sourceIndex, string jsonPath, string message)
        {
            Code = code;
            Severity = severity;
            SourceIndex = sourceIndex;
            JsonPath = jsonPath;
            Message = message;
        }

        public string Code { get; private set; }

        public DatasetValidationSeverity Severity { get; private set; }

        public int? SourceIndex { get; private set; }

        public string JsonPath { get; private set; }

        public string Message { get; private set; }
    }

    public sealed class DatasetValidationResult
    {
        private readonly List<DatasetValidationIssue> _issues = new List<DatasetValidationIssue>();
        private readonly IReadOnlyList<DatasetValidationIssue> _readOnlyIssues;

        internal DatasetValidationResult()
        {
            _readOnlyIssues = new ReadOnlyCollection<DatasetValidationIssue>(_issues);
        }

        public IReadOnlyList<DatasetValidationIssue> Issues
        {
            get { return _readOnlyIssues; }
        }

        public bool IsValid
        {
            get { return _issues.Count == 0; }
        }

        internal void AddDocumentIssue(string code, string jsonPath, string message)
        {
            Add(code, null, jsonPath, message);
        }

        internal void AddShapeIssue(string code, int sourceIndex, string jsonPath, string message)
        {
            Add(code, sourceIndex, jsonPath, message);
        }

        private void Add(string code, int? sourceIndex, string jsonPath, string message)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("A validation issue code is required.", nameof(code));
            }

            _issues.Add(new DatasetValidationIssue(code, DatasetValidationSeverity.Error, sourceIndex, jsonPath ?? string.Empty, message ?? string.Empty));
        }
    }
}
