# XPanel 统一通信协议 Draft (V0.5, Binary First)

> 状态: Draft
> 日期: 2026-08-30
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
- V0.5 默认二进制。
- JSON 仅作为调试模式（开发阶段可选），不作为量产主通道。

---

## 3. 二进制消息格式（V0.5）

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

### 3.4 通用 TLV 类型表（V0.5）

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
| 0x10 | time_unix_sec | uint32 | 要设置的 Unix 时间（秒） |
| 0x11 | time_tz_offset_min | int16(two's complement) | 时区偏移分钟（如 UTC+8=480） |
| 0x12 | time_src | uint8 | 时间来源（1=manual,2=ntp,3=phone） |
| 0x13 | time_set_mode | uint8 | 设置模式（1=RTC only,2=RTC+system） |
| 0x14 | notify_id | uint32 | 通知唯一ID（幂等去重） |
| 0x15 | notify_title | bytes(UTF-8) | 通知标题 |
| 0x16 | notify_text | bytes(UTF-8) | 通知正文 |
| 0x17 | notify_priority | uint8 | 0=low,1=normal,2=high,3=urgent |
| 0x18 | notify_ttl_ms | uint32 | 通知过期时间 |
| 0x19 | notify_channel | uint8 | 1=general,2=alarm,3=calendar,4=system |
| 0x1A | notify_image_mode | uint8 | 0=none,1=ref,2=inline,3=asset_id |
| 0x1B | notify_image_format | uint8 | 0=auto,1=png,2=jpg,3=jpeg,4=bmp,5=gif,6=webp |
| 0x1C | notify_image_uri | bytes(UTF-8) | 图片引用地址/路径（ref模式） |
| 0x1D | notify_image_data | bytes | 图片二进制（inline/chunk） |
| 0x1E | notify_image_size | uint32 | 图片总大小 |
| 0x1F | notify_asset_id | uint32 | 图片资源ID（上传后引用） |
| 0x20 | chunk_index | uint16 | 分片序号（从0开始） |
| 0x21 | chunk_total | uint16 | 分片总数 |
| 0x22 | chunk_crc32 | uint32 | 整图CRC32 |
| 0x23 | cfg_scope | uint8 | 配置作用域（1=device_nvm,2=controller_cfg） |
| 0x24 | cfg_count | uint8 | 本帧配置项数量 |
| 0x25 | cfg_id | uint16 | 统一配置ID |
| 0x26 | cfg_value_type | uint8 | 1=bool,2=int32,3=float32,4=utf8,5=bytes |
| 0x27 | cfg_value | bytes | 配置值（二进制） |
| 0x28 | cfg_item_status | uint8 | 单项状态（0=ok,1=not_found,2=invalid,3=denied,4=failed） |
| 0x29 | cfg_flags | uint8 | bit0:masked, bit1:readonly |
| 0x30~0x7F | op params | mixed | 业务参数区 |
| 0xF0~0xFF | vendor ext | bytes | 厂商扩展 |

参数编码约定:
- uint8/16/32: Big Endian。
- bool: uint8（0/1）。
- string: UTF-8 bytes（不含 `\0`）。

---

## 4. op_code 与 app_id 路由（V0.5）

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
| 9 | NotiShow |
| 10 | Paint（仅可通过 `paint.begin` 进入，见 §16） |

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
| 107 | RTCMgr（建议） |
| 108 | DisplayMgr（截图/显示服务） |

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
| 0x0021 | notify.asset_begin |
| 0x0022 | notify.asset_chunk |
| 0x0023 | notify.asset_end |
| 0x0024 | notify.cancel |
| 0x0030 | weather.update |
| 0x0040 | nvm.write |
| 0x0041 | nvm.read |
| 0x0050 | net.scan |
| 0x0051 | time.sync |
| 0x0052 | time.query |
| 0x0060 | radio.play |
| 0x0070 | display.screenshot |
| 0x0071 | display.shot_chunk |
| 0x0072 | display.shot_end |
| 0x0080 | clock.config_set |
| 0x0081 | clock.bg_chunk |
| 0x0082 | clock.config_commit |
| 0x0090 | paint.begin |
| 0x0091 | paint.stroke |
| 0x0092 | paint.fill |
| 0x0093 | paint.frame_begin |
| 0x0094 | paint.frame_chunk |
| 0x0095 | paint.frame_end |
| 0x0096 | paint.sync |
| 0x0097 | paint.end |
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
| 4020 | Unsupported Image Format |
| 4021 | Image Too Large |
| 4022 | Image Chunk Error |
| 4023 | Notify Payload Invalid |
| 4030 | Config Not Found |
| 4031 | Config Type Mismatch |
| 4032 | Config Access Denied |
| 4040 | Screenshot Busy（上一次截图未完成） |
| 4041 | Unsupported Shot Format |
| 4042 | Shot Chunk Error（缺片/重复/超时） |
| 4050 | Invalid Clock Config（时钟设置值越界或组合非法） |
| 4051 | Clock Background Transfer Busy（已有背景图片传输未完成） |
| 4052 | Clock Background Chunk Error（传输ID错误、缺片、重复、越界或超时） |
| 4053 | Clock Background Verify Failed（图片大小或 CRC32 校验失败） |
| 4060 | Paint Not Active（Paint 会话未开始） |
| 4061 | Paint Frame Error（关键帧 ID 不匹配、缺片、乱序、超长、超时或 CRC32 校验失败） |
| 5001 | Internal Error |
| 5002 | Storage Error |
| 5003 | Network Error |
| 5004 | Framebuffer Unavailable（画布未初始化） |

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

## 10. 时间同步（控制端 -> 设备）字段填充规范

结论:
- 现在协议可以发时间同步，但建议按本节补充后的 `time.sync` 标准实现，避免各端自定义字段。

### 10.1 请求包字段（推荐）

- Header:
  - `msg_type = cmd`
  - `flags.need_ack = 1`
  - `qos_level = 1`
  - `app_id = 107 (RTCMgr)`，若当前实现未接 RTC 路由，可临时用 `100 (NetworkMgr)`
  - `op_code = 0x0051 (time.sync)`
  - `msg_id = 控制端递增/随机 uint32`
  - `ts_sec = 控制端发送时刻`
- TLV:
  - 必填 `session_id(0x0E)`：握手后会话ID
  - 必填 `time_unix_sec(0x10)`：目标 Unix 秒
  - 必填 `time_tz_offset_min(0x11)`：时区分钟偏移
  - 必填 `time_src(0x12)`：建议填 `3 (phone)` 或 `2 (ntp)`
  - 可选 `time_set_mode(0x13)`：建议填 `2 (RTC+system)`
  - 可选 `req_id(0x0A)`：用于幂等

### 10.2 响应包字段（推荐）

- `msg_type = resp`（或 `error`）
- `op_code = 0x0051`
- TLV 至少包含:
  - `ack_for_msg_id(0x01)`
  - 成功时可回 `time_unix_sec(0x10)`（设备最终生效值）
  - 失败时回 `err_code(0x07)` + `err_msg(0x08)`

### 10.3 典型返回码建议

- `4010 Handshake Required`：未握手
- `4011 Invalid Session`：会话ID错误
- `4012 Session Expired / Keepalive Timeout`：会话过期
- `4001 Bad Request`：缺少时间字段或字段类型非法

### 10.4 time.sync TLV 示例

场景:
- 设置时间为 `2026-07-31 12:00:00 UTC+8`
- `time_unix_sec = 1785499200`（示例值）
- `tz_offset = +480`

TLV 示例（十六进制）:

```text
0E 00 04 12 34 56 78   // session_id
10 00 04 6A 6F 2D 20   // time_unix_sec (示例)
11 00 02 01 E0         // time_tz_offset_min = 480
12 00 01 03            // time_src = phone
13 00 01 02            // time_set_mode = RTC+system
0A 00 04 00 00 00 9A   // req_id (可选)
```

说明:
- `time_unix_sec` 示例值仅示意编码格式，控制端应使用实时计算值。

---

## 11. 通知下发（文本/图片/混合）规范

目标:
- 控制端可向设备下发“仅文字通知、仅图片通知、文字+图片通知”。

### 11.1 路由与操作

- 推荐路由：`app_id = 101 (NotificationMgr)`
- 核心操作：`op_code = 0x0020 (notify.push)`
- 大图上传操作：
  - `0x0021 notify.asset_begin`
  - `0x0022 notify.asset_chunk`
  - `0x0023 notify.asset_end`

### 11.2 notify.push 必填与可选字段

Header 建议:
- `msg_type = cmd`
- `flags.need_ack = 1`
- `qos_level = 1`

TLV:
- 必填:
  - `session_id(0x0E)`
  - `notify_id(0x14)`
  - `notify_priority(0x17)`
  - `notify_image_mode(0x1A)`
- 条件必填:
  - 文本通知（含混合通知）: `notify_text(0x16)`
  - 图片引用模式（mode=1）: `notify_image_uri(0x1C)`
  - 图片内嵌模式（mode=2）: `notify_image_data(0x1D)` + `notify_image_size(0x1E)` + `notify_image_format(0x1B)`
  - 图片资源模式（mode=3）: `notify_asset_id(0x1F)`
- 可选:
  - `notify_title(0x15)`
  - `notify_ttl_ms(0x18)`
  - `notify_channel(0x19)`

### 11.3 三种通知载荷模式

1. 仅文字
- `notify_image_mode=0`
- `notify_text` 必填

2. 仅图片
- 引用模式: `notify_image_mode=1` + `notify_image_uri`
- 内嵌模式: `notify_image_mode=2` + `notify_image_data`
- 资源模式: `notify_image_mode=3` + `notify_asset_id`

3. 文字+图片
- 同时携带 `notify_text` 与图片字段（1/2/3 任一图片模式）

### 11.4 图片传输策略（建议）

1. 小图（建议 <= 4KB）
- 直接使用 `notify.push` + `notify_image_mode=2` + `notify_image_data`

2. 大图（> 4KB）
- 采用三阶段上传再推送：
  1) `notify.asset_begin`：声明 `notify_asset_id`、`notify_image_size`、`notify_image_format`、`chunk_total`、`chunk_crc32`
  2) `notify.asset_chunk`：每包携带 `notify_asset_id`、`chunk_index`、`notify_image_data`
  3) `notify.asset_end`：请求设备完成组包和校验
