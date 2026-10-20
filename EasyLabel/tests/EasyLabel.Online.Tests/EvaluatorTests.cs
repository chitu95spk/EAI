using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EasyLabel.Online;
using Xunit;

namespace EasyLabel.Online.Tests
{
    public sealed class EvaluatorTests
    {
        [Theory]
        [InlineData("any(scratch)", true)]
        [InlineData("not any(dent)", true)]
        [InlineData("count(scratch)==1", true)]
        [InlineData("count(scratch)!=0", true)]
        [InlineData("count(scratch)>=1 and count(dent)<=0", true)]
        [InlineData("area(scratch)>149.5 and area(dent)<1", true)]
        [InlineData("area(scratch)==150", true)]
        [InlineData("area(scratch)!=150", false)]
        [InlineData("count(scratch)<1", false)]
        [InlineData("any(scratch) or any(dent) and not any(scratch)", true)]
        [InlineData("(any(scratch) or any(dent)) and not any(scratch)", false)]
        [InlineData("not not any(scratch)", true)]
        public void ParserAcceptsAndEvaluates(string expression, bool ng)
        {
            Recipe recipe = Fixtures.Recipe();
            recipe.Verdict.NgIf = expression;
            VerdictParser.Validate(expression, recipe.ClassMap.Values);
            Assert.Equal(ng ? "NG" : "OK", new RecipeEvaluator(recipe).Evaluate("f", "r", new[] { Fixtures.Detection() }).Verdict);
        }

        [Theory]
        [InlineData("")]
        [InlineData("any(unknown)")]
        [InlineData("Any(scratch)")]
        [InlineData("any(scratch); throw()")]
        [InlineData("any(scratch) || any(dent)")]
        [InlineData("count(scratch)")]
        [InlineData("count(scratch)=1")]
        [InlineData("count(scratch)>-1")]
        [InlineData("count(scratch)>1.5")]
        [InlineData("count(scratch)>1000001")]
        [InlineData("area(scratch)>NaN")]
        [InlineData("area(scratch)>Infinity")]
        [InlineData("area(scratch)>1e3")]
        [InlineData("area(scratch)>1.")]
        [InlineData("any('scratch')")]
        [InlineData("(any(scratch)")]
        [InlineData("any(scratch) trailing")]
        [InlineData("System.IO.File.Delete(scratch)")]
        public void ParserRejectsWithAnOffset(string expression)
        {
            VerdictParseException error = Assert.Throws<VerdictParseException>(() => VerdictParser.Validate(expression, new[] { "scratch", "dent" }));
            Assert.Contains("offset", error.Message);
            Assert.True(error.Position >= 0);
        }

        [Fact]
        public void ParserBoundsNesting()
        {
            Assert.Throws<VerdictParseException>(() => VerdictParser.Validate(new string('(', 65) + "any(scratch)" + new string(')', 65), new[] { "scratch" }));
        }

        [Fact]
        public void RepeatEvaluationIsByteIdenticalAndHasAGoldenResultRow()
        {
            Recipe recipe = Fixtures.Recipe();
            RecipeEvaluator evaluator = new RecipeEvaluator(recipe);
            Detection[] detections = { Fixtures.Detection() };
            Dictionary<string, double> stages = new Dictionary<string, double> { { "rules", 0.25 }, { "inference", 2 } };
            ResultRow first = evaluator.Evaluate("frame-1", "recipe-1", detections, stages);
            ResultRow second = evaluator.Evaluate("frame-1", "recipe-1", detections, stages);
            Assert.Equal(first.Verdict, second.Verdict);
            Assert.Equal(first.Serialize(), second.Serialize());
            Assert.Equal("{\"detections\":[{\"areaPx\":150,\"bbox\":{\"height\":20,\"width\":20,\"x\":10,\"y\":10},\"centroid\":{\"x\":20,\"y\":20},\"class\":\"scratch\",\"score\":0.9}],\"frameId\":\"frame-1\",\"perStageMs\":{\"inference\":2,\"rules\":0.25},\"recipeId\":\"recipe-1\",\"verdict\":\"NG\"}", Encoding.UTF8.GetString(first.Serialize()));
            recipe.Verdict.NgIf = "not any(scratch)";
            stages["rules"] = 99;
            Assert.Equal(first.Serialize(), evaluator.Evaluate("frame-1", "recipe-1", detections, new Dictionary<string, double> { { "inference", 2 }, { "rules", 0.25 } }).Serialize());
            Assert.Equal(0.25, first.PerStageMs["rules"]);
        }

        [Fact]
        public void FiltersThenSumsAreaAndAppliesMaxCount()
        {
            Recipe recipe = Fixtures.Recipe();
            recipe.Verdict.NgIf = "count(scratch)==2 and area(scratch)==300";
            Detection[] detections = { Fixtures.Detection(), Fixtures.Detection(), Fixtures.Detection(score: 0.1), Fixtures.Detection(area: 119), Fixtures.Detection(x: 630) };
            ResultRow result = new RecipeEvaluator(recipe).Evaluate("f", "r", detections);
            Assert.Equal("NG", result.Verdict);
            Assert.Equal(2, result.Detections.Count);
            recipe.Verdict.NgIf = "count(scratch)>5";
            recipe.Classes[0].MaxCount = 0;
            Assert.Equal("NG", new RecipeEvaluator(recipe).Evaluate("f", "r", new[] { Fixtures.Detection() }).Verdict);
            Assert.Equal("OK", new RecipeEvaluator(recipe).Evaluate("f", "r", new Detection[0]).Verdict);
        }

        [Fact]
        public void InvalidInputsAreRefused()
        {
            RecipeEvaluator evaluator = new RecipeEvaluator(Fixtures.Recipe());
            Assert.Throws<ArgumentException>(() => evaluator.Evaluate("f", "r", new[] { Fixtures.Detection("unknown") }));
            Assert.Throws<ArgumentException>(() => evaluator.Evaluate("f", "r", new[] { Fixtures.Detection(score: double.NaN) }));
            Assert.Throws<ArgumentException>(() => evaluator.Evaluate("f", "r", new Detection[0], new Dictionary<string, double> { { "rules", -1 } }));
        }

        [Fact]
        public void ResultRowContainsOnlyRuntimeFields()
        {
            Assert.Equal(new[] { "Detections", "FrameId", "PerStageMs", "RecipeId", "Verdict" }, typeof(ResultRow).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { "AreaPx", "Bbox", "Centroid", "Class", "Score" }, typeof(Detection).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Empty(typeof(ResultRow).GetFields());
        }
    }
}
