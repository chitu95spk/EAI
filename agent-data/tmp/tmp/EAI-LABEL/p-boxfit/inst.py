def edit(p, pairs):
    s = open(p, encoding='utf-8', newline='').read()
    crlf = '\r\n' in s
    s = s.replace('\r\n', '\n')
    for a, b in pairs:
        assert a in s, a
        s = s.replace(a, b, 1)
    if crlf: s = s.replace('\n', '\r\n')
    open(p, 'w', encoding='utf-8', newline='').write(s)

edit('Convert/SegLabelMeConverter.cs', [
("public static List<SegInstance> ToInstances(IReadOnlyList<LabelShape> shapes, SegClassMap map, bool includeRectangles = false)\n    {",
 "public static List<SegInstance> ToInstances(IReadOnlyList<LabelShape> shapes, SegClassMap map, bool includeRectangles = false, int imageWidth = 0, int imageHeight = 0)\n    {"),
("        }\n\n        return instances;\n    }\n\n    /// <summary>Position projects: circles as",
 "        }\n\n        // Out-of-bounds labels: the native trainer drops a centre outside its grid without a trace (SegInstanceTargets.cpp:210-212) and a centre in\n        // the letterbox padding would train a peak on padding, so only instances whose centre is inside the image reach it (partly-outside ones stay).\n        if (imageWidth > 0 && imageHeight > 0)\n            instances.RemoveAll(i => i.Cx < 0 || i.Cy < 0 || i.Cx > imageWidth || i.Cy > imageHeight);\n\n        return instances;\n    }\n\n    /// <summary>Position projects: circles as"),
])
edit('SegAiSession.cs', [
("        var sampleShapes = new List<List<LabelShape>?>();\n", "        var sampleShapes = new List<List<LabelShape>?>();\n        var sampleSizes = new List<(int Width, int Height)>();\n"),
("                instances = SegLabelMeConverter.ToInstances(shapes, _map);\n            }\n",
 "                GetImageSize(rel, ImagePathOf(rel), out int imageW, out int imageH);\n                instances = SegLabelMeConverter.ToInstances(shapes, _map, false, imageW, imageH);\n            }\n"),
("            sampleShapes.Add(shapes);\n", "            sampleShapes.Add(shapes);\n            GetImageSize(rel, ImagePathOf(rel), out int sampleW, out int sampleH);\n            sampleSizes.Add((sampleW, sampleH));\n"),
("SegLabelMeConverter.ToInstances(sampleShapes[i]!, _map, includeRectangles: true);", "SegLabelMeConverter.ToInstances(sampleShapes[i]!, _map, true, sampleSizes[i].Width, sampleSizes[i].Height);"),
("sampleShapes.Where(s => s != null).SelectMany(s => SegLabelMeConverter.ToInstances(s!, _map))", "sampleShapes.Select((s, i) => (s, i)).Where(t => t.s != null).SelectMany(t => SegLabelMeConverter.ToInstances(t.s!, _map, false, sampleSizes[t.i].Width, sampleSizes[t.i].Height))"),
])
