# XPanel 统一通信协议 Draft (V0.3, Binary First)

> 状态: Draft
> 日期: 2026-07-28
> 目标: 定义 XPanel 在 BLE / 串口 / MQTT 等通信方式下统一的二进制 payload 格式，支持可扩展、可靠传输、按 app_id 路由。

---

## 1. 设计目标

1. 统一且轻量
- 业务 payload 统一为二进制编码，跨 BLE/UART/MQTT 保持同一语义。
- 比 JSON 更省带宽、解析开销更低、实时性更好。

2. 可扩展
- 固定头 + TLV 体，允许新增字段与新增操作。
- 保留扩展区，接收端可忽略未知 TLV。

3. 可靠性
- 支持 msg_id、ACK、重传、去重、TTL。
- 支持 cmd/resp/event/ack/error 五种消息类型。

4. 路由隔离
- 强制包含 app_id，支持 App/Service 路由。

---

## 2. 协议分层

- L3 业务层: XPanel Binary Payload（本草案定义）
- L2 会话可靠层: ACK/重传/去重/超时
- L1 传输层: BLE / UART / MQTT

说明:
- BLE/UART/MQTT 仅负责承载，不改 payload 语义。
- V0.2 默认二进制。
- JSON 仅作为调试模式（开发阶段可选），不作为量产主通道。

---

## 3. 二进制消息格式（V0.2）

### 3.1 总体结构

```text
XPF Frame = Fixed Header (24B) + TLV Body (N bytes)
```

### 3.2 Fixed Header（24字节，网络字节序 Big Endian）

| 偏移 | 长度 | 字段 | 说明 |
|---|---:|---|---|
| 0 | 2 | magic | 固定 `0x58 0x50`（ASCII: XP） |
| 2 | 1 | ver_major | 主版本，当前 `0x01` |
| 3 | 1 | ver_minor | 次版本，当前 `0x00` |
| 4 | 1 | msg_type | 1=cmd,2=resp,3=event,4=ack,5=error |
| 5 | 1 | flags | bit0:need_ack, bit1:encrypted, bit2:compressed |
| 6 | 1 | qos_level | 0=at-most-once,1=at-least-once |
| 7 | 1 | hop | 转发跳数 |
| 8 | 2 | app_id | 目标 app/service ID |
| 10 | 2 | op_code | 操作码（替代字符串 op） |
| 12 | 4 | msg_id | 32位消息 ID（发送端递增或随机） |
| 16 | 4 | ts_sec | Unix 秒时间戳 |
| 20 | 2 | body_len | TLV Body 长度 |
| 22 | 2 | hdr_crc16 | Header CRC16-CCITT(F0~F21) |

备注:
- 为压缩体积，`msg_id` 用 uint32（替代字符串 ULID）。
- 如需更强唯一性，可在 TLV 中附加 `client_id` 与 `boot_id`。

### 3.3 TLV Body 编码

TLV 单元格式:

```text
T(1B) + L(2B) + V(L bytes)
```

通用规则:
- T 为字段类型 ID。
- L 为 value 长度（0~65535）。
- V 按字段约定解释。
- 可包含多个 TLV，顺序不敏感。

### 3.4 通用 TLV 类型表（V0.3）

| T(hex) | 名称 | V 类型 | 说明 |
|---|---|---|---|
| 0x01 | ack_for_msg_id | uint32 | 对应被确认的 msg_id |
| 0x02 | timeout_ms | uint16 | ACK 超时 |
| 0x03 | retry | uint8 | 重传次数 |
| 0x04 | ttl_ms | uint32 | 消息有效期 |
| 0x05 | device_id | bytes | 设备标识（ASCII/UTF-8） |
| 0x06 | endpoint_id | bytes | 发送端标识 |
| 0x07 | err_code | uint16 | 错误码 |
| 0x08 | err_msg | bytes | 错误描述 |
| 0x09 | corr_id | uint32 | 链路追踪 ID |
| 0x0A | req_id | uint32 | 幂等请求 ID |
| 0x0B | client_nonce | uint32 | 握手客户端随机数 |
| 0x0C | server_nonce | uint32 | 握手设备端随机数 |
| 0x0D | keepalive_ms | uint16 | 保活周期（毫秒） |
| 0x0E | session_id | uint32 | 会话ID |
| 0x0F | session_ttl_ms | uint32 | 会话有效期（毫秒） |
| 0x20~0x7F | op params | mixed | 业务参数区 |
| 0xF0~0xFF | vendor ext | bytes | 厂商扩展 |

