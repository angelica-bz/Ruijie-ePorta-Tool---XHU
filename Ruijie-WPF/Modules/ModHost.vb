Imports System.Diagnostics
Imports Microsoft.VisualBasic

''' <summary>
''' 应用级宿主：托盘、网络监控、主窗口的**唯一归属者**。
'''
''' 为什么要它：
'''     原先托盘与 NetworkMonitor 都建在 FormMain 内部，而 App.xaml 用
'''     StartupUri="FormMain.xaml" 无条件创建主窗口 —— 于是「后台启动」也必然
'''     解析整棵窗口 XAML、创建 HWND、初始化 WPF 渲染栈，只为了一个托盘图标。
'''     实测两条路径的稳态内存完全相同（Private ≈ 71 MB、模块 111、线程 25），
'''     唯一的差别只是那两个页面。
'''     去掉 StartupUri 后，后台模式实测：模块 111 → 81、Private 70 → 35 MB、
'''     d3d9 / D3DCOMPILER / nvd3dumx / nvgpucomp64 全部不再加载。
'''
''' 拆分后的职责：
'''     ModHost  —— 托盘图标、NetworkMonitor、主窗口的创建与激活（与是否有窗口无关）
'''     FormMain —— 纯 GUI 外壳：显示/隐藏、Tab 切换、拖拽
'''     App      —— 只决定「这次要不要 GUI」
''' </summary>
Public Module ModHost

    ''' <summary>托盘图标。整个进程只有这一个。</summary>
    Public TrayIcon As System.Windows.Forms.NotifyIcon

    ''' <summary>后台网络监控。整个进程只有这一个。</summary>
    Public Monitor As NetworkMonitor

    ''' <summary>主窗口。后台启动时为 Nothing，直到用户真的要打开界面才创建。</summary>
    Private _Main As FormMain

    ''' <summary>主窗口是否已被真正关闭（Closed 之后不可再 Show）。</summary>
    Private _MainClosed As Boolean = False

    ''' <summary>本次启动是否走的 --background。</summary>
    Public BackgroundMode As Boolean = False

    Private _IsShuttingDown As Boolean = False
    Private _BgNotified As Boolean = False

    ''' <summary>是否已经开始收尾（托盘「退出」后置位）。</summary>
    Public ReadOnly Property IsShuttingDown As Boolean
        Get
            Return _IsShuttingDown
        End Get
    End Property

    ''' <summary>后台实例是否已就连接事件弹过气泡提示。</summary>
    Public Property BgNotified As Boolean
        Get
            Return _BgNotified
        End Get
        Set(value As Boolean)
            _BgNotified = value
        End Set
    End Property

    ''' <summary>主窗口实例；未创建时为 Nothing。</summary>
    Public ReadOnly Property Main As FormMain
        Get
            Return _Main
        End Get
    End Property

#Region "托盘"

    ''' <summary>创建托盘图标（幂等）。</summary>
    Public Sub EnsureTray()
        If TrayIcon IsNot Nothing Then Return

        ' NotifyIcon 属于 WinForms，构造期间会碰 SynchronizationContext，
        ' 这里保持与原实现一致：先存后还原，避免污染 WPF 的上下文。
        Dim PrevContext = Threading.SynchronizationContext.Current
        Try
            Dim IconStream = Application.GetResourceStream(New Uri("Images/icon.ico", UriKind.Relative)).Stream
            TrayIcon = New System.Windows.Forms.NotifyIcon With {
                .Icon = New System.Drawing.Icon(IconStream),
                .Text = "锐捷 ePorta 连接工具",
                .Visible = True
            }

            Dim Menu As New System.Windows.Forms.ContextMenuStrip()
            Menu.Items.Add("显示主窗口", Nothing,
                           Sub()
                               MarkCreateReason("托盘菜单「显示主窗口」")
                               ShowMain()
                           End Sub)
            Menu.Items.Add(New System.Windows.Forms.ToolStripSeparator())
            Menu.Items.Add("退出", Nothing, Sub() ExitApp())
            TrayIcon.ContextMenuStrip = Menu

            AddHandler TrayIcon.MouseClick, Sub(s, e)
                                                If e.Button = System.Windows.Forms.MouseButtons.Left Then
                                                    MarkCreateReason("托盘左键点击")
                                                    ToggleMain()
                                                End If
                                            End Sub

            ' 托盘图标在 explorer 重启后可能消失，5 秒后重挂一次（沿用原实现）
            Dim TrayRetry As New System.Windows.Forms.Timer With {.Interval = 5000}
            AddHandler TrayRetry.Tick, Sub(s, e)
                                           TrayRetry.Dispose()
                                           If TrayIcon IsNot Nothing Then
                                               TrayIcon.Visible = False
                                               TrayIcon.Visible = True
                                           End If
                                       End Sub
            TrayRetry.Start()
        Catch ex As Exception
            Log(ex, "[Host] 创建托盘图标失败")
        Finally
            Threading.SynchronizationContext.SetSynchronizationContext(PrevContext)
        End Try
    End Sub

    ''' <summary>在托盘上弹气泡提示（窗口不可见时才有意义）。</summary>
    Public Sub ShowTrayNotification(Title As String, Message As String, Optional Timeout As Integer = 3000)
        If _IsShuttingDown Then Return
        If TrayIcon Is Nothing OrElse Not TrayIcon.Visible Then Return
        ' 主窗口没建 = 一定不可见；建了就按实际可见性判断
        If _Main IsNot Nothing AndAlso _Main.Visibility = Windows.Visibility.Visible Then Return
        Try
            TrayIcon.ShowBalloonTip(Timeout, Title, Message, System.Windows.Forms.ToolTipIcon.Info)
        Catch ex As Exception
            Log(ex, "[Host] 气泡提示失败")
        End Try
    End Sub

#End Region

#Region "网络监控"

    ''' <summary>确保配置已加载（托盘与监控都依赖它）。</summary>
    Public Sub EnsureConfig()
        If SharedCfg Is Nothing Then InitSharedConfig()
    End Sub

    ''' <summary>创建并启动 NetworkMonitor（幂等 —— 打开 GUI 后不会重复创建）。</summary>
    Public Sub EnsureMonitor()
        If Monitor IsNot Nothing Then Return
        EnsureConfig()
        Monitor = New NetworkMonitor(SharedCfg)
        Monitor.Start()
    End Sub

    Public Sub StopMonitor()
        If Monitor IsNot Nothing Then
            Monitor.Stop()
            Monitor = Nothing
        End If
    End Sub

#End Region

#Region "主窗口"

    ''' <summary>创建主窗口的原因，供启动身份日志记录（仅诊断用）。</summary>
    Private _PendingCreateReason As String = "(初始化路径)"

    ''' <summary>
    ''' 标记接下来的主窗口创建由什么触发。各入口在调用 ShowMain 前先设置它，
    ''' 这样日志里能直接看出窗口是「用户点托盘」还是「不该建却建了」。
    ''' </summary>
    Public Sub MarkCreateReason(Reason As String)
        _PendingCreateReason = If(Reason, "(未标注)")
    End Sub

    ''' <summary>
    ''' 创建主窗口（幂等）。**只有真正需要 GUI 时才调用**。
    ''' 后台启动路径不调用它，因此不会解析窗口 XAML、不会创建 HWND。
    '''
    ''' 生命周期保护：若旧窗口已经 Closed（例如被外部强制关闭的路径），
    ''' 必须先丢弃旧引用再重建 —— 否则对已关闭的 Window 调 Show() 会抛
    ''' InvalidOperationException（Window.VerifyCanShow）。
    ''' 注意：**不能**把「UI 已加载」当成「窗口仍然有效」，
    ''' 窗口生命周期与 UI 加载状态是两件独立的事。
    ''' </summary>

    Public Function EnsureMainWindow() As FormMain
        If _Main IsNot Nothing Then
            If Not _MainClosed Then Return _Main
            ' 旧窗口已关闭：丢弃引用，下面重建
            Log("[Host] 旧主窗口已关闭，重新创建")
            _Main = Nothing
        End If
        EnsureConfig()
        _MainClosed = False
        ' 记录是谁、在什么模式下创建了主窗口。
        ' 后台模式下这里必然对应一次「激活」，日志会同时给出调用栈，
        ' 事后可据此判断窗口是用户点出来的，还是不该建却建了。
        ModStartupIdentity.LogMainWindowCreation(BackgroundMode, _PendingCreateReason)
        _PendingCreateReason = "(未标注的调用来源)"
        _Main = New FormMain()
        ModStartupProfile.Mark("MainWindowConstruct")
        Return _Main
    End Function

    ''' <summary>
    ''' 已有实例收到激活请求时调用：创建（或复用）主窗口并显示。
    ''' 后台启动的实例就是在这里第一次拥有窗口。
    ''' </summary>
    Public Sub OnActivate()
        MarkCreateReason("第二实例激活（EventWaitHandle）")
        ShowMain()
    End Sub

    ''' <summary>显示主窗口（必要时才创建），并置前激活。</summary>
    Public Sub ShowMain()
        If _IsShuttingDown Then Return
        RunInUi(Sub()
                    Dim W = EnsureMainWindow()
                    If Not W.IsVisible Then W.Show()
                    If W.WindowState = WindowState.Minimized Then W.WindowState = WindowState.Normal
                    W.Activate()
                    W.Topmost = True
                    W.Topmost = False
                    W.Focus()
                End Sub)
    End Sub

    ''' <summary>托盘左键：可见则收起，不可见则显示。</summary>
    Public Sub ToggleMain()
        If _Main IsNot Nothing AndAlso _Main.IsVisible Then
            _Main.Hide()
        Else
            ShowMain()
        End If
    End Sub

    ''' <summary>
    ''' GUI 自检入口（--ui-test）调用：显式标注创建原因。
    ''' </summary>
    Public Function CreateMainWindowForTest() As FormMain
        MarkCreateReason("--ui-test 自检入口")
        Return EnsureMainWindow()
    End Function

    ''' <summary>
    ''' 主窗口已关闭（Closed 事件）。
    ''' 只把「已关闭」这件事记下来，让 EnsureMainWindow 知道要重建；
    ''' **不**释放 TrayIcon / Monitor —— 它们是应用级的，窗口关掉不等于程序退出。
    ''' </summary>
    Public Sub OnMainWindowClosed(Window As FormMain)
        If Window Is _Main Then _MainClosed = True
    End Sub