- 上传成功后再发 `notify.push`，使用 `notify_image_mode=3` + `notify_asset_id`

分片建议:
- 单片 `notify_image_data` 在 UART/BLE 建议 256~1024 字节。
- `chunk_index` 必须连续，缺片或重复片按 `4022` 处理。

### 11.5 图片格式约束

- `notify_image_format` 仅表示“发送端声明格式”。
- 设备端最终是否支持，以设备 `get_caps` 返回为准。
- 不支持格式返回 `4020 Unsupported Image Format`。

### 11.6 ACK/RESP 语义

- ACK: 表示“协议层收包成功”。
- RESP: 表示“业务层处理结果”（是否入显示队列、图片是否可解码）。
- 建议 `notify.push` 返回 RESP 中包含:
  - `ack_for_msg_id`
  - `notify_id`
  - 处理结果（可用 `err_code/err_msg` 表达失败原因）

### 11.7 示例：文字+图片引用通知

Header:
- `app_id=101`
- `op_code=0x0020`

TLV（示意）:

```text
0E 00 04 12 34 56 78               // session_id
14 00 04 00 00 03 E9               // notify_id=1001
15 00 06 57 65 43 68 61 74         // notify_title="WeChat"
16 00 0F 4E 65 77 20 6D 65 73 73 61 67 65 21 // notify_text="New message!"
17 00 01 02                         // notify_priority=high
1A 00 01 01                         // notify_image_mode=ref
1B 00 01 01                         // notify_image_format=png
1C 00 16 2F 64 61 74 61 2F 6E 6F 74 69 66 79 2E 70 6E 67 // /data/notify.png
```

---

## 12. 天气同步（控制端 <-> 设备）规范

目标:
- 让控制端可通过现有 `weather.update` 指令同步天气数据/城市设置，且字段可直接映射当前固件 `WeatherData` 与 `WeatherScene` 的显示逻辑。

与当前实现对齐结论:
- 当前 `WeatherScene` 数据来源是 `NetworkMgr::getWeatherData()`，不是直接读 `WeatherApp` 私有状态。
- 因此天气同步建议路由到 `app_id=100 (NetworkMgr)`，由服务层落库并发布通知给 UI。

### 12.1 路由与操作

- Header 建议:
  - `msg_type = cmd`
  - `flags.need_ack = 1`
  - `qos_level = 1`
  - `app_id = 100 (NetworkMgr)`
  - `op_code = 0x0030 (weather.update)`
- TLV 必填:
  - `session_id(0x0E)`
  - `wx_mode(0x30)`

`wx_mode` 定义:
- `1 = weather_data_sync`：同步天气数据（当前天气 + 未来两天）
- `2 = weather_location_sync`：同步省市（对应 `submitWeatherLocation`）
- `3 = weather_refresh_now`：触发立即刷新（等价 `requestWeatherUpdate(true)`）

### 12.2 wx_mode=1（天气数据同步）字段

字段编号使用 `0x31~0x3F`（均属于协议定义的 op params 区间）。

| TLV | 名称 | 类型 | 约束 | 对应当前固件字段 |
|---|---|---|---|---|
| 0x31 | wx_valid | uint8 | 0/1 | `WeatherData.valid` |
| 0x32 | wx_has_temp | uint8 | 0/1 | `WeatherData.hasTemperature` |
| 0x33 | wx_has_code | uint8 | 0/1 | `WeatherData.hasWeatherCode` |
| 0x34 | wx_city | bytes(UTF-8) | 建议 <=23B | `WeatherData.city[24]` |
| 0x35 | wx_temp_c_x10 | int16 | 温度*10，支持负温 | `WeatherData.temperatureC` |
| 0x36 | wx_code | uint8 | 0~255 | `WeatherData.weatherCode` |
| 0x37 | wx_future_count | uint8 | 0~2 | `WeatherData.futureDayCount` |
| 0x38 | wx_day1_min_c_x10 | int16 | 仅 count>=1 时必填 | `futureDays[0].tempMinC` |
| 0x39 | wx_day1_max_c_x10 | int16 | 仅 count>=1 时必填 | `futureDays[0].tempMaxC` |
| 0x3A | wx_day1_code | uint8 | 仅 count>=1 时必填 | `futureDays[0].weatherCode` |
| 0x3B | wx_day2_min_c_x10 | int16 | 仅 count>=2 时必填 | `futureDays[1].tempMinC` |
| 0x3C | wx_day2_max_c_x10 | int16 | 仅 count>=2 时必填 | `futureDays[1].tempMaxC` |
| 0x3D | wx_day2_code | uint8 | 仅 count>=2 时必填 | `futureDays[1].weatherCode` |
| 0x3E | wx_update_unix_sec | uint32 | 可选 | 设备可转为本地 `updateMs` |
| 0x3F | wx_error_code | uint16 | 可选 | 可映射 `lastError` |

编码约定:
- `int16` 使用 Big Endian two's complement。
- 温度按 `x10` 定点编码，设备端转换: `temperatureC = value / 10.0f`。

### 12.3 wx_mode=2（省市同步）字段

用于同步天气查询地点（与现有 `submitWeatherLocation(province, city)` 对齐）。

| TLV | 名称 | 类型 | 约束 |
|---|---|---|---|
| 0x40 | wx_province | bytes(UTF-8) | 必填，trim 后非空 |
| 0x41 | wx_city_name | bytes(UTF-8) | 必填，trim 后非空 |

行为建议:
- 设备端成功写入后，仅返回 RESP 成功；是否立即拉取天气由控制端显式再发 `wx_mode=3` 决定。

### 12.4 wx_mode=3（立即刷新）

- 无附加业务 TLV（除 `session_id` 与 `wx_mode`）。
- 设备收到后触发一次天气拉取流程。

### 12.5 响应语义

成功:
- `msg_type=resp`
- TLV 至少包含 `ack_for_msg_id(0x01)`。
- 可选回包字段:
  - `wx_mode(0x30)`：回显执行模式
  - `wx_valid(0x31)`、`wx_city(0x34)`：用于确认当前生效状态

失败:
- `msg_type=error`
- TLV: `ack_for_msg_id(0x01)` + `err_code(0x07)` + 可选 `err_msg(0x08)`

推荐错误场景:
- `4001 Bad Request`：字段缺失、长度错误、`wx_future_count > 2`
- `4010 Handshake Required`：未建会话
- `4011 Invalid Session`：`session_id` 缺失或不匹配
- `5003 Network Error`：`wx_mode=3` 触发刷新失败

### 12.6 与 WeatherScene 显示逻辑的一致性要求

- `wx_valid=1` 且 `wx_has_code=1` 时，WeatherScene 才进入“有效天气”渲染。
- `wx_has_temp=0` 时，图标仍可显示，但温度文本应隐藏（与当前实现一致）。
- `wx_future_count` 超过 2 的部分必须丢弃（当前 UI 仅显示未来两天）。

### 12.7 wx_mode=1 TLV 示例（3天数据）

场景:
- 城市: 北京
- 当前: 26.3C, code=2
- 未来1: 22.0/30.0, code=3
- 未来2: 21.0/28.0, code=61

