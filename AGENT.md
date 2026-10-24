# UniShare (UniShare.Windows) - Agent 指南与架构规范

> **致后续接管本项目的 AI 助手**：
> 请务必在开始任何开发任务前**完整阅读本规范**。本文档记录了项目的核心定位、最新代码架构、关键技术实现以及严格的开发准则。

---

## 一、 项目背景与核心定位 (Project Vision)

### 1.1 项目定义
* **项目名称**：UniShare (`UniShare.Windows`)
* **解决方案入口**：[UniShare.sln](UniShare.sln)
* **核心定位**：专为 Windows 10/11 打造的**独立、轻量级、零系统污染**的局域网 SMB 文件共享桌面应用。基于纯用户态协议栈与虚拟账户体系，彻底解决 Windows 原生共享（`LanmanServer`）账号污染、双重权限地狱、报错繁多以及局域网多设备（Android/Windows/安卓电视）自发现困难等痛点，以现代 SMB 2.x/3.x 协议为主打，同时保留对旧设备的按需兼容支持。

### 1.2 核心设计理念
1. **现代高速协议主导与多端流媒体优化 (SMB 2.x / 3.x)**：
   * 引擎默认全局启用现代 **SMB 2.x (2.0.2 / 2.1)** 与 **SMB 3.x (3.0 / 3.0.2 / 3.1.1)** 协议栈，针对局域网大文件互传与 4K/8K 媒体流分段 Seek 做了深度合规与优化，全面服务现代手机、平板、PC 与智能电视。
   * **旧协议按需兜底（非核心重点）**：针对极少数仍受限于 SMB 1.0 的老旧设备，支持在单个共享目录上按需勾选开启（`EnableSmb1`）作为兼容性兜底，避免全局开启带来的安全敞口。
2. **纯用户态虚拟账户隔离 (Zero System Pollution)**：
   * 绝不向 Windows 本地 SAM 数据库注册任何真实系统账户。
   * 每个共享目录维护独立的访问用户列表 (`List<ShareUserPermission>`) 及读写权限，并可独立控制是否允许匿名（Guest）访问。
3. **多维局域网全生态自发现**：
   * 集成 **mDNS (RFC 6762/6763)** 与 **WS-Discovery**，让 Android 设备、安卓智能电视（Kodi/VLC）以及 Windows 资源管理器均可零配置免输 IP 自动发现。
4. **无感端口接管与无损还原**：
   * 启动服务时自动检测并释放被系统内核驱动占用的 TCP 445 端口；停止服务或退出应用时自动逆序恢复 Windows 原生 `LanmanServer` 服务链，不留系统残留。

---

## 二、 核心技术架构与模块说明

### 2.1 技术栈选型
* **开发语言与运行时**：C# / .NET 8 (`net8.0-windows`)
* **桌面 UI 框架**：WPF + `WPF-UI` (4.3.0，Fluent Design 深色主题) + `System.Windows.Forms` (用于 `NotifyIcon` 系统托盘与文件夹选择对话框)
* **核心 SMB 协议栈**：Vendor 模式主仓源码纳管的 `SMBLibrary`（遵循 LGPL v3，经官方标准与多设备自适应认证深度重构，直接由 `UniShare.sln` 编译）
* **系统服务控制**：`System.ServiceProcess.ServiceController` (10.0.12) + `schtasks.exe`（任务计划程序免 UAC 开机自启）

