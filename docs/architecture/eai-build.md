# EAI — bố cục repo và lệnh build/test

Tách từ `CLAUDE.md` (2026-10-21) để giữ file luật dưới ~15 KB.

## Bố cục

| Đường dẫn | Nội dung |
| --- | --- |
| `EasyLabel/EasyLabel.sln` | net9.0 / net9.0-windows, C# 12 (`Directory.Build.props`), NuGet tập trung (`Directory.Packages.props`). |
| `EasyLabel/src/EasyAI.Wpf` | App WPF chính (WPF-UI, CommunityToolkit.Mvvm). Exe: `bin\Release\net9.0-windows\EasyAI.exe`. |
| `EasyLabel/src/EasyLabel.{Core,Vision,Ai,SegAI,Camera,Commands,Online,App}` | Thư viện; Camera = Hikrobot MVS (`Mvs/MvsSdk.cs`). |
| `EasyLabel/tests/*` | xUnit + bench (`OnlineBench`, `Train512Bench`, cần `BeeNativeSegAI/` là thư mục anh em). |
| `EasyLabel/Lib/native/segai` | DLL native git-ignored (BeeNativeSegAI, libtorch, OpenCV 455, OpenVINO, Models). |
| `BeeNativeSegAI/` | Source C++ engine Segment AI (vcxproj, v143, x64). |
| `tmp/<ID>/`, `artifacts/<ID>/<run>/` | Scratch / evidence theo task. |
| `agent-data/` | Bản gốc dữ liệu bàn giao (chỉ đọc). |

## Lệnh build/test chuẩn

| Việc | Lệnh |
| --- | --- |
| App (chỉ **Any CPU**) | `dotnet build EasyLabel/src/EasyAI.Wpf/EasyAI.Wpf.csproj -c Release -v:minimal > tmp/build.log 2>&1` → `grep -cE ": error " tmp/build.log` |
| Test | `dotnet test EasyLabel/EasyLabel.sln -c Release > tmp/test.log 2>&1` → grep `Passed!\|Failed!` |
| Native (chỉ khi sửa `BeeNativeSegAI/`) | `MSBuild BeeNativeSegAI/BeeNativeSegAI.vcxproj /p:Configuration=Release /p:Platform=x64 /v:minimal > tmp/native.log 2>&1` |

- MSBuild: `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe` (MSVC 14.44).
- Không truyền `-p:Platform=x64`/`-r win-x64` cho EasyAI.Wpf; `bin\x64` là output cũ, không bao giờ trỏ user vào đó.
  Đóng `EasyAI.exe` trước khi build vào `bin\Release`; cần build khi app đang chạy thì dùng `-p:OutDir=tmp/<task>/out/`.
- Test SegAI native cần `EASYAI_SEGAI_DIR=D:\CODE\EAI\EasyLabel\Lib\native\segai` (đã đặt cấp user trên máy này).
- Số warning không được tăng so với lần build trước của task (baseline 2026-10-21: 2 warning).
- **Native**: vcxproj trỏ libtorch/OpenCV theo `ThirdParty` của repo gốc → phải chỉnh trước lần build đầu. ABI = 1; đổi struct
  công khai thì tăng ABI (sai lệch ABI làm app từ chối nạp DLL). Chỉ copy DLL mới vào `EasyLabel/Lib/native/segai` khi user yêu cầu.
