; -- UniShare Inno Setup 6 安装向导脚本 --
#define MyAppName "UniShare"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "UniShare"
#define MyAppURL "https://github.com/xfgdck/UniShare.Windows"
#define MyAppExeName "UniShare.exe"
#define PublishDir "..\bin\publish-fdd"

[Setup]
AppId={{5A8D4971-893C-43D4-A76F-B9582A02C3F2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=UniShare-v{#MyAppVersion}-Setup-x64
SetupIconFile=..\src\UniShare.UI\Assets\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 完成安装后可选立即启动（继承安装程序当前的管理员权限，防止提权受阻或打开失败）
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; 卸载前结束进程并清理计划任务
Filename: "taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillUniShareProcess"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""UniShare_AutoStart"" /F"; Flags: runhidden; RunOnceId: "DelAutoStartTask"
; 确保 Windows 系统原生共享服务恢复
Filename: "sc.exe"; Parameters: "config LanmanServer start= auto"; Flags: runhidden; RunOnceId: "RestoreLanmanConfig"
Filename: "sc.exe"; Parameters: "start LanmanServer"; Flags: runhidden; RunOnceId: "RestoreLanmanStart"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  InstallProgressPage: TOutputProgressWizardPage;
  NeedDownloadDotNet: Boolean;

function IsDotNet8DesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
  Path: String;
begin
  Result := False;
  // 1. 优先检查 %ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App 是否存在 8.* 目录
  Path := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*');
  if FindFirst(Path, FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;

  // 2. 检查 64位注册表 InstalledVersions 下的 sharedfx
  if not Result then
  begin
    if RegKeyExists(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App') then
      Result := True;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsAdminInstallMode then
  begin
    MsgBox('UniShare 安装程序需要管理员权限以接管网络驱动并安装必要运行库。'#13#10'请以管理员身份重新运行安装包。', mbCriticalError, MB_OK);
    Result := False;
  end;
end;

procedure InitializeWizard;
begin
  NeedDownloadDotNet := not IsDotNet8DesktopRuntimeInstalled;

  if NeedDownloadDotNet then
  begin
    DownloadPage := CreateDownloadPage(
      '正在下载所需运行库',
      '检测到您的电脑尚未安装 .NET 8 桌面运行时环境，正在自动下载补全...',
      nil
    );
    DownloadPage.ShowBaseNameInsteadOfUrl := True;

    InstallProgressPage := CreateOutputProgressPage(
      '正在安装系统运行库',
      '正在安装 Microsoft .NET 8 桌面运行时环境，请稍候...'
    );
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  InstallerPath: String;
  ResultCode: Integer;
  DotNetParams: String;
begin
  Result := True;
  if (CurPageID = wpReady) and NeedDownloadDotNet then
  begin
    DownloadPage.Clear;
    // 微软官方长期有效的 .NET 8 桌面运行时 (x64) 官方直链
    DownloadPage.Add(
      'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe',
      'dotnet8-desktop-runtime.exe',
      ''
    );
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Log('用户取消了运行库下载')
        else
          MsgBox('下载 .NET 8 桌面运行时失败：'#13#10 + GetExceptionMessage + #13#10#13#10 + '请检查网络连接后重试，或自行从微软官网安装 .NET 8 Desktop Runtime (x64)。', mbError, MB_OK);
        Result := False;
        Exit;
      end;
    finally
      DownloadPage.Hide;
    end;

    // 下载完成，进入安装阶段并展示安装进度界面
    InstallerPath := ExpandConstant('{tmp}\dotnet8-desktop-runtime.exe');
    if WizardSilent then
      DotNetParams := '/install /quiet /norestart'
    else
      DotNetParams := '/install /passive /norestart';

    InstallProgressPage.SetText(
      '正在安装 Microsoft .NET 8 桌面运行时...',
      '系统正在解包并配置核心组件，请查看弹出的安装进度指示...'
    );
    InstallProgressPage.SetProgress(50, 100);
    InstallProgressPage.Show;

    try
      // 使用 /passive 参数启动微软官方安装包，展示官方原生安装进度条窗口
      if Exec(InstallerPath, DotNetParams, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      begin
        InstallProgressPage.SetProgress(100, 100);
        if (ResultCode = 0) or (ResultCode = 3010) then
        begin
          NeedDownloadDotNet := False;
          Result := True;
        end
        else
        begin
          MsgBox('安装 .NET 8 桌面运行时返回异常代码: ' + IntToStr(ResultCode) + '。请尝试手动安装后再继续。', mbError, MB_OK);
          Result := False;
        end;
      end
      else
      begin
        MsgBox('无法启动 .NET 8 运行时安装包程序。', mbError, MB_OK);
        Result := False;
      end;
    finally
      InstallProgressPage.Hide;
    end;
  end;
end;
