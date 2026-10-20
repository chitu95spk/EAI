using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using EasyAI.Desktop.Commands;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Online;
using Xunit;

namespace EasyAI.Wpf.Tests
{
    public sealed class OnlineStatusViewModelTests
    {
        private sealed class Host : IInferenceHost
        {
            public IReadOnlyList<Detection> Infer(OnlineFrame frame, IDictionary<string, double> stageMs)
            {
                return new List<Detection>();
            }
        }

        [Fact]
        public void TryParseStatusHandlesIdleFullMissingP95NegativeAndMalformedJson()
        {
            OnlineStatusViewModel.OnlineStatusSnapshot snapshot;

            Assert.True(OnlineStatusViewModel.TryParseStatus("{\"running\":false}", out snapshot));
            Assert.False(snapshot.Running);
            Assert.Equal(0, snapshot.Accepted);
            Assert.Null(snapshot.TotalP95Ms);

            Assert.True(OnlineStatusViewModel.TryParseStatus("{\"running\":true,\"recipeId\":\"r1\",\"accepted\":2,\"dropped\":3,\"completed\":4,\"failed\":5,\"totalP95Ms\":12.5}", out snapshot));
            Assert.True(snapshot.Running);
            Assert.Equal("r1", snapshot.RecipeId);
            Assert.Equal(2, snapshot.Accepted);
            Assert.Equal(3, snapshot.Dropped);
            Assert.Equal(4, snapshot.Completed);
            Assert.Equal(5, snapshot.Failed);
            Assert.Equal(12.5, snapshot.TotalP95Ms.Value);

            Assert.True(OnlineStatusViewModel.TryParseStatus("{\"running\":true,\"accepted\":1,\"completed\":1,\"dropped\":0,\"failed\":0}", out snapshot));
            Assert.Null(snapshot.TotalP95Ms);
            Assert.False(OnlineStatusViewModel.TryParseStatus("{\"running\":true,\"accepted\":-1}", out snapshot));
            Assert.False(OnlineStatusViewModel.TryParseStatus("{", out snapshot));
        }

        [Theory]
        [InlineData("0", "0", true)]
        [InlineData("1", "1", true)]
        [InlineData("0.5", "0.5", true)]
        [InlineData("1.01", "0.5", false)]
        [InlineData("-0.1", "0.5", false)]
        [InlineData("NaN", "0.5", false)]
        [InlineData("abc", "0.5", false)]
        [InlineData("0,5", "0.5", false)]
        public void TryParseThresholdsAcceptsOnlyInvariantUnitRange(string scoreText, string nmsText, bool expected)
        {
            double score;
            double nms;
            Assert.Equal(expected, OnlineStatusViewModel.TryParseThresholds(scoreText, nmsText, out score, out nms));
        }

