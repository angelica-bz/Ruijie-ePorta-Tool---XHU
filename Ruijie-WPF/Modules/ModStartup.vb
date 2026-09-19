Imports System.IO
Imports System.Diagnostics
Imports System.Text
Imports System.Security.Principal
Imports Microsoft.Win32
Imports Microsoft.VisualBasic

''' <summary>
''' 开机自启 —— 生产实现。**唯一的自启动入口**，GUI 与命令行都走这里。
'''
''' 生产机制：Task Scheduler LogonTrigger
'''     Windows 登录 -> LogonTrigger(UserId=当前用户, Delay=PT1S)
'''                  -> "Ruijie ePorta Tool.exe" --background
'''
''' 为什么换掉注册表 Run 键（实测数据）：
'''     HKCU\Run 与启动文件夹都由 Explorer 在登录后**错峰**启动，
'''     实测 Login -> Process ≈ 67.5 s（历史中位数 56.9 s，n=6）。
'''     计划任务由 Task Scheduler 服务（svchost）触发，绕开该错峰，
'''     实测 Login -> Process ≈ 15.9 s。同一次开机里，仍走 Run 键的
'''     TrafficMonitor 被延迟到 +47 s，本程序在 +16 s，因果明确。
'''
''' 安全模型（实测澄清，勿沿用旧结论）：
'''     非提权进程用 `schtasks /create /xml` 注册「当前用户 + InteractiveToken +
'''     LeastPrivilege」的登录任务**可以成功**。
'''     但 COM 的 RegisterTaskDefinition 在非提权下返回 0x80070005，
'''     所以本模块一律走 schtasks + XML，不走 COM。
'''     即便如此仍保留 UAC 回退：注册真的失败时提权重试一次，
'''     而不是把失败直接抛给用户。
'''
''' 任务命名带用户 SID，多用户环境各自独立（参考 G-Helper 的做法）。
''' </summary>
Public Module ModStartup

#Region "常量"

    ''' <summary>计划任务显示名（不含 SID 的部分）。</summary>
    Public Const TaskBaseName As String = "Ruijie ePorta Tool"

    ''' <summary>登录触发后延迟多久再启动。</summary>
    Private Const LogonDelay As String = "PT1S"

    ''' <summary>旧版自启动位置：注册表 Run 键。</summary>
    Private Const RunKeyName As String = "Ruijie ePorta Tool"
    Private Const RunKeyPath As String = "Software\Microsoft\Windows\CurrentVersion\Run"

    ''' <summary>历史遗留：启动文件夹快捷方式（旧版本可能留下）。</summary>
    Private Const LegacyShortcutName As String = "Ruijie_ePorta_Tool.lnk"

#End Region

#Region "状态类型"

    ''' <summary>自启动任务的状态。</summary>
    Public Enum StartupState
        ''' <summary>任务不存在。</summary>
        NotInstalled = 0
        ''' <summary>任务存在且配置正确，指向当前 exe。</summary>
        Installed = 1
        ''' <summary>任务存在但指向别的路径（程序被移动 / 改名 / 换了版本目录）。</summary>
        Outdated = 2
        ''' <summary>任务存在但关键配置不对（触发器 / Principal / 参数不符合预期）。</summary>
        Invalid = 3
    End Enum

    ''' <summary>启用自启动的结果。</summary>
    Public Enum EnableResult
        ''' <summary>已是正确状态，无需改动。</summary>
        AlreadyOk = 0
        ''' <summary>本轮注册成功（未提权）。</summary>
        Registered = 1
        ''' <summary>非提权注册失败，已拉起提权进程去完成，GUI 应提示用户。</summary>
        ElevationPending = 2
        ''' <summary>注册失败，已回退到 Run 键，用户仍有可用的自启动。</summary>
        FallbackToRun = 3
        ''' <summary>彻底失败（连回退都不可用）。</summary>
        Failed = 4
    End Enum

#End Region

#Region "身份与命名"

    Public Function CurrentUserSid() As String
        Try
            Return WindowsIdentity.GetCurrent().User.Value
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>当前用户账户名（DOMAIN\User）。</summary>
    Public Function CurrentUserName() As String
        Try
            Return WindowsIdentity.GetCurrent().Name
        Catch
            Return Environment.UserName
        End Try
    End Function

    ''' <summary>本用户专属的任务名；多用户环境各自独立，不会互相覆盖。</summary>
    Public Function TaskName() As String
        Dim Sid As String = CurrentUserSid()
        If String.IsNullOrEmpty(Sid) Then Return TaskBaseName
        Return TaskBaseName & " (" & Sid & ")"
    End Function

    ''' <summary>是否以管理员身份（提权令牌）运行。</summary>
    Public Function IsElevated() As Boolean
        Try
            Return New WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)
        Catch
            Return False
        End Try
    End Function

#End Region

#Region "schtasks 调用"

    ''' <summary>
    ''' 调 schtasks，返回 (输出, 退出码)。
    ''' 判据一律用**退出码**，不解析输出文本 —— 输出是本地化且按控制台码页编码的
    ''' （中文系统 GBK），字符串匹配既不可靠也不可移植。
    ''' </summary>
    Private Function RunSchtasks(Args As String) As (Out As String, Code As Integer)
        Try
            Using Proc As New Process()
                Proc.StartInfo = New ProcessStartInfo With {
                    .FileName = "schtasks",
                    .Arguments = Args,
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True
                }
                Proc.Start()
                Dim Out As String = Proc.StandardOutput.ReadToEnd()
                Dim Err As String = Proc.StandardError.ReadToEnd()
                Proc.WaitForExit(15000)
                Dim Code As Integer = -1
                Try
                    Code = Proc.ExitCode
                Catch
                End Try
                Return (Out & If(String.IsNullOrWhiteSpace(Err), "", vbCrLf & Err), Code)
            End Using
        Catch ex As Exception
            Return ("EXCEPTION: " & ex.Message, -1)
        End Try
    End Function