```text
0E 00 04 12 34 56 78   // session_id
30 00 01 01            // wx_mode=1 (weather_data_sync)
31 00 01 01            // wx_valid=1
32 00 01 01            // wx_has_temp=1
33 00 01 01            // wx_has_code=1
34 00 06 E5 8C 97 E4 BA AC // wx_city="北京"
35 00 02 01 07         // wx_temp_c_x10 = 263
36 00 01 02            // wx_code=2
37 00 01 02            // wx_future_count=2
38 00 02 00 DC         // day1 min = 220
39 00 02 01 2C         // day1 max = 300
3A 00 01 03            // day1 code=3
3B 00 02 00 D2         // day2 min = 210
3C 00 02 01 18         // day2 max = 280
3D 00 01 3D            // day2 code=61
```

---

## 13. 统一配置同步 Case（通用配置读写）

目标:
- 提供一个跨应用可复用的配置访问格式，既支持“请求对方配置”，也支持“主动下发并要求保存”。
- 本节可覆盖天气城市配置读取，也可扩展到其他设备/控制端配置。

设计原则:
- 复用现有 `op_code`，避免新增指令导致当前实现枚举不一致。
- 使用统一 `cfg_id` 抽象底层 `namespace + key`，控制端无需硬编码存储细节。

### 13.1 操作与方向

1. 请求读取配置（Pull）
- `op_code = 0x0041 (nvm.read)`
- `msg_type = cmd`
- 请求方携带若干 `cfg_id`，响应方返回对应 `cfg_value`。

2. 主动下发保存配置（Push/Set）
- `op_code = 0x0040 (nvm.write)`
- `msg_type = cmd`
- 发送方携带 `cfg_id + cfg_value`，接收方按规则保存并返回逐项状态。

3. 双向通用
- 方向不固定，设备与控制端都可作为请求方/响应方。
- 通过 `cfg_scope(0x23)` 区分“写入哪一侧配置空间”。

### 13.2 帧字段规范

Header 建议:
- `flags.need_ack = 1`
- `qos_level = 1`
- `app_id = 102 (NvmMgr)`（推荐）

TLV:
- 必填:
  - `session_id(0x0E)`
  - `cfg_scope(0x23)`
  - `cfg_count(0x24)`
- 读请求（0x0041）每项至少:
  - `cfg_id(0x25)`
- 写请求（0x0040）每项至少:
  - `cfg_id(0x25)` + `cfg_value_type(0x26)` + `cfg_value(0x27)`

多项编码顺序（推荐）:
- 按项连续编码，每项保持固定三元组顺序：
  - 读: `cfg_id`
  - 写: `cfg_id -> cfg_value_type -> cfg_value`

响应编码（resp）:
- 每项返回:
  - `cfg_id(0x25)`
  - `cfg_item_status(0x28)`
  - 成功读到值时附加 `cfg_value_type(0x26)` + `cfg_value(0x27)`
  - 敏感字段掩码返回时附加 `cfg_flags(0x29, bit0=1)`

### 13.3 统一配置 ID 定义（基于当前已落地 NVM）

来源:
- `NetworkMgr` 当前使用 namespace: `wifi_cfg`
- `TetrisApp` 当前使用 namespace: `tetris`
- `ClockSettings` 当前使用 namespace: `clock`

| cfg_id | 语义名 | namespace.key | 类型 | 访问建议 |
|---:|---|---|---|---|
| 0x0001 | wifi.configured | `wifi_cfg.configured` | bool | read/write |
| 0x0002 | wifi.ssid | `wifi_cfg.ssid` | utf8 | read/write |
| 0x0003 | wifi.password | `wifi_cfg.pass` | utf8 | write-only（读建议掩码或拒绝） |
| 0x0004 | weather.province | `wifi_cfg.province` | utf8 | read/write |
| 0x0005 | weather.city | `wifi_cfg.city` | utf8 | read/write |
| 0x0006 | weather.cache_blob | `wifi_cfg.wx_cache` | bytes | read/write（调试/迁移用途） |
| 0x0101 | tetris.state_blob | `tetris.state` | bytes | read/write |
| 0x0201 | clock.font | `clock.font` | int32 | read/write（0=粗体,1=正常，语义同 `clock_font_index`） |
| 0x0202 | clock.bg_mode | `clock.bgmode` | int32 | read/write（1~10 内置背景,11 图片，语义同 `clock_bg_mode`） |
| 0x0203 | clock.color_rgb | `clock.color` | int32 | read/write（低 24 位 `0x00RRGGBB`，语义同 `clock_color_rgb`） |
| 0x0204 | clock.pos_x | `clock.posx` | int32 | read/write（第一个数字左上角 x，语义同 `clock_x`） |
| 0x0205 | clock.pos_y | `clock.posy` | int32 | read/write（第一个数字左上角 y，语义同 `clock_y`） |

说明:
- 本表只纳入当前代码中已存在的持久化项，后续新增配置项应继续扩展 `cfg_id` 表。
- `weather.city` 即控制端需要读取的设备天气城市配置。
- `clock.*` 项与 §15 `clock.config_set` 的专用 TLV 一一对应：`nvm.read/write` 适合单项读写，§15 适合整组设置与背景图片传输，两条路径最终落到同一份 `clock` 命名空间。

### 13.4 天气城市读取推荐流程

控制端读取设备天气配置（省/市）:
1. 发送 `nvm.read`，`cfg_scope=1 (device_nvm)`，`cfg_count=2`
2. 请求项: `cfg_id=0x0004`（province）和 `cfg_id=0x0005`（city）
3. 设备返回 resp，逐项附带 `cfg_value_type=utf8` 与 `cfg_value`

控制端下发并保存天气配置:
1. 发送 `nvm.write`，`cfg_scope=1`，`cfg_count=2`
2. 写入项: `0x0004=省`，`0x0005=市`
3. 设备返回逐项 `cfg_item_status=0` 代表保存成功

### 13.5 与错误码关系

- 全局失败可返回 `msg_type=error` + `err_code`
- 部分成功建议返回 `msg_type=resp`，并用每项 `cfg_item_status` 表达细粒度结果

推荐映射:
- `4030 Config Not Found`：请求了未实现的 `cfg_id`
- `4031 Config Type Mismatch`：`cfg_value_type` 与目标配置类型不匹配
- `4032 Config Access Denied`：访问受限（如读取 `wifi.password` 明文）
- `5002 Storage Error`：底层 NVM 读写失败

### 13.6 读天气城市示例（nvm.read）

```text
0E 00 04 12 34 56 78   // session_id
23 00 01 01            // cfg_scope = device_nvm
24 00 01 02            // cfg_count = 2
25 00 02 00 04         // cfg_id = weather.province
25 00 02 00 05         // cfg_id = weather.city
```

### 13.7 写天气城市示例（nvm.write）

```text
0E 00 04 12 34 56 78   // session_id
23 00 01 01            // cfg_scope = device_nvm
24 00 01 02            // cfg_count = 2

25 00 02 00 04         // cfg_id = weather.province
26 00 01 04            // cfg_value_type = utf8
27 00 06 E5 B9 BF E4 B8 9C // cfg_value = "广东"

25 00 02 00 05         // cfg_id = weather.city
26 00 01 04            // cfg_value_type = utf8
27 00 06 E6 B7 B1 E5 9C B3 // cfg_value = "深圳"
```

---

## 14. 屏幕截图（Screenshot）规范

目标:
- 控制端发起一次截图请求，设备端把当前显示画布（framebuffer）完整回传给控制端。
- 明确像素排列方式，使控制端可以直接按行重建图像。

### 14.1 路由与操作

- `app_id = 108 (DisplayMgr)`
- 操作:
  - `0x0070 display.screenshot`：控制端请求截图 / 设备返回截图元信息
  - `0x0071 display.shot_chunk`：设备回传像素数据分片
  - `0x0072 display.shot_end`：设备声明本次截图传输结束

方向说明:
- `0x0070` 为 `cmd`（控制端 -> 设备），设备回 `resp`（元信息）或 `error`。
- `0x0071` / `0x0072` 由设备发起，`msg_type = event`。

### 14.2 截图 TLV 类型表（0x50~0x5F）

| T(hex) | 名称 | V 类型 | 说明 |
|---|---|---|---|
| 0x50 | shot_format | uint8 | 1=RGB888, 2=RGB565_BE, 3=RGB565_LE |
| 0x51 | shot_width | uint16 | 画布宽度（像素） |
| 0x52 | shot_height | uint16 | 画布高度（像素） |
| 0x53 | shot_pixel_order | uint8 | 像素排列方式，见 14.4 |
| 0x54 | shot_bytes_per_pixel | uint8 | 每像素字节数（3 或 2） |
| 0x55 | shot_total_size | uint32 | 像素数据总字节数 |
| 0x56 | shot_frame_id | uint32 | 本次截图会话 ID（设备生成，用于分片归属） |
| 0x57 | shot_data | bytes | 像素数据分片内容 |
| 0x58 | shot_chunk_size | uint16 | 建议/实际单片数据长度 |
| 0x59 | shot_brightness | uint8 | 面板全局亮度（0~255），仅信息，未叠加到像素值 |

