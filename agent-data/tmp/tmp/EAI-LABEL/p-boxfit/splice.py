p = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\src\EasyAI.Wpf\Services\Agent\AgentSam2Refiner.cs'
s = open(p, encoding='utf-8', newline='').read()
crlf = '\r\n' in s
s = s.replace('\r\n', '\n')
mid = open(r'E:\Code\EasyVision_Unisen - NoService\tmp\EAI-LABEL\p-boxfit\mid.txt', encoding='utf-8').read()
a = s.index('    private async Task<RefineOutcome> TryRefineAsync')
b = s.index('    private static IReadOnlyList<double[]> ToPoints')
s = s[:a] + mid + s[b:]

def rep(old, new):
    global s
    assert old in s, old
    s = s.replace(old, new, 1)

rep("""    public static AgentSam2Refiner CreateLocal(string sam2Folder, Func<string, CancellationToken, AiImage> loadImage)
    {
        Sam2Segmenter segmenter = new Sam2Segmenter(sam2Folder, loadImage);
        return new AgentSam2Refiner(segmenter.SegmentAsync, segmenter);
    }""", """    public static AgentSam2Refiner CreateLocal(string sam2Folder, Func<string, CancellationToken, AiImage> loadImage, RefineOptions? options = null)
    {
        Sam2Segmenter segmenter = new Sam2Segmenter(sam2Folder, loadImage);
        return new AgentSam2Refiner(segmenter.SegmentAsync, segmenter, options);
    }""")
rep("""        public async Task<IReadOnlyList<double[]>?> SegmentAsync(AgentRunInput input, double[] box, CancellationToken ct)""",
    """        public async Task<IReadOnlyList<double[]>?> SegmentAsync(AgentRunInput input, RefinePrompt refinePrompt, CancellationToken ct)""")
rep("""            AiPrompt prompt = new AiPrompt(null, new AiBox((float)box[0], (float)box[1], (float)box[2], (float)box[3]));""",
    """            double[] box = refinePrompt.Box;
            List<AiPoint> clicks = new List<AiPoint>();
            clicks.AddRange(refinePrompt.Positive.Select(p => new AiPoint((float)p[0], (float)p[1], true)));
            clicks.AddRange(refinePrompt.Negative.Select(p => new AiPoint((float)p[0], (float)p[1], false)));
            AiPrompt prompt = new AiPrompt(clicks, new AiBox((float)box[0], (float)box[1], (float)box[2], (float)box[3]));""")
if crlf:
    s = s.replace('\n', '\r\n')
open(p, 'w', encoding='utf-8', newline='').write(s)
