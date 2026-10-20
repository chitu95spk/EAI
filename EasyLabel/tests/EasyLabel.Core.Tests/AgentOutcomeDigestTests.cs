using EasyLabel.Core.Io;

namespace EasyLabel.Core.Tests
{
    public sealed class AgentOutcomeDigestTests
    {
        private static AgentOutcome Make(string label, bool accepted, double w, double h, string shape = "rectangle")
        {
            return new AgentOutcome(new System.DateTime(2026, 10, 11, 8, 0, 0, System.DateTimeKind.Utc), "a.png", "r", label, shape, "add", accepted, w, h);
        }

        private static System.Collections.Generic.List<AgentOutcome> Many(string label, bool accepted, int count, double w, double h)
        {
            return Enumerable.Range(0, count).Select(i => Make(label, accepted, w, h)).ToList();
        }

        private static System.Collections.Generic.List<AgentOutcome> Sample()
        {
            System.Collections.Generic.List<AgentOutcome> list = Many("Pad", true, 14, 0.10, 0.20);
            list.AddRange(Many("Pad", false, 6, 0.30, 0.20));
            list.AddRange(Many("Scratch", true, 2, 0.10, 0.10));
            list.AddRange(Many("Scratch", false, 8, 0.30, 0.20));
            return list;
        }

        [Xunit.Fact]
        public void TheDigestIsDeterministic()
        {
            string first = AgentOutcomeDigest.Build(Sample());
            string second = AgentOutcomeDigest.Build(Sample());
            Xunit.Assert.NotEqual(string.Empty, first);
            Xunit.Assert.Equal(first, second);

            // The input order does not matter either.
            System.Collections.Generic.List<AgentOutcome> reversed = Sample();
            reversed.Reverse();
            Xunit.Assert.Equal(first, AgentOutcomeDigest.Build(reversed));
        }

        [Xunit.Fact]
        public void FewerThanFiveRecordsGiveNothing()
        {
            Xunit.Assert.Equal(string.Empty, AgentOutcomeDigest.Build(Many("Pad", true, 4, 0.1, 0.1)));
            Xunit.Assert.Equal(string.Empty, AgentOutcomeDigest.Build(new AgentOutcome[0]));
        }

        [Xunit.Fact]
        public void ManyLabelsEachTooRareToCountGiveNothing()
        {
            System.Collections.Generic.List<AgentOutcome> list = Enumerable.Range(0, 10).Select(i => Make("L" + i, true, 0.1, 0.1)).ToList();
            Xunit.Assert.Equal(string.Empty, AgentOutcomeDigest.Build(list));
        }

        [Xunit.Fact]
        public void TheDigestNeverExceedsMaxCharsAndKeepsWholeLines()
        {
            System.Collections.Generic.List<AgentOutcome> list = new System.Collections.Generic.List<AgentOutcome>();
            for (int i = 0; i < 20; i++)
            {
                list.AddRange(Many("Label" + i.ToString("D2"), true, 4, 0.1, 0.1));
                list.AddRange(Many("Label" + i.ToString("D2"), false, 4, 0.3, 0.3));
            }

            string full = AgentOutcomeDigest.Build(list, 100000);
            Xunit.Assert.True(full.Length > 600);
            foreach (int max in new[] { 1, 50, 200, 300, 600, 1200 })
            {
                string cut = AgentOutcomeDigest.Build(list, max);
                Xunit.Assert.True(cut.Length <= max, "max " + max);
                if (cut.Length > 0)
                {
                    Xunit.Assert.StartsWith(cut, full);
                    Xunit.Assert.True(full.Length == cut.Length || full[cut.Length] == '\n', "whole lines at max " + max);
                }
            }

            Xunit.Assert.Equal(string.Empty, AgentOutcomeDigest.Build(list, 0));
        }

        [Xunit.Fact]
        public void OneMoreRecordInTheSameBucketsLeavesTheTextUnchanged()
        {
            string before = AgentOutcomeDigest.Build(Sample());

            // Pad: 14/20 accepted = 70 %, 15/21 = 71.4 % -> still ~70 %, still the "some" band, same size buckets (0.11 and 0.19 round to 10 % / 20 %).
            System.Collections.Generic.List<AgentOutcome> more = Sample();
            more.Add(Make("Pad", true, 0.11, 0.19));
            Xunit.Assert.Equal(before, AgentOutcomeDigest.Build(more));

            // A further rejected record of an already listed pattern keeps the digest too (Scratch 2/10 -> 2/11 = 18 % -> ~20 %).
            more.Add(Make("Scratch", false, 0.31, 0.21));
            Xunit.Assert.Equal(before, AgentOutcomeDigest.Build(more));
        }

        [Xunit.Fact]
        public void RatesSizesAndRejectedPatternsAreSummarisedPerLabel()
        {
            string text = AgentOutcomeDigest.Build(Sample());
            string[] lines = text.Split('\n');

            Xunit.Assert.StartsWith("Learned from review", lines[0]);
            Xunit.Assert.Equal("- Pad: accept rate ~70% (some reviews); accepted size width ~10%, height ~20%", lines[1]);
            Xunit.Assert.Equal("- Scratch: accept rate ~20% (some reviews); accepted size width ~10%, height ~10%", lines[2]);
            Xunit.Assert.Contains("Most often rejected (label, shape, size):", lines);
            Xunit.Assert.Contains("- Scratch, rectangle, width ~30% height ~20%", lines);
            Xunit.Assert.Contains("- Pad, rectangle, width ~30% height ~20%", lines);
            Xunit.Assert.True(System.Array.IndexOf(lines, "- Scratch, rectangle, width ~30% height ~20%") < System.Array.IndexOf(lines, "- Pad, rectangle, width ~30% height ~20%"));
            Xunit.Assert.DoesNotContain("\r", text);
        }

        [Xunit.Fact]
        public void TheAcceptedSizeRangeUsesTheTenthAndNinetiethPercentile()
        {
            System.Collections.Generic.List<AgentOutcome> list = new System.Collections.Generic.List<AgentOutcome>();
            for (int i = 1; i <= 10; i++)
            {
                list.Add(Make("Pad", true, i * 0.05, 0.40));
            }

            // Widths 5 %..50 %: nearest-rank p10 = 5 %, p90 = 45 %.
            Xunit.Assert.Contains("- Pad: accept rate ~100% (some reviews); accepted size width 5-45%, height ~40%", AgentOutcomeDigest.Build(list));
        }
    }
}
