# PlainSnip 开发指南

面向开发者 / agent。使用者看 `README.md`，规格以 `PROJECT.md` 为单一事实源。

## 环境

- .NET SDK（9/10 均可编译 net8.0-windows 目标）
- 运行需 .NET 8 Desktop Runtime（本机已装 8.0.25）
- 无第三方 NuGet 依赖

## 构建

```powershell
tools\build.ps1            # 发布 framework-dependent 单 exe 到 dist\
tools\build.ps1 -SelfTest  # 构建后顺带跑 --selftest
```

- 发布方式：`dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`
- 中文源文件一律 UTF-8 with BOM（Windows PowerShell 5.1 / 老工具链按 ANSI 读会乱码）

## 验证三步（每个里程碑照例走完）

1. **构建**：`tools\build.ps1`
2. **selftest**：`dist\PlainSnip.exe --selftest`，退出码 0 = 全过
   - WinExe 无控制台，结果输出到 `%TEMP%\PlainSnip-selftest.log`（覆盖写）
   - 覆盖：DPI 模式断言、热键解析 round-trip、非法热键拒绝、设置持久化 round-trip + 损坏回退、注册表可读
3. **UI 启动冒烟**：`dist\PlainSnip.exe --settings` 直开设置窗（不占单实例互斥体，可与托盘并存），
   用用户级 skill `win-ui-selfscreenshot`（`scripts/shot_window.py "PlainSnip 设置" out.png`）截图自验

## 命令行

| 参数 | 行为 |
|---|---|
| （无） | 托盘常驻，单实例（`Local\PlainSnip.SingleInstance` 互斥体） |
| `--settings` | 只开设置窗，落盘后退出；不建托盘、不占互斥体 |
| `--selftest` | 无界面自测，退出码 0/1 |

## 仓库结构

```
src/PlainSnip/            主工程（net8.0-windows WinForms）
  Program.cs              入口：参数分发 + 单实例互斥
  AppContext.cs           托盘常驻上下文 + 热键挂载
  HotkeySpec.cs           热键规格（Mod+VK ⇄ 字符串）
  HotkeyManager.cs        RegisterHotKey/WM_HOTKEY 封装（隐藏消息窗）
  HotkeyBox.cs            按键捕获文本框
  AppSettings.cs          设置持久化（System.Text.Json → %APPDATA%\PlainSnip\settings.json）
  Autostart.cs            开机自启（HKCU Run）
  SettingsForm.cs         设置窗（含手动 DPI 缩放，见下）
  IconFactory.cs          从嵌入资源加载 App.ico（托盘/窗体图标）
  SelfTest.cs             --selftest
tools/build.ps1           构建脚本
tools/make_icon.ps1       重新生成 App.ico（虚线选框+剪刀，16/24/32/48/256 多尺寸；DIB 条目 16-48 + PNG 条目 256）
src/PlainSnip/App.ico     应用图标（csproj ApplicationIcon + EmbeddedResource 共用；生成产物，勿手改）
dist/                     产物（gitignore）
```

## 关键设计决策

- **DPI**：manifest PerMonitorV2 + csproj `ApplicationHighDpiMode=PerMonitorV2`。
  ⚠️ 实测坑（200% 主屏踩过）：PMV2 下 **GDI 字体随窗口 DPI 自动放大，但代码里的布局尺寸是物理像素不缩放**，
  纯代码窗体 `AutoScaleMode` 不生效——所有布局尺寸必须经 `S(logical)` = `logical * DeviceDpi / 96` 手动缩放
  （见 SettingsForm）。M1 遮罩/放大镜照此办理。
- **热键**：`RegisterHotKey` + 隐藏 `NativeWindow` 消息窗；注册失败显式气泡报错 + 打开设置，不静默。
- **默认热键**：截图 F1 / 贴图 F3（与 Snipaste 一致，2026-10-29 定案）。
- **标注可编辑推后到 v1.5、滚动截图 M4**：见 PROJECT.md §9 决策表。

## 里程碑进度

- [x] **M0 骨架**（2026-10-29）：托盘 + 热键 + 设置持久化 + selftest + UI 冒烟（截图闭环，200% DPI 下验证）
- [ ] M1 截图核心（建议切 M1a 遮罩框选放大镜 / M1b 两级窗口检测）
- [ ] M2 贴图（前置：贴图窗状态机设计文档）
- [ ] M3 标注精简版
- [ ] M4 滚动截图（前置：拼接算法设计文档 + ShareX ScrollingCapture 研读）
- [ ] M5 v1.5 增强
