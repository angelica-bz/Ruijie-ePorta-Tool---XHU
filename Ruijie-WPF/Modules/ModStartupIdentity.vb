Imports System.IO
Imports System.Diagnostics
Imports System.Text
Imports System.Runtime.InteropServices
Imports Microsoft.VisualBasic

''' <summary>
''' 启动身份日志 + GUI 越界检测。
'''
''' 为什么需要它：
'''     曾出现「任务计划以 --background 启动，但进程里存在完整主窗口、
'''     D3D 已加载、Private 147 MB」的现象，而应用日志中没有任何激活记录。
'''     当时的排查是在几分钟后回头查进程，PID 早已变化，证据链断了。
'''     所以要**在启动的第一时间**把身份写死到日志里，并在任何创建主窗口的
'''     位置留下带调用栈的记录 —— 否则下一次仍然只能靠猜。
'''
''' 本模块只做记录，不参与任何业务判定，也不改变启动行为。
''' 注意：绝不记录密码或配置内容。
''' </summary>
Public Module ModStartupIdentity

    Private _Logged As Boolean = False

    ''' <summary>进程级启动身份的日志前缀，便于事后 grep。</summary>
    Public Const Prefix As String = "[StartupIdentity]"

    ''' <summary>后台模式下创建了主窗口时使用的标记。</summary>
    Public Const ViolationPrefix As String = "[StartupViolation]"

#Region "早期身份记录"

    ''' <summary>
    ''' 在启动的最早可执行位置调用一次，记录进程身份与命令行。
    ''' 写入每日日志（TraceLifecycle），因此重启后可直接复查。
    ''' </summary>
    Public Sub LogOnce()
        If _Logged Then Return
        _Logged = True

        Try
            Dim Args As String() = Environment.GetCommandLineArgs()
            ' 第 0 项是 exe 自身路径，其余才是参数
            Dim Passed As New List(Of String)
            For I As Integer = 1 To Args.Length - 1
                Passed.Add(Args(I))
            Next

            Dim HasBg As Boolean = False
            For Each A In Passed
                If String.Equals(A, "--background", StringComparison.Ordinal) Then HasBg = True
            Next

            Dim Sb As New StringBuilder()
            Sb.AppendLine(Prefix)
            Sb.AppendLine("  PID            = " & Process.GetCurrentProcess().Id.ToString())
            Sb.AppendLine("  Executable     = " & PathExe)
            Sb.AppendLine("  Arguments      = " & If(Passed.Count = 0, "(none)", String.Join(" ", Passed.ToArray())))
            Sb.AppendLine("  HasBackgroundArgument = " & HasBg.ToString())
            Sb.AppendLine("  SessionId      = " & Process.GetCurrentProcess().SessionId.ToString())
            Sb.AppendLine("  User           = " & SafeUserName())
            Sb.AppendLine("  Parent         = " & DescribeParent())
            Sb.AppendLine("  StartedAt      = " & SafeStartTime())
            Sb.AppendLine("  IsElevated     = " & SafeElevated().ToString())

            TraceLifecycle(Sb.ToString())
        Catch ex As Exception
            Log(ex, "[StartupIdentity] 记录启动身份失败")
        End Try
    End Sub

    Private Function SafeUserName() As String
        Try
            Return System.Security.Principal.WindowsIdentity.GetCurrent().Name
        Catch
            Return Environment.UserName
        End Try
    End Function

    Private Function SafeStartTime() As String
        Try
            Return Process.GetCurrentProcess().StartTime.ToString("yyyy-MM-dd HH:mm:ss.fff")
        Catch
            Return "(unavailable)"
        End Try
    End Function

    Private Function SafeElevated() As Boolean
        Try
            Return New System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(
                System.Security.Principal.WindowsBuiltInRole.Administrator)
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 描述父进程。任务计划启动时父进程应为 svchost.exe（Task Scheduler 服务），
    ''' 手动启动时通常是 explorer.exe 或某个终端 —— 据此可判断启动来源。
    '''
    ''' 实现说明：本项目**零外部依赖**（不引用 System.Management），
    ''' 因此用 NtQueryInformationProcess 取父 PID，再按 PID 找进程名。
    ''' </summary>
    Private Declare Function NtQueryInformationProcess Lib "ntdll.dll" (
        ProcessHandle As IntPtr, ProcessInformationClass As Integer,
        ByRef ProcessInformation As PROCESS_BASIC_INFORMATION,
        ProcessInformationLength As Integer, ByRef ReturnLength As Integer) As Integer

    Private Structure PROCESS_BASIC_INFORMATION
        Public Reserved1 As IntPtr
        Public PebBaseAddress As IntPtr
        Public Reserved2_0 As IntPtr
        Public Reserved2_1 As IntPtr
        Public UniqueProcessId As IntPtr
        Public InheritedFromUniqueProcessId As IntPtr
    End Structure

    Private Function DescribeParent() As String
        Try
            Dim Me_ As Process = Process.GetCurrentProcess()
            Dim Info As PROCESS_BASIC_INFORMATION
            Dim Ret As Integer
            Dim Rc As Integer = NtQueryInformationProcess(Me_.Handle, 0, Info, Marshal.SizeOf(Info), Ret)
            If Rc <> 0 Then Return "(unknown)"
            Dim Ppid As Integer = Info.InheritedFromUniqueProcessId.ToInt32()
            If Ppid <= 0 Then Return "(unknown)"
            Try
                Using Par As Process = Process.GetProcessById(Ppid)
                    Return Ppid.ToString() & " / " & Par.ProcessName & ".exe"
                End Using
            Catch
                Return Ppid.ToString() & " / (exited)"
            End Try
        Catch
        End Try
        Return "(unknown)"
    End Function

