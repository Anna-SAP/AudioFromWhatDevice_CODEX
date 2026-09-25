# AudioFromWhatDevice

## v0.5：仅一个托盘图标，明确当前输出设备

通知区域始终只有一个图标。播放时显示实际输出设备的简称；没有音频、暂停或静音时显示 Windows 当前默认输出设备的简称，不会变成无名称的灰色横线。蓝牙使用宝蓝色背景（#4169E1），内置扬声器使用绿色背景（#12704A）。其他已连接设备可在设备列表中查看，不再各自创建托盘图标。

如果多台设备同时输出，优先显示正在输出的系统默认设备；默认设备未参与输出时，保持当前仍有输出的设备，避免图标来回轮换。悬浮提示注明其他输出数量，菜单和列表显示所有设备的实际状态。默认设备不可用时选择仍可用的设备；仅没有可用设备时显示灰色横线。读取异常使用橙色提示。

可在设备列表中选中设备，保存 1–2 个汉字或 1–4 个字母/数字作为简称，如“耳机”“音箱”。越短越清楚；设置按完整 Endpoint ID 保存到 `%LOCALAPPDATA%\AudioFromWhatDevice\tray-labels.json`。同名设备或重复简称会自动追加编号区分，完整名称仍可在悬浮提示和设备列表查看。

已针对 16/24/32 像素检查文字绘制，图标会按任务栏 DPI 选择尺寸，显示内容不变时复用现有图标。Windows 仍决定图标是否放进折叠区，用户可在任务栏设置中将它设为始终显示。

2026-09-26 验证：64 项检查通过，覆盖三设备启动、默认设备切换、非默认设备实际播放、静默、静音、零音量、断开重连、同时输出、同名设备与简称保存。真实单文件 EXE 在无音频和正在播放时均只显示一个绿色 RT 图标，对应系统默认的 Realtek 扬声器；MagicMic 未增加托盘图标。自包含运行时和退出验证通过。发布版本为 0.5.0。

**可直接运行：双击项目根目录的 AudioFromWhatDevice.exe。** Windows 11 x64 单文件版（约 111 MiB），包含 .NET 运行时，无需安装 SDK 或 .NET。启动后在系统托盘中显示；左键查看设备，右键退出。

2026-09-26：已使用 .NET SDK 10.0.401 正式发布并通过独立 EXE 启动、真实音频读取、托盘退出及内嵌运行时验证。

Windows 11 音频输出设备监测示例。启动后驻留系统托盘；左键打开实时设备列表，右键查看设备和退出。关闭列表窗口后仍继续监测。

## 技术栈建议

**C# + .NET 10 LTS + WinForms NotifyIcon + Windows Core Audio API。**

| 方案 | 对本项目的适用性 |
| --- | --- |
| C# / .NET / WinForms | 首选。托盘与消息循环是框架内置能力；COM 互操作方便；容易发布 Windows EXE。 |
| C# / WPF | 若后续增加复杂设置页、图表、主题，可使用 WPF 做主界面，托盘部分仍可复用 NotifyIcon。 |
| Python / pycaw / pystray | 适合快速验证；最终分发还需处理 Python、COM、GUI 事件循环和打包依赖。 |
| C++ / Win32 | 能直接调用相同音频 API、精细控制资源；开发和维护成本更高。 |

.NET 10 是当前受支持的 LTS，支持期至 2028 年 11 月。参见 [Microsoft .NET 支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy)。本项目直接声明所需 COM 接口，没有第三方 NuGet 包。也可以用 NAudio 封装这些接口，但应固定版本并核对对应版本 API。

## 检测原理

```text
IMMDeviceEnumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)
    └─ 逐个可用输出端点
         ├─ IMMDevice.GetId：唯一身份
         ├─ IPropertyStore：显示名称、Container ID
         ├─ IAudioMeterInformation.GetPeakValue：当前采样电平
         └─ IAudioEndpointVolume：系统静音、主音量
                    ↓
          独立线程 50 ms 采样 → 600 ms 显示保持
                    ↓
          托盘图标 / 悬浮提示 / 设备列表 200 ms 刷新
```

`DEVICE_STATE_ACTIVE` 表示设备可用，并不表示正在播放。默认输出设备也不能作为播放依据：应用可以被路由到另一个输出端点。程序对每个端点单独监测。TrayPresentation.Build 返回唯一的 TrayBadge，界面只创建一个 NotifyIcon。优先从正在输出（含短暂保持）的端点中选择；没有音频时依据 DefaultOutputId 显示默认设备。默认设备 ID 通过 GetDefaultAudioEndpoint(eRender, eMultimedia) 获取，默认设备通知和定期重扫都会刷新。仅配对但未连接、已断开或已禁用的设备不计入可用列表。

