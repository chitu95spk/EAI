using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Commands;
using EasyLabel.Commands.Discovery;
using EasyLabel.Commands.Policy;
using EasyLabel.Commands.Results;
using EasyLabel.Core.Editing;

// ICommand's parameter is intentionally null for parameterless VM commands (C# 7.3-compatible declarations).
#pragma warning disable CS8600, CS8603, CS8604, CS8625
namespace EasyAI.Desktop.Commands
{
    /// <summary>App-owned composition service; all VM reads, CanExecute and execution enter the UI dispatcher.</summary>
    public sealed class WpfCommandHost : ICommandContext, IViewModelCommandSource, ICommandDispatcher
    {
        private readonly MainViewModel _main;
        private readonly Dispatcher _dispatcher;
        private readonly CommandInvoker _invoker;
        private readonly Dictionary<CommandId, DiscoveredCommand> _commands;

        public WpfCommandHost(MainViewModel main, Dispatcher dispatcher, CommandRegistry registry)
        {
            _main = main ?? throw new ArgumentNullException(nameof(main));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            var scanner = new CommandScanner();
            Catalog = OnlineCommandDescriptors.Discover(new ViewModelCommandHandlers(this, this), new RunModeHandlers(this, this));
            scanner.Register(Registry, Catalog);
            _commands = Catalog.ToDictionary(c => c.Descriptor.Id);
            _invoker = new CommandInvoker(Registry);
            _main.SlashCommandHost = this;
        }

        public CommandRegistry Registry { get; private set; }
        public IReadOnlyList<DiscoveredCommand> Catalog { get; private set; }
        public string Mode { get { return Invoke(() => _main.IsSimpleMode ? CommandModes.Simple : CommandModes.Advanced); } }
        public CtxFlags Flags
        {
            get
            {
                return Invoke(() =>
                {
                    CtxFlags flags = _main.HasProject ? CtxFlags.ProjectOpen : CtxFlags.None;
                    if (_main.SelectedImage != null) flags |= CtxFlags.ImageSelected;
                    if (_main.Canvas.HasSelection) flags |= CtxFlags.ShapeSelected;
                    bool busy = _main.IsBusy || _main.Label.IsRunning || _main.Label.IsBatchRunning ||
                        _main.Train.IsRunning || _main.Inspect.IsRunning;
                    flags |= busy ? CtxFlags.Busy : CtxFlags.NotBusy;
                    if (_main.Live.IsConnected && _main.Live.IsLiveWanted && !_main.Live.IsFrozen)
                        flags |= CtxFlags.CameraLive;
                    return flags;
                });
            }
        }

        public bool CanExecute(CommandId id, JsonElement args)
        {
            DiscoveredCommand command;
            JsonElement validated;
            string error;
            return _commands.TryGetValue(id, out command) &&
                command.Descriptor.ArgsSchema.TryValidate(args, out validated, out error) &&
                command.Endpoint.CanExecute(this, validated);
        }

        public Task<CommandResult> ExecuteAsync(CommandId id, JsonElement args,
            CommandOrigin origin = CommandOrigin.UserDirect, CommandPolicyEvidence evidence = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return InvokeAsync(() => _invoker.ExecuteAsync(id, args, this, origin, evidence, cancellationToken), cancellationToken);
        }

        public T Invoke<T>(Func<T> action)
        {
            return _dispatcher.CheckAccess() ? action() : _dispatcher.Invoke(action);
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_dispatcher.CheckAccess()) return action();
            return _dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task.Unwrap();
        }

