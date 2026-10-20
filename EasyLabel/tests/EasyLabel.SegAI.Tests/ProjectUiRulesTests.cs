namespace EasyLabel.SegAI.Tests;

/// <summary>The UI-free rules behind the New Project / Save Project As dialogs, the title, the recent rows and the copy progress.</summary>
public sealed class ProjectUiRulesTests
{
    private const string Location = @"C:\Work\Projects";

    private static EasyAI.Desktop.Infrastructure.ProjectInputCheck Check(string? name, string? location = Location, string? images = null, bool imagesFree = false,
        System.Func<string, bool>? fileExists = null, System.Func<string, bool>? directoryExists = null)
    {
        return EasyAI.Desktop.Infrastructure.NewProjectInputRules.Check(name, location, images, imagesFree, fileExists ?? (_ => false), directoryExists ?? (_ => false));
    }

    [Theory]
    [InlineData("", EasyAI.Desktop.Infrastructure.ProjectInputError.NameEmpty)]
    [InlineData("   ", EasyAI.Desktop.Infrastructure.ProjectInputError.NameEmpty)]
    [InlineData("a:b", EasyAI.Desktop.Infrastructure.ProjectInputError.NameInvalidCharacter)]
    [InlineData("a/b", EasyAI.Desktop.Infrastructure.ProjectInputError.NameInvalidCharacter)]
    [InlineData("name.", EasyAI.Desktop.Infrastructure.ProjectInputError.NameTrailingDot)]
    [InlineData("CON", EasyAI.Desktop.Infrastructure.ProjectInputError.NameReserved)]
    [InlineData("nul.txt", EasyAI.Desktop.Infrastructure.ProjectInputError.NameReserved)]
    [InlineData("Demo.easyai", EasyAI.Desktop.Infrastructure.ProjectInputError.NameReservedSuffix)]
    [InlineData("Demo.easyai.data", EasyAI.Desktop.Infrastructure.ProjectInputError.NameReservedSuffix)]
    public void Check_ReportsTheBrokenNameRuleFirst(string name, EasyAI.Desktop.Infrastructure.ProjectInputError expected)
    {
        Assert.Equal(expected, Check(name).Error);
    }