判定阈值为 `0.0001`（约 −80 dBFS，设备主音量调节前）。最新采样超过阈值且端点未静音、音量大于零时显示“正在输出”；之后最多 600 ms 显示“刚有输出（保持）”，与即时信号区分；静音立即清除保持状态。未检测到信号不代表设备一定空闲。

设备通知通过 `IMMNotificationClient` 接收，回调只设置刷新标志，不执行 COM 查询或阻塞操作。查询、释放、重建设备对象均在独立 MTA 线程执行；每 3 秒额外重新枚举，作为遗漏通知和暂时读取异常的恢复手段。音频服务错误会显示异常并重试。

## 多蓝牙设备如何区分

- **内部以完整 Endpoint ID 为键**，不以蓝牙名称为键；两台同名耳机不会合并。ID 按不透明字符串使用，不解析其中的内容。
- 显示 Windows 提供的完整名称及 Endpoint ID 的 8 位 SHA-256 前缀，帮助区分同名条目。短码只用于显示；选择条目可查看、复制完整 ID。
- 展示 `PKEY_Device_ContainerId`，为后续关联同一物理设备的多个端点提供信息，但本示例不自动合并端点。部分设备返回系统共享容器或不提供有效值，不能仅凭 Container ID 无条件合并。
- Windows 11 的经典蓝牙音频通常为 A2DP/HFP 提供统一输出端点，系统会根据麦克风使用和通信流切换模式。不能靠设备名中的 `Stereo` / `Hands-Free` 猜测播放状态或蓝牙模式。
- 图标颜色依据 `PKEY_Device_EnumeratorName` 的蓝牙总线枚举器属性判定（BTHENUM、BTHHFENUM、BTHLE 等）。若端点提供 PnP Instance ID，也会检查设备及其父节点。不会根据可改名的设备名称、耳机形态或默认输出设置猜测。
- USB、HDMI、声卡、虚拟设备也使用相同监测机制，非蓝牙输出沿用绿色。Windows 暴露的端点才是可观测单位。

官方说明：[Bluetooth Classic Audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/bluetooth-classic-audio)、[Endpoint ID](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdevice-getid)、[Container ID](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-containerid)。

## 核心代码位置

| 文件 | 内容 |
| --- | --- |
| `src/AudioFromWhatDevice/Program.cs` | STA 入口、单实例、Application.Run、诊断参数 |
| `src/AudioFromWhatDevice/TrayApplicationContext.cs` | 唯一文字图标、右键菜单、设备窗口与简称设置 |
| `src/AudioFromWhatDevice/TrayBadgeRenderer.cs` | DPI 感知的文字图标绘制、图标资源复用与释放 |
| `src/AudioFromWhatDevice/TrayLabels.cs` | 自动简称、同名冲突消解、按设备 ID 保存设置、托盘显示状态 |
| `src/AudioFromWhatDevice/AudioMonitor.cs` | MTA 采样、端点枚举、电平/静音读取、通知注册和恢复 |
| `src/AudioFromWhatDevice/BluetoothDeviceDetector.cs` | 蓝牙枚举器识别与 PnP 父节点查询 |
| `src/AudioFromWhatDevice/CoreAudioInterop.cs` | 所需原生 COM 接口、GUID、PROPVARIANT 生命周期 |
| `src/AudioFromWhatDevice/ActivityState.cs` | 阈值、保持、静音状态、按端点区分的快照 |

音频采样的核心调用位于 `AudioMonitor.Endpoint.Sample`：

```csharp
Native.Check(meter!.GetPeakValue(out var peak));
Native.Check(volume!.GetMute(out var muted));
Native.Check(volume.GetMasterVolumeLevelScalar(out var level));
return new(Id, name, containerId, peak, level, muted, hardwareMeter,
    State.Update(peak, muted, level, now), null);
```

托盘常驻通过没有主窗体的 `ApplicationContext` 实现：

```csharp
tray = new NotifyIcon
{
    Icon = TrayBadgeRenderer.Create("RT", TrayBadgeKind.NonBluetooth, 16),
    Text = "音频输出监测 · 正在连接",
    Visible = true,
    ContextMenuStrip = menu
};
// 入口：using var context = new TrayApplicationContext();
//       Application.Run(context);
```

## 构建与运行

