; ---------------------------------------------------------------------------
; ExplorerTabUtility - WinUI 3 重构版 安装脚本（框架依赖发布）
;
; 与"自包含"版本的差异：安装包只带应用本体，运行时改为在缺失时下载并静默安装：
;   1. .NET 10 Desktop Runtime        （约 60 MB）
;   2. Windows App Runtime (WinAppSDK)（约 120 MB）
;
; 体积权衡：自包含免依赖但压缩包 98 MB；框架依赖压缩后约 30 MB 出头，
; 代价是首次安装需要联网补齐两个运行时（已安装则跳过，不重复下载）。
;
; 关键路径约定：应用文件直接由 [Files] 从 publish 目录铺到 {app}\ExplorerTabUtility\，
; 因此启动路径是 {app}\ExplorerTabUtility\ExplorerTabUtility.exe。
;
; 为什么不再走"先放 zip、装完再解压"：解压写在 [Run] 段，而 [Run] 的执行时机**晚于**
; [Icons]。也就是说快捷方式是在目标 exe 还不存在的时候创建的 —— 资源管理器按当时的结果
; 缓存了一个"空白图标"，装完就一直是白板。
; 改用 [Files] 直铺后，[Files] 先于 [Icons] 执行，建快捷方式时 exe 已经就位。
; ---------------------------------------------------------------------------

#ifndef MyAppVersion
  #define MyAppVersion "v1.0.1"
#endif

#define MyAppVersionWithoutV StringChange(MyAppVersion, "v", "")
#define DashPos Pos("-", MyAppVersionWithoutV)
#define MyAppNumericVersion (DashPos > 0) ? Copy(MyAppVersionWithoutV, 1, DashPos - 1) : MyAppVersionWithoutV

; ---------------------------------------------------------------------------
; 目标架构：x64（缺省）或 arm64。
;
; tools/release.py 用 `ISCC /DMyAppArch=arm64` 选择 arm64；不带这个参数时与历史行为逐字节
; 一致（x64 包，产物名不变）。手工在 Git Bash 里敲时注意 MSYS2 会把 `/D...` 当路径吃掉，
; 需要 MSYS2_ARG_CONV_EXCL='*' 前缀。
;
; 为什么一个架构一个安装包、而不是做成"二合一"：
;   1. 应用本体是按 RID 发布的框架依赖产物，两个架构的 exe/dll 不能混进同一个 [Files]；
;   2. ArchitecturesAllowed 必须收紧到单一架构 —— ARM64 包如果放行 x64，用户会在 x64 机器上
;      装到一个启动不了的 exe，而这属于"装完没反应、没有任何报错"那一类最难查的故障。
; ---------------------------------------------------------------------------
#ifndef MyAppArch
  #define MyAppArch "x64"
#endif

; 产物名后缀：与便携包的 _Portable_<arch> 命名对齐成 _Setup_x64 / _Setup_arm64，
; 两个架构一眼可辨。x64 早先不带后缀（历史资产名 ExplorerTabUtility_v1.0.1_Setup.exe），
; 已统一到带后缀的写法 —— release.py、choco 模板与发布说明的资产名都跟着这一处变。
#if MyAppArch == "arm64"
  #define MyAppArchSuffix "_arm64"
#else
  #define MyAppArchSuffix "_x64"
#endif

#define MyAppPublisher "saillill"
#define MyAppName "ExplorerTabUtility"
#define MyAppExeName MyAppName + ".exe"
#define MyAppRelativePath MyAppName + "\" + MyAppExeName
#define MyAppURL "https://github.com/saillill/ExplorerTabUtility-WinUI3"

; 官方稳定地址：aka.ms 始终指向该主版本的最新补丁，无需随补丁号改脚本。
; 每个架构各一份下载器：arm64 机器上必须装 arm64 的运行时，否则应用仍会退回模拟执行。
#if MyAppArch == "arm64"
  #define DotNetUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-arm64.exe"
  #define DotNetFileName "windowsdesktop-runtime-10-win-arm64.exe"
  #define WinAppRuntimeUrl "https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-arm64.exe"
  #define WinAppRuntimeFileName "windowsappruntimeinstall-arm64.exe"
