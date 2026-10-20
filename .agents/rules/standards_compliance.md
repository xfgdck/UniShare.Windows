# 严格遵循官方标准规范实施规则 (Strict Standards Compliance Rule)

在本项目（UniShare.Windows）中，凡涉及任何 SMB 协议交互（协商、会话建立、鉴权、树连接、文件 I/O、锁定、通知等）、身份认证机制（NTLMv1、NTLMv2、SPNEGO、Kerberos）以及局域网设备自发现引擎（WS-Discovery、mDNS/DNS-SD、NetBIOS NBNS、LLMNR），必须无条件严格遵照官方标准文档执行：

### 一、 核心准则
1. **官方标准为唯一真理（Strictly Spec-Driven）**：
   * 所有代码实现、优化与 Bug 修复，必须严格对照 `docs/standards/` 目录下的官方标准规范文档（包含微软开放规范 `[MS-SMB2]`, `[MS-NLMP]`, `[MS-SPNG]`, `[MS-SMB]`, `[MS-CIFS]`, `[MS-KILE]`, `[MS-NBTE]`, `[MS-BRWS]`，OASIS 标准 `OASIS-WSDD-1.1` 以及 IETF RFC 6762/6763/1001/1002/4795/4178/2743/4120）。
   * **严禁凭空臆造、主观推测或随意调整协议字段、Flags 标志位、Capabilities 能力集、SOAP/XML 命名空间、DNS 报文格式、缓冲区大小或加密哈希逻辑**。
2. **规范条款对齐与引用（Spec Section Verification）**：
   * 在排查协议兼容性 Bug 或实施新特性时，**必须在分析汇报与代码关键处明确引用官方规范的章节号**（例如引用 [MS-SMB2] Section 3.3.5.3.1、[MS-NLMP] Section 3.3.1/3.3.2、OASIS WSDD Section 5、RFC 6762 Section 6 等）。
   * 确保状态码（NTStatus）、错误响应、结构体对齐（例如 SMB2 的 8 字节边界对齐）、签名密钥派生算法、组播地址与报文结构与官方规范 100% 吻合。

### 二、 本地标准库速查索引 (`docs/standards/`)
* **SMB 传输与会话**：
  * `[MS-SMB2].pdf`：SMB 2.x/3.x 报文规范、方言协商、0x02FF 响应规则、SESSION_SETUP 会话。
  * `[MS-SMB].pdf` / `[MS-CIFS].pdf`：SMB 1.0 协议规范与向下兼容细节。
* **身份认证**：
  * `[MS-NLMP].pdf`：NTLM 认证、挑战应答、NTLMv1、NTLMv2、SessionKey 派生。
  * `[MS-SPNG].pdf` / `RFC4178`：SPNEGO 安全协商包裹、NegTokenInit / NegTokenResp。
  * `[MS-KILE].pdf` / `RFC4120`：Kerberos 协议与 PAC 票据扩展。
* **局域网设备自发现**：
  * `OASIS-WSDD-1.1.pdf` / `[MS-PCCRD].pdf` / `[MS-BPDP].pdf`：Windows 网络邻居 WS-Discovery 核心标准（UDP 3702, SOAP 1.2, `pub:Computer`）。
  * `RFC6762-mDNS.txt` / `RFC6763-DNS-SD.txt`：苹果、安卓、Linux、智能电视 mDNS/DNS-SD 国际标准（UDP 5353, `_smb._tcp.local`）。
  * `[MS-NBTE].pdf` / `[MS-BRWS].pdf` / `[MS-RAP].pdf` / `RFC1001/1002`：老旧设备与安防摄像机 NetBIOS 广播与主浏览器标准（UDP 137/138）。
  * `RFC4795-LLMNR.txt`：单标签主机名链路本地多播名称解析标准（UDP 5355）。