分片复用通用 TLV:
- `chunk_index(0x20)`：分片序号，从 0 开始连续递增
- `chunk_total(0x21)`：分片总数
- `chunk_crc32(0x22)`：完整像素数据的 CRC32

### 14.3 请求包（control -> device）

Header:
- `msg_type = cmd`
- `app_id = 108`
- `op_code = 0x0070`
- `flags.need_ack = 1`
- `qos_level = 1`

TLV:
- 必填 `session_id(0x0E)`
- 可选 `shot_format(0x50)`：不填默认 `1 (RGB888)`
- 可选 `shot_chunk_size(0x58)`：控制端建议单片大小，设备会按链路 MTU 夹紧
- 可选 `req_id(0x0A)`

### 14.4 像素排列定义（与当前固件实现对齐）

设备内部画布（`dot2d::Renderer::_dotCanvas`）按硬件走线顺序存储，索引由 `dotOrder()` 决定：

```text
physical_index = (PANEL_HEIGHT - 1 - y) * PANEL_WIDTH + x
```

该顺序是硬件相关的，**不作为协议格式**。设备在发送前必须转换为逻辑顺序。

协议规定 `shot_pixel_order` 取值:

| 值 | 含义 |
|---:|---|
| 1 | row_major_top_left：逐行扫描，原点在左上角，x 向右递增，y 向下递增 |
| 2 | row_major_bottom_left：逐行扫描，原点在左下角（即设备物理存储序，仅调试用） |

默认且推荐值为 `1`。控制端重建索引:

```text
byte_offset = (y * shot_width + x) * shot_bytes_per_pixel
```

像素字节编码:

- `shot_format = 1 (RGB888)`，`shot_bytes_per_pixel = 3`
  - 字节顺序: `R, G, B`（各 1 字节，来源为 `DTRGB.r/.g/.b`）
- `shot_format = 2 (RGB565_BE)`，`shot_bytes_per_pixel = 2`
  - `value = ((R>>3)<<11) | ((G>>2)<<5) | (B>>3)`，Big Endian 两字节
- `shot_format = 3 (RGB565_LE)`，`shot_bytes_per_pixel = 2`
  - 同上，Little Endian 两字节

当前固件画布参数:
- `shot_width = 32`（`PANEL_WIDTH`）
- `shot_height = 32`（`PANEL_HEIGHT`）
- RGB888 下 `shot_total_size = 32 * 32 * 3 = 3072` 字节

色彩说明:
- 像素值为渲染层原始颜色，不含面板全局亮度（`kDisplayBrightness`，当前 125）与 HUB75 驱动的伽马处理。
- 控制端如需与实机观感一致，可自行按 `shot_brightness / 255` 线性缩放。

### 14.5 响应包（device -> control，元信息）

- `msg_type = resp`
- `op_code = 0x0070`
- TLV:
  - `ack_for_msg_id(0x01)`
  - `shot_frame_id(0x56)`
  - `shot_format(0x50)`
  - `shot_width(0x51)`、`shot_height(0x52)`
  - `shot_pixel_order(0x53)`
  - `shot_bytes_per_pixel(0x54)`
  - `shot_total_size(0x55)`
  - `chunk_total(0x21)`
  - `chunk_crc32(0x22)`
  - 可选 `shot_chunk_size(0x58)`、`shot_brightness(0x59)`

控制端收到 resp 后即可按 `chunk_total` 预分配缓冲区。

### 14.6 数据分片包（device -> control）

- `msg_type = event`
- `op_code = 0x0071`
- `flags.need_ack = 0`（当前实现；可靠性由整帧 CRC32 + 重新截图保证）
- TLV:
  - `session_id(0x0E)`
  - `shot_frame_id(0x56)`
  - `chunk_index(0x20)`
  - `shot_data(0x57)`

规则:
- `chunk_index` 从 0 连续递增，最后一片长度可小于 `shot_chunk_size`。
- 各片 `shot_data` 按 14.4 的线性字节流顺序拼接，无额外行填充（no row padding）。
- 单片建议大小：BLE 128~180 字节，UART 512~1024 字节。

### 14.7 结束包（device -> control）

- `msg_type = event`
- `op_code = 0x0072`
- TLV:
  - `session_id(0x0E)`
  - `shot_frame_id(0x56)`
  - `chunk_total(0x21)`
  - `chunk_crc32(0x22)`

控制端校验 CRC32 失败或缺片时，可重新发起 `0x0070` 请求整帧（当前不支持单片重传）。

### 14.8 错误场景

- `4001 Bad Request`：TLV 字段类型/长度非法
- `4010 / 4011`：未握手或 `session_id` 无效
- `4040 Screenshot Busy`：上一次截图传输尚未结束
- `4041 Unsupported Shot Format`：`shot_format` 不在支持列表
- `4042 Shot Chunk Error`：分片缺失、重复或超时
- `5004 Framebuffer Unavailable`：画布未初始化（显示任务未启动）

### 14.9 交互示例

请求（control -> device）:

```text
// header: app_id=108, op_code=0x0070, msg_type=cmd, need_ack=1
0E 00 04 12 34 56 78   // session_id
50 00 01 01            // shot_format = RGB888
58 00 02 00 B4         // shot_chunk_size = 180
```

响应元信息（device -> control）:

```text
// header: app_id=108, op_code=0x0070, msg_type=resp
01 00 04 00 00 00 2A   // ack_for_msg_id
56 00 04 00 00 00 07   // shot_frame_id = 7
50 00 01 01            // shot_format = RGB888
51 00 02 00 20         // shot_width = 32
52 00 02 00 20         // shot_height = 32
53 00 01 01            // shot_pixel_order = row_major_top_left
54 00 01 03            // shot_bytes_per_pixel = 3
55 00 04 00 00 0C 00   // shot_total_size = 3072
21 00 02 00 12         // chunk_total = 18
22 00 04 DE AD BE EF   // chunk_crc32
58 00 02 00 B4         // shot_chunk_size = 180
59 00 01 7D            // shot_brightness = 125
```

分片（device -> control，第 0 片）:

```text
// header: app_id=108, op_code=0x0071, msg_type=event
0E 00 04 12 34 56 78   // session_id
56 00 04 00 00 00 07   // shot_frame_id
20 00 02 00 00         // chunk_index = 0
57 00 B4 ...           // shot_data (180 bytes)
```

结束（device -> control）:

```text
// header: app_id=108, op_code=0x0072, msg_type=event
0E 00 04 12 34 56 78   // session_id
56 00 04 00 00 00 07   // shot_frame_id
21 00 02 00 12         // chunk_total = 18
22 00 04 DE AD BE EF   // chunk_crc32
```

---

## 15. 时钟显示设置与背景图片传输规范

目标:
- 控制端一次下发时钟字体、位置、颜色和背景模式等全部显示设置。
- 背景模式 `1~10` 使用设备内置背景；模式 `11` 使用控制端上传的图片。
- 图片校验成功后保存到设备文件系统，固定 basename 为 `ClockBackground`。

### 15.1 路由与操作

- `app_id = 2 (Clock)`
- 操作:
  - `0x0080 clock.config_set`：下发完整时钟设置；图片模式时同时开始一次图片传输
  - `0x0081 clock.bg_chunk`：下发背景图片数据分片
  - `0x0082 clock.config_commit`：结束图片传输、校验文件并一次性应用设置

所有请求均使用 `msg_type=cmd`、`flags.need_ack=1`、`qos_level=1`，并携带有效的 `session_id(0x0E)`。控制端应为每个请求使用不同的 `msg_id`，同一图片传输通过 `clock_transfer_id` 关联。

### 15.2 时钟 TLV 类型表（0x60~0x6F）

| T(hex) | 名称 | V 类型 | 长度 | 说明 |
|---|---|---|---:|---|
| 0x60 | clock_font_index | uint8 | 1 | 设备内置时钟字体索引；有效范围由 `system.get_caps` 返回 |
| 0x61 | clock_x | uint8 | 1 | 时钟左上角 x 坐标，单位为像素 |
| 0x62 | clock_y | uint8 | 1 | 时钟左上角 y 坐标，单位为像素 |
| 0x63 | clock_color_rgb | bytes | 3 | 字节顺序固定为 R、G、B，每通道 `0~255` |
| 0x64 | clock_bg_mode | uint8 | 1 | `1~10`=设备内置背景，`11`=图片背景 |
| 0x65 | clock_image_format | uint8 | 1 | `1=png,2=jpg,3=jpeg,4=bmp,5=gif,6=webp` |
| 0x66 | clock_image_size | uint32 | 4 | 完整图片文件字节数，不含 TLV 头 |
| 0x67 | clock_transfer_id | uint32 | 4 | 控制端生成的非零图片传输 ID |
| 0x68 | clock_image_data | bytes | 1~65535 | 当前图片分片的原始文件字节 |
| 0x69 | clock_image_width | uint16 | 2 | 图片宽度（像素） |
| 0x6A | clock_image_height | uint16 | 2 | 图片高度（像素） |

