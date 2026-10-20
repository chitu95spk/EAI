p='EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj'
b=open(p,'rb').read()
b=b.replace(b'Lib\x0bcrt', b'Lib' + bytes([92]) + b'vcrt')
open(p,'wb').write(b)
