using EasyLabel.Core.Convert;
using EasyLabel.Core.Model;

namespace EasyLabel.App.Dialogs
{
    public sealed class UltralyticsExportForm : Form
    {
        private readonly Project _project;
        private readonly UltralyticsExportService _service;
        private readonly TextBox _outputTextBox = new TextBox();
        private readonly Button _browseButton = new Button();
        private readonly ComboBox _formatComboBox = new ComboBox();
        private readonly NumericUpDown _trainNumeric = new NumericUpDown();
        private readonly NumericUpDown _valNumeric = new NumericUpDown();
        private readonly NumericUpDown _testNumeric = new NumericUpDown();
        private readonly NumericUpDown _seedNumeric = new NumericUpDown();
        private readonly CheckBox _stratifyCheckBox = new CheckBox();
        private readonly TextBox _resultTextBox = new TextBox();
        private readonly Button _exportButton = new Button();
        private readonly Button _closeButton = new Button();

        public UltralyticsExportForm(Project project, UltralyticsExportService service)
        {
            _project = project ?? throw new ArgumentNullException(nameof(project));
            _service = service ?? throw new ArgumentNullException(nameof(service));

            Text = "Export Ultralytics";
            Name = "UltralyticsExportForm";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 360);
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            InitializeLayout();
            WireEvents();
        }

        private void InitializeLayout()
        {
            TableLayoutPanel layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 9,
                Padding = new Padding(12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

            _outputTextBox.Name = "UltralyticsExportOutputTextBox";
            _outputTextBox.Dock = DockStyle.Fill;
            _browseButton.Name = "UltralyticsExportBrowseButton";
            _browseButton.Text = "Browse";
            _browseButton.Width = 80;

            _formatComboBox.Name = "UltralyticsExportFormatComboBox";
            _formatComboBox.Dock = DockStyle.Fill;
            _formatComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _formatComboBox.Items.AddRange(new object[]
            {
                LabelConvertFormat.YoloDetect,
                LabelConvertFormat.YoloObb,
                LabelConvertFormat.YoloSegment
            });
            _formatComboBox.SelectedItem = LabelConvertFormat.YoloDetect;

            ConfigureRatio(_trainNumeric, 0.8M);
            ConfigureRatio(_valNumeric, 0.1M);
            ConfigureRatio(_testNumeric, 0.1M);
            _seedNumeric.Name = "UltralyticsExportSeedNumeric";
            _seedNumeric.Minimum = 0;
            _seedNumeric.Maximum = int.MaxValue;
            _seedNumeric.Value = 1337;
            _seedNumeric.Width = 120;
            _stratifyCheckBox.Name = "UltralyticsExportStratifyCheckBox";
            _stratifyCheckBox.Text = "Stratify By Class";
            _stratifyCheckBox.AutoSize = true;

            _resultTextBox.Name = "UltralyticsExportResultTextBox";
            _resultTextBox.Dock = DockStyle.Fill;
            _resultTextBox.Multiline = true;
            _resultTextBox.ReadOnly = true;
            _resultTextBox.ScrollBars = ScrollBars.Vertical;

            _exportButton.Name = "UltralyticsExportRunButton";
            _exportButton.Text = "Export";
            _exportButton.Width = 96;
            _closeButton.Name = "UltralyticsExportCloseButton";
            _closeButton.Text = "Close";
            _closeButton.Width = 96;
            _closeButton.DialogResult = DialogResult.Cancel;

            layout.Controls.Add(CreateLabel("Output"), 0, 0);
            layout.Controls.Add(_outputTextBox, 1, 0);
            layout.Controls.Add(_browseButton, 2, 0);
            layout.Controls.Add(CreateLabel("Format"), 0, 1);
            layout.Controls.Add(_formatComboBox, 1, 1);
            layout.SetColumnSpan(_formatComboBox, 2);
            layout.Controls.Add(CreateLabel("Train"), 0, 2);
            layout.Controls.Add(_trainNumeric, 1, 2);
            layout.SetColumnSpan(_trainNumeric, 2);
            layout.Controls.Add(CreateLabel("Val"), 0, 3);
            layout.Controls.Add(_valNumeric, 1, 3);
            layout.SetColumnSpan(_valNumeric, 2);
            layout.Controls.Add(CreateLabel("Test"), 0, 4);
            layout.Controls.Add(_testNumeric, 1, 4);
            layout.SetColumnSpan(_testNumeric, 2);
            layout.Controls.Add(CreateLabel("Seed"), 0, 5);
            layout.Controls.Add(_seedNumeric, 1, 5);
            layout.SetColumnSpan(_seedNumeric, 2);
            layout.Controls.Add(CreateLabel("Split"), 0, 6);
            layout.Controls.Add(_stratifyCheckBox, 1, 6);
            layout.SetColumnSpan(_stratifyCheckBox, 2);
            layout.Controls.Add(_resultTextBox, 0, 7);
            layout.SetColumnSpan(_resultTextBox, 3);

            FlowLayoutPanel buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 12, 0, 0)
            };
            buttonPanel.Controls.Add(_closeButton);
            buttonPanel.Controls.Add(_exportButton);
            layout.Controls.Add(buttonPanel, 0, 8);
            layout.SetColumnSpan(buttonPanel, 3);

            Controls.Add(layout);
            AcceptButton = _exportButton;
            CancelButton = _closeButton;
        }

        private static void ConfigureRatio(NumericUpDown numeric, decimal value)
        {
            numeric.Minimum = 0;
            numeric.Maximum = 1;
            numeric.DecimalPlaces = 2;
            numeric.Increment = 0.05M;
            numeric.Value = value;
            numeric.Width = 120;
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
            _browseButton.Click += BrowseButton_Click;
            _exportButton.Click += ExportButton_Click;
        }

        private void BrowseButton_Click(object? sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select export folder";
                dialog.UseDescriptionForTitle = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _outputTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void ExportButton_Click(object? sender, EventArgs e)
        {
            try
            {
                UltralyticsExportResult result = _service.Export(new UltralyticsExportRequest
                {
                    ProjectRoot = _project.RootDir,
                    Paths = _project.Paths,
                    OutputDirectory = _outputTextBox.Text.Trim(),
                    LabelFormat = GetSelectedFormat(),
                    TrainRatio = (double)_trainNumeric.Value,
                    ValRatio = (double)_valNumeric.Value,
                    TestRatio = (double)_testNumeric.Value,
                    Seed = (int)_seedNumeric.Value,
                    StratifyByClass = _stratifyCheckBox.Checked
                });
                _resultTextBox.Text = FormatResult(result);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is NotSupportedException)
            {
                _resultTextBox.Text = ex.Message;
            }
        }

        private LabelConvertFormat GetSelectedFormat()
        {
            if (_formatComboBox.SelectedItem is LabelConvertFormat format)
            {
                return format;
            }

            return LabelConvertFormat.YoloDetect;
        }

        private static string FormatResult(UltralyticsExportResult result)
        {
            List<string> lines = new List<string>
            {
                "Exported",
                "Images: " + result.TotalImages.ToString(),
                "Labels: " + result.TotalLabels.ToString(),
                "data.yaml: " + result.DataYamlPath
            };

            foreach (UltralyticsExportSplitResult split in result.Splits)
            {
                lines.Add(split.Name + ": " + split.ImageCount.ToString() + " images");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