图片分片复用通用 TLV:
- `chunk_index(0x20)`：分片序号，从 `0` 开始
- `chunk_total(0x21)`：分片总数
- `chunk_crc32(0x22)`：完整原始图片文件的 CRC32

`clock_image_format` 与通知图片格式数值保持一致，但字段独立，禁止使用 `notify_image_*` TLV 传输时钟背景。

### 15.3 clock.config_set 请求

无论背景模式为何值，控制端必须在同一个 `clock.config_set` 请求中携带全部基础设置:
- `session_id(0x0E)`
- `clock_font_index(0x60)`
- `clock_x(0x61)`
- `clock_y(0x62)`
- `clock_color_rgb(0x63)`
- `clock_bg_mode(0x64)`
- 建议携带 `req_id(0x0A)`，用于设置操作幂等

模式条件:

1. `clock_bg_mode=1~10`
- 不得携带 `clock_image_*`、`clock_transfer_id`、`chunk_total` 或 `chunk_crc32`。
- 设备校验全部设置成功后立即一次性应用，并返回 `resp`。
- 切换到内置背景不要求删除已保存的 `ClockBackground.*`，设备只是不加载该文件。

2. `clock_bg_mode=11`
- 除全部基础设置外，必须同时携带 `clock_image_format(0x65)`、`clock_image_size(0x66)`、`clock_transfer_id(0x67)`、`clock_image_width(0x69)`、`clock_image_height(0x6A)`、`chunk_total(0x21)` 和 `chunk_crc32(0x22)`。
- 设备仅暂存本请求中的显示设置，不得在图片传输完成前使其生效。
- 设备校验图片格式、文件大小、尺寸和可用存储空间后返回 `resp`，表示允许控制端开始发送分片；拒绝时返回 `error`，且不得改变当前时钟设置或背景文件。

坐标校验:
- `clock_x`、`clock_y` 是无符号单字节坐标，协议可表达范围均为 `0~255`。
- 设备必须结合选定字体的实际边界检查时钟内容是否完整落在显示区域内；越界返回 `4050`，不得静默截断或夹紧坐标。

### 15.4 clock.bg_chunk 请求

每个分片必须携带:
- `session_id(0x0E)`
- `clock_transfer_id(0x67)`
- `chunk_index(0x20)`
- `clock_image_data(0x68)`

传输规则:
- `chunk_index` 必须从 `0` 开始严格连续递增，并小于 `clock.config_set` 声明的 `chunk_total`。
- 除最后一片外，建议各片数据长度相同；BLE 建议 `128~180B`，UART 建议 `512~1024B`。
- 所有分片拼接后的长度必须等于 `clock_image_size`，字节内容必须是完整图片文件，不能是解码后的像素数组。
- 设备应把数据写入临时文件，不得直接覆盖当前 `ClockBackground.*`。
- 每片成功写入后返回 `resp`，至少包含 `ack_for_msg_id(0x01)`、`clock_transfer_id(0x67)` 和 `chunk_index(0x20)`。
- 传输 ID 不匹配、分片缺失、重复、越界或超时返回 `4052`，设备删除临时文件并放弃暂存设置。

### 15.5 clock.config_commit 请求

控制端发送完全部分片后发起提交，请求必须携带:
- `session_id(0x0E)`
- `clock_transfer_id(0x67)`
- `chunk_total(0x21)`
- `chunk_crc32(0x22)`

设备提交顺序:
1. 确认分片数量与总字节数分别等于先前声明的 `chunk_total` 和 `clock_image_size`。
2. 对临时文件计算完整 CRC32，并与 `chunk_crc32` 比较。
3. 按 `clock_image_format` 解码或检查文件签名、尺寸；格式必须属于设备 `system.get_caps` 声明的支持集合。
4. 校验全部通过后，枚举并删除文件系统中 basename 恰好为 `ClockBackground` 的所有旧文件，不区分后缀，例如 `ClockBackground.png`、`ClockBackground.jpg`。不得删除 `ClockBackgroundBackup.png` 等 basename 不同的文件。
5. 将临时文件保存为 `ClockBackground.<ext>`，其中 `<ext>` 由格式枚举确定：`png`、`jpg`、`jpeg`、`bmp`、`gif` 或 `webp`；禁止采用控制端提供的路径或文件名。
6. 文件保存成功后，一次性应用在 `clock.config_set` 中暂存的全部显示设置，并持久化配置；随后返回 `resp`。

任一步骤失败时:
- 大小或 CRC32 不一致返回 `4053`。
- 格式不支持返回 `4020`，文件过大返回 `4021`，文件系统操作失败返回 `5002`。
- 设备必须删除临时文件、放弃暂存设置，并保持提交前的显示设置。
- 删除旧文件后若最终保存失败，设备应尽可能从备份恢复旧背景；实现时建议使用临时文件和文件系统 rename 完成原子替换。

### 15.6 响应与并发规则

成功响应统一使用 `msg_type=resp`，并至少携带:
- `ack_for_msg_id(0x01)`
- `clock_bg_mode(0x64)`
- 图片模式下附加 `clock_transfer_id(0x67)`

失败响应使用 `msg_type=error`，携带 `ack_for_msg_id(0x01)`、`err_code(0x07)` 和可选 `err_msg(0x08)`。

设备同一时刻只维护一个时钟背景图片传输:
- 已有传输未提交或取消时，新 `clock.config_set(mode=11)` 返回 `4051`。
- 传输超时建议为 `30s`；超时后删除临时文件并释放传输状态。
- 相同 `req_id + endpoint_id` 的重试必须返回原处理结果，不得重复删除或写入文件。

### 15.7 图片模式交互示例

开始传输并下发全部设置（control -> device）:

```text
// header: app_id=2, op_code=0x0080, msg_type=cmd
0E 00 04 12 34 56 78   // session_id
60 00 01 02            // clock_font_index=2
61 00 01 03            // clock_x=3
62 00 01 08            // clock_y=8
63 00 03 FF C0 20      // clock_color_rgb=(255,192,32)
64 00 01 0B            // clock_bg_mode=11 (image)
65 00 01 01            // clock_image_format=png
66 00 04 00 00 0C 00   // clock_image_size=3072
67 00 04 00 00 00 2A   // clock_transfer_id=42
69 00 02 00 20         // clock_image_width=32
6A 00 02 00 20         // clock_image_height=32
21 00 02 00 06         // chunk_total=6
22 00 04 DE AD BE EF   // chunk_crc32
```

发送第 0 片（control -> device）:

```text
// header: app_id=2, op_code=0x0081, msg_type=cmd
0E 00 04 12 34 56 78   // session_id
67 00 04 00 00 00 2A   // clock_transfer_id=42
20 00 02 00 00         // chunk_index=0
68 02 00 ...           // clock_image_data (512 bytes)
```

提交（control -> device）:

```text
// header: app_id=2, op_code=0x0082, msg_type=cmd
0E 00 04 12 34 56 78   // session_id
67 00 04 00 00 00 2A   // clock_transfer_id=42
21 00 02 00 06         // chunk_total=6
22 00 04 DE AD BE EF   // chunk_crc32
```

---

## 16. 画板（Paint）实时同步规范

目标:
- 控制端提供与设备画布同尺寸的模拟画布，用户可加载图片，也可用画笔自由涂画；设备端实时镜像控制端画布。
- 笔画走低延迟的“操作流”；图片加载、撤销/重做、纠错走“像素关键帧”；通过序号与 CRC32 实现最终一致。

### 16.1 基本约定

1. 权威来源
- 控制端是画布内容的唯一权威来源。撤销/重做历史、图片解码、缩放、抖动均只在控制端完成。
- 设备端只按本节规则执行收到的操作，不保存历史，不解码图片文件。

2. 坐标系
- 逻辑坐标，原点在左上角，x 向右递增，y 向下递增。
- 画布宽高以 `paint.begin` 响应中的 `paint_width` / `paint_height` 为准（当前固件为 32x32），控制端不得写死。
- 合法坐标: `0 <= x < paint_width`，`0 <= y < paint_height`。

3. 颜色与像素格式（唯一格式，无协商）
- 所有颜色（画笔、填充）和所有像素数据统一为 RGB888: 每像素 3 字节，顺序固定为 `R, G, B`，每通道 `0~255`。
- 不支持 RGB565、调色板或 alpha 通道。
- 画布中存储的是原始颜色值；设备显示时叠加的全局亮度与 HUB75 伽马处理不写入画布，也不参与 CRC 计算。

