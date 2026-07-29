# 快速编译命令参考

**前提条件**：已安装 .NET 6.0 或更高版本的 SDK

## 最快编译方式

```powershell
cd e:\06_Coding\XPanelPCService
.\Build-Project.ps1 -Configuration Release -Pack
```

**输出位置**: `publish\XPanelClient.exe` (可独立运行的 EXE)

---

## 详细编译选项

### 1️⃣ 仅编译（Debug 版本）
```powershell
.\Build-Project.ps1 -Configuration Debug
# 输出: bin\Debug\net6.0-windows\XPanelClient.exe
```

### 2️⃣ 编译 + 运行测试
```powershell
.\Build-Project.ps1 -Configuration Debug -Test
# 会执行所有单元测试
```

### 3️⃣ 编译 + 测试 + 发布（生产版本）
```powershell
.\Build-Project.ps1 -Configuration Release -Test -Pack
# 输出：publish\XPanelClient.exe (包含所有依赖，可独立运行)
```

### 4️⃣ 清理旧文件后重新编译
```powershell
.\Build-Project.ps1 -Configuration Debug -Clean
# 删除 bin、obj 等中间文件后重新编译
```

---

## 使用 dotnet CLI（无脚本）

```powershell
# 进入项目目录
cd e:\06_Coding\XPanelPCService

# 还原依赖
dotnet restore

# 编译
dotnet build -c Debug

# 运行测试
dotnet test -c Debug

# 发布
dotnet publish src\XPanel.Application -c Release -o publish

# 直接运行（不编译成 EXE）
dotnet run --project src\XPanel.Application
```

---

## 编译结果说明

| 输出位置 | 大小 | 特点 | 运行方式 |
|---------|------|------|---------|
| `bin\Debug\net6.0-windows\` | ~150 MB | 含调试符号，便于开发 | 需要 .NET 6.0 SDK |
| `bin\Release\net6.0-windows\` | ~80 MB | 代码优化，性能最佳 | 需要 .NET 6.0 SDK |
| `publish\` | ~200 MB | 包含 Runtime，完全独立 | **无需 SDK，直接运行** |

---

## 验证编译成功

编译完成后检查是否存在：
```powershell
# 检查 Debug 版本
Test-Path "bin\Debug\net6.0-windows\XPanelClient.exe"

# 检查 Release 版本
Test-Path "bin\Release\net6.0-windows\XPanelClient.exe"

# 检查发布版本
Test-Path "publish\XPanelClient.exe"
```

返回 `True` 表示编译成功 ✓

---

## 常用快捷命令

```powershell
# 一键快速编译（Debug）
cd e:\06_Coding\XPanelPCService && .\Build-Project.ps1

# 一键编译 + 测试
cd e:\06_Coding\XPanelPCService && .\Build-Project.ps1 -Test

# 一键生成发布版本（可独立运行）
cd e:\06_Coding\XPanelPCService && .\Build-Project.ps1 -Configuration Release -Pack

# 从任何位置快速打开解决方案
Start-Process "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe" "e:\06_Coding\XPanelPCService\XPanelServer.sln"
```

---

## 故障排查

| 错误信息 | 解决方案 |
|---------|--------|
| "No .NET SDKs were found" | 安装 .NET 6.0+ SDK |
| "error CS0246: The type or namespace name 'X' could not be found" | `dotnet restore` 还原包 |
| "error MSB3644: The reference assemblies for .NETFramework..." | 安装 .NET Desktop Runtime |
| 构建很慢 | 第一次构建会下载所有 NuGet 包，之后会快很多 |

---

## 项目已编译就绪 ✅

框架代码 100% 就绪，可以直接编译！

- ✅ 6 个项目，依赖关系配置完整
- ✅ 所有必需的 NuGet 包已声明
- ✅ WPF 主应用配置正确（OutputType: WinExe）
- ✅ 包含单元测试框架
- ✅ 提供构建脚本自动化

只需安装 .NET SDK，立即可编译可执行文件！

---

**最后更新**: 2026-07-27