#End Region

#Region "任务 XML"

    Private Function Esc(S As String) As String
        If S Is Nothing Then Return ""
        Return System.Security.SecurityElement.Escape(S)
    End Function

    ''' <summary>
    ''' 生成任务定义 XML。关键设计（对齐 G-Helper 的成熟做法）：
    '''     LogonTrigger + UserId = 当前用户 + Delay = 1 秒
    '''     Principal    + UserId = 当前用户 + InteractiveToken + LeastPrivilege
    '''   LeastPrivilege 保证**运行时不再弹 UAC**；
    '''   InteractiveToken 保证程序跑在当前用户的交互会话里（托盘图标可见）。
    ''' </summary>
    Public Function BuildTaskXml() As String
        Dim User As String = CurrentUserName()
        Dim Exe As String = PathExe
        Dim WorkDir As String = PathExeFolder
        Dim Sb As New StringBuilder()
        Sb.AppendLine("<?xml version=""1.0"" encoding=""UTF-16""?>")
        Sb.AppendLine("<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">")
        Sb.AppendLine("  <RegistrationInfo>")
        Sb.AppendLine("    <Description>Ruijie ePorta Tool - " & Esc(User) & " - logon autostart</Description>")
        Sb.AppendLine("  </RegistrationInfo>")
        Sb.AppendLine("  <Triggers>")
        Sb.AppendLine("    <LogonTrigger>")
        Sb.AppendLine("      <Enabled>true</Enabled>")
        Sb.AppendLine("      <UserId>" & Esc(User) & "</UserId>")
        Sb.AppendLine("      <Delay>" & LogonDelay & "</Delay>")
        Sb.AppendLine("    </LogonTrigger>")
        Sb.AppendLine("  </Triggers>")
        Sb.AppendLine("  <Principals>")
        Sb.AppendLine("    <Principal id=""Author"">")
        Sb.AppendLine("      <UserId>" & Esc(User) & "</UserId>")
        Sb.AppendLine("      <LogonType>InteractiveToken</LogonType>")
        Sb.AppendLine("      <RunLevel>LeastPrivilege</RunLevel>")
        Sb.AppendLine("    </Principal>")
        Sb.AppendLine("  </Principals>")
        Sb.AppendLine("  <Settings>")
        ' 同一任务重复触发不再起第二个（EXE 级保护仍由 ModSingleInstance 的 Mutex 负责）
        Sb.AppendLine("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>")
        Sb.AppendLine("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>")
        Sb.AppendLine("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>")
        Sb.AppendLine("    <AllowHardTerminate>true</AllowHardTerminate>")
        Sb.AppendLine("    <AllowStartOnDemand>true</AllowStartOnDemand>")
        Sb.AppendLine("    <Enabled>true</Enabled>")
        Sb.AppendLine("    <Hidden>false</Hidden>")
        Sb.AppendLine("    <RunOnlyIfIdle>false</RunOnlyIfIdle>")
        Sb.AppendLine("    <WakeToRun>false</WakeToRun>")
        ' PT0S = 不限时长：托盘常驻程序不能被计划任务到点杀掉
        Sb.AppendLine("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>")
        Sb.AppendLine("    <Priority>7</Priority>")
        Sb.AppendLine("  </Settings>")
        Sb.AppendLine("  <Actions Context=""Author"">")
        Sb.AppendLine("    <Exec>")
        Sb.AppendLine("      <Command>""" & Esc(Exe) & """</Command>")
        Sb.AppendLine("      <Arguments>--background</Arguments>")
        Sb.AppendLine("      <WorkingDirectory>" & Esc(WorkDir) & "</WorkingDirectory>")
        Sb.AppendLine("    </Exec>")
        Sb.AppendLine("  </Actions>")
        Sb.AppendLine("</Task>")
        Return Sb.ToString()
    End Function

    Private Function WriteTaskXmlFile() As String
        Dim XmlPath As String = Path.Combine(Path.GetTempPath(), "ruijie_startup_task.xml")
        IO.File.WriteAllText(XmlPath, BuildTaskXml(), Encoding.Unicode)
        Return XmlPath
    End Function

#End Region

#Region "任务状态检测"

    ''' <summary>任务是否存在于 Task Scheduler。</summary>
    Public Function IsTaskRegistered() As Boolean
        Return RunSchtasks("/query /tn """ & TaskName() & """").Code = 0
    End Function

    ''' <summary>取任务 XML 原文（用于逐项校验）；失败返回空串。</summary>
    Private Function GetTaskXml() As String
        Dim R = RunSchtasks("/query /tn """ & TaskName() & """ /xml")
        If R.Code <> 0 Then Return ""
        Return R.Out
    End Function

    ''' <summary>
    ''' 检测任务状态。逐项校验 Action / Principal / Trigger 是否符合预期，
    ''' 而不只是「存在与否」—— 程序被移动、被改名、任务被手工改坏都要能识别。
    '''
    ''' 注意：Task Scheduler 会**省略等于默认值的元素**。实测注册后落库的 XML 里
    ''' 没有 &lt;RunLevel&gt; 元素，因为 LeastPrivilege 正是默认值。
    ''' 因此这里不能要求某个元素「必须出现」，而要检查「不能是错的值」。
    ''' </summary>
    Public Function GetState() As StartupState
        If Not IsTaskRegistered() Then Return StartupState.NotInstalled

        Dim Xml As String = GetTaskXml()
        If String.IsNullOrEmpty(Xml) Then Return StartupState.Invalid

        ' exe 路径：程序被移动 / 改名 / 换目录后这里会不匹配
        If Xml.IndexOf(PathExe, StringComparison.OrdinalIgnoreCase) < 0 Then
            Return StartupState.Outdated
        End If

        ' 必须存在的元素
        Dim MustExist As String() = {
            "<LogonTrigger>",
            "<Delay>" & LogonDelay & "</Delay>",
            "<LogonType>InteractiveToken</LogonType>",
            "<Arguments>--background</Arguments>",
            "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>",
            PathExeFolder.TrimEnd("\"c)
        }
        For Each S In MustExist
            If Xml.IndexOf(S, StringComparison.OrdinalIgnoreCase) < 0 Then Return StartupState.Invalid
        Next

        ' 必须**不**出现的元素（出现了就说明被改坏 / 需要提权 / 会弹 UAC）
        Dim MustNotExist As String() = {
            "<RunLevel>HighestAvailable</RunLevel>",
            "<LogonType>Password</LogonType>",
            "<LogonType>S4U</LogonType>",
            "<LogonType>ServiceAccount</LogonType>"
        }
        For Each S In MustNotExist
            If Xml.IndexOf(S, StringComparison.OrdinalIgnoreCase) >= 0 Then Return StartupState.Invalid
        Next

        Return StartupState.Installed
    End Function

    ''' <summary>任务是否处于可用的正确状态。</summary>
    Public Function IsTaskReady() As Boolean
        Return GetState() = StartupState.Installed
    End Function

    ''' <summary>取任务详情（诊断用）。</summary>
    Public Function QueryTaskDetail() As String
        Return RunSchtasks("/query /tn """ & TaskName() & """ /fo LIST /v").Out
    End Function

#End Region

#Region "注册 / 删除"

    ''' <summary>用 schtasks + XML 注册（或覆盖更新）本用户的登录任务。</summary>
    Private Function RegisterTask() As Boolean
        Try
            Dim XmlPath As String = WriteTaskXmlFile()
            Dim R = RunSchtasks("/create /tn """ & TaskName() & """ /xml """ & XmlPath & """ /f")
            If R.Code = 0 Then Return True
            Log("[Startup] 注册任务失败(exit=" & R.Code & "): " & R.Out.Trim())
            Return False
        Catch ex As Exception
            Log(ex, "[Startup] 注册任务异常")
            Return False
        End Try
    End Function

    ''' <summary>删除本用户的登录任务。</summary>
    Public Function RemoveTask() As Boolean
        If Not IsTaskRegistered() Then Return True
        Return RunSchtasks("/delete /tn """ & TaskName() & """ /f").Code = 0
    End Function

    ''' <summary>
    ''' 以管理员身份重启本程序并执行指定参数 —— 即触发**一次** UAC。
    ''' 用户点「否」时返回 False。
    ''' </summary>
    Public Function RelaunchElevated(Args As String) As Boolean
        Try
            Process.Start(New ProcessStartInfo With {
                .FileName = PathExe,
                .Arguments = Args,
                .UseShellExecute = True,
                .Verb = "runas",
                .WorkingDirectory = PathExeFolder
            })
            Return True
        Catch ex As Exception
            Log(ex, "[Startup] 提权被取消或失败")
            Return False
        End Try
    End Function

#End Region

#Region "旧 Run 键 / 历史遗留"

    ''' <summary>旧版自启动（HKCU\Run）当前是否存在。</summary>
    Public Function IsRunKeyPresent() As Boolean
        Try
            Using Key = Registry.CurrentUser.OpenSubKey(RunKeyPath, False)
                If Key IsNot Nothing Then Return Key.GetValue(RunKeyName) IsNot Nothing
            End Using
        Catch ex As Exception
            Log(ex, "查询 Run 键失败")
        End Try
        Return False
    End Function

    ''' <summary>写入 / 删除旧版 Run 键（仅用于回退与迁移）。</summary>
    Private Function SetRunKey(Enable As Boolean) As Boolean
        Try
            Using Key = Registry.CurrentUser.OpenSubKey(RunKeyPath, True)
                If Key Is Nothing Then Return False
                If Enable Then
                    Key.SetValue(RunKeyName, """" & PathExe & """ --background")
                Else
                    Key.DeleteValue(RunKeyName, False)
                End If
                Return True
            End Using
        Catch ex As Exception
            Log(ex, "设置 Run 键失败")
            Return False
        End Try
    End Function

    Private Sub DeleteRunKey()
        SetRunKey(False)
    End Sub

    Private Sub CleanupLegacyShortcut()
        Try
            Dim P As String = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft\Windows\Start Menu\Programs\Startup\" & LegacyShortcutName)
            If File.Exists(P) Then File.Delete(P)
        Catch
        End Try
    End Sub

#End Region

#Region "对 GUI 的统一接口"

    ''' <summary>
    ''' 开机自启是否已生效。
    ''' 以**实际任务状态**为准；旧 Run 键存在（回退态）也算已启用。
    ''' </summary>
    Public Function IsEnabled() As Boolean
        If IsTaskReady() Then Return True
        Return IsRunKeyPresent()
    End Function

    ''' <summary>
    ''' 启用开机自启。流程（对应设计要求）：
    '''     检查任务 -> 不存在则创建 / 路径不对则更新 / 已正确则保持
    '''     -> 验证任务可用 -> 删除旧 Run 键
    ''' 非提权注册失败时拉起一次 UAC；再失败则**回退到旧 Run 键**，
    ''' 保证用户始终有一种可用的自启动方式。
    ''' </summary>
    Public Function Enable() As EnableResult
        CleanupLegacyShortcut()

        ' 已经是正确状态：只做迁移收尾（清掉可能残留的 Run 键）
        If IsTaskReady() Then
            If IsRunKeyPresent() Then DeleteRunKey()
            Return EnableResult.AlreadyOk
        End If

        ' 创建或更新
        If RegisterTask() AndAlso IsTaskReady() Then
            If IsRunKeyPresent() Then DeleteRunKey()
            Return EnableResult.Registered
        End If

        ' 非提权失败 -> 提权回退（仅一次）
        If Not IsElevated() Then
            If RelaunchElevated("--apply-startup") Then
                Return EnableResult.ElevationPending
            End If
        End If

        ' 两条路都不通 -> 保证用户仍有可用的自启动方式
        If SetRunKey(True) Then
            Log("[Startup] 任务注册失败，已回退到 Run 键")
            Return EnableResult.FallbackToRun
        End If
        Return EnableResult.Failed
    End Function

    ''' <summary>关闭开机自启：任务与旧 Run 键一并删除。</summary>
    Public Function Disable() As Boolean
        Dim Ok As Boolean = RemoveTask()

        If Not Ok AndAlso Not IsElevated() Then
            ' 删除失败也可能需要提权
            If RelaunchElevated("--apply-startup-off") Then Return True
        End If

        DeleteRunKey()
        CleanupLegacyShortcut()
        Return Not IsTaskRegistered()
    End Function

    ''' <summary>
    ''' 命令行 / 提权进程用的一次性应用入口。
    ''' 返回进程退出码：0 成功 / 1 失败。
    ''' </summary>
    Public Function ApplyOnce(Enable As Boolean) As Integer
        If Enable Then
            If RegisterTask() AndAlso IsTaskReady() Then
                DeleteRunKey()
                CleanupLegacyShortcut()
                Return 0
            End If
            ' 提权后仍失败：保住回退路径
            SetRunKey(True)
            Return 1
        Else
            If Not RemoveTask() Then Return 1
            DeleteRunKey()
            CleanupLegacyShortcut()
            Return 0
        End If
    End Function

#End Region

End Module
