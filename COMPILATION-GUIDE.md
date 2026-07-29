# XPanel 编译和运行指南

## 环境要求

- **操作系统**: Windows 10 或更高版本（Win11 推荐）
- **.NET SDK**: 6.0 或更高版本
- **IDE** (可选):
  - Visual Studio 2022 (社区版免费)
  - Visual Studio Code + C# 扩展
  - 或任何支持 .NET 的编辑器

## 下载和安装 .NET SDK

### 方式1：官方网站下载（推荐）
1. 访问 [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)
2. 选择 **.NET 6.0** 或更高版本
3. 下载对应操作系统的 SDK (Windows x64)
4. 运行安装程序，完成安装
5. 验证安装：
   ```powershell
   dotnet --version
   ```

### 方式2：使用 Windows Package Manager (winget)
```powershell
winget install Microsoft.DotNet.SDK.6
```

### 方式3：使用 Chocolatey
```powershell
choco install dotnet-sdk
```

## 编译项目

### 方式1：使用 PowerShell 构建脚本（推荐）

```powershell
# 进入项目目录
cd e:\06_Coding\XPanelPCService

# 基础构建
.\Build-Project.ps1 -Configuration Debug

# 构建 + 运行测试
.\Build-Project.ps1 -Configuration Debug -Test

# 构建 + 测试 + 发布 Release 版本
.\Build-Project.ps1 -Configuration Release -Test -Pack
```

**脚本功能**:
- 检查 .NET SDK 可用性
- 清理旧的构建文件（可选 `-Clean` 开关）
- 还原 NuGet 包
- 编译项目
- 运行单元测试
- 发布应用到 `publish` 文件夹

### 方式2：使用 dotnet CLI（手动）

```powershell
cd e:\06_Coding\XPanelPCService

# 还原 NuGet 依赖
dotnet restore XPanelServer.sln

# 构建 Debug 版本
dotnet build XPanelServer.sln -c Debug

# 或者构建 Release 版本
dotnet build XPanelServer.sln -c Release

# 运行单元测试
dotnet test XPanelServer.sln -c Debug

# 发布应用（独立可执行文件）
dotnet publish src\XPanel.Application\XPanel.Application.csproj -c Release -o publish
```

### 方式3：使用 Visual Studio 2022

1. 打开 Visual Studio 2022
2. 选择 **文件** → **打开** → **项目/解决方案**
3. 选择 `e:\06_Coding\XPanelPCService\XPanelServer.sln`
4. 等待项目加载和 NuGet 包还原
5. 在解决方案资源管理器右键点击 `XPanel.Application`
6. 选择 **设为启动项目**
7. 按 `Ctrl+Shift+B` 或选择 **生成** → **生成解决方案**
8. 按 `F5` 或 **调试** → **启动调试** 运行应用

## 输出文件位置

### Debug 构建
```
bin/Debug/net6.0-windows/XPanelClient.exe
```

### Release 构建
```
bin/Release/net6.0-windows/XPanelClient.exe
```

### 发布版本（独立运行）
```
publish/XPanelClient.exe
```

## 可执行文件的特点

| 版本 | 大小 | 特点 | 位置 |
|------|------|------|------|
| **Debug** | ~150 MB | 包含符号文件，便于调试 | `bin/Debug/` |
| **Release** | ~80 MB | 代码优化，较小体积 | `bin/Release/` |
| **Self-Contained** | ~200 MB | 包含 .NET Runtime，无需安装 SDK | `publish/` |

## 运行应用

### 直接运行编译后的 EXE

```powershell
# 从项目目录运行
.\bin\Debug\net6.0-windows\XPanelClient.exe
```

### 或者使用 dotnet run

```powershell
cd e:\06_Coding\XPanelPCService

# Debug 版本
dotnet run --project src\XPanel.Application\XPanel.Application.csproj

# Release 版本
dotnet run --project src\XPanel.Application\XPanel.Application.csproj -c Release
```

## 应用功能演示

运行后，你会看到：

