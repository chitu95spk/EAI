using EasyLabel.Core.Convert;

namespace EasyLabel.App.Dialogs
{
    public sealed class StandaloneConvertForm : Form
    {
        private readonly StandaloneConvertService _service;
        private readonly TextBox _inputTextBox = new TextBox();
        private readonly TextBox _outputTextBox = new TextBox();
        private readonly Button _inputBrowseButton = new Button();
        private readonly Button _outputBrowseButton = new Button();
        private readonly ComboBox _sourceFormatComboBox = new ComboBox();
        private readonly ComboBox _targetFormatComboBox = new ComboBox();
        private readonly TextBox _resultTextBox = new TextBox();
        private readonly Button _runButton = new Button();
        private readonly Button _closeButton = new Button();

        public StandaloneConvertForm(StandaloneConvertService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));

            Text = "Convert Labels";
            Name = "StandaloneConvertForm";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 340);
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
                RowCount = 6,
                Padding = new Padding(12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

            _inputTextBox.Name = "StandaloneConvertInputTextBox";
            _inputTextBox.Dock = DockStyle.Fill;
            _outputTextBox.Name = "StandaloneConvertOutputTextBox";
            _outputTextBox.Dock = DockStyle.Fill;
            _inputBrowseButton.Name = "StandaloneConvertInputBrowseButton";
            _inputBrowseButton.Text = "Browse";
            _inputBrowseButton.Width = 80;
            _outputBrowseButton.Name = "StandaloneConvertOutputBrowseButton";
            _outputBrowseButton.Text = "Browse";
            _outputBrowseButton.Width = 80;

            ConfigureFormatCombo(_sourceFormatComboBox, "StandaloneConvertSourceFormatComboBox");
            ConfigureFormatCombo(_targetFormatComboBox, "StandaloneConvertTargetFormatComboBox");
            _sourceFormatComboBox.SelectedItem = LabelConvertFormat.LabelMeJson;
            _targetFormatComboBox.SelectedItem = LabelConvertFormat.YoloDetect;

            _resultTextBox.Name = "StandaloneConvertResultTextBox";
            _resultTextBox.Dock = DockStyle.Fill;
            _resultTextBox.Multiline = true;
            _resultTextBox.ReadOnly = true;
            _resultTextBox.ScrollBars = ScrollBars.Vertical;

            _runButton.Name = "StandaloneConvertRunButton";
            _runButton.Text = "Run";
            _runButton.Width = 96;
            _closeButton.Name = "StandaloneConvertCloseButton";
            _closeButton.Text = "Close";
            _closeButton.Width = 96;
            _closeButton.DialogResult = DialogResult.Cancel;

            layout.Controls.Add(CreateLabel("Input"), 0, 0);
            layout.Controls.Add(_inputTextBox, 1, 0);
            layout.Controls.Add(_inputBrowseButton, 2, 0);
            layout.Controls.Add(CreateLabel("Output"), 0, 1);
            layout.Controls.Add(_outputTextBox, 1, 1);
            layout.Controls.Add(_outputBrowseButton, 2, 1);
            layout.Controls.Add(CreateLabel("From"), 0, 2);
            layout.Controls.Add(_sourceFormatComboBox, 1, 2);
            layout.SetColumnSpan(_sourceFormatComboBox, 2);
            layout.Controls.Add(CreateLabel("To"), 0, 3);
            layout.Controls.Add(_targetFormatComboBox, 1, 3);
            layout.SetColumnSpan(_targetFormatComboBox, 2);
            layout.Controls.Add(_resultTextBox, 0, 4);
            layout.SetColumnSpan(_resultTextBox, 3);

            FlowLayoutPanel buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 12, 0, 0)
            };
            buttonPanel.Controls.Add(_closeButton);
            buttonPanel.Controls.Add(_runButton);
            layout.Controls.Add(buttonPanel, 0, 5);
            layout.SetColumnSpan(buttonPanel, 3);

            Controls.Add(layout);
            AcceptButton = _runButton;
            CancelButton = _closeButton;
        }

        private static void ConfigureFormatCombo(ComboBox comboBox, string name)
        {
            comboBox.Name = name;
            comboBox.Dock = DockStyle.Fill;
            comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            comboBox.Items.AddRange(new object[]
            {
                LabelConvertFormat.LabelMeJson,
                LabelConvertFormat.YoloDetect,
                LabelConvertFormat.YoloObb,
                LabelConvertFormat.YoloSegment,
                LabelConvertFormat.CocoJson,
                LabelConvertFormat.PascalVoc
            });
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
            _inputBrowseButton.Click += InputBrowseButton_Click;
            _outputBrowseButton.Click += OutputBrowseButton_Click;
            _runButton.Click += RunButton_Click;
        }

        private void InputBrowseButton_Click(object? sender, EventArgs e)
        {
            BrowseFolder(_inputTextBox, "Select input label folder");
        }

        private void OutputBrowseButton_Click(object? sender, EventArgs e)
        {
            BrowseFolder(_outputTextBox, "Select output label folder");
        }

        private void RunButton_Click(object? sender, EventArgs e)
        {
            try
            {
                StandaloneConvertResult result = _service.Convert(new StandaloneConvertRequest
                {
                    InputDirectory = _inputTextBox.Text.Trim(),
                    OutputDirectory = _outputTextBox.Text.Trim(),
                    SourceFormat = GetSelectedFormat(_sourceFormatComboBox, LabelConvertFormat.LabelMeJson),
                    TargetFormat = GetSelectedFormat(_targetFormatComboBox, LabelConvertFormat.YoloDetect)
                });
                _resultTextBox.Text = string.Format(
                    "Converted{0}Files scanned: {1}{0}Files written: {2}",
                    Environment.NewLine,
                    result.FilesScanned,
                    result.FilesWritten);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is InvalidOperationException)
            {
                _resultTextBox.Text = ex.Message;
            }
        }

        private void BrowseFolder(TextBox target, string description)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.UseDescriptionForTitle = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    target.Text = dialog.SelectedPath;
                }
            }
        }

        private static LabelConvertFormat GetSelectedFormat(ComboBox comboBox, LabelConvertFormat fallback)
        {
            if (comboBox.SelectedItem is LabelConvertFormat format)
            {
                return format;
            }

            return fallback;
        }
    }
}
