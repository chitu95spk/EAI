using EasyAI.Desktop.Infrastructure;
using EasyLabel.SegAI.Model;

namespace EasyLabel.SegAI.Tests;

public class ClassTableEditRulesTests
{
    private static List<SegAiClassEntry> Ng_Ok()
    {
        return new List<SegAiClassEntry>
        {
            new SegAiClassEntry("NG", SegAiClassKind.Defect, true),
            new SegAiClassEntry("OK", SegAiClassKind.Good, true),
        };
    }

    [Fact]
    public void Validate_ValidTable_ReturnsNull()
    {
        var classes = Ng_Ok();
        classes.Add(new SegAiClassEntry("scratch", SegAiClassKind.Defect, false));
        Assert.Null(ClassTableEditRules.Validate(classes));
    }

    [Fact]
    public void Validate_EmptyName_IsRejectedByModelRules()
    {
        var classes = Ng_Ok();
        classes.Add(new SegAiClassEntry("", SegAiClassKind.Defect, true));
        Assert.NotNull(ClassTableEditRules.Validate(classes));
    }

    [Fact]
    public void Validate_LeadingOrTrailingSpaces_AreRejected()
    {
        var classes = Ng_Ok();
        classes[0] = new SegAiClassEntry(" NG", SegAiClassKind.Defect, true);
        Assert.NotNull(ClassTableEditRules.Validate(classes));
        classes[0] = new SegAiClassEntry("NG ", SegAiClassKind.Defect, true);
        Assert.NotNull(ClassTableEditRules.Validate(classes));
    }

    [Fact]
    public void Validate_DuplicateNameIgnoringCase_IsRejected()
    {
        var classes = Ng_Ok();
        classes.Add(new SegAiClassEntry("ng", SegAiClassKind.Defect, true));
        Assert.NotNull(ClassTableEditRules.Validate(classes));
    }

    [Fact]
    public void Validate_TwoGoodClasses_IsRejected()
    {
        var classes = Ng_Ok();
        classes.Add(new SegAiClassEntry("spare", SegAiClassKind.Good, true));
        Assert.NotNull(ClassTableEditRules.Validate(classes));
    }

    [Fact]
    public void Validate_AllDisabled_IsRejected()
    {
        var classes = new List<SegAiClassEntry>
        {
            new SegAiClassEntry("NG", SegAiClassKind.Defect, false),
            new SegAiClassEntry("OK", SegAiClassKind.Good, false),
        };
        Assert.NotNull(ClassTableEditRules.Validate(classes));
    }

    [Fact]
    public void Validate_TooManyClasses_IsRejected()
    {
        var classes = new List<SegAiClassEntry>();
        for (int i = 0; i <= SegAiClassTable.MaxClasses; i++)
        {
            classes.Add(new SegAiClassEntry("c" + i, SegAiClassKind.Defect, true));
        }

        Assert.NotNull(ClassTableEditRules.Validate(classes));
    }

    [Fact]
    public void HasChanges_IdenticalTable_IsFalse()
    {
        Assert.False(ClassTableEditRules.HasChanges(Ng_Ok(), Ng_Ok()));
    }

    [Fact]
    public void HasChanges_RenameKindEnabledOrAdd_IsTrue()
    {
        var renamed = Ng_Ok();
        renamed[0] = new SegAiClassEntry("Scratch", SegAiClassKind.Defect, true);
        Assert.True(ClassTableEditRules.HasChanges(Ng_Ok(), renamed));

        var kind = Ng_Ok();
        kind[1] = new SegAiClassEntry("OK", SegAiClassKind.Defect, true);
        Assert.True(ClassTableEditRules.HasChanges(Ng_Ok(), kind));

        var disabled = Ng_Ok();
        disabled[1] = new SegAiClassEntry("OK", SegAiClassKind.Good, false);
        Assert.True(ClassTableEditRules.HasChanges(Ng_Ok(), disabled));

        var added = Ng_Ok();
        added.Add(new SegAiClassEntry("dent", SegAiClassKind.Defect, true));
        Assert.True(ClassTableEditRules.HasChanges(Ng_Ok(), added));
    }
}