#End Region

#Region "GUI 越界检测"

    ''' <summary>
    ''' 主窗口创建时调用。记录是谁、在什么模式下创建的。
    ''' 若「后台模式」下出现创建，额外输出 StartupViolation 与调用栈。
    ''' </summary>
    Public Sub LogMainWindowCreation(BackgroundMode As Boolean, Caller As String)
        Try
            Dim Sb As New StringBuilder()
            Sb.AppendLine("[StartupMode] 创建主窗口")
            Sb.AppendLine("  StartupMode           = " & If(BackgroundMode, "Background", "Normal"))
            Sb.AppendLine("  HasBackgroundArgument = " & BackgroundMode.ToString())
            Sb.AppendLine("  FormMainCreated       = True")
            Sb.AppendLine("  Caller                = " & Caller)
            Sb.AppendLine("  PID                   = " & Process.GetCurrentProcess().Id.ToString())

            If BackgroundMode Then
                ' 后台模式下创建窗口 = 一定走了「激活」路径（用户点托盘 / 第二实例 / 诊断入口），
                ' 但如果**没有任何激活来源**就创建了，那就是缺陷，必须留下调用栈。
                Sb.AppendLine(ViolationPrefix)
                Sb.AppendLine("  StartupMode   = Background")
                Sb.AppendLine("  FormMainCreated = True")
                Sb.AppendLine("  Note          = 后台启动本不应创建主窗口；请核对下方调用栈确认来源")
                Sb.AppendLine("  StackTrace    =")
                For Each Line In FilteredStack()
                    Sb.AppendLine("    " & Line)
                Next
            End If

            TraceLifecycle(Sb.ToString())
        Catch ex As Exception
            Log(ex, "[StartupIdentity] 记录主窗口创建失败")
        End Try
    End Sub

    ''' <summary>只保留本程序自身的帧，去掉冗长的框架栈。</summary>
    Private Function FilteredStack() As String()
        Dim Out As New List(Of String)
        Try
            Dim Raw As String() = Environment.StackTrace.Split(New String() {vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)
            For Each L In Raw
                Dim T As String = L.Trim()
                If T.IndexOf("Ruijie", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    Out.Add(T)
                End If
                If Out.Count >= 12 Then
                    Exit For
                End If
            Next
        Catch
        End Try
        If Out.Count = 0 Then Out.Add("(无法获取调用栈)")
        Return Out.ToArray()
    End Function

#End Region

End Module