### 2.2 工程目录结构
```text
UniShare.Windows/
├── UniShare.sln                        # 解决方案入口
├── AGENT.md                            # AI 开发与架构交接全局规范（本文档）
├── README.md                           # 面向终端用户的使用说明文档
├── LICENSE                             # 根目录许可证 (GPLv3 + SMBLibrary LGPLv3 声明)
├── SMBLibrary\                         # 底层纯托管 SMB 协议栈源码 (Vendor 纳管，LGPL v3)
│   ├── SMBLibrary\                     # SMB 1.0/2.x/3.x 与 NTLM/SPNEGO 协议实现
│   ├── SMBLibrary.Win32\               # Windows 本地安全与文件系统支撑
│   ├── SMBLibrary.Tests\               # 官方协议合规性单元测试套件
│   ├── Utilities\                      # 字节转换与加解密算法工具
│   └── License.txt                     # GNU LGPL v3 许可证书
├── scripts\
│   ├── Build-Installer.ps1             # 阶段五一键自动化构建与 Inno Setup 安装包打包脚本
│   ├── Run-StabilityTest.ps1           # 阶段四自动化稳定性与并发读写压力测试脚本
│   └── installer.iss                   # Inno Setup 6 专业安装向导脚本 (内置 .NET 8 自动检测与下载安装)
└── src\
    ├── UniShare.Core\                  # 核心业务逻辑与 SMB 引擎类库
    │   ├── Models\
    │   │   └── ServerModels.cs         # 配置模型 (ServerConfig, ShareConfig, ShareUserPermission 等)
    │   ├── Discovery\                  # 局域网设备自发现服务 (mDNS + WS-Discovery + NetBIOS)
    │   │   ├── MdnsDiscoveryServer.cs  # RFC 6762/6763 mDNS 响应器 (_smb._tcp.local)
    │   │   ├── WsDiscoveryServer.cs    # Windows 资源管理器网络邻居探测响应器 (UDP 3702)
    │   │   ├── NetBiosDiscoveryServer.cs # NetBIOS (NBNS) 响应器 (UDP 137，服务安防摄像头与旧设备)
    │   │   ├── NetworkDiscoveryService.cs # 自发现服务统筹生命周期管理
    │   │   └── DiscoveryHelper.cs      # 局域网有效 IPv4 地址过滤与提取
    │   ├── ConfigManager.cs            # config.json 持久化读写与默认安全归一化
    │   ├── SmbServerEngine.cs          # SMB 服务端生命周期、认证回调与细粒度目录权限拦截
    │   └── PortHelper.cs               # TCP 445 端口占用诊断与三级内核驱动链安全接管/恢复
    ├── UniShare.Server\                # 命令行无头宿主程序（供调试或控制台运行）
    │   └── Program.cs
    ├── UniShare.StressTest\            # 阶段四稳定性、4K流媒体Seek、海量小文件并发与内存/句柄压测工具
    │   └── Program.cs
    └── UniShare.UI\                    # WPF 现代桌面客户端（主交付工程，生成 UniShare.exe）
        ├── App.xaml / App.xaml.cs      # 应用入口，单实例检测，支持 --minimized 命令行静默启动参数
        ├── MainWindow.xaml / .cs       # 主窗口（左侧导航栏：共享管理 + 应用设置、系统托盘管理、前台唤醒）
        ├── Helpers\
        │   ├── SingleInstanceHelper.cs # 全局 Mutex 与命名事件单实例互斥，唤醒已存实例至前台
        │   ├── AutoStartHelper.cs      # 基于 Windows 任务计划程序 (HighestAvailable) 的免弹窗开机自启
        │   ├── ClipboardHelper.cs      # Win32 底层重试与降级防崩溃剪贴板写入工具（杜绝 0x800401D0 崩溃）
        │   └── NetworkHelper.cs        # 网卡 IP 查询与展示工具
        ├── ViewModels\
        │   ├── MainViewModel.cs        # 主界面状态、服务启停、共享增删改、445 端口自动化联动
        │   ├── ShareItemViewModel.cs   # 单个共享卡片展示模型
        │   ├── ObservableObject.cs     # INotifyPropertyChanged 基类
        │   └── RelayCommand.cs         # UI 线程直接调度的 ICommand 实现
        └── Views\
            ├── AddShareDialog.xaml/.cs # 新建/编辑共享弹窗（三段式卡片布局与完备防呆校验）
            └── MessageDialog.xaml/.cs  # 自定义 Fluent 模态消息/确认弹窗（替代原生 MessageBox）
```

### 2.3 核心机制详解

