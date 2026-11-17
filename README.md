# 轻点连点器 LiteClicker

Windows 11 x64 的中文鼠标工具：在当前鼠标位置连续点击，或持续按住左键。当前版本 **1.0.0，待 Windows 验证**。

已完成源码、Linux 交叉构建，以及 GitHub Actions Windows Server 2025 上的 Release 单文件构建；两个环境的 42 项核心逻辑测试均通过。**尚未执行 WPF/Win32 真实输入、干净 Windows 11 离线运行和性能验收**，本次为供验收的预发布。Windows CI 记录见 `docs/windows-ci-test-report.md`；最初的交叉构建记录与验收清单保留在 `docs/test-report.md`。

**下载：[v1.0.0-preview.1 预发布](https://github.com/AsleepDragon/LiteClicker/releases/tag/v1.0.0-preview.1)**。附件包括单 EXE、源码压缩包、Windows 验收工具、测试报告和 SHA-256。发布的 EXE 来自 Windows CI，校验值以该发行页的 `SHA256SUMS.txt` 为准。

## 运行

将 `dist/LiteClicker-win-x64.exe` 复制到 Windows 11 x64 电脑后双击。自包含包包含 .NET 10.0.15 和 Windows Desktop 运行库，设计上无需用户安装 SDK 或运行时，也无需联网；独立运行仍需完成干净机验证。只需 EXE；README、校验值和许可说明是随附文件，不是运行依赖。程序未签名，Windows 可能显示信任提示。

1. 初次打开保持停止：连续点击、3 档 10 次/秒、启动延迟 2 秒。
2. 选择档位或输入 1–100 的整数；选择延迟。鼠标移至目标窗口后按 F6 开始连点，再按 F6 停止。
3. F7 开始持续按住，再按 F7 释放。运行中按另一模式快捷键会先停止并清理，再进入另一模式的启动等待。
4. F8 随时停止所有动作。倒计时可用同模式快捷键或“取消启动”取消。

| 默认键 | 动作 |
| --- | --- |
| F6 | 连点开／关 |
| F7 | 按住／释放 |
| F8 | 紧急停止所有动作 |

展开“全局快捷键”可改成 F1–F11 与 Ctrl、Alt、Shift 组合。三个动作不能重复，必须全部注册成功。修改失败会尝试恢复原配置；必要快捷键不可用时禁止启动。带修饰键启动会同时等待启动延迟和整组键松开；超过 5 秒未松开会取消。

五档分别为 1、5、10、20、50 次/秒。数字框手动输入切换为自定义，空值、小数和越界值不能启动。运行及倒计时期间不能编辑模式、速度、延迟和快捷键。

## 停止、托盘和错误

鼠标仍在本应用窗口内时不会开始。运行中移入本应用窗口会停止，按住会释放。开始前如果左键已按下，也会取消。按住期间避免同时按物理左键。

最小化会隐藏到托盘，已开始的任务继续。托盘菜单只提供显示、停止、退出；打开菜单或恢复窗口前先停止。关闭窗口或 Alt+F4 表示退出，正常退出会清理输入。锁屏、挂起、注销通知也会触发停止，恢复后不会自动运行。重复打开时已有实例先停止并显示，新进程退出；权限不允许唤醒时提示，不启动第二个输入引擎。

输入提交失败会停止。“已发送 X 次”只计成功提交的完整按下／抬起对，不能代表目标软件完成了动作。释放最多尝试 3 次，失败时显示“无法确认左键已释放”，禁止新任务并保留再次停止入口。正常关闭遇到释放失败时，用户可留在界面再次停止，或明确选择仍然退出。强制结束进程、崩溃、断电和受保护桌面不保证补发抬起。

## 权限边界

程序默认普通用户权限，不自动提权。目标若以管理员权限运行，可以退出后手动以相同权限打开本工具，再自行开始。登录界面、锁屏、UAC 安全桌面和受保护窗口不支持。某些软件忽略合成输入，游戏、独占全屏和远程桌面不保证兼容。程序不会把输入失败精确归因于某一类权限拦截。

## 构建

开发环境：Windows 11 x64、**.NET SDK 10.0.404**、PowerShell 5.1 或更新版本。SDK 用 `global.json` 精确固定；正式依赖只有 .NET 标准运行库。测试包版本及传递依赖见各项目 `packages.lock.json`。

```powershell
.\scripts\build.ps1
```

脚本依次执行锁定依赖还原、Release 构建、xUnit 测试、单文件发布、输出检查和 SHA-256 生成，任一步失败返回非零。首次构建需要获取 SDK/NuGet 包；应用运行不需要联网。输出：

- `dist/LiteClicker-win-x64.exe`：主程序。
- `dist/SHA256SUMS.txt`：主程序 SHA-256。
- `artifacts/test-target/LiteClicker.TestTarget.exe`：仅验收使用的独立接收器。
- `artifacts/test-results/core-tests.trx`：逻辑测试原始结果。

Linux 上可用 PowerShell 7 执行 `./scripts/build.ps1 -AllowCrossBuild`，只验证交叉构建和纯逻辑，不能取代 Windows 验收。`EnableWindowsTargeting=true` 支持交叉编译；关闭裁剪和 AOT，原生库内嵌、运行时允许临时解包。WinForms DPI 分析器 WFO0003 仅因 NotifyIcon 引入而禁用；DPI 由 WPF 清单的 PerMonitorV2 设置负责，没有忽略输入、线程和发布警告。

工程划分：`Core` 为状态、调度、设置及快捷键注册事务；`Windows` 为 Win32 适配；`App` 为 WPF/MVVM、托盘与生命周期。`tests` 包含 xUnit 和独立消息接收器。

## 验收与诊断

按 `docs/windows-validation.md` 进行真实输入、独立运行、DPI、单实例和性能验证。接收器同时记录 WM_LBUTTONDOWN、WM_LBUTTONDBLCLK 与 WM_LBUTTONUP，并识别 LiteClicker 的输入标记。

仅在验收停止延迟时，可这样启动：

```powershell
.\LiteClicker-win-x64.exe --diagnostics
```

它按每次停止记录请求时间、最后一次按下提交时间、释放完成时间和停止完成时间，不记录窗口标题、屏幕、输入内容或逐次点击轨迹。诊断文件最多 3 个、每个不超过 1 MiB，保存于个人数据目录。此选项不改变停止或启动行为。

## 数据与卸载

设置保存在 `%LOCALAPPDATA%\LiteClicker\settings.json`。不保存运行状态，启动始终停止。配置按字段校验；未知版本先备份，备份失败不自动覆盖。保存失败仍可使用内存设置。单文件原生组件可能使用 .NET 标准临时解包目录。

退出程序后删除 EXE 即可卸载；要清除个人数据，再删除 `%LOCALAPPDATA%\LiteClicker`。不创建开机启动项、后台服务或自定义系统注册项。运行不含账号、遥测和自动更新。

许可：项目源代码使用 MIT；运行库及测试依赖说明见 `THIRD-PARTY-NOTICES.md` 和 `licenses/`。
