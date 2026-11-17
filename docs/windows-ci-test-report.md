# LiteClicker Windows CI 构建报告

**总体状态：待 Windows 11 实机验证。**

已在 GitHub Actions Windows 托管环境完成锁定依赖还原、Release 编译、逻辑测试和自包含单 EXE 发布。
这不是干净 Windows 11 桌面的交互验收；没有执行真实 SendInput、GUI、托盘、DPI、多屏、离线独立运行或性能验收。

- 构建运行：https://github.com/AsleepDragon/LiteClicker/actions/runs/44008944327
- 源码提交：2cf7e6eef29a66e0dacdb615b6dd069e754f835c
- 系统：Microsoft Windows Server 2025 Datacenter；版本 10.0.26100；构建 26100
- CPU：INTEL(R) XEON(R) PLATINUM 8573C；逻辑处理器 4
- 总内存：15.99 GiB
- .NET SDK：10.0.404
- 逻辑测试：42 通过、0 失败、0 未执行
- EXE：173233810 字节（165.21 MiB），未签名
- SHA-256：c6e4b69ebcdec28ccbc9f20782841e2e1ab0288728f8dd3cb4d4da041a755307

完整 CI 日志见上面的构建运行；原始测试结果见 windows-core-tests.trx。
cross-build-test-report.md 保留此前 Linux 交叉构建记录及 AT01–AT27 状态；其中旧 EXE 校验值只对应原交叉构建文件。
本次下载的 Windows CI 构建文件以本报告及 SHA256SUMS.txt 为准。未完成的 Windows 11 验收仍按 docs/windows-validation.md 执行。

发行页：https://github.com/AsleepDragon/LiteClicker/releases/tag/v1.0.0-preview.1

已核对 9 个发行附件的服务端 SHA-256；源码压缩包中的 53 个文件与构建提交一致，文本的 Windows 换行已在比较时规范化。