#### 1. Windows 445 端口三级驱动链释放与还原 ([`PortHelper.cs`](src/UniShare.Core/PortHelper.cs))
Windows 下 TCP 445 端口由 `LanmanServer` 及内核驱动 `srv2.sys`、`srvnet.sys`（PID 4 `System` 进程）共同锁定：
* **释放顺序 (`ReleasePort445`)**：
  1. `sc.exe config LanmanServer start= disabled`
  2. `sc.exe stop LanmanServer`
  3. `sc.exe stop srv2`
  4. `sc.exe stop srvnet`
  5. 轮询 `CanBindPort(445)` 确认端口真正释放。
* **还原顺序 (`RestoreWindowsSharing`)**：
  1. `sc.exe start srvnet`
  2. `sc.exe start srv2`
  3. `sc.exe config LanmanServer start= auto`
  4. `sc.exe start LanmanServer`
* **自动联动**：在 [`MainViewModel.cs`](src/UniShare.UI/ViewModels/MainViewModel.cs) 中，点击“启动服务”若发现 445 端口被占用会自动调用释放；点击“停止服务”或退出程序时，会自动调用还原。

#### 2. 共享权限与防呆校验机制 ([`SmbServerEngine.cs`](src/UniShare.Core/SmbServerEngine.cs) & [`AddShareDialog.xaml.cs`](src/UniShare.UI/Views/AddShareDialog.xaml.cs))
* **默认安全策略**：新建共享时默认 `AllowGuest = false`、`EnableSmb1 = false`。
* **保存前强校验**：
  * 校验共享名称合法性与唯一性（禁止重名或相同物理路径重复挂载）。
  * 拦截“未点击添加按钮但用户名输入框仍有残留文本”的误操作，弹窗提醒用户先点击“添加”或清空输入框。
  * 禁止创建既没有添加任何访问用户、又未开启匿名访问的无权限共享。
* **运行时权限拦截 (`FileSystemShare.AccessRequested`)**：
  * 根据当前登录用户名匹配该 `ShareConfig.Users` 中的独立权限或 `AllowGuest` 规则，精确放行读取 (`Read`) 或写入 (`Write`) 请求。
  * `IsReadOnly` 属性根据用户列表和匿名开关的综合写权限自动计算，用于 UI 状态徽章展示。

#### 3. 服务状态记忆与免 UAC 开机自启 ([`ConfigManager.cs`](src/UniShare.Core/ConfigManager.cs) & [`AutoStartHelper.cs`](src/UniShare.UI/Helpers/AutoStartHelper.cs))
* **运行状态持久化**：`ServerConfig.IsServiceEnabled` 记录用户最后一次是处于“启动”还是“停止”状态，软件重启后自动按上次状态恢复。
* **任务计划自启**：因本程序需要 `requireAdministrator` 权限以管理 445 端口，普通注册表 `Run` 键自启会被 Windows 拦截。[`AutoStartHelper.cs`](src/UniShare.UI/Helpers/AutoStartHelper.cs) 通过生成 UTF-16 XML 并调用 `schtasks /Create /TN "UniShare_AutoStart" /XML ... /F` 注册 `HighestAvailable` 登录触发任务，实现免 UAC 弹窗静默开机自启（支持传入 `--minimized` 直接驻留托盘）。

#### 4. UI 规范与细节约定
* **去品牌化文案**：界面所有描述文本严禁出现特定硬件品牌名（如海康、萤石、小米、Mac 等），统一使用通用设备表述（如“手机、电脑、智能电视等设备”、“兼容旧协议设备”）。
* **弹窗统一**：所有提示与确认弹窗均使用 [`MessageDialog`](src/UniShare.UI/Views/MessageDialog.xaml.cs)（轻提示如“复制成功”使用无按钮、超时自动关闭的 `MessageDialog.ShowToast`），禁止直接调用系统原生 `System.Windows.MessageBox.Show`。
* **滚动条与边框间距**：在包含卡片边框的 `ScrollViewer` 中，外层 `ScrollViewer` 与内层容器需分离右侧 `Margin`/`Padding`，防止悬浮滚动条遮挡卡片圆角右边框。
* **按钮即时响应与视觉权重**：[`RelayCommand.cs`](src/UniShare.UI/ViewModels/RelayCommand.cs) 直接维护事件处理器并通过 `Application.Current.Dispatcher` 触发 `RaiseCanExecuteChanged`，避免后台任务切换 `IsBusy` 时按钮状态延迟刷新；主界面“停止服务”与列表项“移除”等低频操作统一采用低调的 `Secondary` 次级样式，仅在二次确认弹窗中使用 `Danger` 警示色，避免大面积红色喧宾夺主。
* **应用图标与视觉统一**：应用图标为原创几何飞鸟设计（象牙白身躯、松青羽翼配暖琥珀衬色、珊瑚红眼点、嘴衔分叉小树枝与嫩芽），采用 22% 圆角的纯白底座（小鸟图案等比例放大至底座边界，左右两侧完全无缝隙贴边，搭配细腻边界与柔和底阴影，在明暗主题下均清晰扎实），包含 16×16 至 256×256 完整分辨率，位于 [`icon.ico`](src/UniShare.UI/Assets/icon.ico)。