#End Region

#Region "后台形态自检"

    ''' <summary>
    ''' 后台启动后延迟一段时间做一次 UI 形态快照并落盘。
    '''
    ''' 目的：确认「后台启动确实没有创建 GUI」。此前的排查是在几分钟后回头查进程，
    ''' 那时 PID 早已变化、证据链断裂；如果在启动早期就把形态写进日志，
    ''' 事后无论进程还在不在都能对账。
    ''' 只在后台模式调用一次，开销可忽略。
    ''' </summary>
    Public Sub ScheduleBackgroundFormSnapshot(DelaySeconds As Integer)
        If Not BackgroundMode Then Return
        Dim Tm As New System.Windows.Threading.DispatcherTimer With {
            .Interval = TimeSpan.FromSeconds(DelaySeconds)
        }
        AddHandler Tm.Tick,
            Sub()
                Tm.Stop()
                Try
                    Dim HasMain As Boolean = (_Main IsNot Nothing)
                    Dim WinCount As Integer = -1
                    Try
                        WinCount = Application.Current.Windows.Count
                    Catch
                    End Try
                    Dim ModCount As Integer = 0
                    Dim HasD3d As Boolean = False
                    Try
                        Dim Me_ As Process = Process.GetCurrentProcess()
                        Dim Names = Me_.Modules.Cast(Of ProcessModule)().Select(Function(M) M.ModuleName).ToList()
                        ModCount = Names.Count
                        HasD3d = Names.Any(Function(N) N.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase) OrElse
                                                  N.Equals("D3DCOMPILER_47.dll", StringComparison.OrdinalIgnoreCase) OrElse
                                                  N.Equals("nvd3dumx.dll", StringComparison.OrdinalIgnoreCase))
                    Catch
                    End Try
                    Dim WS As Double = 0
                    Dim PV As Double = 0
                    Try
                        Dim Proc_ As Process = Process.GetCurrentProcess()
                        Proc_.Refresh()
                        WS = Math.Round(Proc_.WorkingSet64 / 1048576.0, 2)
                        PV = Math.Round(Proc_.PrivateMemorySize64 / 1048576.0, 2)
                    Catch
                    End Try

                    Dim Sb As New Text.StringBuilder()
                    Sb.AppendLine("[BackgroundForm] 后台启动形态快照（+" & DelaySeconds & "s）")
                    Sb.AppendLine("  StartupMode      = " & If(BackgroundMode, "Background", "Normal"))
                    Sb.AppendLine("  FormMainCreated  = " & HasMain.ToString())
                    Sb.AppendLine("  WpfWindowCount   = " & WinCount.ToString())
                    Sb.AppendLine("  ModuleCount      = " & ModCount.ToString())
                    Sb.AppendLine("  D3D/GPU Loaded   = " & HasD3d.ToString())
                    Sb.AppendLine("  WorkingSet_MB    = " & WS.ToString())
                    Sb.AppendLine("  Private_MB       = " & PV.ToString())
                    Sb.AppendLine("  TrayVisible      = " & (TrayIcon IsNot Nothing AndAlso TrayIcon.Visible).ToString())
                    Sb.AppendLine("  MonitorRunning   = " & (Monitor IsNot Nothing).ToString())
                    If HasMain OrElse HasD3d Then
                        Sb.AppendLine(ModStartupIdentity.ViolationPrefix & " 后台模式本不应创建 GUI，但上述指标显示已创建")
                    End If
                    TraceLifecycle(Sb.ToString())
                Catch ex As Exception
                    Log(ex, "[Host] 后台形态快照失败")
                End Try
            End Sub
        Tm.Start()
    End Sub

