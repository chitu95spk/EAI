using EasyLabel.Core.Batch;
using EasyLabel.Core.Model;

namespace EasyLabel.App.Dialogs
{
    public sealed class AugmentForm : Form
    {
        private readonly Project _project;
        private readonly ImageItem _image;
        private readonly AugmentService _service;
        private readonly NumericUpDown _cloneCountNumeric = new NumericUpDown();
        private readonly CheckBox _flipHorizontalCheckBox = new CheckBox();
        private readonly CheckBox _flipVerticalCheckBox = new CheckBox();
        private readonly CheckBox _rotate90CheckBox = new CheckBox();
        private readonly CheckBox _rotate180CheckBox = new CheckBox();
        private readonly CheckBox _rotate270CheckBox = new CheckBox();
        private readonly CheckBox _rotateArbitraryCheckBox = new CheckBox();
        private readonly NumericUpDown _rotationDegreesNumeric = new NumericUpDown();
        private readonly CheckBox _hsvJitterCheckBox = new CheckBox();
        private readonly NumericUpDown _hueDeltaNumeric = new NumericUpDown();
        private readonly NumericUpDown _saturationScaleNumeric = new NumericUpDown();
        private readonly NumericUpDown _valueScaleNumeric = new NumericUpDown();
        private readonly CheckBox _scaleCheckBox = new CheckBox();
        private readonly NumericUpDown _scaleFactorNumeric = new NumericUpDown();
        private readonly CheckBox _cropCheckBox = new CheckBox();
        private readonly NumericUpDown _cropXNumeric = new NumericUpDown();
        private readonly NumericUpDown _cropYNumeric = new NumericUpDown();
        private readonly NumericUpDown _cropWidthNumeric = new NumericUpDown();
        private readonly NumericUpDown _cropHeightNumeric = new NumericUpDown();
        private readonly TextBox _previewTextBox = new TextBox();
        private readonly Button _previewButton = new Button();
        private readonly Button _applyButton = new Button();
        private readonly Button _cancelButton = new Button();

