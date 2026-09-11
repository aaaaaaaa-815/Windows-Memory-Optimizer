# Windows Memory Optimizer (x64)

一个基于 C# / .NET 开发的高效 Windows 内存深度清理与托盘监控工具。

## 🌟 核心特性

- **Native API 深度清理**：调用 `ntdll!NtSetSystemInformation` 批量释放工作集 (Working Sets) 与备用列表 (Standby List)。
- **动态托盘柱状图**：托盘图标实时根据当前内存使用率绘制动态颜色柱状图。
- **🔕 静默模式支持**：可随时开启/关闭右下角气泡弹窗通知，拒绝打扰。
- **自动化清理规则**：支持自定义定时清理（如每 30 分钟）与阈值自动清理（如占用率 > 80%）。
- **完全异步与防重复触发**：使用 CAS 原子锁与后台线程，清理过程零卡顿、零假死。

## ⚙️ 运行环境要求

- **操作系统**：Windows 10及以上
- **架构**：纯 x64 架构
- **权限**：需要以 **管理员身份 (Administrator)** 运行

## 🔨 命令行编译与打包

本程序完全支持使用 .NET CLI 命令行进行编译和发布。请打开终端（PowerShell 或 CMD）并进入项目根目录：

### 1. 调试编译 (Debug)

```bash
dotnet build -c Debug -r win-x64
```

### 2. 发布发布版 (Release - 单文件免依赖打包)

使用以下命令可直接打包为**单个独立可执行文件 (.exe)**，无需目标电脑预先安装 .NET 运行时：

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

编译完成后，打包好的 `.exe` 文件将生成在以下路径：
`.\bin\Release\net11.0\win-x64\publish\`

---

## 表达感谢 (Acknowledgements)

特别感谢 **[PCLCE (Plain Craft Launcher CE)](https://github.com/PCL-Community/PCL-CE)** 项目及开源社区！
本工具在内存管理优化逻辑与 Windows Native API 的高稳定性调优思路上，参考并借鉴了 PCLCE 在极致性能与内存控制方面的优秀实践。感谢开源社区为开发者们提供的卓越灵感与宝贵经验！