    [Fact]
    public void Check_ReportsATooLongName()
    {
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.NameTooLong, Check(new string('a', EasyLabel.Core.Projects.ProjectNameRules.MaxLength + 1)).Error);
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.None, Check(new string('a', EasyLabel.Core.Projects.ProjectNameRules.MaxLength)).Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Check_RequiresALocation(string? location)
    {
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.LocationEmpty, Check("Demo", location).Error);
    }

    [Fact]
    public void Check_RefusesARelativeLocationAndALocationThatIsAFile()
    {
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.LocationInvalid, Check("Demo", @"relative\folder").Error);

        EasyAI.Desktop.Infrastructure.ProjectInputCheck file = Check("Demo", fileExists: path => string.Equals(path, Location, System.StringComparison.OrdinalIgnoreCase));
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.LocationInvalid, file.Error);
        Assert.Equal(Location, file.Path);
    }

    [Fact]
    public void Check_RequiresAnExistingImageFolderWhenOneIsGiven()
    {
        EasyAI.Desktop.Infrastructure.ProjectInputCheck missing = Check("Demo", images: @"D:\Images");
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.ImageFolderMissing, missing.Error);
        Assert.Equal(@"D:\Images", missing.Path);

        Assert.Equal(
            EasyAI.Desktop.Infrastructure.ProjectInputError.None,
            Check("Demo", images: @"D:\Images", directoryExists: path => path == @"D:\Images").Error);
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.None, Check("Demo", images: "  ").Error);
    }

    [Fact]
    public void Check_NeverOverwritesTheProjectFileTheDataFolderOrTheImagesFolder()
    {
        string manifest = EasyLabel.Core.Projects.ProjectManager.ManifestPathFor(Location, "Demo");
        string data = EasyLabel.Core.Projects.ProjectManager.DataFolderPathFor(Location, "Demo");
        string images = EasyLabel.Core.Projects.ProjectManager.ImagesFolderPathFor(Location, "Demo");

        EasyAI.Desktop.Infrastructure.ProjectInputCheck file = Check("Demo", fileExists: path => path == manifest);
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.TargetExists, file.Error);
        Assert.Equal(manifest, file.Path);

        EasyAI.Desktop.Infrastructure.ProjectInputCheck folder = Check("Demo", directoryExists: path => path == data);
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.TargetExists, folder.Error);
        Assert.Equal(data, folder.Path);

        // The images folder only matters when the project is going to create it.
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.None, Check("Demo", imagesFree: false, directoryExists: path => path == images).Error);
        EasyAI.Desktop.Infrastructure.ProjectInputCheck taken = Check("Demo", imagesFree: true, directoryExists: path => path == images);
        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.TargetExists, taken.Error);
        Assert.Equal(images, taken.Path);
    }

    [Fact]
    public void Check_AcceptsAFreeNameAndLocation()
    {
        EasyAI.Desktop.Infrastructure.ProjectInputCheck check = Check("  Demo project  ", imagesFree: true);

        Assert.Equal(EasyAI.Desktop.Infrastructure.ProjectInputError.None, check.Error);
        Assert.Null(check.Path);
        Assert.Null(EasyAI.Desktop.Infrastructure.ProjectMessages.For(check));
    }

    [Fact]
    public void Messages_MapEveryInputErrorToAKeyAndItsArgument()
    {
        Assert.Equal("ProjErrNameEmpty", Message(EasyAI.Desktop.Infrastructure.ProjectInputError.NameEmpty).Key);
        EasyAI.Desktop.Infrastructure.ProjectMessage tooLong = Message(EasyAI.Desktop.Infrastructure.ProjectInputError.NameTooLong);
        Assert.Equal("ProjErrNameTooLong", tooLong.Key);
        Assert.Equal("100", tooLong.Argument);
        Assert.Equal("ProjErrNameChars", Message(EasyAI.Desktop.Infrastructure.ProjectInputError.NameInvalidCharacter).Key);
        Assert.Equal("ProjErrNameDot", Message(EasyAI.Desktop.Infrastructure.ProjectInputError.NameTrailingDot).Key);
        Assert.Equal("ProjErrNameReserved", Message(EasyAI.Desktop.Infrastructure.ProjectInputError.NameReserved).Key);
        Assert.Equal("ProjErrNameSuffix", Message(EasyAI.Desktop.Infrastructure.ProjectInputError.NameReservedSuffix).Key);
        Assert.Equal("ProjErrLocationEmpty", Message(EasyAI.Desktop.Infrastructure.ProjectInputError.LocationEmpty).Key);

        EasyAI.Desktop.Infrastructure.ProjectMessage exists = EasyAI.Desktop.Infrastructure.ProjectMessages.For(
            new EasyAI.Desktop.Infrastructure.ProjectInputCheck(EasyAI.Desktop.Infrastructure.ProjectInputError.TargetExists, @"C:\x\Demo.easyai"))!.Value;
        Assert.Equal("ProjErrTargetExists", exists.Key);
        Assert.Equal(@"C:\x\Demo.easyai", exists.Argument);
    }

    private static EasyAI.Desktop.Infrastructure.ProjectMessage Message(EasyAI.Desktop.Infrastructure.ProjectInputError error)
    {
        return EasyAI.Desktop.Infrastructure.ProjectMessages.For(new EasyAI.Desktop.Infrastructure.ProjectInputCheck(error, null))!.Value;
    }

    [Theory]
    [InlineData(EasyLabel.Core.Projects.ProjectErrorCode.InvalidLocation, "ProjErrLocationInvalid")]
    [InlineData(EasyLabel.Core.Projects.ProjectErrorCode.TargetExists, "ProjErrTargetExists")]
    [InlineData(EasyLabel.Core.Projects.ProjectErrorCode.ManifestMissing, "ProjErrManifestMissing")]
    [InlineData(EasyLabel.Core.Projects.ProjectErrorCode.ManifestInvalid, "ProjErrManifestInvalid")]
    [InlineData(EasyLabel.Core.Projects.ProjectErrorCode.ImageRootMissing, "ProjErrImageMissing")]
    [InlineData(EasyLabel.Core.Projects.ProjectErrorCode.InvalidLayout, "ProjErrLayout")]
    [InlineData(EasyLabel.Core.Projects.ProjectErrorCode.NotANamedProject, "ProjErrNotNamed")]
    public void Messages_MapEveryProjectErrorCodeToAKey(EasyLabel.Core.Projects.ProjectErrorCode code, string key)
    {
        EasyAI.Desktop.Infrastructure.ProjectMessage message = EasyAI.Desktop.Infrastructure.ProjectMessages.For(
            new EasyLabel.Core.Projects.ProjectException(code, "English text", @"C:\p\thing"));

        Assert.Equal(key, message.Key);
        if (code != EasyLabel.Core.Projects.ProjectErrorCode.InvalidLayout && code != EasyLabel.Core.Projects.ProjectErrorCode.NotANamedProject)
        {
            Assert.Equal(@"C:\p\thing", message.Argument);
        }
    }

    [Fact]
    public void Messages_MapAnInvalidNameByItsRule_AndFallBackToTheEnglishTextForUnknownCases()
    {
        EasyAI.Desktop.Infrastructure.ProjectMessage name = EasyAI.Desktop.Infrastructure.ProjectMessages.For(
            new EasyLabel.Core.Projects.ProjectException(EasyLabel.Core.Projects.ProjectErrorCode.InvalidName, "bad", null, EasyLabel.Core.Projects.ProjectNameError.ReservedName));
        Assert.Equal("ProjErrNameReserved", name.Key);

        // A manifest problem without a path shows the English detail instead of an empty text.
        EasyAI.Desktop.Infrastructure.ProjectMessage invalid = EasyAI.Desktop.Infrastructure.ProjectMessages.For(
            new EasyLabel.Core.Projects.ProjectException(EasyLabel.Core.Projects.ProjectErrorCode.ManifestInvalid, "schema 9 is not supported"));
        Assert.Equal("ProjErrManifestInvalid", invalid.Key);
        Assert.Equal("schema 9 is not supported", invalid.Argument);
    }

    [Fact]
    public void EveryMessageKeyAndPhaseKeyExistsInBothLanguageFiles()
    {
        string resources = ResourcesFolder();
        string[] keys =
        {
            EasyAI.Desktop.Infrastructure.ProjectMessages.NameEmpty, EasyAI.Desktop.Infrastructure.ProjectMessages.NameTooLong, EasyAI.Desktop.Infrastructure.ProjectMessages.NameChars,
            EasyAI.Desktop.Infrastructure.ProjectMessages.NameDot, EasyAI.Desktop.Infrastructure.ProjectMessages.NameReserved, EasyAI.Desktop.Infrastructure.ProjectMessages.NameSuffix,
            EasyAI.Desktop.Infrastructure.ProjectMessages.LocationEmpty, EasyAI.Desktop.Infrastructure.ProjectMessages.LocationInvalid, EasyAI.Desktop.Infrastructure.ProjectMessages.ImageMissing,
            EasyAI.Desktop.Infrastructure.ProjectMessages.TargetExists, EasyAI.Desktop.Infrastructure.ProjectMessages.ManifestMissing, EasyAI.Desktop.Infrastructure.ProjectMessages.ManifestInvalid,
            EasyAI.Desktop.Infrastructure.ProjectMessages.Layout, EasyAI.Desktop.Infrastructure.ProjectMessages.NotNamed, EasyAI.Desktop.Infrastructure.ProjectMessages.Generic,
            EasyAI.Desktop.Infrastructure.ProjectCopyProgressRules.PhaseKey(EasyLabel.Core.Projects.ProjectCopyPhase.Scanning),
            EasyAI.Desktop.Infrastructure.ProjectCopyProgressRules.PhaseKey(EasyLabel.Core.Projects.ProjectCopyPhase.CopyingData),
            EasyAI.Desktop.Infrastructure.ProjectCopyProgressRules.PhaseKey(EasyLabel.Core.Projects.ProjectCopyPhase.CopyingImages),
            EasyAI.Desktop.Infrastructure.ProjectCopyProgressRules.PhaseKey(EasyLabel.Core.Projects.ProjectCopyPhase.Finishing),
            EasyAI.Desktop.Infrastructure.ProjectCopyProgressRules.PhaseKey(EasyLabel.Core.Projects.ProjectCopyPhase.Done),
        };

        foreach (string file in new[] { "Strings.resx", "Strings.vi.resx" })
        {
            System.Collections.Generic.HashSet<string> names = new System.Collections.Generic.HashSet<string>(
                System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(resources, file)).Root!.Elements("data").Select(e => (string)e.Attribute("name")!));
            foreach (string key in keys)
            {
                Assert.True(names.Contains(key), key + " is missing in " + file);
            }
        }
    }

    private static string ResourcesFolder([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        string testsDir = System.IO.Path.GetDirectoryName(thisFile)!;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(testsDir, "..", "..", "src", "EasyAI.Wpf", "Resources"));
    }

    [Fact]
    public void Title_ShowsTheProjectNameForANamedProjectAndTheImageFolderForALegacyOne()
    {
        string separator = " \u00B7 ";
        Assert.Equal("EasyAI" + separator + "Demo", EasyAI.Desktop.Infrastructure.ProjectTitleRules.BaseTitle("EasyAI", "Demo", @"D:\Images"));
        Assert.Equal("EasyAI" + separator + @"D:\Images", EasyAI.Desktop.Infrastructure.ProjectTitleRules.BaseTitle("EasyAI", null, @"D:\Images"));
        Assert.Equal("EasyAI" + separator + @"D:\Images", EasyAI.Desktop.Infrastructure.ProjectTitleRules.BaseTitle("EasyAI", "  ", @"D:\Images"));
        Assert.Equal("EasyAI", EasyAI.Desktop.Infrastructure.ProjectTitleRules.BaseTitle("EasyAI", null, null));
    }

    [Fact]
    public void RecentRows_ShowTheFolderOfAProjectFileOrTheImageFolder()
    {
        EasyLabel.Core.Projects.RecentProject named = new EasyLabel.Core.Projects.RecentProject(
            @"C:\Work\Projects\Demo.easyai", "Demo", System.DateTime.UtcNow, EasyLabel.Core.Projects.RecentProjectKind.Named);
        EasyLabel.Core.Projects.RecentProject folder = new EasyLabel.Core.Projects.RecentProject(
            @"D:\Images", "Images", System.DateTime.UtcNow, EasyLabel.Core.Projects.RecentProjectKind.Folder);

        Assert.Equal(@"C:\Work\Projects", EasyAI.Desktop.Infrastructure.RecentProjectDisplay.SecondaryText(named));
        Assert.Equal(@"D:\Images", EasyAI.Desktop.Infrastructure.RecentProjectDisplay.SecondaryText(folder));
    }

    [Fact]
    public void CopyProgress_HasAnIndeterminateScanAndAnOrderedFraction()
    {
        Assert.Null(EasyAI.Desktop.Infrastructure.ProjectCopyProgressRules.Fraction(
            new EasyLabel.Core.Projects.ProjectCopyProgress(EasyLabel.Core.Projects.ProjectCopyPhase.Scanning, 0, 0, 0, 0, string.Empty)));
        Assert.Equal(0.25, EasyAI.Desktop.Infrastructure.ProjectCopyProgressRules.Fraction(
            new EasyLabel.Core.Projects.ProjectCopyProgress(EasyLabel.Core.Projects.ProjectCopyPhase.CopyingImages, 1, 4, 25, 100, "a.jpg")));
        Assert.Equal(0.5, EasyAI.Desktop.Infrastructure.ProjectCopyProgressRules.Fraction(
            new EasyLabel.Core.Projects.ProjectCopyProgress(EasyLabel.Core.Projects.ProjectCopyPhase.CopyingData, 2, 4, 0, 0, "x")));
        Assert.Equal(1d, EasyAI.Desktop.Infrastructure.ProjectCopyProgressRules.Fraction(
            new EasyLabel.Core.Projects.ProjectCopyProgress(EasyLabel.Core.Projects.ProjectCopyPhase.Done, 0, 0, 0, 0, string.Empty)));
        Assert.Equal(1d, EasyAI.Desktop.Infrastructure.ProjectCopyProgressRules.Fraction(
            new EasyLabel.Core.Projects.ProjectCopyProgress(EasyLabel.Core.Projects.ProjectCopyPhase.CopyingImages, 9, 9, 200, 100, "over")));
    }

    [Fact]
    public void Defaults_UseTheLastLocationWhenItIsUsable_ElseDocumentsEasyAiProjects()
    {
        Assert.Equal(@"D:\Last", EasyAI.Desktop.Infrastructure.ProjectDefaults.Location(@"D:\Last", @"C:\Users\me\Documents"));
        Assert.Equal(@"C:\Users\me\Documents\EasyAI Projects", EasyAI.Desktop.Infrastructure.ProjectDefaults.Location(null, @"C:\Users\me\Documents"));
        Assert.Equal(@"C:\Users\me\Documents\EasyAI Projects", EasyAI.Desktop.Infrastructure.ProjectDefaults.Location("relative", @"C:\Users\me\Documents"));
    }

    [Fact]
    public void FreeName_AddsANumberUntilTheProjectFileDataFolderAndImagesFolderAreFree()
    {
        System.Collections.Generic.HashSet<string> taken = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            EasyLabel.Core.Projects.ProjectManager.ManifestPathFor(Location, "Project"),
            EasyLabel.Core.Projects.ProjectManager.ImagesFolderPathFor(Location, "Project 2"),
        };

        Assert.Equal("Project 3", EasyAI.Desktop.Infrastructure.ProjectDefaults.FreeName("Project", Location, taken.Contains));
        Assert.Equal("Other", EasyAI.Desktop.Infrastructure.ProjectDefaults.FreeName("Other", Location, taken.Contains));
        // A stem that is not a valid name falls back to a plain default.
        Assert.Equal("Project 3", EasyAI.Desktop.Infrastructure.ProjectDefaults.FreeName("a:b", Location, taken.Contains));
    }
}