4. 绘制模型
- 覆盖写: 目标像素直接替换为新颜色，不做混合。
- 橡皮擦即颜色为 `(0,0,0)` 的普通笔画，协议不定义独立的橡皮擦操作。

5. 画布字节流（关键帧与 CRC 共用）
- 按行优先: 从上到下逐行，每行从左到右，每像素 `R, G, B`，无行填充。
- 区域 `rect=(rx, ry, rw, rh)` 的字节流只包含区域内像素，像素 `(x, y)` 的字节偏移为:

```text
offset = ((y - ry) * rw + (x - rx)) * 3
```

- 整个画布即 `rect=(0, 0, paint_width, paint_height)`，32x32 时为 3072 字节。

6. CRC32 算法
- CRC-32/ISO-HDLC（与 zlib `crc32()`、固件 `crc32Ieee()` 相同）: 反射多项式 `0xEDB88320`，初值 `0xFFFFFFFF`，结果异或 `0xFFFFFFFF`。
- 自检: ASCII `"123456789"` 的 CRC32 为 `0xCBF43926`。
- `paint_canvas_crc32` = 对整个画布字节流（第 5 条）计算的 CRC32。

7. 会话
- 所有 Paint 请求必须携带有效 `session_id(0x0E)`。
- 会话检查遵循附录 B.4/B.5: 即使 `need_ack=0`，会话错误也会返回 `error`（4010/4011）。

### 16.2 路由与操作

所有 Paint 消息均使用 `app_id = 10 (Paint)`。

| op_code | 名称 | 方向 | msg_type | need_ack | qos_level | 设备回包 |
|---:|---|---|---|---:|---:|---|
| 0x0090 | paint.begin | C→D | cmd | 1 | 1 | resp / error |
| 0x0091 | paint.stroke | C→D | cmd | 0 | 0 | 无 |
| 0x0092 | paint.fill | C→D | cmd | 0 | 0 | 无 |
| 0x0093 | paint.frame_begin | C→D | cmd | 1 | 1 | resp / error |
| 0x0094 | paint.frame_chunk | C→D | cmd | 0 | 0 | 无 |
| 0x0095 | paint.frame_end | C→D | cmd | 1 | 1 | resp / error |
| 0x0096 | paint.sync | C→D | cmd | 1 | 1 | resp / error |
| 0x0096 | paint.sync | D→C | event | 0 | 0 | 控制端不回包 |
| 0x0097 | paint.end | C→D | cmd | 1 | 1 | resp / error |
| 0x0097 | paint.end | D→C | event | 0 | 0 | 控制端不回包 |

回包规则:
- `need_ack=1` 的请求，设备只返回一个 `resp`（成功）或一个 `error`（失败），不再额外发送 `ack`。两者均携带 `ack_for_msg_id(0x01)`。
- `need_ack=0` 的请求（stroke / fill / frame_chunk），除会话错误（16.1 第 7 条）外，设备不返回任何 `ack/resp/error`。业务层异常只通过 `paint.sync` 的 `out_of_sync` 标志或 `paint.frame_end` 的结果反映。
- 控制端必须按上表设置 `need_ack`。如果请求的 `need_ack` 与上表不符，设备不执行该请求；若该请求 `need_ack=1`，设备返回 `4001`。
- Paint 的 op_code 若使用了 `app_id != 10`，设备返回 `4004 Invalid AppId`（仅在 `need_ack=1` 时回包）。

### 16.3 Paint TLV 类型表（0x70~0x7F）

所有 TLV 长度固定（`paint_points`、`paint_pixels` 除外），设备必须严格校验长度。

| T(hex) | 名称 | V 类型 | 长度 | 说明 |
|---|---|---|---:|---|
| 0x70 | paint_seq | uint16 | 2 | 操作序号，规则见 16.5 |
| 0x71 | paint_color | bytes | 3 | RGB888，顺序 `R, G, B` |
| 0x72 | paint_brush_shape | uint8 | 1 | `1=square`，`2=round`，其他值非法 |
| 0x73 | paint_brush_size | uint8 | 1 | 笔刷直径（像素），`1~8` |
| 0x74 | paint_stroke_flags | uint8 | 1 | bit0=STROKE_START，bit1=STROKE_END；bit2~7 发送端置 0，接收端忽略 |
| 0x75 | paint_points | bytes | 2N | `N` 个点，每点 `x(uint8), y(uint8)`；`1 <= N <= 64` |
| 0x76 | paint_rect | bytes | 4 | `x, y, w, h`，各 uint8 |
| 0x77 | paint_frame_id | uint32 | 4 | 关键帧 ID，控制端生成，非 0 |
| 0x78 | paint_pixels | bytes | 1~65535 | 关键帧像素分片（16.1 第 5 条字节流的连续片段） |
| 0x79 | paint_total_size | uint32 | 4 | 关键帧像素总字节数，必须等于 `w * h * 3` |
| 0x7A | paint_canvas_crc32 | uint32 | 4 | 设备当前整个画布的 CRC32 |
| 0x7B | paint_sync_flags | uint8 | 1 | bit0=out_of_sync，bit1=frame_transfer_active；bit2~7 为 0 |
| 0x7C | paint_width | uint16 | 2 | 画布宽度（像素） |
| 0x7D | paint_height | uint16 | 2 | 画布高度（像素） |
| 0x7E | (reserved) | - | - | 保留 |
| 0x7F | paint_end_reason | uint8 | 1 | `1=device_user`（用户在设备上切走），`2=device_system`（系统原因，如强制休眠、重启） |

关键帧复用通用 TLV:
- `chunk_index(0x20)`: 分片序号，从 0 开始
- `chunk_total(0x21)`: 分片总数，`>= 1`
- `chunk_crc32(0x22)`: 关键帧像素字节流（仅 rect 区域）的 CRC32

### 16.4 设备端 Paint 状态与生命周期

设备维护以下状态:

| 状态 | 说明 |
|---|---|
| active | Paint 会话是否激活 |
| canvas | `paint_width * paint_height * 3` 字节的 RGB888 画布 |
| last_seq | 最近一次被接受的 `paint_seq`（uint16） |
| last_point | 上一笔画末点（含 valid 标志） |
| out_of_sync | 设备已知自身画布可能与控制端不一致 |
| frame transfer | 当前关键帧传输（frame_id、rect、已收字节、下一期望 chunk_index、失败标志、接收缓冲区） |
| prev_app | 进入 Paint 前的 App |

生命周期规则:

1. 进入
- Paint 只能通过 `paint.begin` 进入；不参与设备按键的 App 轮换。`app.switch` 目标为 10 时返回 `4004`。

2. `paint.begin`（任意状态下均可调用）
- 若当前未激活: 记录当前 App 为 `prev_app`，然后切换到 Paint。
- 若已激活: 保留原 `prev_app` 不变。
- 无论是否已激活，都执行以下重置: 画布全部置为 `(0,0,0)`；`last_seq=0`；`last_point` 置为无效；`out_of_sync=false`；放弃进行中的关键帧传输。

3. 控制端退出（`paint.end` cmd）
- 设备将记录的 `prev_app` 重置为 Clock（`app_id=2`），放弃关键帧传输，丢弃画布，切回 Clock，返回 `resp`。
- 未激活时收到 `paint.end` 也返回 `resp`（幂等）。
- 退出后恢复通知展示和其他自动跳转；遗留的 Paint 返回目标一律按 Clock 处理，不得重新进入 Paint。

4. 设备端退出
- 用户在设备上切换 App，或系统原因（低电强制休眠、重启前等）导致退出时，设备先发送 `paint.end` event（携带 `paint_end_reason`），再退出 Paint。

5. 会话失效
- BLE 断开、`session.bye`、重新 `session.hello` 都会使会话失效。此时设备直接退出 Paint 并切回 `prev_app`，不发送 event。
- 控制端重新握手后，必须重新 `paint.begin`。

6. 未激活时收到请求
- `need_ack=1` 的请求（`paint.begin` / `paint.end` 除外）返回 `4060`。
- `need_ack=0` 的请求静默丢弃。

7. 通知隔离与休眠
- Paint 激活期间，任何通知消息均不得打断 Paint 或自动切换到其他 App，包括天气自动展示、图片通知和时钟配置引发的自动展示。
- 天气与时钟配置数据仍正常更新，但不触发界面跳转；图片通知跳过展示并释放其临时图片缓冲区，不进入显示队列。
- 用户主动按键切换、`paint.end`、会话失效和系统强制退出仍按上述生命周期规则执行。
- Paint 激活期间，设备不因“无操作超时”进入休眠。

### 16.5 序号规则（paint_seq）

1. 消耗序号的操作只有 `paint.stroke`、`paint.fill`、`paint.frame_begin` 三种；其他 Paint 消息不携带 `paint_seq`。