#End Region

#Region "退出"

    ''' <summary>
    ''' 托盘「退出」：真正结束程序。**顺序很重要**，每步都在收尾状态机里有明确位置：
    '''     1. 置收尾标志 —— 让 FormMain.Closing 不再把关闭改成 Hide
    '''     2. 停动画线程 —— 它曾经是无限循环 + 前台线程，是进程残留的元凶
    '''     3. 关主窗口   —— 释放 HWND 与 WPF 资源
    '''     4. 停网络监控 —— 置停止标志并等线程真正退出
    '''     5. 释放托盘   —— 先隐藏再 Dispose，否则图标会残留在通知区
    '''     6. Application.Shutdown() —— 触发 Application_Exit，在那里释放 Mutex 与 IPC 事件
    '''
    ''' 【为什么以前退出后进程仍在】RunInNewThread 当时没有设置 IsBackground，
    ''' 而 Thread 默认是**前台线程**；动画线程又是 `Do While True` 无退出路径。
    ''' 只要有一个前台线程存活，CLR 就不会结束进程 —— 表现为托盘消失、
    ''' 内存从约 30 MB 降到约 20 MB，但 Task Manager 里进程还在。
    ''' 现已双管齐下：RunInNewThread 统一用后台线程 + 动画线程有明确停止路径。
    ''' </summary>
    Public Sub ExitApp()
        If _IsShuttingDown Then Return
        _IsShuttingDown = True
        TraceLifecycle("[Exit] 开始退出")

        ' 1) 动画线程：先请求停止，避免它继续往已关闭的窗口派发帧
        Try
            AniStopAll()
            TraceLifecycle("[Exit] 动画线程已请求停止")
        Catch ex As Exception
            Log(ex, "[Host] 停止动画线程失败")
        End Try

        ' 2) 关窗口：此时 IsShuttingDown 已为 True，Closing 不会再取消
        Try
            If _Main IsNot Nothing Then
                _Main.Close()
                _Main = Nothing
                _MainClosed = True
                TraceLifecycle("[Exit] 主窗口已关闭")
            Else
                TraceLifecycle("[Exit] 无主窗口（后台模式）")
            End If
        Catch ex As Exception
            Log(ex, "[Host] 关闭主窗口失败")
        End Try

        ' 3) 网络监控：Stop 内部会等待监控线程真正退出
        StopMonitor()
        TraceLifecycle("[Exit] 网络监控已停止")

        ' 4) 托盘：先隐藏再释放
        If TrayIcon IsNot Nothing Then
            Try
                TrayIcon.Visible = False
                TrayIcon.Dispose()
            Catch ex As Exception
                Log(ex, "[Host] 释放托盘失败")
            End Try
            TrayIcon = Nothing
            TraceLifecycle("[Exit] 托盘已释放")
        End If

        ' 5) 结束消息循环 -> 触发 Application_Exit -> 释放 Mutex / IPC 事件
        TraceLifecycle("[Exit] 调用 Application.Shutdown")
        Application.Current.Shutdown()
    End Sub

#End Region

End Module
