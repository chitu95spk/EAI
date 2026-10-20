using EasyLabel.Core.Batch;
using EasyLabel.Core.Model;

namespace EasyLabel.App.Dialogs
{
    public sealed class ClassRemapForm : Form
    {
        private readonly Project _project;
        private readonly ClassRemapService _service;
        private readonly ComboBox _operationComboBox = new ComboBox();
        private readonly ComboBox _sourceComboBox = new ComboBox();
        private readonly ComboBox _targetComboBox = new ComboBox();
        private readonly TextBox _previewTextBox = new TextBox();
        private readonly Button _previewButton = new Button();
        private readonly Button _applyButton = new Button();
        private readonly Button _cancelButton = new Button();

        public ClassRemapForm(Project project, ClassRemapService service)
        {
            _project = project ?? throw new ArgumentNullException(nameof(project));
            _service = service ?? throw new ArgumentNullException(nameof(service));

            Text = "Class Remap";
            Name = "ClassRemapForm";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(520, 360);
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            InitializeLayout();
            WireEvents();
            UpdateTargetState();
            ResetPreview();
        }

        private void InitializeLayout()
        {
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

            _operationComboBox.Name = "ClassRemapOperationComboBox";
            _operationComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _operationComboBox.Items.AddRange(new object[] { "Rename", "Merge", "Delete" });
            _operationComboBox.SelectedIndex = 0;

            _sourceComboBox.Name = "ClassRemapSourceComboBox";
            _sourceComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _sourceComboBox.Items.AddRange(_project.Classes.Cast<object>().ToArray());
            if (_sourceComboBox.Items.Count > 0)
            {
                _sourceComboBox.SelectedIndex = 0;
            }

            _targetComboBox.Name = "ClassRemapTargetComboBox";
            _targetComboBox.DropDownStyle = ComboBoxStyle.DropDown;
            _targetComboBox.Items.AddRange(_project.Classes.Cast<object>().ToArray());

            _previewTextBox.Name = "ClassRemapPreviewTextBox";
            _previewTextBox.Dock = DockStyle.Fill;
            _previewTextBox.Multiline = true;
            _previewTextBox.ReadOnly = true;
            _previewTextBox.ScrollBars = ScrollBars.Vertical;

            _previewButton.Name = "ClassRemapPreviewButton";
            _previewButton.Text = "Preview";
            _previewButton.Width = 96;

            _applyButton.Name = "ClassRemapApplyButton";
            _applyButton.Text = "Apply";
            _applyButton.Width = 96;
            _applyButton.Enabled = false;

            _cancelButton.Name = "ClassRemapCancelButton";
            _cancelButton.Text = "Cancel";
            _cancelButton.Width = 96;
            _cancelButton.DialogResult = DialogResult.Cancel;

            layout.Controls.Add(CreateLabel("Operation"), 0, 0);
            layout.Controls.Add(_operationComboBox, 1, 0);
            layout.Controls.Add(CreateLabel("Source"), 0, 1);
            layout.Controls.Add(_sourceComboBox, 1, 1);
            layout.Controls.Add(CreateLabel("Target"), 0, 2);
            layout.Controls.Add(_targetComboBox, 1, 2);
            layout.Controls.Add(_previewTextBox, 0, 3);
            layout.SetColumnSpan(_previewTextBox, 2);

            FlowLayoutPanel buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 12, 0, 0)
            };
            buttonPanel.Controls.Add(_cancelButton);
            buttonPanel.Controls.Add(_applyButton);
            buttonPanel.Controls.Add(_previewButton);
            layout.Controls.Add(buttonPanel, 0, 4);
            layout.SetColumnSpan(buttonPanel, 2);

            Controls.Add(layout);
            AcceptButton = _previewButton;
            CancelButton = _cancelButton;
        }

        private static Label CreateLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private void WireEvents()
        {
            _operationComboBox.SelectedIndexChanged += OperationComboBox_SelectedIndexChanged;
            _sourceComboBox.SelectedIndexChanged += AnyInputChanged;
            _targetComboBox.SelectedIndexChanged += AnyInputChanged;
            _targetComboBox.TextChanged += AnyInputChanged;
            _previewButton.Click += PreviewButton_Click;
            _applyButton.Click += ApplyButton_Click;
        }

        private void OperationComboBox_SelectedIndexChanged(object? sender, EventArgs e)
        {
            UpdateTargetState();
            ResetPreview();
        }

        private void AnyInputChanged(object? sender, EventArgs e)
        {
            ResetPreview();
        }

        private void PreviewButton_Click(object? sender, EventArgs e)
        {
            try
            {
                ClassRemapResult result = _service.Preview(CreateRequest());
                _previewTextBox.Text = FormatResult(result, false);
                _applyButton.Enabled = true;
                AcceptButton = _applyButton;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                _previewTextBox.Text = ex.Message;
                _applyButton.Enabled = false;
            }
        }

        private void ApplyButton_Click(object? sender, EventArgs e)
        {
            try
            {
                ClassRemapResult result = _service.Apply(CreateRequest());
                _previewTextBox.Text = FormatResult(result, true);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                _previewTextBox.Text = ex.Message;
                _applyButton.Enabled = false;
            }
        }

        private ClassRemapRequest CreateRequest()
        {
            return new ClassRemapRequest
            {
                ProjectRoot = _project.RootDir,
                Paths = _project.Paths,
                Operation = GetSelectedOperation(),
                SourceClass = (_sourceComboBox.Text ?? string.Empty).Trim(),
                TargetClass = (_targetComboBox.Text ?? string.Empty).Trim()
            };
        }

        private ClassRemapOperation GetSelectedOperation()
        {
            if (_operationComboBox.SelectedIndex == 1)
            {
                return ClassRemapOperation.Merge;
            }

            if (_operationComboBox.SelectedIndex == 2)
            {
                return ClassRemapOperation.Delete;
            }

            return ClassRemapOperation.Rename;
        }

        private void UpdateTargetState()
        {
            bool needsTarget = GetSelectedOperation() != ClassRemapOperation.Delete;
            _targetComboBox.Enabled = needsTarget;
            if (!needsTarget)
            {
                _targetComboBox.Text = string.Empty;
            }
        }

        private void ResetPreview()
        {
            _applyButton.Enabled = false;
            AcceptButton = _previewButton;
            _previewTextBox.Text = "Preview has not run.";
        }

        private static string FormatResult(ClassRemapResult result, bool applied)
        {
            string status = applied ? "Applied" : "Preview";
            return string.Format(
                "{0}{1}Files scanned: {2}{1}Files touched: {3}{1}Shapes rewritten: {4}{1}Shapes deleted: {5}{1}Classes changed: {6}",
                status,
                Environment.NewLine,
                result.FilesScanned,
                result.FilesChanged,
                result.ShapesRewritten,
                result.ShapesDeleted,
                result.ClassesChanged ? "yes" : "no");
        }
    }
}
