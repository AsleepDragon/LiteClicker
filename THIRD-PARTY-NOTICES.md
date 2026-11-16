# 组件与许可

本项目只在最终 EXE 中分发应用代码、原始本地图标和 Microsoft .NET 运行组件。

| 组件 | 版本 | 用途 | 许可 |
| --- | --- | --- | --- |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.15 | 随单文件分发的 .NET 运行时 | MIT 及其附带第三方声明 |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 | 10.0.15 | WPF、Windows Forms（托盘） | MIT；参见实际包的 LICENSE |
| xunit | 2.9.3 | 仅构建／测试，不随主 EXE 分发 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | 仅测试适配器 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 18.0.1 | 仅测试基础设施 | MIT |

运行组件实际包内许可原文保存在 `licenses/DOTNET-RUNTIME-LICENSE.txt`、`licenses/DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt`、`licenses/WINDOWS-DESKTOP-LICENSE.txt`。全部传递测试依赖和版本在测试项目 `packages.lock.json`；它们不打入主应用。

项目未使用外部 UI 框架、网络字体或外部图片。应用图标为此项目绘制；中文字体使用 Windows 自带字体，不另行分发。