2. 控制端分配规则
- `paint.begin` 成功后，控制端本地 `last_sent_seq = 0`。
- 每个新操作使用 `seq = (last_sent_seq + 1) mod 65536`。第一个操作为 1；65535 之后为 0。
- 序号不得复用于不同内容。唯一例外: `paint.frame_begin` 收到 `error` 时序号未被消耗，重试时可复用该序号。

3. 设备判定规则
- 设备计算 `d = (uint16)(seq - last_seq)`:

| d | 判定 | 设备行为 |
|---|---|---|
| `0` 或 `>= 0x8000` | 重复/过期 | stroke/fill: 丢弃，不改变任何状态；frame_begin: 返回 `4001`（`Stale paint_seq`），不消耗序号 |
| `1` | 正常 | 执行，然后 `last_seq = seq` |
| `2 ~ 0x7FFF` | 有丢包 | 先 `out_of_sync = true` 并将 `last_point` 置为无效，再执行，然后 `last_seq = seq` |

4. 无效包
- stroke/fill 通过序号判定后，若字段缺失、长度错误或取值越界，则: 不修改画布；`out_of_sync = true`；`last_point` 置为无效；`last_seq = seq`（序号视为已消耗）。
- 若 `paint_seq` 本身缺失或长度错误，则整包丢弃，并置 `out_of_sync = true`。

### 16.6 paint.begin

请求 TLV:
- 必填 `session_id(0x0E)`

成功 `resp` TLV:
- `ack_for_msg_id(0x01)`
- `paint_width(0x7C)`、`paint_height(0x7D)`
- `paint_seq(0x70)`，固定为 `0`
- `paint_canvas_crc32(0x7A)`，即全黑画布的 CRC32

控制端收到 `resp` 后:
- 将本地画布初始化为同尺寸全黑，并置 `last_sent_seq = 0`。
- 若需要初始图片，随后发送一个整幅关键帧（16.10）。

失败:
- 画布缓冲区分配失败返回 `5001`。

### 16.7 paint.stroke（画笔）

请求 TLV（全部必填，每包都要携带，包与包之间不继承颜色或笔刷）:
- `session_id(0x0E)`
- `paint_seq(0x70)`
- `paint_color(0x71)`
- `paint_brush_shape(0x72)`
- `paint_brush_size(0x73)`
- `paint_stroke_flags(0x74)`
- `paint_points(0x75)`

字段约束:
- `paint_points` 的长度必须为偶数，`N = L / 2`，`1 <= N <= 64`；每个点都必须在画布范围内。违反任一条按无效包处理（16.5 第 4 条）。

设备执行算法（点为 `P0 .. P(N-1)`）:

1. 若 `STROKE_START=1`，或本包判定为丢包，或 `last_point` 无效: 在 `P0` 盖一次笔刷。否则: 绘制线段 `last_point -> P0`。
2. 对 `i = 1 .. N-1`，依次绘制线段 `P(i-1) -> P(i)`。
3. 若 `STROKE_END=1`: `last_point` 置为无效；否则 `last_point = P(N-1)`。

补充规则:
- 所有线段（包括第 1 步连接上一包末点的那一段）都使用本包的颜色和笔刷。
- 单击画点: `N=1`，`paint_stroke_flags=0x03`。
- 一笔跨多个包时: 首包 `STROKE_START=1`，中间包为 `0x00`，末包 `STROKE_END=1`。

控制端发送建议（不影响互通）:
- 每 20~30ms 合并一次指针事件，作为一包发送。
- 去掉与前一点相同的连续点。中间像素无需发送，由设备端插值。
- 发送前用本节算法在本地画布上执行同样的绘制，以保证 CRC 一致。

帧长:
- `24(header) + 7(session_id) + 5(seq) + 6(color) + 4(shape) + 4(size) + 4(flags) + 3 + 2N = 57 + 2N` 字节，`N=64` 时为 185 字节。
- BLE 控制端应协商 MTU `>= 247`，使单帧不超过 `MTU - 3`、无需 §6.1 分片。若 MTU 较小，控制端应减小 `N`，使单帧 `<= MTU - 3`。

### 16.8 笔刷与线段光栅化（双方必须逐像素一致）

盖笔刷 `stamp(cx, cy)`，其中 `d = paint_brush_size`:

```text
x0 = cx - (d - 1) / 2        // 整数除法向下取整，d 为偶数时笔刷偏向右下
y0 = cy - (d - 1) / 2
for py in [y0, y0 + d - 1]:
  for px in [x0, x0 + d - 1]:
    if shape == round:
      u = 2 * (px - x0) - (d - 1)
      v = 2 * (py - y0) - (d - 1)
      if u*u + v*v > (d - 1)*(d - 1) + 1: continue
    if px, py 在画布范围内: canvas[px, py] = color   // 越界像素裁剪
```

参考: round 笔刷在 `d=1/2/3/4/5` 时分别覆盖 `1/4/5/12/21` 个像素；square 笔刷覆盖 `d*d` 个像素。

线段 `drawSegment(A -> B)`: 按以下整数 Bresenham 算法从 A 走到 B（方向不得交换），在每个经过的点（含两端）调用 `stamp`。所有变量为有符号整数:

```text
x = Ax; y = Ay
dx =  abs(Bx - Ax); sx = (Ax < Bx) ? 1 : -1
dy = -abs(By - Ay); sy = (Ay < By) ? 1 : -1
err = dx + dy
loop:
  stamp(x, y)
  if x == Bx and y == By: break
  e2 = 2 * err
  if e2 >= dy: err += dy; x += sx
  if e2 <= dx: err += dx; y += sy
```

### 16.9 paint.fill（清屏/矩形填充）

请求 TLV:
- 必填 `session_id(0x0E)`、`paint_seq(0x70)`、`paint_color(0x71)`
- 可选 `paint_rect(0x76)`: 不携带时填充整个画布

约束:
- `w >= 1`，`h >= 1`，`x + w <= paint_width`，`y + h <= paint_height`；违反时按无效包处理。

执行:
- rect 内全部像素置为 `paint_color`，然后 `last_point` 置为无效。
- 清屏即不带 rect、颜色为 `(0,0,0)` 的 fill。

### 16.10 关键帧（加载图片 / 撤销重做 / 纠错）

用途:
- 加载图片: 控制端将图片解码、缩放并抖动到画布尺寸，转为 RGB888 像素，作为整幅关键帧发送。
- 撤销/重做: 控制端发送受影响区域（或整幅）的关键帧。
- 纠错: 发送整幅关键帧（16.11）。

#### 16.10.1 paint.frame_begin

请求 TLV（全部必填）:
- `session_id(0x0E)`
- `paint_seq(0x70)`
- `paint_frame_id(0x77)`: 非 0，且与本 Paint 会话内之前使用过的 frame_id 都不同
- `paint_rect(0x76)`: 整幅为 `(0, 0, paint_width, paint_height)`
- `paint_total_size(0x79)`: 必须等于 `w * h * 3`
- `chunk_total(0x21)`
- `chunk_crc32(0x22)`: rect 像素字节流的 CRC32

设备校验:
- 序号判定（16.5）、rect 合法性（同 16.9）、`paint_total_size`，以及 `1 <= chunk_total <= paint_total_size`。
- 任一项失败返回 `4001`，此时序号不消耗、画布与已有的传输状态均不变。

接受后:
1. 若有进行中的关键帧传输，直接丢弃（以新传输替换旧传输）。
2. 按 16.5 消耗序号（判定为丢包时置 `out_of_sync=true`），`last_point` 置为无效。
3. 建立新传输: 独立接收缓冲区，下一期望 `chunk_index=0`。此时画布不变。
4. 返回 `resp`: `ack_for_msg_id`、`paint_frame_id`、`paint_seq`。

#### 16.10.2 paint.frame_chunk

请求 TLV（全部必填）:
- `session_id(0x0E)`
- `paint_frame_id(0x77)`
- `chunk_index(0x20)`
- `paint_pixels(0x78)`

设备处理:
- `paint_frame_id` 不是当前传输的 ID: 丢弃，不改变任何状态。
- `chunk_index` 不等于下一期望值，或累计字节数将超过 `paint_total_size`: 将本次传输标记为失败，此后的分片全部忽略。
- 否则: 追加到接收缓冲区，期望值加 1。

控制端规则:
- 必须收到 `frame_begin` 的 `resp` 后，才能发送分片。
- 分片按 `chunk_index` 从 0 起连续发送；各片长度可不同，总和必须等于 `paint_total_size`。
- 帧长为 `46 + L` 字节；BLE（MTU 247）下建议 `L <= 180`。整幅 32x32 画布共 3072 字节，按 180 字节分片为 18 片。

#### 16.10.3 paint.frame_end

请求 TLV（全部必填）:
- `session_id(0x0E)`
- `paint_frame_id(0x77)`
- `chunk_total(0x21)`
- `chunk_crc32(0x22)`