参数编码约定:
- uint8/16/32: Big Endian。
- bool: uint8（0/1）。
- string: UTF-8 bytes（不含 `\0`）。

---

## 4. op_code 与 app_id 路由（V0.2）

### 4.1 app_id 映射（与现有代码一致）

来自 `src/App/AppIds.h`:

| app_id | 名称 |
|---|---|
| 0 | None |
| 1 | Start |
| 2 | Clock |
| 3 | Humidity |
| 4 | Weather |
| 5 | Temperature |
| 6 | Tetris |
| 7 | Radio |
| 8 | Reset |

### 4.2 服务 ID 预留段

建议 `100~199` 为系统服务:

| app_id | 服务 |
|---|---|
| 100 | NetworkMgr |
| 101 | NotificationMgr |
| 102 | NvmMgr |
| 103 | SleepMgr |
| 104 | WebServerMgr |
| 105 | BLEMgr |
| 106 | ProtocolMgr（未来） |

### 4.3 op_code 建议表（MVP）

| op_code | 含义 |
|---:|---|
| 0x0001 | system.ping |
| 0x0002 | system.get_caps |
| 0x0003 | session.hello |
| 0x0004 | session.bye |
| 0x0005 | session.keepalive |
| 0x0010 | app.switch |
| 0x0020 | notify.push |
| 0x0030 | weather.update |
| 0x0040 | nvm.write |
| 0x0041 | nvm.read |
| 0x0050 | net.scan |
| 0x0060 | radio.play |
| 0x00F0 | system.reboot |

---

## 5. 可靠性机制

### 5.1 ACK 机制

- `flags.need_ack=1` 时，接收方必须返回 `msg_type=ack/resp/error`。
- ACK/RESP/ERROR 必须携带 TLV: `ack_for_msg_id(0x01)`。

### 5.2 重传建议

- 发送端维护 pending 表: `msg_id -> frame, send_ts, retry_count`。
- 超时未确认按 `retry` 重传。
- 默认值建议:
  - timeout_ms = 1500
  - retry = 2
  - qos_level = 1

### 5.3 去重建议

- 接收端维护最近窗口（建议 256 条）msg_id。
- 重复包不重复执行业务，只回 ACK。

### 5.4 幂等

- 关键写操作（nvm.write/system.reboot）建议带 `req_id(0x0A)`。
- 服务端以 `req_id + endpoint_id` 做幂等判定。

### 5.5 握手机制（Session Handshake）

目标:
- 在业务消息前建立会话上下文，协商保活周期，避免“半连接”状态下误控制。

握手流程（推荐）:

1. `HELLO`（控制端 -> 设备）
- `msg_type=cmd`
- `op_code=0x0003 (session.hello)`
- `app_id=106 (ProtocolMgr)`
- `flags.need_ack=1`
- TLV 至少包含:
  - `endpoint_id(0x06)`
  - `client_nonce(0x0B)`
  - `keepalive_ms(0x0D)`（建议值，如 25000）

2. `HELLO-RESP`（设备 -> 控制端）
- `msg_type=resp`（或 `ack` + 后续 `event`）
- `op_code=0x0003 (session.hello)`
- TLV 至少包含:
  - `ack_for_msg_id(0x01)`
  - `server_nonce(0x0C)`
  - `session_id(0x0E)`
  - `keepalive_ms(0x0D)`（设备最终采用值）
  - `session_ttl_ms(0x0F)`

3. 会话生效
- 握手成功后，双方缓存 `session_id`。
- 非握手业务包建议携带 `session_id(0x0E)`。

4. `BYE`（任一端主动断开）
- `op_code=0x0004 (session.bye)`
- 发送后会话立即失效，需重新握手。

实现建议:
- 若设备重启/固件升级，旧 `session_id` 一律作废。
- 设备在无有效会话时可拒绝高风险写操作并返回错误码。

### 5.6 Alive Check（保活机制）

目标:
- 快速检测链路失活，并在必要时触发重连和重握手。

规则:

1. 心跳消息
- `msg_type=cmd`
- `op_code=0x0005 (session.keepalive)`
- `flags.need_ack=1`
- TLV 包含 `session_id(0x0E)`。

2. 发送时机
- 当链路在 `keepalive_ms` 内没有任何业务包时，发送 keepalive。
- 任意有效入站包可刷新“最近活跃时间”，可不额外发 keepalive。