#else
  #define DotNetUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
  #define DotNetFileName "windowsdesktop-runtime-10-win-x64.exe"
  #define WinAppRuntimeUrl "https://aka.ms/windowsappsdk/2.5/latest/windowsappruntimeinstall-x64.exe"
  #define WinAppRuntimeFileName "windowsappruntimeinstall-x64.exe"
#endif

#define DotNetPage "https://dotnet.microsoft.com/download/dotnet/10.0"
#define WinAppRuntimePage "https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads"

; 应用要求的 .NET 主版本
#define DotNetMajor "10."

; Setup.exe 的输出目录
#ifndef SourceDir
  #define SourceDir "..\artifacts"
#endif

; 应用本体的来源目录（dotnet publish 的框架依赖输出），与目标架构一一对应。
#ifndef PublishDir
  #if MyAppArch == "arm64"
    #define PublishDir "..\publish\win-arm64-fd"
  #else
    #define PublishDir "..\publish\win-x64-fd"
  #endif
#endif

[Setup]
; AppId 刻意与旧版 WPF 安装器保持一致：升级时覆盖同一安装位置，复用同一条卸载登记。
AppId={{E1F2A3C4-F5D4-3B2E-1D5A-8F7B2F8D3E7A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={userpf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
PrivilegesRequired=lowest
OutputDir={#SourceDir}
OutputBaseFilename={#MyAppName}_{#MyAppVersion}_Setup{#MyAppArchSuffix}
SetupIconFile=..\ExplorerTabUtility.App.WinUI\Assets\Icon.ico
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 每个安装包只放行它自己的架构：arm64 包若能装在 x64 机器上，用户拿到的是一个启动不了的 exe。
; x64 包放行 arm64 机器是有意的历史行为 —— ARM64 设备上的默认体验是跑 x64 版（模拟），
; 原生 arm64 包是额外的可选产物。
#if MyAppArch == "arm64"
ArchitecturesInstallIn64BitMode=arm64
ArchitecturesAllowed=arm64
#else
ArchitecturesInstallIn64BitMode=x64compatible arm64
ArchitecturesAllowed=x64compatible arm64
#endif
UninstallDisplayIcon={app}\{#MyAppRelativePath}
UninstallDisplayName={#MyAppName}
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
VersionInfoTextVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppNumericVersion}
; 按用户 UI 语言自动选择向导语言。
LanguageDetectionMethod=uilanguage

; 自定义文案按"一条消息 × 全部语言"分组，便于核对漏翻。
; 每一组都必须写满 [Languages] 里的所有语言：Inno 不会为缺失的语言回落，
; 运行时会直接显示成 {cm:...} 原文。
[CustomMessages]
english.AdditionalTasks=Additional options
chinesesimplified.AdditionalTasks=附加选项
japanese.AdditionalTasks=追加オプション
korean.AdditionalTasks=추가 옵션
german.AdditionalTasks=Zusätzliche Optionen
french.AdditionalTasks=Options supplémentaires
spanish.AdditionalTasks=Opciones adicionales
russian.AdditionalTasks=Дополнительные параметры

english.AutoStartTask=Start with Windows
chinesesimplified.AutoStartTask=开机自动启动
japanese.AutoStartTask=Windows の起動時に実行
korean.AutoStartTask=Windows 시작 시 실행
german.AutoStartTask=Beim Start ausführen
french.AutoStartTask=Lancer au démarrage
spanish.AutoStartTask=Iniciar con Windows
russian.AutoStartTask=Запускать при входе

english.NeedDotNet=This app needs the .NET 10 Desktop Runtime, which is not installed.%n%nDownload and install it now? (about 60 MB)%n%nChoose No to open the download page instead.
chinesesimplified.NeedDotNet=本应用需要 .NET 10 桌面运行时，当前系统未安装。%n%n现在下载并安装吗？（约 60 MB）%n%n选择「否」将打开官方下载页面。
japanese.NeedDotNet=このアプリには .NET 10 デスクトップ ランタイムが必要ですが、インストールされていません。%n%n今すぐダウンロードしてインストールしますか？（約 60 MB）%n%n「いいえ」を選ぶとダウンロード ページを開きます。
korean.NeedDotNet=이 앱에는 .NET 10 데스크톱 런타임이 필요하지만 설치되어 있지 않습니다.%n%n지금 다운로드하여 설치할까요? (약 60MB)%n%n아니요를 선택하면 다운로드 페이지가 열립니다.
german.NeedDotNet=Diese App benötigt die .NET 10 Desktop-Runtime, die nicht installiert ist.%n%nJetzt herunterladen und installieren? (ca. 60 MB)%n%nWähle „Nein“, um stattdessen die Downloadseite zu öffnen.
french.NeedDotNet=Cette application nécessite .NET 10 Desktop Runtime, qui n’est pas installé.%n%nLe télécharger et l’installer maintenant ? (environ 60 Mo)%n%nChoisissez « Non » pour ouvrir la page de téléchargement.
spanish.NeedDotNet=Esta aplicación necesita .NET 10 Desktop Runtime, que no está instalado.%n%n¿Quieres descargarlo e instalarlo ahora? (unos 60 MB)%n%nElige «No» para abrir la página de descarga.
russian.NeedDotNet=Для этого приложения требуется .NET 10 Desktop Runtime, который не установлен.%n%nСкачать и установить сейчас? (около 60 МБ)%n%nВыберите «Нет», чтобы открыть страницу загрузки.

english.NeedWinAppRuntime=This app needs the Windows App Runtime, which is not installed.%n%nDownload and install it now? (about 120 MB)%n%nChoose No to open the download page instead.
chinesesimplified.NeedWinAppRuntime=本应用需要 Windows App Runtime，当前系统未安装。%n%n现在下载并安装吗？（约 120 MB）%n%n选择「否」将打开官方下载页面。
japanese.NeedWinAppRuntime=このアプリには Windows App Runtime が必要ですが、インストールされていません。%n%n今すぐダウンロードしてインストールしますか？（約 120 MB）%n%n「いいえ」を選ぶとダウンロード ページを開きます。
korean.NeedWinAppRuntime=이 앱에는 Windows App Runtime이 필요하지만 설치되어 있지 않습니다.%n%n지금 다운로드하여 설치할까요? (약 120MB)%n%n아니요를 선택하면 다운로드 페이지가 열립니다.
german.NeedWinAppRuntime=Diese App benötigt die Windows App Runtime, die nicht installiert ist.%n%nJetzt herunterladen und installieren? (ca. 120 MB)%n%nWähle „Nein“, um stattdessen die Downloadseite zu öffnen.
french.NeedWinAppRuntime=Cette application nécessite Windows App Runtime, qui n’est pas installé.%n%nLe télécharger et l’installer maintenant ? (environ 120 Mo)%n%nChoisissez « Non » pour ouvrir la page de téléchargement.
spanish.NeedWinAppRuntime=Esta aplicación necesita Windows App Runtime, que no está instalado.%n%n¿Quieres descargarlo e instalarlo ahora? (unos 120 MB)%n%nElige «No» para abrir la página de descarga.
russian.NeedWinAppRuntime=Для этого приложения требуется Windows App Runtime, который не установлен.%n%nСкачать и установить сейчас? (около 120 МБ)%n%nВыберите «Нет», чтобы открыть страницу загрузки.

english.Downloading=Downloading required runtimes...
chinesesimplified.Downloading=正在下载所需的运行时...
japanese.Downloading=必要なランタイムをダウンロードしています...
korean.Downloading=필요한 런타임을 다운로드하는 중...
german.Downloading=Erforderliche Runtimes werden heruntergeladen...
french.Downloading=Téléchargement des runtimes requis...
spanish.Downloading=Descargando los runtimes necesarios...
russian.Downloading=Загрузка необходимых компонентов...

english.RuntimeFailed=Failed to install a required runtime:%n%n%1%n%nPlease install it manually from:%n%2
chinesesimplified.RuntimeFailed=所需的运行时安装失败：%n%n%1%n%n请手动从以下地址安装：%n%2
japanese.RuntimeFailed=必要なランタイムをインストールできませんでした：%n%n%1%n%n以下から手動でインストールしてください：%n%2
korean.RuntimeFailed=필요한 런타임을 설치하지 못했습니다:%n%n%1%n%n다음에서 직접 설치하세요:%n%2
german.RuntimeFailed=Eine erforderliche Runtime konnte nicht installiert werden:%n%n%1%n%nBitte manuell installieren von:%n%2
french.RuntimeFailed=Échec de l’installation d’un runtime requis :%n%n%1%n%nVeuillez l’installer manuellement depuis :%n%2
spanish.RuntimeFailed=No se pudo instalar un runtime necesario:%n%n%1%n%nInstálalo manualmente desde:%n%2
russian.RuntimeFailed=Не удалось установить необходимый компонент:%n%n%1%n%nУстановите его вручную со страницы:%n%2

english.RemoveSettings=Also delete your user settings?
chinesesimplified.RemoveSettings=是否同时删除用户配置？
japanese.RemoveSettings=ユーザー設定も削除しますか？
korean.RemoveSettings=사용자 설정도 삭제할까요?
german.RemoveSettings=Benutzereinstellungen ebenfalls löschen?
french.RemoveSettings=Supprimer également vos paramètres utilisateur ?
spanish.RemoveSettings=¿Eliminar también la configuración de usuario?
russian.RemoveSettings=Также удалить пользовательские настройки?

; 向导界面本身由 Inno 自带的 .isl 提供，因此这里只需列出即可。
; 顺序与应用内的语言选择器一致（常用的在前），唯一例外是 english 必须放第一条：
; Inno 的 [Languages] 不支持 Flags，默认语言永远是列表里的第一条，而未识别到
; 用户界面语言时需要回落到英文而不是列表里的第一个（中文）。
; 注意：Inno 6.7 没有随附繁体中文的 .isl（只有简体），所以繁體中文用户看到的
; 是英文向导 —— 这是本安装器唯一不支持的语言，应用界面本身仍完整支持繁中。
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:AdditionalTasks}"; Flags: unchecked

[Files]
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
; 应用本体直接铺开（不再走 zip + PowerShell 解压）：[Files] 早于 [Icons] 执行，
; 建快捷方式时目标 exe 已存在，图标才能被正确解析并缓存。
Source: "{#PublishDir}\*"; DestDir: "{app}\{#MyAppName}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppRelativePath}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppRelativePath}"; Tasks: desktopicon

[Registry]
; 自启项写入 --startup 标记，应用据此识别「由系统登录项拉起」。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppRelativePath}"" --startup"; Flags: uninsdeletevalue; Tasks: startupicon
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: binary; ValueName: "{#MyAppName}"; ValueData: 02 00 00 00 00 00 00 00 00 00 00 00; Flags: uninsdeletevalue; Tasks: startupicon

[InstallDelete]
Type: filesandordirs; Name: "{app}\*"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\*"
Type: dirifempty; Name: "{app}"
; 开始菜单组目录：Inno 只删它自己创建过的快捷方式，历史版本留下的同名目录会被跳过
Type: filesandordirs; Name: "{userprograms}\{#MyAppName}"

[Run]
Filename: "{app}\{#MyAppRelativePath}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  SHCNE_ASSOCCHANGED = $08000000;
  SHCNF_IDLIST = $0000;

// 通知资源管理器图标关联已变化。装完后调一次，可以让"这次安装之前"就已经存在的
// 快捷方式（例如旧版本留下的白板图标）重新解析，而不必等用户手动刷新图标缓存。
procedure SHChangeNotify(wEventId: Longint; uFlags: LongWord; dwItem1, dwItem2: Cardinal);
  external 'SHChangeNotify@shell32.dll stdcall';

var
  DownloadPage: TDownloadWizardPage;
  MissingDotNet: Boolean;
  MissingWinAppRuntime: Boolean;

//------------------------------------------------------------------------------
// 运行时检测
//------------------------------------------------------------------------------

// .NET 的共享框架位于 dotnet\shared\Microsoft.WindowsDesktop.App\<版本> 目录下。
// 注意：注册表那条路径里版本号是「值名」而非「子键名」，而脚本环境没有枚举值名的函数，
// 首次实现误用 RegGetSubkeyNames 导致恒为 False（会白白重装一次运行时），故改用目录扫描。
function IsDotNetDesktopInstalled(const MajorPrefix: String): Boolean;
var
  FindRec: TFindRec;
  SearchPath: String;
begin
  Result := False;
  SearchPath := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');

  if not FindFirst(SearchPath + '\' + MajorPrefix + '*', FindRec) then
    Exit;

  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Log('found .NET desktop runtime: ' + FindRec.Name);
        Result := True;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

// Windows App Runtime 以 MSIX 框架包形式按用户注册 
function IsWindowsAppRuntimeInstalled: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;

  if not RegGetSubkeyNames(HKCU,
       'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages',
       Names) then
    Exit;

  for I := 0 to GetArrayLength(Names) - 1 do
    if Pos('Microsoft.WindowsAppRuntime.2_', Names[I]) = 1 then
    begin
      Result := True;
      Exit;
    end;
end;

//------------------------------------------------------------------------------
// 下载并静默安装
//------------------------------------------------------------------------------

function InstallRuntime(const Url, FileName, Page, NeedMessage: String): Boolean;
var
  ResultCode: Integer;
  FilePath: String;
begin
  Result := False;

  // 静默安装时 SuppressibleMsgBox 返回默认值（是），因此也会自动下载 
  if SuppressibleMsgBox(NeedMessage, mbConfirmation, MB_YESNO, IDYES) <> IDYES then
  begin
    // 用户选择自行处理：打开官方下载页 
    ShellExec('open', Page, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    Exit;
  end;

  DownloadPage.Clear;
  DownloadPage.Add(Url, FileName, '');
  DownloadPage.SetText(CustomMessage('Downloading'), '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      FilePath := ExpandConstant('{tmp}\') + FileName;
      if not FileExists(FilePath) then Exit;

      // /quiet /norestart 对 .NET 与 Windows App Runtime 的官方安装器都适用 
      Result := Exec(FilePath, '/quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode)
                and (ResultCode = 0);
    except
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure InstallMissingRuntimes;
begin
  if MissingDotNet then
  begin
    if not InstallRuntime('{#DotNetUrl}', '{#DotNetFileName}', '{#DotNetPage}',
                          CustomMessage('NeedDotNet')) then
      SuppressibleMsgBox(
        FmtMessage(CustomMessage('RuntimeFailed'), ['.NET 10 Desktop Runtime', '{#DotNetPage}']),
        mbError, MB_OK, MB_OK);
  end;

  if MissingWinAppRuntime then
  begin
    if not InstallRuntime('{#WinAppRuntimeUrl}', '{#WinAppRuntimeFileName}', '{#WinAppRuntimePage}',
                          CustomMessage('NeedWinAppRuntime')) then
      SuppressibleMsgBox(
        FmtMessage(CustomMessage('RuntimeFailed'), ['Windows App Runtime', '{#WinAppRuntimePage}']),
        mbError, MB_OK, MB_OK);
  end;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(CustomMessage('Downloading'),
                                     CustomMessage('Downloading'), @OnDownloadProgress);
end;

function InitializeSetup: Boolean;
begin
  // 先探测；真正的下载放到向导的最后一步，那时下载页才可用 
  MissingDotNet := not IsDotNetDesktopInstalled('{#DotNetMajor}');
  MissingWinAppRuntime := not IsWindowsAppRuntimeInstalled;

  Log(Format('runtime check: dotnet-missing=%d, winappruntime-missing=%d', [Ord(MissingDotNet), Ord(MissingWinAppRuntime)]));

  Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  // 安装开始前补齐运行时——应用首次启动就必须找到它们 
  if (CurPageID = wpReady) and (MissingDotNet or MissingWinAppRuntime) then
    InstallMissingRuntimes;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // 装完文件、建完快捷方式之后再刷新一次图标缓存，确保桌面/开始菜单的图标立即正确。
  if CurStep = ssPostInstall then
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SettingsDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // 个人设置不在 {app} 下，卸载器默认不会碰它。
    // 这里主动询问，默认「保留」（静默卸载也走默认值），需要时选「是」彻底删除。
    SettingsDir := ExpandConstant('{userappdata}\{#MyAppName}');
    if DirExists(SettingsDir) then
    begin
      if SuppressibleMsgBox(CustomMessage('RemoveSettings'),
                            mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      begin
        DelTree(SettingsDir, True, True, True);
        Log('removed user settings: ' + SettingsDir);
      end
      else
        Log('kept user settings: ' + SettingsDir);
    end;
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    // 无论自启项当初是怎么写入的，卸载时一律清除
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#MyAppName}');
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', '{#MyAppName}');
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  // 卸载前先结束正在运行的实例，否则文件被占用删不掉 
  Exec('taskkill.exe', '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
