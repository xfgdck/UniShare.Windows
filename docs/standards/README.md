# SMB 共享、身份认证与局域网自发现官方标准文档库 (Local Standards Repository)

本目录完整收录了 UniShare 项目所涉及的 **SMB 文件共享协议栈**、**Windows/通用网络身份认证** 以及 **局域网设备自发现体系** 的全部官方权威技术规范（包含 Microsoft Open Specifications、OASIS 国际标准与 IETF RFC 规范）。

---

## 一、 协议与身份认证标准索引

| 文件名 | 规范编号 | 规范全称 | 大小 | 归属层级 | 核心关注章节 |
| :--- | :---: | :--- | :---: | :---: | :--- |
| `[MS-NLMP].pdf` | **MS-NLMP** | NT LAN Manager (NTLM) Authentication Protocol | 3.03 MB | 身份认证层 (NTLM) | **Section 3.3.1/3.3.2** (NTLMv1 与 NTLMv2 算法、哈希计算与会话密钥派生) |
| `[MS-SMB2].pdf` | **MS-SMB2** | Server Message Block (SMB) Protocol Versions 2 and 3 | 11.67 MB | 应用会话层 (SMB 2/3) | **Section 3.3.5.3.1** (多协议通配协商 0x02FF 响应标准)、**Section 3.3.5.5** (SESSION_SETUP 会话认证) |
| `[MS-SMB].pdf` | **MS-SMB** | Server Message Block (SMB) Protocol (SMB 1.0) | 5.02 MB | 应用会话层 (SMB 1.0) | **Section 3.3.5.3** (SMB1 SessionSetupAndX 鉴权) |
| `[MS-CIFS].pdf` | **MS-CIFS** | Common Internet File System (CIFS) Protocol | 23.44 MB | 底层传统协议 | CIFS 原生命令字与历史实现对照 |
| `[MS-SPNG].pdf` | **MS-SPNG** | Simple and Protected GSS-API Negotiation Mechanism (SPNEGO) Protocol Extensions | 1.24 MB | 安全包装层 (SPNEGO) | **Section 3.1.5** (NegTokenInit 与 NegTokenResp 格式) |
| `[MS-KILE].pdf` | **MS-KILE** | Kerberos Protocol Extensions | 2.89 MB | 身份认证层 (域环境) | Windows Active Directory 域环境下 Kerberos 认证与 PAC 扩展 |
| `[MS-AUTHSOD].pdf` | **MS-AUTHSOD** | Authentication Services Protocols System Overview | 3.34 MB | 系统总览 | Windows 身份验证全家桶架构与协议关系图 |
| `RFC4178-SPNEGO.txt` | **RFC 4178** | The Simple and Protected GSS-API Negotiation Mechanism | 46.5 KB | IETF 国际标准 | 通用安全服务应用编程接口简易保护协商规范 |
| `RFC2743-GSSAPI.txt` | **RFC 2743** | Generic Security Service Application Program Interface (GSS-API) | 229.4 KB | IETF 国际标准 | GSS-API 核心概念、上下文与安全缓冲区语义 |
| `RFC4120-KerberosV5.txt` | **RFC 4120** | The Kerberos Network Authentication Service (V5) | 340.3 KB | IETF 国际标准 | Kerberos v5 密码体系、票据与鉴权流程规范 |

---

## 二、 局域网设备自发现官方标准索引