        public ViewModelCommandTarget Resolve(string id, JsonElement args)
        {
            _dispatcher.VerifyAccess();
            if (id.StartsWith("AGT.", StringComparison.Ordinal) && id != "AGT.CHECK" && id != "AGT.PROMPT.SUGGEST")
                return _main.Label.ResolveSlashCommandTarget(id, args);
            switch (id)
            {
                case "PRJ.NEW": return Target(_main.NewProjectCommand);
                case "PRJ.OPEN.FILE": return Target(_main.OpenProjectFileDialogCommand);
                case "PRJ.OPEN.FOLDER": return Target(_main.OpenFolderCommand);
                case "PRJ.OPEN.RECENT":
                    int index = args.GetProperty("index").GetInt32();
                    return Target(_main.OpenRecentCommand, index < _main.Recent.Items.Count ? _main.Recent.Items[index] : null);
                case "PRJ.SAVE": return Target(_main.SaveProjectCommand);
                case "PRJ.SAVE.AS": return Target(_main.SaveProjectAsCommand);
                case "IMG.NEXT": return Target(_main.NextImageCommand);
                case "IMG.PREV": return Target(_main.PreviousImageCommand);
                case "IMG.ADD": return Target(_main.Data.AddImagesCommand);
                case "IMG.DELETE": return Target(Text(args, "scope", "image") == "annotation" ?
                    (ICommand)_main.DeleteAnnotationFilesCommand : _main.DeleteImageCommand);
                case "CAN.TOOL":
                    string tool = Text(args, "tool");
                    if (tool == "aipoints") return Target(_main.Canvas.SelectAiPointsCommand);
                    if (tool == "aibox") return Target(_main.Canvas.SelectAiBoxCommand);
                    return Target(_main.Canvas.SelectToolCommand, Enum.Parse(typeof(CanvasTool), tool, true));
                case "CAN.UNDO": return Target(_main.Canvas.UndoCommand);
                case "CAN.REDO": return Target(_main.Canvas.RedoCommand);
                case "CAN.SHAPE.DELETE": return Target(_main.Canvas.DeleteCommand);
                case "CAN.SHAPE.MERGE": return Target(_main.Canvas.MergeShapesCommand);
                case "CAN.SHAPE.CONVERT":
                    string shape = Text(args, "to");
                    return Target(_main.Canvas.ConvertShapeCommand, shape == "rotated" ? "oriented_rectangle" : shape);
                case "CAN.CLASS.SET": return Target(_main.Canvas.SetClassIndexCommand, args.GetProperty("index").GetInt32());
                case "CAN.VIEW.ZOOM":
                    switch (Text(args, "mode"))
                    {
                        case "in": return Target(_main.Canvas.ZoomInCommand);
                        case "out": return Target(_main.Canvas.ZoomOutCommand);
                        case "fit": return Target(_main.Canvas.FitCommand);
                        case "actual": return Target(_main.Canvas.ActualSizeCommand);
                        case "width": return Target(_main.Canvas.FitWidthCommand);
                    }
                    break;
                case "CAN.SHAPES.TOGGLE":
                    JsonElement visible;
                    return Target(args.TryGetProperty("visible", out visible) ?
                        (visible.GetBoolean() ? (ICommand)_main.Canvas.ShowShapesCommand : _main.Canvas.HideShapesCommand) :
                        _main.Canvas.ToggleShapesCommand);
                case "LBL.SOURCE.SET":
                    string source = Text(args, "source");
                    AutoLabelSource value = source == "segment" ? AutoLabelSource.SegmentModel :
                        source == "ai" ? AutoLabelSource.AiModel : AutoLabelSource.AgentPrompt;
                    return Target(_main.Label.SetSourceCommand, value);
                case "LBL.RUN.AGENT":
                    string scope = Text(args, "scope", "current");
                    return scope == "current" ? Target(_main.Label.RunAgentCommand) : Target(_main.Label.RunBatchCommand, scope);
                case "LBL.RUN.SEG": return Target(Text(args, "scope", "current") == "all" ?
                    (ICommand)_main.Label.AutoLabelAllCommand : _main.Label.AutoLabelCurrentCommand);
                case "LBL.RUN.AI": return Target(_main.Label.RunAiSourceCommand);
                case "LBL.CANCEL": return Target(_main.Label.CancelRunCommand);
                case "LBL.ACCEPT": return Target(Text(args, "which", "all") == "selected" ?
                    (ICommand)_main.Label.AcceptSelectedCommand : _main.Label.AcceptAllCommand);
                case "LBL.REJECT.ALL": return Target(_main.Label.RejectAllCommand);
                case "AGT.PROMPT.SUGGEST": return Target(_main.Label.SuggestPromptCommand);
                case "AGT.CHECK": return Target(_main.Label.RunCheckCommand);
                case "TRN.RECOMMEND": return Target(_main.Train.UseRecommendedCommand);
                case "TRN.KIND.SET": return Target(_main.Train.SetKindCommand, Text(args, "kind"));
                case "TRN.QUALITY.SET": return Target(_main.Train.SetInstanceQualityCommand, Text(args, "quality"));
                case "TRN.START": return Target(_main.Train.StartCommand);
                case "TRN.STOP": return Target(_main.Train.StopCommand);
                case "TRN.USE.MODEL": return Target(_main.Train.UseModelCommand);
                case "INS.RUN": return Target(Text(args, "scope", "current") == "all" ?
                    (ICommand)_main.Inspect.RunAllCommand : _main.Inspect.RunCurrentCommand);
                case "INS.EXPORT.CSV": return Target(_main.Inspect.ExportCsvCommand);
                case "CAM.LIVE": return Target(args.GetProperty("on").GetBoolean() ?
                    (ICommand)_main.Live.StartLiveCommand : _main.Live.StopLiveCommand);
                case "CAM.SNAP": return Target(_main.Live.SnapshotCommand);
                case "EXP.RUN": return Target(_main.ExportCommand);
                case "UI.MODE.SET": return Target(_main.SetUiModeCommand, Text(args, "mode"));
                case "UI.TAB.SHOW":
                    switch (Text(args, "tab"))
                    {
                        case "inspect": return Target(_main.ShowInspectTabCommand);
                        case "label": return Target(_main.ShowLabelTabCommand);
                        case "train": return Target(_main.ShowTrainTabCommand);
                    }
                    break;
                case "UI.LAYOUT.RESET": return Target(_main.ResetLayoutCommand);
                case "SYS.SETTINGS.OPEN": return Target(_main.ShowSettingsCommand);
            }
            throw new ArgumentException("Unsupported command or argument: " + id, nameof(id));
        }

        private static string Text(JsonElement args, string key, string fallback = "")
        {
            JsonElement value;
            return args.TryGetProperty(key, out value) ? value.GetString() : fallback;
        }

        private static ViewModelCommandTarget Target(ICommand command, object parameter = null)
        {
            return ViewModelCommandTarget.FromCommand(command, parameter);
        }
    }
}
#pragma warning restore CS8600, CS8603, CS8604, CS8625