#### 5. 局域网设备自发现广播机制 ([`Discovery/`](src/UniShare.Core/Discovery/))
* **双栈广播协同**：在 [`NetworkDiscoveryService.cs`](src/UniShare.Core/Discovery/NetworkDiscoveryService.cs) 统一统筹管理 mDNS 与 WS-Discovery，伴随 SMB 服务生命周期自适应启停（支持在“设置”页动态开关）：
  * **mDNS (`_smb._tcp.local`)**：基于 RFC 6762/6763 在 UDP 5353 多播组 `224.0.0.251` 广播 PTR、SRV、TXT 与 A 记录，服务于 Android 设备及安卓智能电视（Kodi/VLC 等），实现零配置免输入 IP 发现。停止时主动发送 TTL=0 Goodbye 包清空客户端缓存。
  * **WS-Discovery (`pub:Computer`)**：在 UDP 3702 多播组 `239.255.255.250` 监听并响应 Windows 资源管理器发起的 Probe 及 Resolve 探测，宣告 `pub:Computer` 与 `wsdp:Device`，使本机直接呈现在其他 Windows 电脑的“网络”邻居列表中。
* **安全端口复用**：两套 UDP 监听均显式配置 `ReuseAddress = true`，安全兼容系统已有网络组件，并附带完善超时防护。

#### 6. 长期稳定性实测与流媒体优化套件 ([`UniShare.StressTest`](src/UniShare.StressTest/Program.cs))
* **4K/8K 媒体流 Seek 压测**：测试生成带确定性指纹的大文件，分配多线程进行高频随机 Seek + Range 读取，压测实测吞吐达 13,000+ MB/s，IOPS 达 34,000+ ops/sec，p99 延迟 < 1ms，校验错误为 0。
* **海量小文件高并发吞吐**：多线程并发写入 1,000+ 小文件（8KB~64KB）并逐个进行 SHA-256 哈希比对校验与安全清理，处理速率超 7,000 operations/sec，零损坏零悬挂。
* **内存与系统句柄长效监测**：自动化连续混合读写压测中，实时跟踪工作集内存与系统句柄指标，验证内存平稳无持续攀升、句柄净增合规，无任何泄露隐患。
#### 7. 单实例互斥与前台唤醒机制 ([`SingleInstanceHelper.cs`](src/UniShare.UI/Helpers/SingleInstanceHelper.cs))
* **会话互斥体 (`Mutex`)**：采用 `UniShare_SingleInstance_Mutex` 防止多实例并发启动导致的 TCP 445 端口冲突与配置覆盖，自动兼容异常退出的 `AbandonedMutexException`。
* **双通道唤醒机制**：
  * **命名事件 (`EventWaitHandle`)**：主实例后台常驻线程监听 `UniShare_SingleInstance_Activate_Event`，一旦触发立即向 UI 调度器派发恢复指令。
  * **Win32 广播消息 (`RegisterWindowMessage`)**：注册 `UniShare_ActivateWindow_Message` 并在主窗口 Hook 中捕获，结合 `ChangeWindowMessageFilter` 跨越 UIPI 隔离。