| 文件名 | 规范编号 / 组织 | 规范全称 | 大小 | 通信端口与地址 | 核心作用与受众 |
| :--- | :---: | :--- | :---: | :---: | :--- |
| `OASIS-WSDD-1.1.pdf` | **OASIS WSDD 1.1** | Web Services Dynamic Discovery (WS-Discovery) Version 1.1 Specification | 769.3 KB | UDP 3702<br>`239.255.255.250` / `[FF02::C]` | **Windows 资源管理器“网络”核心标准**。规定 SOAP 1.2 组播探针（`Probe`/`ProbeMatches`）与上下线广播（`Hello`/`Bye`） |
| `[MS-PCCRD].pdf` | **MS-PCCRD** (微软) | Peer Content Caching and Retrieval: Discovery Protocol | 1.19 MB | UDP 3702 | 微软在 Windows 上基于 WS-Discovery 规范实现的局域网对等发现协议扩展 |
| `[MS-BPDP].pdf` | **MS-BPDP** (微软) | BITS Peer-Caching: Peer Discovery Protocol | 1.12 MB | UDP 3702 | 微软 BITS 对等缓存与服务自发现协议（遵循 WS-Discovery 报文结构） |
| `RFC6762-mDNS.txt` | **RFC 6762** (IETF) | Multicast DNS (mDNS) Specification | 180.7 KB | UDP 5353<br>`224.0.0.251` / `[FF02::FB]` | **苹果/安卓/Linux 跨平台黄金标准**。无中心 DNS 组播解析 `.local` 域名 |
| `RFC6763-DNS-SD.txt` | **RFC 6763** (IETF) | DNS-Based Service Discovery (DNS-SD) Specification | 122.3 KB | UDP 5353 | 基于 DNS 记录模型（PTR/SRV/TXT/A）的服务自动枚举与属性发现标准（`_smb._tcp.local`） |
| `[MS-NBTE].pdf` | **MS-NBTE** (微软) | NetBIOS over TCP (NBT) Extensions Protocol Specification | 1.10 MB | UDP 137 / UDP 138 | 微软对 NetBIOS 广播、名称查询与点对点报文传输的官方实现规范 |
| `[MS-BRWS].pdf` | **MS-BRWS** (微软) | Common Internet File System (CIFS) Browser Protocol Specification | 2.17 MB | UDP 138 / 邮件槽 | Windows 原生主浏览器（Master Browser）选举机制与共享列表广播同步 |
| `[MS-RAP].pdf` | **MS-RAP** (微软) | Remote Administration Protocol Specification | 4.12 MB | 命名管道 `\PIPE\LANMAN` | 历史协议中用于通过 `NetServerEnum2` 和 `NetShareEnum` 枚举服务器与共享的规范 |
| `RFC1001-NetBIOS-Concepts.txt` | **RFC 1001** (IETF) | Protocol Standard For a NetBIOS Service on a TCP/UDP Transport: Concepts | 150.8 KB | UDP 137/138/TCP 139 | NetBIOS over TCP/UDP 体系概念、节点类型（B/P/M/H-node）与组播模型 |
| `RFC1002-NetBIOS-Specs.txt` | **RFC 1002** (IETF) | Protocol Standard For a NetBIOS Service on a TCP/UDP Transport: Detailed Specs | 161.4 KB | UDP 137/138/TCP 139 | NetBIOS 名称数据包、数据报报头与 16 字节专用名称转义二进制格式 |
| `RFC4795-LLMNR.txt` | **RFC 4795** (IETF) | Link-Local Multicast Name Resolution (LLMNR) | 70.3 KB | UDP 5355<br>`224.0.0.252` | 局域网无 DNS 状态下单标签主机名（如 `\\MY-SERVER`）的组播名称解析标准 |

---

## 三、 自发现系统架构与三位一体协同关系

```text
+---------------------------------------------------------------------------------------------------------+
|                                        局域网多生态设备自发现体系                                       |
+---------------------------------------------------------------------------------------------------------+
       |                                                 |                                          |
       v (UDP 3702 组播)                                 v (UDP 5353 组播)                          v (UDP 137 广播)
+----------------------------------+   +------------------------------------+   +-------------------------+
|        WS-Discovery 引擎         |   |          mDNS / DNS-SD 引擎         |   |    NetBIOS (NBNS) 引擎   |
|   OASIS WSDD 1.1 / [MS-PCCRD]    |   |         IETF RFC 6762 / 6763       |   |   RFC 1001/1002, [MS-NBTE] |
+----------------------------------+   +------------------------------------+   +-------------------------+
       |                                                 |                                          |
       | 响应 ProbeMatches (pub:Computer)                | 宣告 _smb._tcp.local PTR/SRV/A           | 响应 Node Name 查询 (0x20)
       v                                                 v                                          v
Windows 10/11 资源管理器网络邻居               Apple (macOS/iOS), Android 电视,       安防网络监控摄像头,
即时显示计算机图标与名称                       Linux (Avahi), 极空间/群晖/Kodi 自动发现   Windows XP, 嵌入式 CIFS 设备
```

---

## 四、 核心规则与排查指引

1. **WS-Discovery (Windows 网络邻居)**：
   - 查阅 `OASIS-WSDD-1.1.pdf` Section 5（Probe 与 ProbeMatches 报文交互）。
   - 确保 `wsa:Action` 为 `http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches`，类型声明包含 `wsd:Types="pub:Computer"`。
2. **mDNS & DNS-SD (跨平台)**：
   - 查阅 `RFC6762-mDNS.txt` Section 6（响应构建规则）与 `RFC6763-DNS-SD.txt` Section 4/5/6（服务名与 TXT 属性定义）。
   - 服务名必须是 `_smb._tcp.local`，TXT 记录可附加操作系统信息。
3. **老旧设备 NetBIOS 广播**：
   - 查阅 `RFC1002-NetBIOS-Specs.txt` Section 4.2（名称服务报文头结构）。
   - 响应包必须包含第 16 字节为 `0x20`（File Server Service）的 NetBIOS 编码。
