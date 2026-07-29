# XPanel 上位机软件 - 架构设计文档

## 一、项目概述

**项目名称**: XPanel 上位机软件  
**功能定位**: 硬件设备通信与监测上位机  
**运行方式**: 系统托盘应用 + 后台服务  
**目标平台**: Windows 10/11

### 核心功能
- 系统托盘常驻应用
- 系统消息监测与转发（Windows Notification）
- 多种设备通信接口（COM串口、蓝牙 BLE/Classic、以太网 MQTT）
- 图形化管理界面（设备信息、设备控制、系统设置）

---

## 二、整体架构设计

### 2.1 架构模型

```
┌─────────────────────────────────────────────────────────────┐
│                    UI 层（WPF/WinForms）                      │
│  ┌──────────────┬──────────────┬──────────────────────────┐   │
│  │ 设备信息标签页 │ 设备控制标签页 │   系统设置标签页        │   │
│  └──────────────┴──────────────┴──────────────────────────┘   │
│                          ↓                                    │
├─────────────────────────────────────────────────────────────┤
│              业务逻辑层（Service/Manager）                    │
│  ┌──────────────┬──────────────┬──────────────────────────┐   │
│  │ 设备管理服务   │ 消息监测服务   │   通信管理服务          │   │
│  │ DeviceManager │ WatcherService │ CommunicationManager  │   │
│  └──────────────┴──────────────┴──────────────────────────┘   │
│                          ↓                                    │
├─────────────────────────────────────────────────────────────┤
│              通信层（Communication）                          │
│  ┌──────────────┬──────────────┬──────────────┬────────────┐  │
│  │  COM串口驱动  │   蓝牙驱动    │   MQTT驱动   │  序列化   │  │
│  │ SerialDriver │ BluetoothDrv  │ MqttDriver   │ Serializer│  │
│  └──────────────┴──────────────┴──────────────┴────────────┘  │
│                          ↓                                    │
├─────────────────────────────────────────────────────────────┤
│              外部系统集成层                                   │
│  ┌──────────────┬──────────────┬──────────────────────────┐   │
│  │ Windows Toast │   系统日志    │     硬件设备            │   │
│  │   监听器      │   访问      │   (COM/蓝牙)            │   │
│  └──────────────┴──────────────┴──────────────────────────┘   │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### 2.2 核心模块说明

| 模块 | 职责 | 技术方案 |
|------|------|--------|
| **UIModule** | 用户界面展示与交互 | WPF (推荐) 或 WinForms |
| **TrayModule** | 系统托盘管理 | System.Windows.Forms.NotifyIcon |
| **WatcherModule** | Windows消息/通知监测 | WinEventHook + Toast Listener |
| **DeviceModule** | 设备连接与状态管理 | Device Model + Connection Pool |
| **CommunicationModule** | 序列化、报文解析、传输 | Protocol Buffer/JSON + Async I/O |
| **SerialModule** | COM串口通信驱动 | System.IO.Ports.SerialPort |
| **BleModule** | BLE GATT 通信驱动（无需配对，优先方案） | Windows.Devices.Bluetooth.GenericAttributeProfile (WinRT) |
| **BluetoothClassicModule** | 经典蓝牙 SPP 通信驱动（需配对，兼容方案） | 32feet.NET / InTheHand.Net.Bluetooth |
| **MqttModule** | 以太网 MQTT 通信驱动 | MQTTnet |
| **StorageModule** | 配置与日志持久化 | SQLite / JSON文件 |
| **LogModule** | 日志记录与诊断 | Serilog / NLog |

---

## 三、工程目录结构

```
XPanelPCService/
│
├── docs/                           # 文档目录
│   ├── Architecture.md
│   ├── DeviceProtocol.md
│   ├── API.md
│   └── UserGuide.md
│
├── src/                            # 源代码目录
│   ├── XPanel.Application/         # 主应用程序 (WPF项目)
│   │   ├── App.xaml
│   │   ├── App.xaml.cs
│   │   ├── MainWindow.xaml
│   │   ├── MainWindow.xaml.cs
│   │   ├── ViewModels/
│   │   │   ├── MainWindowViewModel.cs
│   │   │   ├── DeviceInfoViewModel.cs
│   │   │   ├── DeviceControlViewModel.cs
│   │   │   └── SystemSettingsViewModel.cs
│   │   ├── Views/
│   │   │   ├── DeviceInfoView.xaml
│   │   │   ├── DeviceControlView.xaml
│   │   │   ├── SystemSettingsView.xaml
│   │   │   └── TrayIconView.xaml
│   │   ├── Resources/
│   │   │   ├── Icons/
│   │   │   ├── Themes/
│   │   │   └── Styles.xaml
│   │   └── Properties/
│   │       └── AssemblyInfo.cs
│   │
│   ├── XPanel.Core/                # 核心业务逻辑 (Class Library)
│   │   ├── Device/
│   │   │   ├── DeviceManager.cs
│   │   │   ├── DeviceInfo.cs
│   │   │   ├── DeviceStatus.cs
│   │   │   └── DeviceEvents.cs
│   │   │
│   │   ├── Watcher/
│   │   │   ├── MessageWatcherService.cs
│   │   │   ├── ToastNotificationListener.cs
│   │   │   ├── WindowEventListener.cs
│   │   │   └── WatcherEvents.cs
│   │   │
│   │   ├── Communication/
│   │   │   ├── ICommunicationChannel.cs
│   │   │   ├── CommunicationManager.cs
│   │   │   ├── Protocol/
│   │   │   │   ├── MessageProtocol.cs
│   │   │   │   ├── ProtocolSerializer.cs
│   │   │   │   └── ProtocolDefines.cs
│   │   │   └── Interfaces/
│   │   │       └── IDevice.cs
│   │   │
│   │   ├── Storage/
│   │   │   ├── ConfigManager.cs
│   │   │   ├── DatabaseManager.cs
│   │   │   ├── Migrations/
│   │   │   └── Models/
│   │   │       ├── DeviceConfig.cs
│   │   │       ├── MessageLog.cs
│   │   │       └── UserSettings.cs
│   │   │
│   │   ├── Logging/
│   │   │   ├── LoggerFactory.cs
│   │   │   └── LogConfig.cs
│   │   │
│   │   ├── Utils/
│   │   │   ├── SerializationHelper.cs
│   │   │   ├── CrcHelper.cs
│   │   │   └── DateTimeHelper.cs
│   │   │
│   │   └── Events/
│   │       ├── MessageReceivedEventArgs.cs
│   │       ├── DeviceConnectedEventArgs.cs
│   │       └── ErrorEventArgs.cs
│   │
│   ├── XPanel.Communication.Serial/  # COM串口驱动 (Class Library)
│   │   ├── SerialDeviceDriver.cs
│   │   ├── SerialPortManager.cs
│   │   ├── SerialConfiguration.cs
│   │   └── Exceptions/
│   │       └── SerialPortException.cs
│   │
│   ├── XPanel.Communication.Bluetooth/  # 蓝牙驱动 (Class Library)
│   │   ├── Ble/                              # BLE GATT（优先，无需配对）
│   │   │   ├── BleDeviceDriver.cs
│   │   │   ├── BleScanner.cs
│   │   │   ├── GattServiceClient.cs
│   │   │   └── GattCharacteristicIds.cs
│   │   ├── Classic/                          # 经典蓝牙 SPP（兼容，需配对）
│   │   │   ├── ClassicBluetoothDriver.cs
│   │   │   └── SppClient.cs
│   │   ├── BluetoothDeviceManager.cs         # 统一管理 BLE / Classic
│   │   ├── BluetoothConfiguration.cs
│   │   └── Exceptions/
│   │       └── BluetoothException.cs
│   │
│   ├── XPanel.Communication.MQTT/           # MQTT 驱动 (Class Library)
│   │   ├── MqttDeviceDriver.cs
│   │   ├── MqttClient.cs
│   │   ├── MqttConfiguration.cs
│   │   ├── MqttSubscriptionManager.cs
│   │   ├── Models/
│   │   │   ├── MqttDeviceInfo.cs
│   │   │   └── MqttMessage.cs
│   │   └── Exceptions/
│   │       └── MqttException.cs
│   │
│   └── XPanel.Tests/                # 单元测试 (xUnit/NUnit)
│       ├── Core.Tests/
│       │   ├── DeviceManagerTests.cs
│       │   ├── MessageWatcherTests.cs
│       │   └── ProtocolSerializerTests.cs
│       ├── Serial.Tests/
│       │   └── SerialDriverTests.cs
│       ├── Bluetooth.Tests/
│       │   └── BluetoothDriverTests.cs
│       ├── Mqtt.Tests/
│       │   └── MqttDriverTests.cs
│       └── Integration.Tests/
│           └── EndToEndTests.cs
│
├── resources/                      # 资源文件
│   ├── icons/
│   │   ├── app-icon.ico
│   │   ├── tray-icon.ico
│   │   ├── tray-icon-connected.ico
│   │   └── tray-icon-disconnected.ico
│   ├── configs/
│   │   ├── default-config.json
│   │   └── protocol-defines.json
│   └── database/
│       └── schema.sql
│
├── tools/                          # 工具脚本
│   ├── Build-Application.ps1
│   ├── Test-Application.ps1
│   ├── Deploy-Application.ps1
│   └── GenerateInstallers.ps1
│
├── docs-legacy/                    # 历史文档（保留Toast Watcher相关）
│   ├── ToastWatcher.ps1
│   ├── Build-ToastWatcherNetFx.ps1
│   └── ToastWatcherNetFx/
│       └── Program.cs
│
├── .gitignore
├── .github/
│   └── workflows/
│       ├── build.yml
│       └── test.yml
├── XPanelServer.sln                # Visual Studio Solution文件
├── README.md                       # 项目说明
└── VERSION                         # 版本号文件