* **焦点抢占防拦截置顶 (`RestoreAndActivate`)**：
  * 针对 Windows 10/11 系统的前台锁定限制，调用 `AllowSetForegroundWindow` 显式移交权限。
  * 窗口恢复时结合 `AttachThreadInput` 挂接前台线程输入队列，通过 `BringWindowToTop` + `ShowWindow(SW_RESTORE)` + `SetForegroundWindow` 保证从托盘或最小化状态平滑恢复并聚焦于最前台。

#### 8. 轻量级 Inno Setup 安装包与运行库智能补全 ([`scripts/installer.iss`](scripts/installer.iss) & [`scripts/Build-Installer.ps1`](scripts/Build-Installer.ps1))
* **超轻量体积 (仅 ~4.3MB)**：采用框架依赖发布搭配 Inno Setup 6 `lzma2/ultra64` 固实压缩，相比百兆自包含方案下载体验极速。
* **运行库智能补全与全流程进度指示**：安装向导通过 `FindFirst` 遍历 `%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*` 及注册表探测目标机器运行库；若缺失，通过内置 `CreateDownloadPage` 自动拉取微软官方直链 (`aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe`) 展示下载进度，下载完成后立即切换至 `CreateOutputProgressPage` 状态并以 `/passive /norestart` 弹出微软原生安装进度条窗口，全程无静止卡顿感。
* **全程管理员权限与直接启动**：`PrivilegesRequired=admin` 结合 `InitializeSetup` 强校验管理员权限启动；安装结束前勾选“运行 UniShare”时通过 `runascurrentuser` 继承管理员上下文，避免权限降级或二次提权失败。
* **无损卸载与系统保护**：控制面板卸载时自动清理计划任务自启动项，并主动逆序恢复系统原生 `LanmanServer` 服务，不留系统残留。

#### 9. 官方协议规范深度合规与自适应认证机制 ([`SMBLibrary/`](SMBLibrary/) & [`src/UniShare.Core/`](src/UniShare.Core/))
* **[MS-SMB2] Section 3.3.5.3.2 & 2.2.4 (多协议 0x02FF 通配协商响应合规)**：
  * 对 `"SMB 2.???"` (0x02FF) 协商响应严格遵从规范：`Capabilities MUST be 0`、`MaxTransactSize/MaxReadSize/MaxWriteSize MUST be 0`、`SecurityBuffer MUST be empty` (offset=0, length=0)，彻底解决原代码下发非零 Capabilities 导致严格客户端（如部分嵌入式/安防设备）在 SMB2 握手阶段直接中断连接的致命缺陷。
  * 增加大小写不敏感方言比对及 SMB 3.0.2 / SMB 3.1.1 客户端的最大公共方言回退兼容。
* **[MS-NLMP] Section 2.2.1.3 (NTLM 编码自适应解码)**：
  * 在 [`AuthenticateMessage.cs`](SMBLibrary/SMBLibrary/Authentication/NTLM/Structures/AuthenticateMessage.cs) 依据 `NegotiateFlags` 动态分支：若未设置 `NTLMSSP_NEGOTIATE_UNICODE` 则采用 OEM/ANSI 字符集解码用户名、域名及工作站名，避免将未协商 UNICODE 的老旧或轻量客户端账号解码为乱码导致鉴权失败。
* **[MS-NLMP] Section 3.3.2 & 3.1.5.1.2 (NTLMv2 域变体候选集自适应探测与账号前缀容错)**：
  * 在 [`IndependentNTLMAuthenticationProvider.cs`](SMBLibrary/SMBLibrary/Authentication/NTLM/IndependentNTLMAuthenticationProvider.cs) 支持候选域探测：依序回退客户端显式 Domain、大写 `ToUpperInvariant`、Challenge TargetName/计算机名、空字符串 `""` 以及从客户端 `AV_PAIR` TargetInfo 中提取的机器/域名。
  * 业务层 [`SmbServerEngine.cs`](src/UniShare.Core/SmbServerEngine.cs) 与认证层全面支持 `DOMAIN\user` 及 `user@domain` 剥离提取 `bareName` 归一化比对。