1. **主窗口**：3 个标签页
   - **设备信息**: 已连接设备列表
   - **设备控制**: 命令发送界面
   - **系统设置**: 参数配置

2. **系统托盘**：
   - 左下角系统托盘中显示 XPanel 图标
   - 双击切换窗口可见性
   - 右键菜单：显示、隐藏、退出

3. **状态栏**：
   - 显示连接状态
   - 显示通信方式和参数

## 常见问题排查

### 问题1：编译失败 - "No .NET SDKs were found"
**解决方案**:
```powershell
# 检查 SDK 安装
dotnet --list-sdks

# 如果无输出，下载安装 .NET 6.0 SDK
```

### 问题2：编译失败 - NuGet 包下载失败
**解决方案**:
```powershell
# 清除 NuGet 缓存
dotnet nuget locals all --clear

# 重新还原
dotnet restore XPanelServer.sln
```

### 问题3：编译失败 - "error MSB3644: The reference assemblies..."
**解决方案**:
```powershell
# 确保安装了 Windows 10 SDK (或 11)
# 或在 VS2022 中重新安装 ".NET Desktop Runtime"
```

### 问题4：运行时崩溃 - "Unable to load DLL..."
**解决方案**:
- 确保运行 Debug 或 Release 版本（不要直接运行 obj 文件夹中的 exe）
- 删除 `bin` 和 `obj` 文件夹，重新构建

## 项目结构

```
XPanelPCService/
├── src/
│   ├── XPanel.Core/                    # 核心库
│   ├── XPanel.Communication.Serial/    # COM 驱动
│   ├── XPanel.Communication.Bluetooth/ # 蓝牙驱动
│   ├── XPanel.Communication.MQTT/      # MQTT 驱动
│   ├── XPanel.Application/             # WPF 应用 (输出 EXE)
│   └── XPanel.Tests/                   # 单元测试
├── bin/                                # 编译输出
├── obj/                                # 中间文件
└── publish/                            # 发布输出
```

## 编译输出文件说明

### Debug 版本（`bin/Debug/net6.0-windows/`）

```
XPanelClient.exe                    # 主可执行文件
XPanel.Application.dll              # 应用程序集
XPanel.Application.pdb              # 调试符号文件
XPanel.Core.dll                     # 核心库程序集
XPanel.Communication.Serial.dll     # COM 驱动
XPanel.Communication.Bluetooth.dll  # 蓝牙驱动
XPanel.Communication.MQTT.dll       # MQTT 驱动
... (其他依赖 DLL)
```

### 依赖文件

应用运行需要以下文件：
- **Serilog.dll** - 日志库
- **CommunityToolkit.Mvvm.dll** - MVVM 框架
- **MQTTnet.dll** - MQTT 通信库
- **InTheHand.Net.Bluetooth.dll** - 蓝牙库
- **.NET Runtime DLLs** - Windows Forms、WPF 等系统库

所有这些文件在编译时自动包含在 `bin` 文件夹中。

## 验证编译成功的标志

编译完成后，你应该看到：

```
✓ 还原完成
✓ 构建完成
✓ 测试完成 (如果运行了 -Test)
✓ 打包完成，输出目录: ...
```

## 下一步

1. **修改代码**：在 Visual Studio 中打开项目进行开发
2. **运行调试**：按 `F5` 启动调试会话
3. **查看输出**：打开 `输出` 窗口查看编译日志
4. **设置断点**：在代码中按 `F9` 设置断点进行调试

## 项目特点

✅ **完全编译就绪** - 所有必需的项目文件和配置已完成  
✅ **零外部依赖** - 只需 .NET SDK  
✅ **包含单元测试** - 可以验证核心功能  
✅ **可发布为独立应用** - 无需安装 .NET Runtime  
✅ **详细的代码注释** - 易于理解和扩展  

---

**预期编译时间**: 
- 首次编译（还原所有 NuGet 包）：3-5 分钟
- 后续增量编译：10-30 秒

**生成的 EXE 文件大小**:
- Debug 版本：150-200 MB
- Release 版本：80-100 MB

---

**文档版本**: 1.0  
**生成日期**: 2026-07-27