        [Fact]
        public void BudgetStateUsesUnknownOkAndOverIncludingExactBudgetBoundary()
        {
            RunOnSta(() =>
            {
                var viewModel = new OnlineStatusViewModel(new OnlineControlAdapter(), 100);
                try
                {
                    FieldInfo p95 = typeof(OnlineStatusViewModel).GetField("_totalP95Ms", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(p95);
                    p95.SetValue(viewModel, null);
                    Assert.Equal(BudgetState.Unknown, viewModel.BudgetState);
                    p95.SetValue(viewModel, 100.0);
                    Assert.Equal(BudgetState.Ok, viewModel.BudgetState);
                    p95.SetValue(viewModel, 100.001);
                    Assert.Equal(BudgetState.Over, viewModel.BudgetState);
                }
                finally
                {
                    viewModel.Dispose();
                }
            });
        }

        [Fact]
        public void CommandsFollowRunningStatus()
        {
            RunOnSta(() =>
            {
                var adapter = new OnlineControlAdapter();
                OnlineRunner runner = new OnlineRunner(new Host(), new RecipeEvaluator(CreateRecipe()), "vm", 2, OverloadPolicy.DropNewest);
                runner.Start();
                adapter.Attach(new OnlineSession(runner, CreateRecipe(), "vm"));
                var viewModel = new OnlineStatusViewModel(adapter);
                try
                {
                    Assert.True(viewModel.StopCommand.CanExecute(null));
                    Assert.True(viewModel.ApplyThresholdCommand.CanExecute(null));
                    viewModel.StopCommand.Execute(null);
                    Assert.False(viewModel.StopCommand.CanExecute(null));
                    Assert.False(viewModel.ApplyThresholdCommand.CanExecute(null));
                }
                finally
                {
                    viewModel.Dispose();
                    adapter.Detach();
                    runner.Dispose();
                }
            });
        }

        [Fact]
        public void AdapterAttachDetachAndStoppedSessionReportNotRunning()
        {
            var adapter = new OnlineControlAdapter();
            Assert.False(adapter.IsRunning);
            Assert.Equal("NOT_RUNNING", adapter.SetThreshold(null, 0.5, 0.5));
            Assert.Equal("{\"running\":false}", adapter.StatusJson());

            OnlineRunner runner = new OnlineRunner(new Host(), new RecipeEvaluator(CreateRecipe()), "adapter", 2, OverloadPolicy.DropNewest);
            var session = new OnlineSession(runner, CreateRecipe(), "adapter");
            adapter.Attach(session);
            try
            {
                Assert.False(adapter.IsRunning);
                Assert.Equal("NOT_RUNNING", adapter.SetThreshold(null, 0.5, 0.5));
                runner.Start();
                Assert.True(adapter.IsRunning);
                Assert.Null(adapter.SetThreshold("scratch", 0.5, 0.5));
                adapter.Detach();
                Assert.False(adapter.IsRunning);
                Assert.Equal("NOT_RUNNING", adapter.SetThreshold(null, 0.5, 0.5));
            }
            finally
            {
                adapter.Detach();
                runner.Dispose();
            }
        }

        private static void RunOnSta(Action action)
        {
            Exception failure = null;
            using (var complete = new ManualResetEventSlim(false))
            {
                Thread thread = new Thread(() =>
                {
                    Dispatcher.CurrentDispatcher.ToString();
                    try { action(); }
                    catch (Exception ex) { failure = ex; }
                    finally
                    {
                        Dispatcher.CurrentDispatcher.InvokeShutdown();
                        complete.Set();
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                Assert.True(complete.Wait(5000));
                Assert.True(thread.Join(1000));
            }
            if (failure != null) throw failure;
        }

        private static Recipe CreateRecipe()
        {
            return new Recipe
            {
                SchemaRev = 1,
                CatalogVersion = "test",
                Classes = new List<RecipeClass>
                {
                    new RecipeClass { Name = "scratch", MinScore = 0.45, MinAreaPx = 120, MaxCount = 10, Roi = new double[] { 0, 0, 640, 480 } }
                },
                Position = new PositionParameters { Mode = "bbox", Units = "px", Calib = null },
                Nms = 0.5,
                Verdict = new VerdictRule { NgIf = "any(scratch)" },
                Preprocess = new PreprocessParameters
                {
                    InputWidth = 640, InputHeight = 480, Channels = 3, ColorOrder = "RGB", Interpolation = "bilinear",
                    Scale = 1.0 / 255, Mean = new double[] { 0, 0, 0 }, Std = new double[] { 1, 1, 1 }, Letterbox = true, Seed = 42
                },
                Wire = new WireParameters { Codec = "json", IncludeDetections = true, MaxPayloadBytes = 65536 },
                Thresholds = new Dictionary<string, double> { { "score", 0.45 } },
                ClassMap = new Dictionary<int, string> { { 0, "scratch" } },
                Model = new ModelArtifact { FileName = "model.onnx", Sha256 = new string('0', 64) }
            };
        }
    }
}