```

---

## 四、执行计划（分阶段实施）

### 阶段 1：基础框架搭建（第1-2周）

**目标**: 建立项目结构、基础通信框架、系统托盘

#### 1.1 项目初期化
- [ ] 创建 Visual Studio Solution 结构
- [ ] 建立项目间依赖关系
- [ ] 配置 NuGet 包管理
- [ ] 配置 CI/CD 流程

#### 1.2 核心基础类库开发
- [ ] 实现 `ICommunicationChannel` 接口定义
- [ ] 实现 `DeviceManager` 基类
- [ ] 实现 `MessageProtocol` 消息协议基类
- [ ] 配置日志框架 (Serilog)

#### 1.3 WPF 应用骨架
- [ ] 创建 WPF 主应用程序
- [ ] 实现系统托盘功能 (`NotifyIcon`)
- [ ] 实现主窗口基础布局
- [ ] 实现 MVVM 框架集成 (如使用 Prism)

**交付物**: 可运行的最小化应用，系统托盘可显示/隐藏

---

### 阶段 2：通信驱动开发（第3-5周）

**目标**: 完成 COM 串口、蓝牙、MQTT 驱动

#### 2.1 COM 串口驱动
- [ ] 实现 `SerialDeviceDriver` - 继承自 `ICommunicationChannel`
- [ ] 实现串口端口枚举、打开、关闭、读写
- [ ] 实现错误处理与自动重连机制
- [ ] 编写单元测试

#### 2.2 蓝牙驱动（BLE 优先 + 经典蓝牙兼容）

**BLE GATT（优先方案，无需配对）**
- [ ] 实现 `BleDeviceDriver` - 继承自 `ICommunicationChannel`
- [ ] 使用 `BluetoothLEAdvertisementWatcher` 扫描设备广播
- [ ] 使用 `BluetoothLEDevice.FromBluetoothAddressAsync()` 直接连接，无需系统配对弹窗
- [ ] 通过 GATT Service / Characteristic UUID 读写数据及订阅 Notify
- [ ] 实现连接断开自动重连机制

**经典蓝牙 SPP（兼容方案，需配对）**
- [ ] 实现 `ClassicBluetoothDriver` - 继承自 `ICommunicationChannel`
- [ ] 集成 32feet.NET 库实现 SPP 串口仿真通信
- [ ] 实现设备枚举与已配对设备管理

**通用**
- [ ] 实现 `BluetoothDeviceManager` 统一管理两种驱动
- [ ] 实现错误处理与异常情况
- [ ] 编写单元测试

#### 2.3 MQTT 驱动

**以太网 MQTT（云端或本地 Broker）**
- [ ] 实现 `MqttDeviceDriver` - 继承自 `ICommunicationChannel`
- [ ] 集成 MQTTnet 库，支持 MQTT 3.1.1 和 5.0
- [ ] 实现连接、订阅、发布、断线重连
- [ ] 实现 Topic 路由与消息转发（设备可通过不同 Topic 区分）
- [ ] 支持 Broker 地址、用户名密码、SSL/TLS 配置
- [ ] 编写单元测试

#### 2.4 通信管理器
- [ ] 实现 `CommunicationManager` - 管理多个通信通道
- [ ] 实现通道切换与故障转移机制
- [ ] 实现消息队列与异步发送

**交付物**: 能正确识别和连接硬件设备的驱动层（支持 COM/BLE/Classic Bluetooth/MQTT）

---

### 阶段 3：消息监测与处理（第5-6周）

**目标**: 完成 Windows 消息监测与转发

#### 3.1 消息监测服务
- [ ] 实现 `ToastNotificationListener` - 监听 Windows Toast 通知
  - 使用 WinRT API 注册 Toast 监听
  - 解析通知元数据（标题、内容、应用名等）
- [ ] 实现 `WindowEventListener` - 监听窗口事件
  - 使用 SetWinEventHook 监听窗口创建、激活等事件
- [ ] 实现事件过滤与优化（避免过度捕获）

#### 3.2 消息处理流程
- [ ] 实现消息去重机制
- [ ] 实现消息格式转换为设备协议
- [ ] 实现消息转发到设备
- [ ] 实现消息缓存与离线队列

#### 3.3 测试
- [ ] 编写集成测试
- [ ] 验证不同应用的消息捕获

**交付物**: 后台自动捕获并转发系统消息到设备

---

### 阶段 4：UI 界面开发（第7-9周）

**目标**: 完成用户界面的所有功能

#### 4.1 设备信息标签页
- [ ] 显示已连接设备列表
- [ ] 显示设备在线状态、信号强度
- [ ] 显示设备版本、型号、MAC地址等
- [ ] 设备详细信息查看
- [ ] 实时状态刷新

#### 4.2 设备控制标签页
- [ ] 实现设备命令发送界面
- [ ] 实现常用控制命令（如亮度调节、开/关等）
- [ ] 实现命令执行反馈显示
- [ ] 实现命令历史记录

#### 4.3 系统设置标签页
- [ ] 通信参数配置（串口号、波特率、蓝牙适配器选择等）
- [ ] 消息监测规则配置（哪些应用的消息需要转发）
- [ ] 日志级别设置
- [ ] 启动选项（开机自启等）
- [ ] 关于与版本信息

#### 4.4 UI 增强
- [ ] 实现深色/浅色主题
- [ ] 美化图标与界面元素
- [ ] 实现拖拽与快捷方式
- [ ] 本地化支持 (中文/英文)

**交付物**: 美观、易用的完整 UI 界面

---

### 阶段 5：数据存储与配置（第10周）

**目标**: 完成数据持久化

#### 5.1 配置管理
- [ ] 使用 JSON 或 SQLite 存储配置
- [ ] 配置版本管理与迁移
- [ ] 配置导入/导出功能

#### 5.2 日志存储
- [ ] 配置日志文件路径与大小限制
- [ ] 实现日志滚动归档
- [ ] 实现日志查询与过滤界面

#### 5.3 消息历史
- [ ] 存储设备收发消息历史
- [ ] 实现消息统计与分析

**交付物**: 数据持久化与管理体系

---

### 阶段 6：测试与优化（第11-12周）

**目标**: 确保软件质量与稳定性

#### 6.1 功能测试
- [ ] 全流程功能测试
- [ ] 边界条件测试
- [ ] 异常情况处理测试

#### 6.2 性能优化
- [ ] 内存泄漏检查
- [ ] CPU 占用率优化
- [ ] 消息处理吞吐量优化

#### 6.3 稳定性测试
- [ ] 长时间运行测试
- [ ] 设备热插拔测试
- [ ] 消息风暴测试

#### 6.4 安装包与部署
- [ ] 制作 MSI 或 NSIS 安装程序
- [ ] 测试卸载与升级流程
- [ ] 编写安装文档

**交付物**: 生产级可发布的应用程序

---

### 阶段 7：文档与发布（第13周）

**目标**: 完成文档与发布

- [ ] 编写用户手册
- [ ] 编写开发者文档与 API 说明
- [ ] 编写协议文档与扩展指南
- [ ] 提交发行版本

**交付物**: 完整文档与正式发布版本

---

## 五、技术栈选型

| 层级 | 技术 | 说明 |
|------|------|------|
| **UI框架** | WPF + MVVM Toolkit | 现代 UI，良好的数据绑定 |
| **通信** | async/await + .NET Standard | 异步处理，提高响应性 |
| **序列化** | Newtonsoft.Json / Protobuf | 灵活的消息格式 |
| **数据库** | SQLite + EF Core | 轻量级本地存储 |
| **日志** | Serilog | 灵活的日志框架 |
| **蓝牙 BLE** | Windows.Devices.Bluetooth.GenericAttributeProfile | WinRT BLE GATT，**无需配对**，优先方案 |
| **蓝牙经典** | 32feet.NET (InTheHand.Net.Bluetooth) | SPP 串口仿真，需系统配对，兼容方案 |
| **以太网 MQTT** | MQTTnet | 云端或本地 Broker，无需设备配对，集中管理多设备 |
| **测试** | xUnit + Moq | 单元测试框架 |
| **打包** | NSIS / WiX | 安装程序制作 |
| **.NET版本** | .NET Framework 4.7.2 / .NET 6+ | 支持 Windows 7 及以上，或仅支持现代系统 |

---

## 六、关键设计决策

### 6.1 通信协议设计考虑
- 使用 CRC 校验保证数据完整性
- 支持请求-应答与异步推送两种模式
- 预留扩展字段用于未来功能

### 6.2 通信方案选择与优先级

#### 6.2.1 蓝牙通信方案选择

| 特性 | BLE GATT（优先） | 经典蓝牙 SPP（兼容） |
|------|-----------------|--------------------|
| 是否需要配对 | **不需要** | 必须配对 |
| 用户体验 | 无弹窗，静默连接 | 需要系统配对弹窗 |
| 功耗 | 低 | 高 |
| 吞吐量 | 中（适合指令/状态） | 高（适合大量数据） |
| Windows API | WinRT（Win10/11原生） | 32feet.NET |
| 推荐场景 | IoT 控制类设备 | 高速数据传输设备 |

**策略**: 默认使用 BLE GATT，设备硬件仅支持经典蓝牙时自动降级为 SPP 模式，通过 `BluetoothDeviceManager` 透明切换，上层业务代码无感知。

#### 6.2.2 以太网 MQTT 通信方案

| 特性 | 优势 | 注意事项 |
|------|------|--------|
| **通信协议** | MQTT 3.1.1 / 5.0 | 轻量级、发布-订阅 |
| **网络要求** | 有线以太网或 WiFi | 需要网络连接 |
| **Broker** | 本地 Mosquitto、云端（Azure IoT Hub/AWS IoT Core）等 | 需要事先部署 Broker |
| **多设备管理** | 单一连接管理多设备（Topic 隔离） | 相比 COM/蓝牙更易扩展 |
| **配对需求** | **无** | 直接连接，认证靠用户名密码/证书 |
| **延迟** | 毫秒级（本地 Broker）、秒级（云端） | 远程部署可能有延迟 |
| **推荐场景** | 多设备统一管理、云端数据同步、远程诊断 | 网络良好的工业环境 |

**策略**: 
- 优先通过 BLE/Classic Bluetooth 进行本地实时通信（低延迟、无网络依赖）
- 提供可选 MQTT 通道用于云端数据同步、远程监控、多PC 协联
- 同一设备可同时支持蓝牙与 MQTT 双通道（冗余与扩展）

### 6.2 线程安全
- 使用 `ConcurrentQueue` 实现消息队列
- 在 UI 线程与业务线程间使用事件与委托通信
- 避免直接的线程间共享状态

### 6.3 资源管理
- 实现 IDisposable 模式管理资源
- 配置文件与缓存使用专用目录 (`%APPDATA%/XPanel/`)
- 定期清理过期日志

### 6.4 可扩展性
- 使用工厂模式创建不同类型的通信驱动
- 使用策略模式处理不同设备类型
- 支持插件化扩展消息监测器

---

## 七、风险与应对

| 风险项 | 概率 | 影响 | 应对方案 |
|--------|------|------|--------|
| 蓝牙 API 兼容性 | 中 | 高 | 提前测试不同 Windows 版本，准备降级方案 |
| 消息捕获漏掉 | 中 | 中 | 实现多层捕获机制（Toast + Event Hook + 日志轮询） |
| 设备连接不稳定 | 中 | 高 | 实现自动重连、心跳检测机制 |
| 性能问题 | 低 | 中 | 提前进行性能测试，优化消息处理流程 |
| 系统兼容性 | 低 | 中 | 支持 Windows 10/11 的多个版本，使用兼容性库 |

---

## 八、后续维护与扩展

### 8.1 可能的扩展功能
- 支持多设备同时连接
- 云端数据同步
- 远程诊断与升级
- REST API 提供给第三方集成
- 电话/邮件告警功能

### 8.2 文档维护
- 定期更新协议文档
- 记录破坏性变更
- 维护常见问题解答

### 8.3 用户反馈机制
- 内置故障报告功能
- 遥测数据收集（崩溃、错误）
- 定期更新与改进

---

## 附录：快速参考

### 核心依赖包
```xml
<!-- UI与MVVM -->
<PackageReference Include="Microsoft.Xaml.Behaviors.Wpf" Version="1.1.31" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.1" />

<!-- 通信与序列化 -->
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
<PackageReference Include="protobuf-net" Version="3.24.4" />

<!-- 数据库 -->
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="7.0.13" />

<!-- 日志 -->
<PackageReference Include="Serilog" Version="3.1.0" />
<PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />

<!-- 测试 -->
<PackageReference Include="xunit" Version="2.6.4" />
<PackageReference Include="Moq" Version="4.20.69" />

<!-- 蓝牙 BLE（优先，无需配对，WinRT 原生） -->
<PackageReference Include="Windows.Devices.Bluetooth" Version="16.0.0.0" />
<!-- 蓝牙经典 SPP（兼容方案，需配对） -->
<PackageReference Include="InTheHand.Net.Bluetooth" Version="4.1.40" />
<!-- 以太网 MQTT（云端/本地 Broker，无需配对） -->
<PackageReference Include="MQTTnet" Version="4.3.2.959" />
```

### 启动检查清单
- [ ] 项目编译无错误
- [ ] 单元测试通过率 > 80%
- [ ] 代码静态分析通过
- [ ] 文档完整且准确
- [ ] 发行版本签名

---

**文档版本**: 1.2  
**最后更新**: 2026-07-27  
**维护人**: XPanel 开发团队
