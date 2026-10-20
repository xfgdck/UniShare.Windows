# 本地协议代码与标准规范指南 (Protocol Standards & In-Tree Engine Guide)

本项目涉及的所有底层协议栈实现与官方规范标准均已完备收录于本地工程中：

1. **SMBLibrary 协议栈源码（Vendor 模式主仓纳管）**：
   * **相对路径**：`SMBLibrary/`（项目根目录下）
   * **开源许可**：GNU LGPL v3（详见 `SMBLibrary/License.txt`）
   * **上游来源**：基于 `https://github.com/TalAloni/SMBLibrary.git`，已深度合规重构并由 `UniShare.sln` 直接编译为 `SMBLibrary.dll`。
   * **定位与用途**：作为 UniShare 的核心用户态 SMB 1.0 / 2.x / 3.x 及 NTLM/SPNEGO 协议栈源码，直接参与构建与单元测试。

2. **官方协议标准规范库（唯一权威参考）**：
   * **相对路径**：`docs/standards/`（项目根目录下）
   * **涵盖范围**：包含 SMB 传输规范（`[MS-SMB2]`, `[MS-SMB]`, `[MS-CIFS]`）、身份认证规范（`[MS-NLMP]`, `RFC 4178`, `[MS-SPNG]`）以及局域网自发现规范（`OASIS-WSDD-1.1`, `RFC 6762`, `RFC 1001/1002` 等）。
   * **准则**：所有协议实现与 Bug 修复均以此处官方文档为唯一真理，严禁臆造。