3. 超时判定
- 连续 `N` 次（建议 N=3）keepalive 未获 ACK/RESP，则判定链路失活。
- 判定失活后：
  - 本地会话状态置为 stale
  - 停止发送高风险业务指令
  - 进入重握手流程（重新发送 `session.hello`）

4. 参数建议
- `keepalive_ms`: 15000~30000（默认 25000）
- 失活阈值: `3 * keepalive_ms`
- `session_ttl_ms`: 60000~300000（默认 120000）

5. 接收端策略
- 收到过期/无效 `session_id` 的业务包，返回会话错误码并要求重握手。

---

## 6. 传输层绑定规则

统一原则: Header+TLV 字节序列跨通道保持一致。

### 6.1 BLE

- GATT 特征值承载 XPF Frame。
- MTU 不足时分片，分片头建议:
  - frag_session_id (2B)
  - frag_index (1B)
  - frag_total (1B)
  - frag_len (2B)
- 重组完成后校验 `hdr_crc16`，再投递协议层。

#### 6.1.1 当前固件 BLE GATT 标识（控制端必读）

> 适用版本: 2026-07-28 当前代码（`src/Services/BLEMgr/BLEMgr.cpp` + `src/main.cpp`）

- 设备广播名:
  - `XPanel-<ID6>`
  - 其中 `ID6` 是由 ESP32 Base MAC 派生的 6 位大写字母数字串（`0-9A-Z`）。
  - 控制端建议按前缀 `XPanel-` 扫描匹配，避免写死完整设备名。

- Service UUID（Primary）:
  - `6E400001-B5A3-F393-E0A9-E50E24DCCA9E`

- RX Characteristic UUID（控制端 -> 设备，Write/WriteWithoutResponse）:
  - `6E400002-B5A3-F393-E0A9-E50E24DCCA9E`

- TX Characteristic UUID（设备 -> 控制端，Notify）:
  - `6E400003-B5A3-F393-E0A9-E50E24DCCA9E`

- 推荐控制端收发顺序:
  1. 扫描并连接设备（按 `XPanel-` 前缀筛选）
  2. 发现并校验 Service UUID
  3. 对 TX 特征开启 Notify
  4. 将 XPF Frame（二进制）写入 RX 特征
  5. 按协议层处理 ACK/RESP/ERROR（见第 5 章）

### 6.2 UART

- 建议外层帧:
  - `SOF(2B=0x55AA) + FRAME_LEN(2B) + XPF_FRAME(NB) + FRAME_CRC16(2B)`
- `FRAME_LEN` 为 XPF_FRAME 长度。
- 串口建议波特率 >= 115200。

### 6.3 MQTT

- Topic 建议:
  - 上行: `xpanel/{device_id}/up`
  - 下行: `xpanel/{device_id}/down`
  - 广播: `xpanel/broadcast/down`
- MQTT payload 直接放 XPF_FRAME 二进制。
- MQTT QoS 与 `qos_level` 可并存。

---

## 7. 错误码草案

| code | 含义 |
|---:|---|
| 0 | OK |
| 4001 | Bad Request（字段缺失/格式错误） |
| 4002 | Unsupported Version |
| 4003 | Unsupported OpCode |
| 4004 | Invalid AppId |
| 4005 | Timeout |
| 4006 | Busy |
| 4010 | Handshake Required |
| 4011 | Invalid Session |
| 4012 | Session Expired / Keepalive Timeout |
| 5001 | Internal Error |
| 5002 | Storage Error |
| 5003 | Network Error |

---

## 8. 调试模式（可选）

开发阶段可开启 JSON 调试桥接:

- 输入 JSON -> 转换为 XPF Frame -> 发送。
- 接收 XPF Frame -> 反解为 JSON -> 打印日志。

注意:
- 调试桥接仅限开发工具，不作为设备量产协议主路径。

---

## 9. 最小落地建议

1. 在 `Services/Protocol/` 先实现 `xpf_encode/xpf_decode`。
2. 增加 `crc16_ccitt` 与 TLV 工具函数。
3. BLE/UART/MQTT 入口统一调用 decode -> router(app_id, op_code)。
4. 实现 ACK 管理器（pending/retry/timeout）。
5. 增加 3 条回归链路:
  - 重复包（去重验证）
  - 超时包（重传验证）
  - 非法头/CRC（健壮性验证）

---

## 附录 A: 二进制示例（app.switch -> Weather）

场景:
- cmd: `app.switch`（op_code=0x0010）
- to app_id=1（Start App 负责路由切换）
- params: target_app_id=4

