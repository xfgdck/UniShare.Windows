# 代码修改后自动编译与生成安装包规则 (Auto-Rebuild & Package Rule)

在本项目（UniShare.Windows）中，任何涉及业务逻辑、界面展现或项目配置的代码修改，必须严格执行以下构建与打包闭环：

### 一、 核心准则
1. **代码修改即打包闭环**：
   * 凡涉及对项目源码、界面文件或配置项（包括但不限于 `.cs`、`.xaml`、`.csproj`、图标与资源文件等）的任何新增、修改或修复，**必须自动执行重新编译并生成最新的 Windows 安装包**。
   * 严禁在修改代码后仅做静态检查或仅在对话中回复而未实际编译打包。
   * 若任务仅修改 `.md`、`.txt` 等纯说明文档，则无需触发重新编译和打包。

2. **标准构建与打包指令**：
   * 统一执行官方构建脚本：
     ```powershell
     powershell -ExecutionPolicy Bypass -File scripts\Build-Installer.ps1
     ```
   * 该脚本会自动完成以下完整流程：
     1. 智能寻址 `.NET 8 SDK`（优先定位 `C:\Users\admin\.dotnet\dotnet.exe` 等有效 SDK 路径）；
     2. 编译并以 `Release` 配置发布至 `bin\publish-fdd`；
     3. 调用 Inno Setup 6 编译器（`iscc.exe`）进行 `lzma2/ultra64` 固实压缩；
     4. 输出最新安装向导安装包至 `dist\UniShare-v1.0.0-Setup-x64.exe` 并打印 SHA-256 校验和。

3. **打包结果核实与汇报**：
   * 构建完成后需确认退出码为 0，且 `dist\UniShare-v1.0.0-Setup-x64.exe` 已成功更新。
   * 在向用户汇报代码变更时，一并提供生成安装包的文件路径、大小及校验信息，确保产物随时可供用户直接测试与安装验证。
