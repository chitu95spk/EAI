using EasyLabel.Commands;
using EasyLabel.Commands.Discovery;

namespace EasyAI.Desktop.Commands
{
    /// <summary>CC-2 wrappers only. Required flags stay empty: the existing VM CanExecute is authoritative.</summary>
    public sealed class ViewModelCommandHandlers : AgentSlashCommandDescriptors
    {
        private readonly IViewModelCommandSource _source;
        private readonly ICommandDispatcher _dispatcher;
        public ViewModelCommandHandlers(IViewModelCommandSource source, ICommandDispatcher dispatcher)
            : base(id => new ViewModelCommandEndpoint(id, source, dispatcher))
        {
            _source = source;
            _dispatcher = dispatcher;
        }
        private ICommandEndpoint Bind(string id) { return new ViewModelCommandEndpoint(id, _source, _dispatcher); }

        [Command("PRJ.NEW", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = false,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint NewProject { get { return Bind("PRJ.NEW"); } }

        [Command("PRJ.OPEN.FILE", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = false,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint OpenProjectFile { get { return Bind("PRJ.OPEN.FILE"); } }

        [Command("PRJ.OPEN.FOLDER", Risk = CommandRisk.R1, Cost = CommandCost.C1, Undo = false, Headless = false,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint OpenFolder { get { return Bind("PRJ.OPEN.FOLDER"); } }

        [Command("PRJ.OPEN.RECENT", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}},\"required\":[\"index\"],\"additionalProperties\":false}")]
        public ICommandEndpoint OpenRecent { get { return Bind("PRJ.OPEN.RECENT"); } }

        [Command("PRJ.SAVE", Risk = CommandRisk.R2, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint SaveProject { get { return Bind("PRJ.SAVE"); } }

        [Command("PRJ.SAVE.AS", Risk = CommandRisk.R2, Cost = CommandCost.C0, Undo = false, Headless = false,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint SaveProjectAs { get { return Bind("PRJ.SAVE.AS"); } }

        [Command("IMG.NEXT", Risk = CommandRisk.R0, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint NextImage { get { return Bind("IMG.NEXT"); } }

        [Command("IMG.PREV", Risk = CommandRisk.R0, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint PreviousImage { get { return Bind("IMG.PREV"); } }

        [Command("IMG.ADD", Risk = CommandRisk.R2, Cost = CommandCost.C1, Undo = true, Headless = false,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint AddImages { get { return Bind("IMG.ADD"); } }

        [Command("IMG.DELETE", Risk = CommandRisk.R4, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"scope\":{\"enum\":[\"image\",\"annotation\"],\"default\":\"image\"}},\"additionalProperties\":false}")]
        public ICommandEndpoint DeleteImage { get { return Bind("IMG.DELETE"); } }

        [Command("CAN.TOOL", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"tool\":{\"enum\":[\"select\",\"polygon\",\"rect\",\"rot\",\"circle\",\"point\",\"line\",\"linestrip\",\"aipoints\",\"aibox\"]}},\"required\":[\"tool\"],\"additionalProperties\":false}")]
        public ICommandEndpoint SelectTool { get { return Bind("CAN.TOOL"); } }

        [Command("CAN.UNDO", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint Undo { get { return Bind("CAN.UNDO"); } }

        [Command("CAN.REDO", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint Redo { get { return Bind("CAN.REDO"); } }

        [Command("CAN.SHAPE.DELETE", Risk = CommandRisk.R2, Cost = CommandCost.C0, Undo = true, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint DeleteShape { get { return Bind("CAN.SHAPE.DELETE"); } }

        [Command("CAN.SHAPE.MERGE", Risk = CommandRisk.R2, Cost = CommandCost.C0, Undo = true, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint MergeShapes { get { return Bind("CAN.SHAPE.MERGE"); } }

        [Command("CAN.SHAPE.CONVERT", Risk = CommandRisk.R2, Cost = CommandCost.C0, Undo = true, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"to\":{\"enum\":[\"polygon\",\"rectangle\",\"rotated\",\"circle\",\"point\"]}},\"required\":[\"to\"],\"additionalProperties\":false}")]
        public ICommandEndpoint ConvertShape { get { return Bind("CAN.SHAPE.CONVERT"); } }

        [Command("CAN.CLASS.SET", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2147483647}},\"required\":[\"index\"],\"additionalProperties\":false}")]
        public ICommandEndpoint SetClass { get { return Bind("CAN.CLASS.SET"); } }

        [Command("CAN.VIEW.ZOOM", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"mode\":{\"enum\":[\"in\",\"out\",\"fit\",\"actual\",\"width\"]}},\"required\":[\"mode\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Zoom { get { return Bind("CAN.VIEW.ZOOM"); } }

        [Command("CAN.SHAPES.TOGGLE", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"visible\":{\"type\":\"boolean\"}},\"additionalProperties\":false}")]
        public ICommandEndpoint ToggleShapes { get { return Bind("CAN.SHAPES.TOGGLE"); } }

        [Command("LBL.SOURCE.SET", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"source\":{\"enum\":[\"segment\",\"ai\",\"agent\"]}},\"required\":[\"source\"],\"additionalProperties\":false}")]
        public ICommandEndpoint SetLabelSource { get { return Bind("LBL.SOURCE.SET"); } }

        [Command("LBL.RUN.AGENT", Risk = CommandRisk.R3, Cost = CommandCost.C3, Undo = true, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"scope\":{\"enum\":[\"current\",\"all\",\"remaining\"],\"default\":\"current\"}},\"additionalProperties\":false}")]
        public ICommandEndpoint RunAgent { get { return Bind("LBL.RUN.AGENT"); } }

        [Command("LBL.RUN.SEG", Risk = CommandRisk.R3, Cost = CommandCost.C2, Undo = true, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"scope\":{\"enum\":[\"current\",\"all\"],\"default\":\"current\"}},\"additionalProperties\":false}")]
        public ICommandEndpoint RunSegment { get { return Bind("LBL.RUN.SEG"); } }

        [Command("LBL.RUN.AI", Risk = CommandRisk.R2, Cost = CommandCost.C1, Undo = true, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint RunAi { get { return Bind("LBL.RUN.AI"); } }

        [Command("LBL.CANCEL", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint CancelLabel { get { return Bind("LBL.CANCEL"); } }

        [Command("LBL.ACCEPT", Risk = CommandRisk.R2, Cost = CommandCost.C0, Undo = true, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"which\":{\"enum\":[\"all\",\"selected\"],\"default\":\"all\"}},\"additionalProperties\":false}")]
        public ICommandEndpoint AcceptSuggestions { get { return Bind("LBL.ACCEPT"); } }

        [Command("LBL.REJECT.ALL", Risk = CommandRisk.R2, Cost = CommandCost.C0, Undo = true, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint RejectSuggestions { get { return Bind("LBL.REJECT.ALL"); } }

        [Command("AGT.PROMPT.SUGGEST", Risk = CommandRisk.R3, Cost = CommandCost.C3, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint SuggestPrompt { get { return Bind("AGT.PROMPT.SUGGEST"); } }

        [Command("AGT.CHECK", Risk = CommandRisk.R3, Cost = CommandCost.C3, Undo = false, Headless = true, Aliases = new[] { "check" },
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint Check { get { return Bind("AGT.CHECK"); } }

        [Command("TRN.RECOMMEND", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint UseRecommended { get { return Bind("TRN.RECOMMEND"); } }

        [Command("TRN.KIND.SET", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\"}},\"required\":[\"kind\"],\"additionalProperties\":false}")]
        public ICommandEndpoint SetTrainingKind { get { return Bind("TRN.KIND.SET"); } }

        [Command("TRN.QUALITY.SET", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"quality\":{\"type\":\"string\"}},\"required\":[\"quality\"],\"additionalProperties\":false}")]
        public ICommandEndpoint SetTrainingQuality { get { return Bind("TRN.QUALITY.SET"); } }

        [Command("TRN.START", Risk = CommandRisk.R3, Cost = CommandCost.C2, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint StartTraining { get { return Bind("TRN.START"); } }

        [Command("TRN.STOP", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint StopTraining { get { return Bind("TRN.STOP"); } }

        [Command("TRN.USE.MODEL", Risk = CommandRisk.R2, Cost = CommandCost.C0, Undo = true, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint UseModel { get { return Bind("TRN.USE.MODEL"); } }

        [Command("INS.RUN", Risk = CommandRisk.R2, Cost = CommandCost.C2, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"scope\":{\"enum\":[\"current\",\"all\"],\"default\":\"current\"}},\"additionalProperties\":false}")]
        public ICommandEndpoint RunInspection { get { return Bind("INS.RUN"); } }

        [Command("INS.EXPORT.CSV", Risk = CommandRisk.R2, Cost = CommandCost.C0, Undo = false, Headless = false,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint ExportCsv { get { return Bind("INS.EXPORT.CSV"); } }

        [Command("CAM.LIVE", Risk = CommandRisk.R3, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"on\":{\"type\":\"boolean\"}},\"required\":[\"on\"],\"additionalProperties\":false}")]
        public ICommandEndpoint Live { get { return Bind("CAM.LIVE"); } }

        [Command("CAM.SNAP", Risk = CommandRisk.R2, Cost = CommandCost.C0, Undo = true, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint Snapshot { get { return Bind("CAM.SNAP"); } }

        [Command("EXP.RUN", Risk = CommandRisk.R4, Cost = CommandCost.C2, Undo = false, Headless = false,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint Export { get { return Bind("EXP.RUN"); } }

        [Command("UI.MODE.SET", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"mode\":{\"enum\":[\"simple\",\"advanced\"]}},\"required\":[\"mode\"],\"additionalProperties\":false}")]
        public ICommandEndpoint SetUiMode { get { return Bind("UI.MODE.SET"); } }

        [Command("UI.TAB.SHOW", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{\"tab\":{\"enum\":[\"inspect\",\"label\",\"train\"]}},\"required\":[\"tab\"],\"additionalProperties\":false}")]
        public ICommandEndpoint ShowTab { get { return Bind("UI.TAB.SHOW"); } }

        [Command("UI.LAYOUT.RESET", Risk = CommandRisk.R1, Cost = CommandCost.C0, Undo = false, Headless = true,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint ResetLayout { get { return Bind("UI.LAYOUT.RESET"); } }

        [Command("SYS.SETTINGS.OPEN", Risk = CommandRisk.R0, Cost = CommandCost.C0, Undo = false, Headless = false,
            Args = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")]
        public ICommandEndpoint ShowSettings { get { return Bind("SYS.SETTINGS.OPEN"); } }
    }
}