TLV 设计:
- T=0x20, L=0x0001, V=0x04  （target_app_id）

示例（十六进制展示，空格分隔）:

```text
58 50 01 00 01 01 01 00 00 01 00 10 00 00 10 2A 66 A0 5A C0 00 04 12 34
20 00 01 04
```

说明:
- 前 24B 为 header。
- `12 34` 为 hdr_crc16 示例值（演示用，实际应按算法计算）。
- body 只有一个 TLV（目标 app_id=4）。

---

## 附录 B: 当前固件实现约束（避免控制端 mismatch）

> 适用版本: 2026-07-28 当前代码（`src/Services/ComProtocol/ProtocolMgr.cpp` + `ComProtocol.cpp`）

以下是“草案之外”的**实现事实**，控制端建议按本节执行。

### B.1 握手请求最小要求（设备当前严格检查）

控制端发送 `session.hello` 时，设备当前要求：

1. Header:
- `msg_type = cmd`
- `app_id = 106 (ProtocolMgr)`
- `op_code = 0x0003 (session.hello)`

2. TLV:
- 必须带 `endpoint_id(0x06)`，长度范围 `1..48` 字节。
- 必须带 `client_nonce(0x0B)`，长度必须是 `4` 字节（uint32）。
- `keepalive_ms(0x0D)` 可选；如果不带或带 `0`，设备使用默认值。

不满足以上条件时，设备返回 `msg_type=error`，`err_code=4001`。

### B.2 keepalive 协商值会被夹紧

设备对 `keepalive_ms` 的采用值会做 clamp：
- 最小 `15000`
- 最大 `30000`
- 默认 `25000`

控制端应以 `HELLO-RESP` 返回的 `keepalive_ms` 为准，不要假设请求值一定被接受。

### B.3 会话与信道绑定规则（当前实现为单会话）

设备当前实现是**全局单会话**：同一时刻只维护一个 active session。

- 握手成功后，设备绑定 `(channel_type, channel_id)`。
- 后续控制端包如果从其他信道进入，会返回 `4011 Invalid Session`（错误文本 `Channel Mismatch`）。
- 绑定信道断开时（例如 BLE 断开），设备立即清除会话。

控制端建议：
- 握手成功后，后续业务包固定走同一物理信道。
- 断链后先重握手，再发业务。

### B.4 非握手包的 session_id 要求（当前实现）

设备在会话已建立后，对除 `session.hello / session.keepalive / session.bye` 以外的包，会检查 TLV `session_id(0x0E)`：

- 缺失 -> `4011 Invalid Session`（错误文本 `Missing SessionId`）
- 不匹配 -> `4011 Invalid Session`（错误文本 `Invalid SessionId`）

控制端建议将 `session_id` 视为握手后业务包必填项。

### B.5 会话未建立时的行为

会话未建立时，设备收到普通 `cmd` 会返回：
- `msg_type=error`
- `err_code=4010 (Handshake Required)`
- 并携带 `ack_for_msg_id(0x01)`。

### B.6 设备响应字段细节

1. HELLO-RESP:
- `msg_type=resp`
- TLV 至少包含：`ack_for_msg_id`、`server_nonce`、`session_id`、`keepalive_ms`、`session_ttl_ms`

2. ACK:
- `msg_type=ack`
- TLV 当前仅包含 `ack_for_msg_id`

3. ERROR:
- `msg_type=error`
- TLV 包含 `ack_for_msg_id` + `err_code`
- `err_msg` 目前最多 60 字节（超长会截断）

### B.7 时间戳与 msg_id 的当前语义

当前固件实现中：

- `ts_sec` 由 `millis()/1000` 生成（设备运行秒），**不是 Unix Epoch 秒**。
- 设备侧发送 `msg_id` 为本地递增计数（启动后从 1 开始），不是随机值。

控制端解析时请按“相对时间/本地计数”处理，不要依赖绝对 Unix 时间语义。

### B.8 当前尚未完整实现项（控制端需自处理）

以下能力在当前代码尚未完整闭环：

- 会话 `session_ttl_ms` 过期淘汰策略（字段会返回，但当前未做超时失效判定）。
- 设备侧 keepalive 丢包计数与自动重握手流程。
- ACK 重传 pending 表（发送端超时重传策略需要控制端先实现）。

建议控制端先实现：
1. `HELLO -> RESP` 建链
2. 基于 `need_ack + ack_for_msg_id` 的超时重传
3. 失联后重握手
