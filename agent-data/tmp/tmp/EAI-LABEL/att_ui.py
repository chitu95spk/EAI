src = r'E:\Code\EasyVision_Unisen - NoService\EasyLabel\src\EasyAI.Wpf'

def edit(path, pairs):
    raw = open(path, 'rb').read()
    bom = raw.startswith(b'\xef\xbb\xbf')
    crlf = b'\r\n' in raw
    s = raw.decode('utf-8-sig').replace('\r\n', '\n')
    for old, new in pairs:
        assert old in s, (path, old)
        s = s.replace(old, new, 1)
    if crlf:
        s = s.replace('\n', '\r\n')
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))

edit(src + r'\ViewModels\LabelStepViewModel.cs', [
    ('            kind,\n            vocabularyBlock);\n    }', '            kind,\n            vocabularyBlock,\n            AttachmentPaths());\n    }'),
])

en = {
    'AgentAttachTitle': 'Attach hint files',
    'AgentAttachFilter': 'Pictures and notes|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.txt;*.md;*.json;*.csv',
    'AgentAttach': 'Attach picture or file',
    'AgentRemoveAttachment': 'Remove',
    'AgentWorkersCaption': 'Parallel runs',
}
vi = {
    'AgentAttachTitle': 'Đính kèm file gợi ý',
    'AgentAttachFilter': 'Ảnh và ghi chú|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.txt;*.md;*.json;*.csv',
    'AgentAttach': 'Đính kèm ảnh hoặc file',
    'AgentRemoveAttachment': 'Bỏ',
    'AgentWorkersCaption': 'Chạy song song',
}

def resx(path, d):
    block = ''.join('  <data name="%s" xml:space="preserve"><value>%s</value></data>\n' % (k, v) for k, v in d.items())
    edit(path, [('  <data name="AgentOptions" xml:space="preserve">', block + '  <data name="AgentOptions" xml:space="preserve">')])

resx(src + r'\Resources\Strings.resx', en)
resx(src + r'\Resources\Strings.vi.resx', vi)
designer = ''.join('    public static string %s => Get(nameof(%s));\n' % (k, k) for k in en)
edit(src + r'\Resources\Strings.Designer.cs', [('    public static string SetAgentMaxImages => Get(nameof(SetAgentMaxImages));\n', '    public static string SetAgentMaxImages => Get(nameof(SetAgentMaxImages));\n' + designer)])