要求：Windows 11 x64，安装 [.NET 10 SDK x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。仅安装 Desktop Runtime 不足以执行 `dotnet build/publish`。

在项目根目录执行：

```powershell
dotnet --list-sdks
dotnet run --project .\src\AudioFromWhatDevice\AudioFromWhatDevice.csproj
```

运行后可以在任务栏隐藏图标区域找到托盘图标。可在 Windows 任务栏设置中让它始终显示。

构建脚本先运行 64 项测试，再发布包含 .NET 运行时的单文件 EXE：

```powershell
.\build.ps1
.\artifacts\publish\win-x64\AudioFromWhatDevice.exe
```

也可直接发布：

```powershell
dotnet publish .\src\AudioFromWhatDevice\AudioFromWhatDevice.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishTrimmed=false -p:DebugType=None `
  -o .\artifacts\publish\win-x64
```

目标电脑无需单独安装 .NET。首轮发布可能需要联网获取运行时包。为了减少文件体积，可执行 `./build.ps1 -FrameworkDependent`，此时目标电脑需要 .NET 10 Desktop Runtime。不要启用裁剪或 NativeAOT；本示例使用传统 COM 互操作和 WinForms。

程序以普通用户权限运行。无需管理员、无需录音权限，不录制音频，也不修改设备路由、音量或静音。此版本未设置开机自启；需要时可把 EXE 的快捷方式放入 `shell:startup`。

## 准确性边界

1. **检测的是 Windows 端点中的信号，不是耳机振膜的物理发声。** 蓝牙设备本体静音、设备故障、另一台手机的音频等无法仅由电脑端点电平证明。
2. 电平是端点音量衰减前的信号，因此同时检查端点静音和音量；驱动自身处理、每声道硬件控制等仍有差异。
3. **独占模式存在限制。** 有硬件电平表时 Core Audio 可在共享/独占模式读取；只有软件电平表时独占播放会返回零。界面显示硬件/软件支持，“未检测到信号”不等于“没有播放”。ASIO 等绕过 Windows 音频引擎的路径不属于本示例保证覆盖范围。
4. 电平值反映上一设备周期，50 ms 轮询不保证捕捉每个极短提示音；600 ms 保持只能防止已捕获信号闪烁，不能补回漏采样。如要分析所有共享模式音频帧，可扩展为逐端点 WASAPI loopback 捕获并计算能量，但会增加采集成本，仍无法绕开所有独占/硬件限制。
5. 本示例不把 `AudioSessionStateActive` 当作发声证据；一个启动但传输静音样本的会话也可能处于 Active。

依据：[IAudioMeterInformation](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nn-endpointvolume-iaudiometerinformation)、[GetPeakValue](https://learn.microsoft.com/en-us/windows/win32/api/endpointvolume/nf-endpointvolume-iaudiometerinformation-getpeakvalue)、[通知回调约束](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient)。

## 验证记录与验收

本机验证（2026-09-25）：

- 使用本机 PowerShell 7.6 自带 Roslyn 和 .NET 10 Desktop Runtime 成功编译所有 C# 源码。
- 11 项状态/标识检查通过，覆盖静音、零音量、阈值、保持期边界、同名不同 ID。
- 实际 Core Audio 枚举/属性/电平/静音 API 成功，读取到 Realtek 与 MagicMic 两个输出端点。
- 托盘程序启动、菜单生成、设备列表更新、自动退出通过；修复了退出期间重复释放资源的问题。
- 当前无可用蓝牙输出端点，尚未实测多蓝牙同时播放、蓝牙热插拔、独占播放、音频服务重启或 UI 各 DPI 下的视觉布局。
- 2026-09-26 已补齐项目内 SDK，并通过 MSBuild、自包含单文件 EXE 发布及隔离目录运行验证；本机实时识别到 Realtek 正在输出。验证脚本：scripts/verify-published.ps1。

本机编译产物在 `artifacts/local-validation`，已安装 .NET 10 Desktop Runtime 时可运行：

```powershell
dotnet .\artifacts\local-validation\AudioFromWhatDevice.dll
```

可复现的 SDK 缺失环境验证脚本为 `scripts/verify-with-local-compiler.ps1`；它依赖本机 Roslyn 和指定版本运行时，仅作为源码/API 验证手段。正式构建请使用上面的 SDK 步骤。

进一步实机验收：连接蓝牙设备 A/B，在 Windows 音量混合器中将两个持续播放的应用分别路由到 A/B；确认两者都显示输出。暂停一个、静音另一个，再断开和重连，分别核对保持到期、立即静音、端点移除与恢复。另用同名耳机确认完整 ID 不同；确认通知区域始终只有一个图标；播放优先，暂停/静音后显示默认设备。切换系统默认设备时，空闲图标相应切换名称和蓝色/绿色；断开后回退到可用设备。

可用 scripts/verify-published.ps1 -RequireIdleSelection 验证无播放时只有一个默认设备文字图标；验证结果保存默认设备 ID、被选设备、图标数量、名称、颜色及 EXE 哈希。

只读诊断参数：

```powershell
# 约 1.8 秒后输出所有端点的单次快照并退出
AudioFromWhatDevice.exe --probe .\probe.json
# 约 2.5 秒后生成托盘菜单与列表数据、保存快照并退出
AudioFromWhatDevice.exe --smoke .\tray-smoke.json
```

默认设备 API：[Microsoft GetDefaultAudioEndpoint](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-getdefaultaudioendpoint)。
