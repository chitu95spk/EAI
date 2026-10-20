# T2 — OnlineStatusWindow smoke: PARTIAL

Independent reviewer ACCEPTS the bounded harness and manually reviewed native 96-DPI visual evidence. OS UIA and native 144-DPI coverage remain unverified.

## Files created

- tmp/EAI-ONL-MODE/smoke/OnlineStatusSmoke.csproj
- tmp/EAI-ONL-MODE/smoke/Program.cs

## Commands and results

Final build: dotnet build tmp/EAI-ONL-MODE/smoke/OnlineStatusSmoke.csproj -c Release -bl:artifacts/EAI-ONL-MODE/t2-fix1/smoke-build-resources2.binlog

Log: tmp/EAI-ONL-MODE/t2-fix1/build-resources2.log — 0 warnings, 0 errors. Current binlog: 425617 bytes, modified 2026-10-18 07:23:50 UTC, after current source and compiled harness.

Run: dotnet tmp/EAI-ONL-MODE/smoke/bin/Release/net9.0-windows/OnlineStatusSmoke.dll artifacts/EAI-ONL-MODE/ui-smoke/20261018T072500Z

Run log: tmp/EAI-ONL-MODE/t2-fix1/run-resources2.log is empty on normal exit. PNGs and manual inspection supply visual evidence; empty stdout alone is not PASS. Capture uses PrintWindow only, actual per-window DPI 96 and width 360 DIP.

| PNG under artifacts/EAI-ONL-MODE/ui-smoke/20261018T072500Z/ | Pixels | Bytes | SHA256 |
| --- | --- | --- | --- |
| en-US-idle-dpi96.png | 360x547 | 18885 | 5AEF6A6C1A2BC1350751EF26A6FE59502E14D53F0F4AEFC4841B765A3F69EE72 |
| en-US-running-dpi96.png | 360x567 | 16974 | EC21455E7A4F58EC443EE7190FEAA754A1D75F6F28C43AAC2F9AF560C876A6BC |
| vi-VN-idle-dpi96.png | 360x547 | 15562 | 48E05C111501F7E44ADA594ACE92CA43BBE607F036D9365F3206C38B562D5A54 |
| vi-VN-running-dpi96.png | 360x567 | 19475 | ADE95237B3A1F5A59DAA331BC1B4BEED0021B497C2F2AF61A40B521E9B8D7694 |

Reviewer opened all four; root opened vi-VN running and en-US idle. Actual Dark UI, language/state differences, counters, p95, thresholds, inputs, Apply and Stop are visible. The long Vietnamese recipe wraps fully. No clipping/overlap observed in these captures. Fixture counters/timing are not production inference or 20 MP performance evidence.

## Rejected evidence and residuals

- Earlier 090000Z and 091500Z runs are retained but rejected blank/unrepresentative evidence. They are not visual PASS.
- ValidatePixels compares individual channel bytes against the first channel; alpha can make uniform BGRA appear nonuniform. Its validity result is unreliable. Manual visual inspection supplied image validity above; do not claim automated blank detection.
- OS UIA and native 144 DPI were not run. No current WPF source build or source/binary parity was established.
- Overall T2 remains PARTIAL. The unreliable validator remains a known harness limitation after two bounded review fix cycles; no further source changes were made.

## Audit

Task ID: EAI-ONL-MODE/T2. Scope: the two new harness files only, actual production adapter/VM/window, real OnlineSession/Runner plus harness-only fake host. Exact production Wpf.Ui Dark ThemesDictionary and ControlsDictionary, per-case culture, real UTF-8 long Vietnamese recipe, explicit Application shutdown, ContentRendered and live handle/DPI checks are present. No production edits, packages, models, training, hardware, global display changes, desktop capture or Git writes.

C# assumptions: harness explicitly compiles LangVersion 7.3; final build passes 0/0. Initial root errors were missing Wpf.Ui reference and unavailable System.Drawing API, corrected. Early invalid handles were caused by harness Application shutdown after closing its first window, not proof of a noninteractive environment. Missing theme dictionaries caused unrepresentative captures and were corrected.

Binary identities (copied artifacts match references):
- EasyAI.dll: 1588224 bytes, modified 2026-10-18 06:50:57 UTC, SHA256 9C570FF8DCDCA8AF7B32D5B9F17E006D2CF31EE70F58322B5EFF9E299D9D8B55.
- EasyLabel.Online.dll: 55808 bytes, SHA256 45B94D0BFAA6FC63D5EF25F552ED8DC747934AF3A1A48647976BF8E9202F6A2C.
- Wpf.Ui.dll: 5949952 bytes, SHA256 13FD69B7DDF871E14C393061922CF557C3D42FFC116E76CFC2F4E4FF7E86986B.

Final overall commercial/online acceptance remains with Claude.

