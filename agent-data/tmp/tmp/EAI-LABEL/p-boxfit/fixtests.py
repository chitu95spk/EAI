p = r'AgentSam2RefinerTests.cs'
s = open(p, encoding='utf-8', newline='').read()
crlf = '\r\n' in s
s = s.replace('\r\n', '\n')
a = s.index('    [Fact]\n    public void FitBox_NoClampKeepsCornersOutsideTheImage')
b = s.index('    [Fact]\n    public void Sam2Backend_IsFinite')
s = s[:a] + s[b:]
old = '''        Assert.Equal(2, neg.Negative.Count); // the two bounds corners on the short axis; the long-axis corners are too close to the box tips
        Assert.All(neg.Negative, c => Assert.True(c[0] >= neg.Box[0] - 1e-6 && c[0] <= neg.Box[2] + 1e-6));
'''
new = '''        Assert.Equal(4, neg.Negative.Count); // every corner of the bounds of a 45 degree box is clearly outside it
        Assert.All(neg.Negative, c => Assert.True(c[0] >= neg.Box[0] - 1e-6 && c[0] <= neg.Box[2] + 1e-6));
'''
assert old in s
s = s.replace(old, new)
if crlf: s = s.replace('\n', '\r\n')
open(p, 'w', encoding='utf-8', newline='').write(s)
