using EasyLabel.SegAI.Options;
using Newtonsoft.Json;

namespace EasyLabel.SegAI.Tests;

public sealed class SegTrainingOptionsTests
{
    [Fact]
    public void Defaults_validate()
    {
        new SegTrainingOptions().Validate();
    }

    [Fact]
    public void Invalid_input_size_is_rejected()
    {
        var options = new SegTrainingOptions { InputSize = 650 };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Json_round_trip_is_equal()
    {
        var options = new SegTrainingOptions { Version = 2, Epochs = 123, EarlyStopPatience = 20, V2LegacyPrior = true };
        options.V2Scale = new SegObjectScaleRange { Minimum = .7f, Maximum = 1.4f, OffScale = true };
        string first = JsonConvert.SerializeObject(options);
        var back = JsonConvert.DeserializeObject<SegTrainingOptions>(first)!;
        Assert.Equal(first, JsonConvert.SerializeObject(back));
        Assert.Equal(123, back.Epochs);
        Assert.True(back.V2Scale.OffScale);
        Assert.Equal(.7f, back.V2Scale.Minimum);
    }

    [Fact]
    public void Legacy_prior_foreground_is_odds_corrected()
    {
        Assert.Equal(0.9375f, SegTrainingOptions.EffectiveV2Foreground(0.5f, false, false), 4);
        Assert.Equal(0.5f, SegTrainingOptions.EffectiveV2Foreground(0.5f, true, false));
    }
}