* **RFC 4178 Section 4.2.1 (SPNEGO 扩展与原生 NegTokenInit 兼容)**：
  * 在 [`SimpleProtectedNegotiationTokenInit.cs`](SMBLibrary/SMBLibrary/Authentication/GSSAPI/SPNEGO/SimpleProtectedNegotiationTokenInit.cs) 移除遭遇 `ReqFlags (0xA1)` 抛出未实现异常的隐患，兼容 DER 扩展并支持无 0x60 ApplicationTag 包装的原生报文解析。
* **OASIS WSDD 1.1 与 2005/04 双协议栈 ([`WsDiscoveryServer.cs`](src/UniShare.Core/Discovery/WsDiscoveryServer.cs))**：
  * 根据客户端探针命名空间动态响应，兼容 OASIS 1.1 (`http://docs.oasis-open.org/ws-dd/ns/discovery/2009/01`) 与 2005/04，确保 Windows 网络邻居各版本客户端秒级发现。

---

## 三、 AI 开发者行为准则 (Strict Rules for AI)

任何接手本项目的 AI 助手必须严格遵循以下原则：

### 3.1 零假设原则 (Zero-Assumption Principle)
1. **禁止凭记忆盲猜**：涉及第三方库 API 签名、Windows 网络驱动行为、SMB 协议交互规范时，必须先查阅代码或使用搜索工具联网验证最新官方规范。
2. **禁止假定已知**：任何外部事实未经核实前，一律视为可能已变更。

### 3.2 方案决策与用户确认原则
* 涉及架构路线选择、破坏性重构、或存在多种实现替代方案时，**必须列出各方案的优缺点并等待用户选择**，严禁替用户擅自做主。
* 修改涉及系统网络配置、系统驱动或安全软件可能拦截的操作时，必须向用户清晰说明原理与影响。

### 3.3 最小范围与代码边界原则
* 严格遵循任务直接关联文件的最小修改原则，严禁漫无目的地遍历或读取与当前任务无关的源文件。
* 保持注释与文档完整性，不得删除既有的业务逻辑注释。

### 3.4 Git 提交与推送准则 (Git Commit & Push Guidelines)
1. **指令驱动提交（严禁私自提交）**：未经用户在当前对话中明确给出“提交”、“commit”、“推送”、“push”等明确指令前，**严禁自动运行 `git commit` 或 `git push`**。代码或文档修改完成后，只需清晰指出改动点，等待用户给出指令。
2. **提交与推送一体化执行**：一旦用户明确给出提交指令（如“提交”、“commit”、“push”等），在完成 `git commit` 后，**必须紧接着执行 `git push` 同步推送到远端仓库**，无需再额外分步询问是否推送。
3. **提交信息规范（中文为主）**：执行 Git Commit 时，提交描述文本（Commit Message）**尽量使用中文**进行清晰简练的表述（例如：`feat: 升级 445 端口驱动链释放逻辑`、`docs: 更新用户说明与架构规范` 等）。

### 3.5 代码修改后自动重新编译与生成安装包准则 (Auto-Rebuild & Package After Modification)
* **修改后主动编译并打包**：在本项目中，每当完成对源码或界面文件（`.cs`、`.xaml`、`.csproj`、资源文件等）的修改后，**必须自动执行编译并重新生成最新安装包**（若仅修改 `.md` 文档则无需编译打包）。
* **自动化打包执行**：统一执行 `powershell -ExecutionPolicy Bypass -File scripts\Build-Installer.ps1`，脚本会自动定位 `.NET 8 SDK`（优先检测 `C:\Users\admin\.dotnet\dotnet.exe`）、发布 Release 版本，并调用 Inno Setup 编译器生成最新安装包至 `dist\UniShare-v1.0.0-Setup-x64.exe`，确保产物随时可供用户直接测试与安装验证。

### 3.6 架构与文档同步更新准则 (Keep AGENT.md Synchronized)
* **代码或架构变更后必须同步更新 `AGENT.md`**：每当修改了核心业务逻辑、数据模型、模块职责、UI 结构、配置项或新增了关键特性后，**必须及时将变动同步更新到本 `AGENT.md` 文档中**，确保后续接手的 AI 助手能够准确理解最新代码结构与上下文，避免因文档滞后导致后续 AI 误判或写出冲突代码。