设备校验（全部满足才算成功）:
- `paint_frame_id` 为当前传输 ID，且传输未被标记为失败。
- 已收分片数 = 本请求的 `chunk_total` = `frame_begin` 声明的 `chunk_total`。
- 已收字节数 = `paint_total_size`。
- 对接收缓冲区计算的 CRC32 = 本请求的 `chunk_crc32` = `frame_begin` 声明的 `chunk_crc32`。

成功:
1. 将缓冲区一次性写入画布 rect，显示上不得出现半幅更新。
2. 若 rect 为整幅画布，置 `out_of_sync = false`。
3. `last_point` 置为无效，并释放传输。
4. 返回 `resp`: `ack_for_msg_id`、`paint_frame_id`、`paint_seq`（当前 `last_seq`）、`paint_canvas_crc32`（应用后的整画布 CRC）。

失败:
- 返回 `4061`，释放传输，画布不变，并置 `out_of_sync = true`。
- 控制端必须使用新的 `paint_seq` 和新的 `paint_frame_id` 重新发起关键帧；不支持单片重传。

超时:
- 传输建立后，若连续 3000ms 未收到属于该传输的 `frame_chunk` 或 `frame_end`，设备放弃该传输并置 `out_of_sync = true`。之后再收到它的 `frame_end` 返回 `4061`。

传输期间的约束:
- 从发送 `frame_begin` 到收到 `frame_end` 的回包之前，控制端不得发送 `paint.stroke` / `paint.fill`。
- 设备在传输期间收到 stroke/fill 时，直接丢弃（不做序号判定，`last_seq` 不变），并置 `out_of_sync = true`。
- 控制端可在任意时刻发送新的 `frame_begin`，以替换当前传输。

### 16.11 同步校验（paint.sync）

#### 16.11.1 设备上报（event，D→C）

TLV:
- `session_id(0x0E)`
- `paint_seq(0x70)`: 当前 `last_seq`
- `paint_canvas_crc32(0x7A)`: 当前画布 CRC32（不含进行中的关键帧缓冲区）
- `paint_sync_flags(0x7B)`

发送时机（Paint 激活期间）:
1. `out_of_sync` 由 false 变为 true 后，在 100ms 内发送一次。
2. 画布被修改（执行了 stroke/fill 或应用了关键帧）后，若连续 300ms 没有新的修改，发送一次。
3. 修改持续不断时，至少每 1000ms 发送一次。
4. 任意两次上报间隔不小于 100ms（期间的触发合并为一次）。画布无变化且 `out_of_sync=false` 时不做周期上报。

#### 16.11.2 控制端查询（cmd，C→D）

- 请求只带 `session_id(0x0E)`。
- 设备返回 `resp`，内容为 `ack_for_msg_id(0x01)` 加上 16.11.1 的全部 TLV。

#### 16.11.3 控制端处理规则

收到上报或查询结果后，控制端按以下规则处理:

1. `out_of_sync=1`: 立即发送整幅关键帧。若已有整幅关键帧在传输中，则不重复发送。
2. `frame_transfer_active=1`: 忽略本次 CRC 比较。
3. `paint_seq` 不等于控制端 `last_sent_seq`: 说明仍有操作在途，忽略本次 CRC 比较。
4. `paint_seq` 等于 `last_sent_seq`，且 CRC32 与控制端本地画布不一致: 发送整幅关键帧。

### 16.12 paint.end

控制端请求（cmd）:
- TLV: `session_id(0x0E)`。
- 设备行为见 16.4 第 3 条；`resp` 只含 `ack_for_msg_id(0x01)`。

设备通知（event）:
- TLV: `session_id(0x0E)`、`paint_end_reason(0x7F)`。
- 控制端收到后应立即停止发送 Paint 操作。
- 若要恢复，控制端需重新 `paint.begin`（设备画布会被清空），然后用整幅关键帧把本地画布恢复到设备。

### 16.13 设备端实现要求

- 操作按接收顺序执行。协议接收回调只负责解码与入队，由显示任务执行绘制，避免与渲染并发访问画布。
- 从收到操作到在屏幕上显示，延迟应不超过一个显示刷新周期。
- Paint 画布独立于 `dot2d` 渲染层的物理存储顺序（`dotOrder()`），一律使用 16.1 的逻辑坐标。
- 本版本不支持画布持久化；画布内容在退出 Paint 后丢弃。

### 16.14 错误场景

| code | 场景 |
|---:|---|
| 4001 | `need_ack=1` 的请求字段缺失、长度错误或取值越界；`frame_begin` 序号过期、rect 非法、`paint_total_size` 不匹配；`need_ack` 与 16.2 不符 |
| 4004 | Paint op_code 的 `app_id` 不为 10；`app.switch` 目标为 10 |
| 4010 / 4011 | 未握手 / `session_id` 缺失或无效 |
| 4060 | Paint 未激活时收到 `frame_begin` / `frame_end` / `paint.sync` cmd |
| 4061 | `frame_end` 校验失败、传输已被替换、已超时或已标记失败 |
| 5001 | `paint.begin` 时画布或关键帧缓冲区分配失败 |

### 16.15 交互示例

进入 Paint（control -> device）:

```text
// header: app_id=10, op_code=0x0090, msg_type=cmd, need_ack=1
0E 00 04 12 34 56 78   // session_id
```

响应（device -> control）:

```text
// header: app_id=10, op_code=0x0090, msg_type=resp
01 00 04 00 00 00 2A   // ack_for_msg_id
7C 00 02 00 20         // paint_width = 32
7D 00 02 00 20         // paint_height = 32
70 00 02 00 00         // paint_seq = 0
7A 00 04 xx xx xx xx   // paint_canvas_crc32（全黑画布）
```

加载图片，整幅关键帧（control -> device）:

```text
// header: app_id=10, op_code=0x0093, msg_type=cmd, need_ack=1
0E 00 04 12 34 56 78   // session_id
70 00 02 00 01         // paint_seq = 1
77 00 04 00 00 00 01   // paint_frame_id = 1
76 00 04 00 00 20 20   // paint_rect = (0,0,32,32)
79 00 04 00 00 0C 00   // paint_total_size = 3072
21 00 02 00 12         // chunk_total = 18
22 00 04 DE AD BE EF   // chunk_crc32

// header: op_code=0x0094, need_ack=0（共 18 片）
0E 00 04 12 34 56 78   // session_id
77 00 04 00 00 00 01   // paint_frame_id = 1
20 00 02 00 00         // chunk_index = 0
78 00 B4 ...           // paint_pixels (180 bytes, RGB888)

// header: op_code=0x0095, need_ack=1
0E 00 04 12 34 56 78   // session_id
77 00 04 00 00 00 01   // paint_frame_id = 1
21 00 02 00 12         // chunk_total = 18
22 00 04 DE AD BE EF   // chunk_crc32
```

画笔（control -> device），新起一笔，橙色 round 笔刷，直径 2，经过 `(3,4) -> (10,4) -> (10,12)`:

```text
// header: app_id=10, op_code=0x0091, msg_type=cmd, need_ack=0, qos_level=0
0E 00 04 12 34 56 78   // session_id
70 00 02 00 02         // paint_seq = 2
71 00 03 FF 80 00      // paint_color = (255,128,0)
72 00 01 02            // paint_brush_shape = round
73 00 01 02            // paint_brush_size = 2
74 00 01 01            // paint_stroke_flags = STROKE_START
75 00 06 03 04 0A 04 0A 0C // paint_points = (3,4),(10,4),(10,12)
```

续画并结束这一笔（下一包）:

```text
0E 00 04 12 34 56 78   // session_id
70 00 02 00 03         // paint_seq = 3
71 00 03 FF 80 00      // paint_color
72 00 01 02            // round
73 00 01 02            // size = 2
74 00 01 02            // paint_stroke_flags = STROKE_END（从 (10,12) 连线）
75 00 02 14 0C         // paint_points = (20,12)
```

设备同步上报（device -> control）:

```text
// header: app_id=10, op_code=0x0096, msg_type=event
0E 00 04 12 34 56 78   // session_id
70 00 02 00 03         // paint_seq = 3
7A 00 04 xx xx xx xx   // paint_canvas_crc32
7B 00 01 00            // paint_sync_flags = 0
```

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

### B.9 截图实现细节

- 路由：`app_id=108`，`op_code=0x0070`；同一时刻只允许一个截图传输，否则返回 `4040`。
- `shot_chunk_size` 默认 `128`，设备夹紧到 `32~200`；实际采用值在 resp 的 `shot_chunk_size` 中回显。
- 分片以约 5ms 间隔从主循环泵出，`msg_type=event`，不要求 ACK；每包额外携带 `session_id`。
- 当前分辨率 32x32，RGB888 总长 3072 字节，默认共 24 片。
- 会话断开或重新握手时，未完成的截图传输会被丢弃。
- 不支持单片重传；`chunk_crc32` 校验失败需重新发起 `0x0070`。
