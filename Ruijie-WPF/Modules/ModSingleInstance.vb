Imports System.Threading
Imports System.Security.Principal
Imports Microsoft.VisualBasic

''' <summary>
''' 单实例保护 + 已有实例激活。
'''
''' 背景：本项目此前**没有任何单实例机制**。实测「任务实例在跑时手动双击 EXE」
''' 会直接产生第 2、第 3 个进程，每个约 78 MB，并且多个实例会同时抢占
''' 同一个校园网会话（这正是验收命令 --accept 出现「Portal Redirect 假失败」的根因）。
''' 计划任务的 MultipleInstancesPolicy=IgnoreNew 只约束**同一个 Task 的重复触发**，
''' 它不是 EXE 级保护，挡不住用户双击。
'''
''' 机制（两个命名内核对象，各自职责单一）：
'''   1. Local\ 命名 Mutex —— 判唯一。用 Local\ 而非 Global\ 是刻意的：
'''      本程序是「每用户一个托盘实例」，配置（DPAPI 密文）本身就绑定当前 Windows 用户，
'''      跨用户共享实例没有意义，也不该互相干扰。
'''   2. Local\ 命名 EventWaitHandle —— 传激活信号。后启动的实例 Set() 一下就退出；
'''      主实例有一个后台线程 WaitOne() 等着，收到后 marshal 到 UI 线程显示主窗口。
'''
''' 为什么不用窗口消息（实测踩坑记录，勿重走）：
'''      最初用 RegisterWindowMessage + PostMessage 通知。两个坑：
'''        a) PostMessage(HWND_BROADCAST) 返回 True，但 1x1 隐藏顶层窗口收不到广播；
'''        b) 改用 FindWindow(标题) 精确定位时返回 0x0 —— 窗口确实存在
'''           （EnumWindows 能看到标题完全一致的窗口），但跨进程就是找不到。
'''      命名事件没有这些问题：不依赖窗口、不依赖消息泵、不需要任何 P/Invoke。
'''
''' 生命周期：
'''   - 主实例正常退出     -> Release() 释放 Mutex 与事件，下一个实例可正常成为主实例
'''   - 主实例异常/被强杀   -> 由操作系统回收命名内核对象，同样能重新启动
'''   - 后启动的实例       -> TryAcquire 返回 False，调用方立即退出，不进入任何 UI 初始化
''' </summary>
Public Class ModSingleInstance

    Private Const MutexName As String = "Local\Ruijie_ePorta_Tool_SingleInstance"
    Private Const EventName As String = "Local\Ruijie_ePorta_Tool_ActivateEvent"

    Private _Mutex As Mutex
    Private _Event As EventWaitHandle
    Private _Listener As Thread
    Private _Stopping As Boolean

    ''' <summary>
    ''' 已有实例收到激活请求时执行。
    ''' 已由本类 marshal 到 UI 线程，回调里可以直接操作窗口。
    ''' </summary>
    Public Property OnActivateRequested As Action

    ''' <summary>打开后把关键步骤写控制台，便于脚本化验证（默认关闭）。</summary>
    Public Property Verbose As Boolean = False

    Private Sub Trace(Msg As String)
        If Not Verbose Then Return
        Try
            Console.WriteLine("[SingleInstance] " & Msg)
            Console.Out.Flush()
        Catch
        End Try
    End Sub

    ''' <summary>本进程是否为主实例。</summary>
    Public ReadOnly Property IsPrimary As Boolean
        Get
            Return _Mutex IsNot Nothing
        End Get
    End Property

    ''' <summary>
    ''' 尝试成为主实例。
    ''' 返回 True  = 本进程是第一个实例，继续正常启动。
    ''' 返回 False = 已有实例在运行；激活信号已发出，调用方应**立即退出**。
    ''' </summary>
    Public Function TryAcquire() As Boolean
        Dim CreatedNew As Boolean = False
        Try
            _Mutex = New Mutex(initiallyOwned:=True, name:=MutexName, createdNew:=CreatedNew)
        Catch ex As Exception
            ' 连 Mutex 都建不出来时不能把程序卡死：放行，退化为无单实例保护
            Log(ex, "[SingleInstance] 创建 Mutex 失败，本次不启用单实例保护")
            Trace("Mutex 创建异常，退化为无保护: " & ex.Message)
            _Mutex = Nothing
            Return True
        End Try

        Trace("CreatedNew=" & CreatedNew.ToString())

        If Not CreatedNew Then
            _Mutex.Dispose()
            _Mutex = Nothing
            Trace("检测到已有实例，发送激活信号…")
            SignalExisting()
            Trace("激活信号已发送，本进程退出")
            Return False
        End If

        StartListener()
        Return True
    End Function

    ''' <summary>启动后台监听线程，等待后启动实例发来的激活信号。</summary>
    Private Sub StartListener()
        Try
            Dim CreatedNew As Boolean = False
            _Event = New EventWaitHandle(False, EventResetMode.AutoReset, EventName, CreatedNew)

            _Listener = New Thread(
                Sub()
                    Try
                        Do While Not _Stopping
                            If _Event.WaitOne(1000) Then
                                If _Stopping Then Exit Do
                                Try
                                    ' 落一行日志：激活是「有人又点了一次程序」的客观证据，
                                    ' 否则这件事事后完全不可见（托盘界面脚本/AI 都看不到）。
                                    DailyWrite("[" & GetTimeNow() & "] 已有实例收到激活请求，显示主窗口" & vbCrLf)
                                    RunInUi(Sub()
                                                 OnActivateRequested?.Invoke()
                                             End Sub)
                                Catch ex As Exception
                                    Log(ex, "[SingleInstance] 处理激活请求失败")
                                End Try
                            End If
                        Loop
                    Catch ex As Exception
                        Log(ex, "[SingleInstance] 监听线程异常")
                    End Try
                End Sub) With {
                .Name = "SingleInstanceListener",
                .IsBackground = True,
                .Priority = ThreadPriority.BelowNormal
            }
            _Listener.Start()
            Trace("监听线程已启动")
        Catch ex As Exception
            Log(ex, "[SingleInstance] 启动监听线程失败（单实例保护仍生效，但双击无法唤起窗口）")
            Trace("监听线程启动失败: " & ex.Message)
        End Try
    End Sub

    ''' <summary>通知已在运行的实例显示主窗口；随后本进程应立即退出。</summary>
    Private Sub SignalExisting()
        Try
            Dim CreatedNew As Boolean = False
            Using E As EventWaitHandle = New EventWaitHandle(False, EventResetMode.AutoReset, EventName, CreatedNew)
                Dim Ok As Boolean = E.Set()
                Trace("EventWaitHandle.Set() -> " & Ok.ToString())
            End Using
        Catch ex As Exception
            Log(ex, "[SingleInstance] 发送激活信号失败")
            Trace("发送激活信号异常: " & ex.Message)
        End Try
    End Sub

    ''' <summary>释放 Mutex 与事件句柄。主实例退出时调用。</summary>
    Public Sub Release()
        _Stopping = True
        Try
            If _Event IsNot Nothing Then _Event.Set()   ' 立刻唤醒监听线程，避免等满 1 秒
        Catch
        End Try
        Try
            If _Listener IsNot Nothing AndAlso _Listener.IsAlive Then
                _Listener.Join(1500)
            End If
        Catch
        End Try
        _Listener = Nothing

        Try
            If _Event IsNot Nothing Then _Event.Dispose()
        Catch
        End Try
        _Event = Nothing

        Try
            If _Mutex IsNot Nothing Then
                _Mutex.ReleaseMutex()
                _Mutex.Dispose()
            End If
        Catch
        End Try
        _Mutex = Nothing
    End Sub

End Class