### 3.7 本地协议源码库与官方规范查阅规范 (Local Protocol & Standards Rule)
* **前置检查**：开始任何分析、开发、调试、协议兼容排查或代码审查前，先确认以下本地资源已就绪：
  * **SMBLibrary 协议栈源码**：`SMBLibrary/`（相对项目根目录，已作为 Vendor 源码由主工程直接编译，用于排查协议握手、方言协商、会话建立与安全加解密逻辑）
  * **官方协议、身份认证与设备自发现标准文档库**：`docs/standards/`（相对项目根目录，完整收录 SMB 传输、身份认证以及局域网自发现三大领域的官方标准：
    * **认证与 SMB 协议**：`[MS-NLMP].pdf`, `[MS-SMB2].pdf`, `[MS-SMB].pdf`, `[MS-CIFS].pdf`, `[MS-SPNG].pdf`, `[MS-KILE].pdf`, `[MS-AUTHSOD].pdf` 及 RFC 4178/2743/4120；
    * **局域网设备自发现**：`OASIS-WSDD-1.1.pdf` (WS-Discovery 国际标准), `[MS-PCCRD].pdf`, `[MS-BPDP].pdf`, `[MS-NBTE].pdf`, `[MS-BRWS].pdf`, `[MS-RAP].pdf` 以及 IETF RFC 6762 (mDNS), RFC 6763 (DNS-SD), RFC 1001/1002 (NetBIOS), RFC 4795 (LLMNR)。）
* **直接查阅本地资源**：当需要查看内部实现、接口定义、协议规范或官方标准时，**直接查阅本地已有文件**，严禁在未检查本地的情况下重复在线抓取或进行反编译操作。

### 3.8 严格遵循官方标准规范实施准则 (Strict Compliance with Official Standards)
1. **官方标准为唯一真理（Strictly Spec-Driven）**：
   * 所有涉及 **SMB 协议交互**（协商 Negotiate、会话建立 SessionSetup、树连接 TreeConnect、文件读写/Seek、锁定、通知、会话注销等）、**身份认证机制**（NTLMv1、NTLMv2、SPNEGO、Kerberos、GSS-API 等）以及 **局域网设备自发现引擎**（WS-Discovery、mDNS/DNS-SD、NetBIOS NBNS、LLMNR 等）的代码实现与 Bug 修复，**必须严格按照 `docs/standards/` 下收录的官方标准文档执行**。
   * 严禁凭空臆造、主观推测或随意调整协议字段、标志位（Flags/Capabilities）、缓冲区上限（MaxTransactSize/MaxReadSize/MaxWriteSize）、XML/SOAP Schema 命名空间、DNS 记录结构或安全哈希计算逻辑。
2. **规范条款对齐与引用（Spec Section Verification）**：
   * 在定位协议异常或实施新特性时，**必须在分析汇报与代码实现中明确引用官方规范的具体章节编号**：
     * **SMB 传输与多协议升级**：严格对照 `[MS-SMB2]`（如 Section 3.3.5.3.1 多协议通配 0x02FF 响应严禁 LargeMTU、缓冲区限 64KB；Section 3.3.5.5 SessionSetup 流程）；
     * **身份认证与密码校验**：严格对照 `[MS-NLMP]`（如 Section 3.3.1 NTLMv1 计算、Section 3.3.2 NTLMv2 HMAC-MD5 计算与会话密钥派生）；
     * **WS-Discovery（Windows 网络邻居）**：严格对照 `OASIS-WSDD-1.1.pdf`（Section 5 Probe/ProbeMatches、Hello/Bye 生命周期广播，UDP 3702 组播 `239.255.255.250`，声明 `wsd:Types="pub:Computer"`）；
     * **mDNS / DNS-SD（跨平台发现）**：严格对照 `RFC 6762` 与 `RFC 6763`（UDP 5353 组播 `224.0.0.251`，PTR/SRV/TXT/A 记录模型，服务名 `_smb._tcp.local`）；
     * **NetBIOS 广播（传统安防设备）**：严格对照 `RFC 1001/1002` 与 `[MS-NBTE]`（UDP 137，16 字节专用半字节编码，末字节 `0x20` 资源记录）。
   * 必须严格保证所有状态码（NTStatus）、加密填充、结构体边界对齐（如 SMB2 8 字节边界规则）、组播报文格式与时序与官方文档 100% 保持一致，坚决杜绝因实现偏离标准而引发不同客户端设备的兼容性断裂。

