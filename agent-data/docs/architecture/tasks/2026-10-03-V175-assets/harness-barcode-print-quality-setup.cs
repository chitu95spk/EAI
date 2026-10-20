using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using BeeCore;
using BeeGlobal;
using BeeGlobal.Localization;
using BeeInterface;
using BeeInterfaceControls;

// Offline-only V175-r4 Setup probe. It creates the production ToolBarcode control in STA without
// Form.Show, camera acquisition, drawing capture, or Complete/external publication.
internal static class V175BarcodePrintQualitySetupHarness
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Run();
            Console.WriteLine("V175 print-quality Setup harness PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("V175 print-quality Setup harness FAIL: " + ex);
            return 1;
        }
    }

    private static void Run()
    {
        Config previousConfig = Global.Config;
        List<List<PropetyTool>> previousTools = Common.PropetyTools;
        int previousProgram = Global.EditingSession.ProgramIndex;
        int previousTool = Global.IndexToolSelected;
        UiLanguage previousLanguage = LanguageStore.CurrentLanguage;
        Func<UiLanguage?> previousLanguageProvider = ParameterAccordion.GlobalLanguageProvider;
        try
        {
            Global.Config = new Config
            {
                SizeCCD = new Size(320, 240),
                ParameterAccordionMode = ParameterAccordionMode.SingleExpanded,
                UiLanguage = UiLanguage.Vietnamese
            };
            ParameterAccordion.GlobalLanguageProvider = delegate { return Global.Config.UiLanguage; };
            LanguageStore.CurrentLanguage = UiLanguage.Vietnamese;
            Global.EditingSession.SelectProgram(0);
            Global.IndexToolSelected = 0;

            Barcode barcode = CreateBarcode();
            PropetyTool owner = new PropetyTool(barcode, TypeTool.BarCode, "V175 Setup");
            Common.SetToolList(0, new List<PropetyTool> { owner });

            using (ToolBarcode control = new ToolBarcode { Propety = barcode, Size = new Size(560, 780) })
            {
                control.CreateControl();
                ApplyProductionLayout(control);
                control.LoadPara();

                TabControl tabs = control.Controls.Find("tabControl2", true).OfType<TabControl>().Single();
                TabPage setupTab = tabs.TabPages[0];
                TabPage advancedTab = tabs.TabPages.Cast<TabPage>().Single(
                    item => item.Name == "toolEditorAdvanced");
                Assert(setupTab.Name == "tabP1" && !ReferenceEquals(setupTab, advancedTab),
                    "production layout must retain the first tab as Setup, separate from Advanced");
                tabs.SelectedTab = setupTab;

                ParameterAccordion accordion = GetPrivate<ParameterAccordion>(control, "parameterAccordion1");
                ParameterAccordion advancedAccordion = advancedTab.Controls.OfType<ParameterAccordion>().Single();
                ParameterAccordionSection section = accordion.Sections.Single(
                    item => item.Name == "sectionPrintQuality");
                Assert(ReferenceEquals(section.Parent, accordion)
                    && control.Controls.Find("sectionPrintQuality", true).Length == 1,
                    "Print quality must appear exactly once in the final Setup accordion");
                InvokePrivate(control, "ConfigureAdvancedGroups", accordion, advancedAccordion);
                InvokePrivate(control, "ConfigureAdvancedGroups", accordion, advancedAccordion);
                Assert(ReferenceEquals(section.Parent, accordion)
                    && control.Controls.Find("sectionPrintQuality", true).Length == 1,
                    "reapplying the production Setup bridge must preserve one Setup Print quality section");
                accordion.SelectSection(section, false);

                TableLayoutPanel blur = GetPrivate<TableLayoutPanel>(control, "_blockBlurCheck");
                TableLayoutPanel streak = GetPrivate<TableLayoutPanel>(control, "_blockStreakCheck");
                TableLayoutPanel contrast = GetPrivate<TableLayoutPanel>(control, "_blockContrastCheck");
                TableLayoutPanel area = GetPrivate<TableLayoutPanel>(control, "_rowQualityAreaLimit");
                RJButton blurOn = GetPrivate<RJButton>(control, "_btnBlurCheckOn");
                RJButton blurOff = GetPrivate<RJButton>(control, "_btnBlurCheckOff");
                RJButton fadedOn = GetPrivate<RJButton>(control, "_btnContrastFadedOn");
                RJButton fadedOff = GetPrivate<RJButton>(control, "_btnContrastFadedOff");
                RJButton heavyOn = GetPrivate<RJButton>(control, "_btnContrastHeavyOn");
                RJButton heavyOff = GetPrivate<RJButton>(control, "_btnContrastHeavyOff");
                RJButton streakOn = GetPrivate<RJButton>(control, "_btnStreakOn");
                RJButton streakOff = GetPrivate<RJButton>(control, "_btnStreakOff");
                RJButton modeMulti = GetPrivate<RJButton>(control, "btnModeMulti");
                RJButton modeSingle = GetPrivate<RJButton>(control, "btnModeSingle");
                ParameterAccordionSection modeSection = GetPrivate<ParameterAccordionSection>(control, "sectionMode");

                Assert(blur.Visible && contrast.Visible && streak.Visible && area.Visible,
                    "Single must show every print-quality block and the shared area row");
                Assert(blur.Enabled && contrast.Enabled && streak.Enabled && area.Enabled,
                    "Single must enable every print-quality control");

                blurOff.PerformClick();
                Assert(!barcode.BlurCheckEnabled, "Single Blur OFF binding must update the model");
                blurOn.PerformClick();
                Assert(barcode.BlurCheckEnabled, "Single Blur ON binding must update the model");
                fadedOff.PerformClick();
                Assert(!barcode.ContrastFadedEnabled, "Single Faded OFF binding must update the model");
                fadedOn.PerformClick();
                Assert(barcode.ContrastFadedEnabled, "Single Faded ON binding must update the model");
                heavyOff.PerformClick();
                Assert(!barcode.ContrastHeavyEnabled, "Single Heavy OFF binding must update the model");
                heavyOn.PerformClick();
                Assert(barcode.ContrastHeavyEnabled, "Single Heavy ON binding must update the model");

                Assert(control.Controls.Find("pictureStreakRules", true).Length == 1
                    && control.Controls.Find("pictureStreakRules", true)[0] is StreakIllustration,
                    "the Streak rule illustration must appear once in the Streak block");
                RJButton[] methodButtons = GetPrivate<RJButton[]>(control, "_streakMethodButtons");
                Assert(methodButtons.Length == 3 && barcode.StreakMethod == BeeCore.Algorithm.BarcodeStreakMethod.ModuleGrid
                    && methodButtons[0].IsCLick && !methodButtons[1].IsCLick,
                    "the module-grid method is the default and is shown selected");
                streakOn.PerformClick(); // the method buttons are only operable while Streak Check is ON
                methodButtons[1].PerformClick();
                Assert(barcode.StreakMethod == BeeCore.Algorithm.BarcodeStreakMethod.Classic && methodButtons[1].IsCLick,
                    "the classic method button must update the model");
                methodButtons[2].PerformClick();
                Assert(barcode.StreakMethod == BeeCore.Algorithm.BarcodeStreakMethod.Anomaly && methodButtons[2].IsCLick,
                    "the AI method button must update the model");
                Assert(control.Controls.Find("rowStreakAiActions", true).Length == 1
                    && control.Controls.Find("rowStreakAiActions", true)[0].Visible
                    && !control.Controls.Find("rowStreakPreset", true)[0].Visible
                    && !control.Controls.Find("pictureStreakRules", true)[0].Visible,
                    "AI method shows its own rows and hides the classic-only rows");
                RJButton[] aiSens = GetPrivate<RJButton[]>(control, "_streakAiSensitivityButtons");
                aiSens[2].PerformClick();
                Assert(barcode.StreakAiSensitivity == 2 && aiSens[2].IsCLick, "AI sensitivity High must update the model");
                aiSens[1].PerformClick();
                Assert(barcode.StreakAiSensitivity == 1, "AI sensitivity Standard must update the model");
                methodButtons[0].PerformClick();
                Assert(barcode.StreakMethod == BeeCore.Algorithm.BarcodeStreakMethod.ModuleGrid
                    && control.Controls.Find("rowStreakPreset", true)[0].Visible
                    && !control.Controls.Find("rowStreakAiActions", true)[0].Visible,
                    "the module-grid method button must restore the classic rows");
                streakOff.PerformClick();
                RJButton[] preFilterButtons = GetPrivate<RJButton[]>(control, "_streakPreFilterButtons");
                Assert(preFilterButtons.Length == 3 && barcode.StreakPreFilter == 0 && preFilterButtons[0].IsCLick,
                    "the contrast pre-filter defaults to Off");
                streakOn.PerformClick();
                preFilterButtons[2].PerformClick();
                Assert(barcode.StreakPreFilter == 2 && preFilterButtons[2].IsCLick, "Strong pre-filter button must update the model");
                preFilterButtons[1].PerformClick();
                Assert(barcode.StreakPreFilter == 1, "Light pre-filter button must update the model");
                preFilterButtons[0].PerformClick();
                Assert(barcode.StreakPreFilter == 0, "Off pre-filter button must update the model");
                streakOff.PerformClick();
                Assert(control.Controls.Find("blockNgDetails", true).Length == 1,
                    "the NG detail toggles must appear once in the Print quality section");
                System.Collections.IList ngPairs = (System.Collections.IList)GetPrivate<object>(control, "_ngDetailButtons");
                Assert(ngPairs.Count == 4, "four NG detail toggles are expected");
                BeeCore.Algorithm.BarcodeNgDetail[] details =
                {
                    BeeCore.Algorithm.BarcodeNgDetail.StreakZone, BeeCore.Algorithm.BarcodeNgDetail.StreakLines,
                    BeeCore.Algorithm.BarcodeNgDetail.PrintQuality, BeeCore.Algorithm.BarcodeNgDetail.Content
                };
                for (int i = 0; i < ngPairs.Count; i++)
                {
                    RJButton[] pair = (RJButton[])ngPairs[i];
                    bool linesDetail = details[i] == BeeCore.Algorithm.BarcodeNgDetail.StreakLines;
                    Assert(pair[0].IsCLick == !linesDetail && pair[1].IsCLick == linesDetail
                        && barcode.IsNgDetailVisible(details[i]) == !linesDetail,
                        "NG detail " + i + " default state (streak lines OFF, others ON)");
                    pair[1].PerformClick();
                    Assert(!barcode.IsNgDetailVisible(details[i]) && !pair[0].IsCLick && pair[1].IsCLick,
                        "NG detail " + i + " OFF must hide only that detail");
                    for (int other = 0; other < ngPairs.Count; other++)
                        if (other != i && details[other] != BeeCore.Algorithm.BarcodeNgDetail.StreakLines)
                            Assert(barcode.IsNgDetailVisible(details[other]), "other NG details stay visible");
                    pair[0].PerformClick();
                    Assert(barcode.IsNgDetailVisible(details[i]) && pair[0].IsCLick,
                        "NG detail " + i + " ON must show it again");
                    if (linesDetail)
                    {
                        pair[1].PerformClick();
                        Assert(!barcode.IsNgDetailVisible(details[i]), "streak lines return to their default OFF");
                    }
                }

                // The Mode section cannot be expanded in this never-shown offline control tree, so the real
                // production handlers behind the Mode buttons are invoked directly instead of PerformClick.
                Assert(ReferenceEquals(modeMulti.Parent.Parent, modeSection.ContentLayout) || modeSection.ContentLayout.Contains(modeMulti),
                    "Multi mode button must belong to the production Mode section");
                InvokePrivate(control, "rjButton2_Click_1", modeMulti, EventArgs.Empty);
                Assert(barcode.ModeCheck == ModeCheck.Multi, "Multi mode handler must update the model");
                accordion.SelectSection(section, false);
                Assert(blur.Visible && contrast.Visible && streak.Visible && area.Visible,
                    "Multi must retain all print-quality blocks and the shared area row");
                Assert(blur.Enabled && contrast.Enabled && streak.Enabled && area.Enabled,
                    "Multi must keep every print-quality block and the shared area row operable");

                blurOff.PerformClick();
                Assert(!barcode.BlurCheckEnabled, "Multi Blur OFF binding must update the model");
                blurOn.PerformClick();
                Assert(barcode.BlurCheckEnabled, "Multi Blur ON binding must update the model");
                fadedOff.PerformClick();
                Assert(!barcode.ContrastFadedEnabled, "Multi Faded OFF binding must update the model");
                fadedOn.PerformClick();
                Assert(barcode.ContrastFadedEnabled, "Multi Faded ON binding must update the model");
                heavyOff.PerformClick();
                Assert(!barcode.ContrastHeavyEnabled, "Multi Heavy OFF binding must update the model");
                heavyOn.PerformClick();
                Assert(barcode.ContrastHeavyEnabled, "Multi Heavy ON binding must update the model");
                streakOn.PerformClick();
                Assert(barcode.StreakCheckEnabled, "Multi Streak ON binding must remain operable");
                streakOff.PerformClick();
                Assert(!barcode.StreakCheckEnabled, "Multi Streak OFF binding must remain operable");

                bool blurSaved = barcode.BlurCheckEnabled;
                bool fadedSaved = barcode.ContrastFadedEnabled;
                bool heavySaved = barcode.ContrastHeavyEnabled;
                float areaSaved = barcode.QualityAreaLimitPercent;
                section.RefreshHeader();
                Assert(section.HeaderValueText.Contains("Nhòe") && section.HeaderValueText.Contains("Nhạt")
                    && section.HeaderValueText.Contains("Đậm") && !section.HeaderValueText.Contains("Single"),
                    "Vietnamese Multi summary must list the active Blur/Faded/Heavy checks");

                Global.Config.UiLanguage = UiLanguage.English;
                LanguageStore.CurrentLanguage = UiLanguage.English;
                Global.RaiseUiLanguageChanged(UiLanguage.English);
                section.RefreshHeader();
                Assert(section.HeaderValueText.Contains("Blur") && section.HeaderValueText.Contains("Faded")
                    && section.HeaderValueText.Contains("Heavy") && !section.HeaderValueText.Contains("Single"),
                    "English Multi summary must list the active Blur/Faded/Heavy checks");

                InvokePrivate(control, "btnModeSingle_Click", modeSingle, EventArgs.Empty);
                Assert(barcode.ModeCheck == ModeCheck.Single, "Single mode handler must restore the model mode");
                accordion.SelectSection(section, false);
                Assert(blur.Enabled && contrast.Enabled && streak.Enabled && area.Enabled,
                    "Single must keep parent enablement without changing saved settings");
                Assert(barcode.BlurCheckEnabled == blurSaved
                    && barcode.ContrastFadedEnabled == fadedSaved
                    && barcode.ContrastHeavyEnabled == heavySaved
                    && barcode.QualityAreaLimitPercent == areaSaved,
                    "Single restore must retain saved quality settings");
            }
        }
        finally
        {
            Common.PropetyTools = previousTools;
            Global.Config = previousConfig;
            Global.EditingSession.SelectProgram(previousProgram);
            Global.IndexToolSelected = previousTool;
            ParameterAccordion.GlobalLanguageProvider = previousLanguageProvider;
            LanguageStore.CurrentLanguage = previousLanguage;
        }
    }

    private static Barcode CreateBarcode()
    {
        Barcode barcode = new Barcode
        {
            Index = 0,
            IndexThread = 0,
            ModeCheck = ModeCheck.Single,
            BlurCheckEnabled = true,
            ContrastFadedEnabled = true,
            ContrastHeavyEnabled = true,
            StreakCheckEnabled = false,
            QualityAreaLimitPercent = 4f,
            rotArea = Rect(160f, 120f, 160f, 100f, TypeCrop.Area),
            rotCrop = Rect(160f, 120f, 240f, 160f, TypeCrop.Crop),
            rotLimit = Rect(0f, 0f, 10f, 10f, TypeCrop.Limit)
        };
        return barcode;
    }

    private static RectRotate Rect(float x, float y, float width, float height, TypeCrop type)
    {
        return new RectRotate(new RectangleF(-width / 2f, -height / 2f, width, height),
            new PointF(x, y), 0f, AnchorPoint.None) { TypeCrop = type };
    }

    private static T GetPrivate<T>(object target, string name) where T : class
    {
        FieldInfo field = target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(field != null, "private field not found: " + name);
        T value = field.GetValue(target) as T;
        Assert(value != null, "private field was null: " + name);
        return value;
    }

    private static void ApplyProductionLayout(ToolBarcode control)
    {
        Assembly assembly = typeof(ToolBarcode).Assembly;
        InvokeStatic(assembly.GetType("BeeInterface.Tool._Base.ToolEditorUiRules", true),
            "Prepare", new Type[] { typeof(Control), typeof(TypeTool) }, control, TypeTool.BarCode);
        InvokeStatic(assembly.GetType("BeeInterface.ToolParameterAccordionBuilder", true),
            "Apply", new Type[] { typeof(Control) }, control);
        InvokeStatic(assembly.GetType("BeeInterface.Tool._Base.PreprocessEditorHost", true),
            "AttachPlaceholder", new Type[] { typeof(Control), typeof(TypeTool) }, control, TypeTool.BarCode);
        InvokeStatic(assembly.GetType("BeeInterface.Tool._Base.ToolEditorUiRules", true),
            "Apply", new Type[] { typeof(Control), typeof(TypeTool) }, control, TypeTool.BarCode);
    }

    private static void InvokeStatic(Type type, string name, Type[] signature, params object[] arguments)
    {
        MethodInfo method = type.GetMethod(name,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            null, signature, null);
        Assert(method != null, "production layout method not found: " + type.FullName + "." + name);
        method.Invoke(null, arguments);
    }

    private static void InvokePrivate(object target, string name, params object[] arguments)
    {
        MethodInfo method = target.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(method != null, "private method not found: " + name);
        method.Invoke(target, arguments);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