        public AugmentForm(Project project, ImageItem image, AugmentService service)
        {
            _project = project ?? throw new ArgumentNullException(nameof(project));
            _image = image ?? throw new ArgumentNullException(nameof(image));
            _service = service ?? throw new ArgumentNullException(nameof(service));

            Text = "Clone & Augment";
            Name = "AugmentForm";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 560);
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            InitializeLayout();
            WireEvents();
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
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 280));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

            _cloneCountNumeric.Name = "AugmentCloneCountNumeric";
            _cloneCountNumeric.Minimum = 0;
            _cloneCountNumeric.Maximum = 100;
            _cloneCountNumeric.Value = 1;
            _cloneCountNumeric.Width = 96;

            ConfigureNumeric(_rotationDegreesNumeric, -360, 360, 15, 1);
            ConfigureNumeric(_hueDeltaNumeric, -179, 179, 10, 0);
            ConfigureNumeric(_saturationScaleNumeric, 0, 5, 1, 2);
            ConfigureNumeric(_valueScaleNumeric, 0, 5, 1, 2);
            ConfigureNumeric(_scaleFactorNumeric, 0.01M, 10, 1, 2);
            _scaleFactorNumeric.DecimalPlaces = 2;
            _scaleFactorNumeric.Increment = 0.1M;
            ConfigureIntegerNumeric(_cropXNumeric, 0);
            ConfigureIntegerNumeric(_cropYNumeric, 0);
            ConfigureIntegerNumeric(_cropWidthNumeric, 1);
            ConfigureIntegerNumeric(_cropHeightNumeric, 1);

            FlowLayoutPanel variantsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };

            ConfigureCheckBox(_flipHorizontalCheckBox, "AugmentFlipHorizontalCheckBox", "Flip Horizontal");
            ConfigureCheckBox(_flipVerticalCheckBox, "AugmentFlipVerticalCheckBox", "Flip Vertical");
            ConfigureCheckBox(_rotate90CheckBox, "AugmentRotate90CheckBox", "Rotate 90");
            ConfigureCheckBox(_rotate180CheckBox, "AugmentRotate180CheckBox", "Rotate 180");
            ConfigureCheckBox(_rotate270CheckBox, "AugmentRotate270CheckBox", "Rotate 270");
            ConfigureCheckBox(_rotateArbitraryCheckBox, "AugmentRotateArbitraryCheckBox", "Rotate Degrees");
            ConfigureCheckBox(_hsvJitterCheckBox, "AugmentHsvJitterCheckBox", "HSV Jitter");
            ConfigureCheckBox(_scaleCheckBox, "AugmentScaleCheckBox", "Scale");
            ConfigureCheckBox(_cropCheckBox, "AugmentCropCheckBox", "Crop");
            variantsPanel.Controls.Add(_flipHorizontalCheckBox);
            variantsPanel.Controls.Add(_flipVerticalCheckBox);
            variantsPanel.Controls.Add(_rotate90CheckBox);
            variantsPanel.Controls.Add(_rotate180CheckBox);
            variantsPanel.Controls.Add(_rotate270CheckBox);
            variantsPanel.Controls.Add(CreateVariantRow(_rotateArbitraryCheckBox, CreateInlineLabel("deg"), _rotationDegreesNumeric));
            variantsPanel.Controls.Add(CreateVariantRow(_hsvJitterCheckBox, CreateInlineLabel("h"), _hueDeltaNumeric, CreateInlineLabel("s"), _saturationScaleNumeric, CreateInlineLabel("v"), _valueScaleNumeric));
            variantsPanel.Controls.Add(CreateVariantRow(_scaleCheckBox, CreateInlineLabel("factor"), _scaleFactorNumeric));
            variantsPanel.Controls.Add(CreateVariantRow(_cropCheckBox, CreateInlineLabel("x"), _cropXNumeric, CreateInlineLabel("y"), _cropYNumeric, CreateInlineLabel("w"), _cropWidthNumeric, CreateInlineLabel("h"), _cropHeightNumeric));

            _previewTextBox.Name = "AugmentPreviewTextBox";
            _previewTextBox.Dock = DockStyle.Fill;
            _previewTextBox.Multiline = true;
            _previewTextBox.ReadOnly = true;
            _previewTextBox.ScrollBars = ScrollBars.Vertical;

            _previewButton.Name = "AugmentPreviewButton";
            _previewButton.Text = "Preview";
            _previewButton.Width = 96;

            _applyButton.Name = "AugmentApplyButton";
            _applyButton.Text = "Apply";
            _applyButton.Width = 96;
            _applyButton.Enabled = false;

            _cancelButton.Name = "AugmentCancelButton";
            _cancelButton.Text = "Cancel";
            _cancelButton.Width = 96;
            _cancelButton.DialogResult = DialogResult.Cancel;

            layout.Controls.Add(CreateLabel("Clones"), 0, 0);
            layout.Controls.Add(_cloneCountNumeric, 1, 0);
            layout.Controls.Add(CreateLabel("Variants"), 0, 1);
            layout.Controls.Add(variantsPanel, 1, 1);
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

        private static void ConfigureCheckBox(CheckBox checkBox, string name, string text)
        {
            checkBox.Name = name;
            checkBox.Text = text;
            checkBox.AutoSize = true;
        }

        private static void ConfigureNumeric(NumericUpDown numeric, decimal minimum, decimal maximum, decimal value, int decimalPlaces)
        {
            numeric.Minimum = minimum;
            numeric.Maximum = maximum;
            numeric.Value = value;
            numeric.DecimalPlaces = decimalPlaces;
            numeric.Width = 72;
        }

        private static void ConfigureIntegerNumeric(NumericUpDown numeric, decimal value)
        {
            numeric.Minimum = 0;
            numeric.Maximum = 100000;
            numeric.Value = value;
            numeric.DecimalPlaces = 0;
            numeric.Width = 72;
        }

        private static FlowLayoutPanel CreateVariantRow(CheckBox checkBox, params Control[] controls)
        {
            FlowLayoutPanel row = new FlowLayoutPanel
            {
                AutoSize = true,
                WrapContents = false
            };
            row.Controls.Add(checkBox);
            foreach (Control control in controls)
            {
                row.Controls.Add(control);
            }

            return row;
        }

        private static Label CreateInlineLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(8, 6, 2, 0)
            };
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
            _cloneCountNumeric.ValueChanged += AnyInputChanged;
            _flipHorizontalCheckBox.CheckedChanged += AnyInputChanged;
            _flipVerticalCheckBox.CheckedChanged += AnyInputChanged;
            _rotate90CheckBox.CheckedChanged += AnyInputChanged;
            _rotate180CheckBox.CheckedChanged += AnyInputChanged;
            _rotate270CheckBox.CheckedChanged += AnyInputChanged;
            _rotateArbitraryCheckBox.CheckedChanged += AnyInputChanged;
            _hsvJitterCheckBox.CheckedChanged += AnyInputChanged;
            _scaleCheckBox.CheckedChanged += AnyInputChanged;
            _cropCheckBox.CheckedChanged += AnyInputChanged;
            _rotationDegreesNumeric.ValueChanged += AnyInputChanged;
            _hueDeltaNumeric.ValueChanged += AnyInputChanged;
            _saturationScaleNumeric.ValueChanged += AnyInputChanged;
            _valueScaleNumeric.ValueChanged += AnyInputChanged;
            _scaleFactorNumeric.ValueChanged += AnyInputChanged;
            _cropXNumeric.ValueChanged += AnyInputChanged;
            _cropYNumeric.ValueChanged += AnyInputChanged;
            _cropWidthNumeric.ValueChanged += AnyInputChanged;
            _cropHeightNumeric.ValueChanged += AnyInputChanged;
            _previewButton.Click += PreviewButton_Click;
            _applyButton.Click += ApplyButton_Click;
        }

        private void AnyInputChanged(object? sender, EventArgs e)
        {
            ResetPreview();
        }

        private void PreviewButton_Click(object? sender, EventArgs e)
        {
            try
            {
                AugmentResult result = _service.Preview(CreateRequest());
                _previewTextBox.Text = FormatResult(result, false);
                _applyButton.Enabled = true;
                AcceptButton = _applyButton;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
            {
                _previewTextBox.Text = ex.Message;
                _applyButton.Enabled = false;
            }
        }

        private void ApplyButton_Click(object? sender, EventArgs e)
        {
            try
            {
                AugmentResult result = _service.Apply(CreateRequest());
                _previewTextBox.Text = FormatResult(result, true);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException)
            {
                _previewTextBox.Text = ex.Message;
                _applyButton.Enabled = false;
            }
        }

        private AugmentRequest CreateRequest()
        {
            AugmentRequest request = new AugmentRequest
            {
                ProjectRoot = _project.RootDir,
                Paths = _project.Paths,
                ImagePath = _image.ImagePath,
                JsonPath = _image.JsonPath,
                SidecarPath = _image.SidecarPath,
                CloneCount = (int)_cloneCountNumeric.Value,
                ArbitraryRotationDegrees = (double)_rotationDegreesNumeric.Value,
                HsvHueDelta = (int)_hueDeltaNumeric.Value,
                HsvSaturationScale = (double)_saturationScaleNumeric.Value,
                HsvValueScale = (double)_valueScaleNumeric.Value,
                ScaleFactor = (double)_scaleFactorNumeric.Value,
                CropX = (int)_cropXNumeric.Value,
                CropY = (int)_cropYNumeric.Value,
                CropWidth = (int)_cropWidthNumeric.Value,
                CropHeight = (int)_cropHeightNumeric.Value
            };

            AddVariant(request, _flipHorizontalCheckBox, AugmentVariant.FlipHorizontal);
            AddVariant(request, _flipVerticalCheckBox, AugmentVariant.FlipVertical);
            AddVariant(request, _rotate90CheckBox, AugmentVariant.Rotate90Clockwise);
            AddVariant(request, _rotate180CheckBox, AugmentVariant.Rotate180);
            AddVariant(request, _rotate270CheckBox, AugmentVariant.Rotate270Clockwise);
            AddVariant(request, _rotateArbitraryCheckBox, AugmentVariant.RotateArbitrary);
            AddVariant(request, _hsvJitterCheckBox, AugmentVariant.HsvJitter);
            AddVariant(request, _scaleCheckBox, AugmentVariant.Scale);
            AddVariant(request, _cropCheckBox, AugmentVariant.Crop);
            return request;
        }

        private static void AddVariant(AugmentRequest request, CheckBox checkBox, AugmentVariant variant)
        {
            if (checkBox.Checked)
            {
                request.Variants.Add(variant);
            }
        }

        private void ResetPreview()
        {
            _applyButton.Enabled = false;
            AcceptButton = _previewButton;
            _previewTextBox.Text = "Preview has not run.";
        }

        private static string FormatResult(AugmentResult result, bool applied)
        {
            string status = applied ? "Applied" : "Preview";
            return string.Format(
                "{0}{1}Images to create: {2}{1}Annotation files: {2}{1}Sidecar files: up to {2}",
                status,
                Environment.NewLine,
                result.ImagesCreated);
        }
    }
}
