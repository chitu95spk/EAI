using EasyLabel.Core.Model;

namespace EasyLabel.Core.Tests;

public sealed class ImageClassFlagsTests
{
    [Fact]
    public void Null_empty_or_both_flags_read_as_unmarked()
    {
        Assert.Equal(ImageClassFlags.Unmarked, ImageClassFlags.Read(null));
        Assert.Equal(ImageClassFlags.Unmarked, ImageClassFlags.Read(new Dictionary<string, bool>()));
        Assert.Equal(ImageClassFlags.Unmarked, ImageClassFlags.Read(new Dictionary<string, bool> { ["ok"] = true, ["ng"] = true }));
        Assert.Equal(ImageClassFlags.Unmarked, ImageClassFlags.Read(new Dictionary<string, bool> { ["ok"] = false, ["ng"] = false }));
    }

    [Theory]
    [InlineData("ok", ImageClassFlags.Ok)]
    [InlineData("OK", ImageClassFlags.Ok)]
    [InlineData("Ng", ImageClassFlags.Ng)]
    [InlineData("NG", ImageClassFlags.Ng)]
    public void Keys_are_case_insensitive(string key, byte expected)
    {
        Assert.Equal(expected, ImageClassFlags.Read(new Dictionary<string, bool> { [key] = true }));
    }

    [Fact]
    public void False_values_and_other_flags_do_not_count()
    {
        var flags = new Dictionary<string, bool> { ["ok"] = false, ["ng"] = true, ["train"] = true };
        Assert.Equal(ImageClassFlags.Ng, ImageClassFlags.Read(flags));
    }

    [Fact]
    public void Apply_sets_one_flag_removes_the_other_in_any_case_and_keeps_other_flags()
    {
        var flags = new Dictionary<string, bool> { ["OK"] = true, ["train"] = true, ["val"] = false };
        ImageClassFlags.Apply(flags, ImageClassFlags.Ng);

        Assert.False(flags.ContainsKey("OK"));
        Assert.True(flags["ng"]);
        Assert.True(flags["train"]);
        Assert.False(flags["val"]);
        Assert.Equal(ImageClassFlags.Ng, ImageClassFlags.Read(flags));
    }

    [Fact]
    public void Switching_mark_is_mutually_exclusive()
    {
        var flags = new Dictionary<string, bool> { ["ng"] = true };
        ImageClassFlags.Apply(flags, ImageClassFlags.Ok);
        Assert.False(flags.ContainsKey("ng"));
        Assert.Equal(ImageClassFlags.Ok, ImageClassFlags.Read(flags));
    }

    [Fact]
    public void Unmarked_removes_both_keys_and_invalid_value_is_refused()
    {
        var flags = new Dictionary<string, bool> { ["ok"] = true, ["Ng"] = false, ["val"] = true };
        ImageClassFlags.Apply(flags, ImageClassFlags.Unmarked);
        Assert.Single(flags);
        Assert.True(flags["val"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageClassFlags.Apply(flags, 3));
    }
}