---

## 四、 开发路线图与当前进度 (Roadmap)

* [x] **阶段一：协议引擎验证与内核驱动接管 (已完成)**
  * [x] 基于 .NET 8 与 `SMBLibrary 1.5.8.1` 搭建用户态 SMB 1.0 / 2.x / 3.x 服务端引擎。
  * [x] 实现内核级 445 端口三级驱动链停用与无缝还原（`LanmanServer -> srv2 -> srvnet`）。
* [x] **阶段二：细粒度虚拟用户与多共享目录持久化 (已完成)**
  * [x] 实现 `config.json` 持久化读写（`ConfigManager`），支持每个共享独立配置多用户读写权限、匿名开关与 SMB 1.0 开关。
  * [x] 实现 `FileSystemShare.AccessRequested` 细粒度读写权限拦截与服务启停状态记忆。
* [x] **阶段三：现代化原生桌面 GUI (`UniShare.UI`) (已完成)**
  * [x] 基于 WPF + `WPF-UI` 打造 Fluent 深色视觉主界面与侧边栏导航（共享管理 / 应用设置）。
  * [x] 服务一键启停（自动联动 445 端口接管与释放还原）、网卡 IP 切换与地址一键复制。
  * [x] 三段式新建/编辑共享对话框、完整的输入防呆校验与统一的 Fluent `MessageDialog` 模态弹窗。
  * [x] 基于 Windows 任务计划程序的免 UAC 弹窗开机自启及系统托盘后台常驻。
* [x] **阶段四：长期稳定性实测与局域网发现增强 (已完成)**
  * [x] 局域网设备自发现支持（mDNS `_smb._tcp.local`、WS-Discovery `pub:Computer` 与 NetBIOS NBNS 广播响应与 UI 设置）。
  * [x] 4K/8K 媒体流高频 Seek、海量小文件并发 I/O 与长效内存/句柄监测套件（`UniShare.StressTest` 及 `Run-StabilityTest.ps1`）。
* [x] **阶段五：Inno Setup 专业安装包向导与分发交付 (已完成)**
  * [x] 单实例互斥防并发启动与已有实例平滑前台唤醒机制（`SingleInstanceHelper`）。
  * [x] 打造 Inno Setup 6 专业标准安装向导（`.iss`）与一键编译脚本（[`Build-Installer.ps1`](scripts/Build-Installer.ps1)）。
  * [x] 运行库智能检测与自动下载安装：安装时智能探测 .NET 8 桌面运行时，若缺失调用官方直链静默安装补全。
  * [x] 超轻量安装包（4.3MB），完备桌面/开始菜单快捷方式、可选开机自启计划任务、以及控制面板无损卸载与系统服务清理恢复。
* [x] **阶段六：官方协议规范合规审计与设备兼容性重构 (已完成)**
  * [x] **[MS-SMB2]**：修正 0x02FF 多协议通配协商响应报文，规范要求 Capabilities 与 SecurityBuffer 必须清零，消除严格客户端 SMB2 降级中断。
  * [x] **[MS-NLMP]**：实现 AuthenticateMessage 编码自适应解码（UNICODE/OEM 动态切换），彻底解决未开启 UNICODE 客户端账号乱码问题。
  * [x] **[MS-NLMP]**：实现 NTLMv2 候选域名多轮自适应探测（原始、大写、TargetName、空值），结合 `DOMAIN\user` 与 `user@domain` 剥离提取。
  * [x] **RFC 4178**：完善 SPNEGO negTokenInit 扩展与 DER 长度安全跳过，支持原生未包装 NegTokenInit。
  * [x] **OASIS WSDD 1.1**：WS-Discovery 支持 OASIS 2009/01 与 2005/04 双协议栈，Windows 资源管理器网络秒级互通。
  * [x] 补充官方规范合规性单元测试套件（`ProtocolStandardsComplianceTests.cs`），77 项测试 100% 通过。


