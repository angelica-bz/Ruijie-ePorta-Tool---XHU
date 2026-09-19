Imports System.Windows.Threading
Imports System.Windows.Media
Imports Microsoft.VisualBasic

Public Class PageStatus

    Private Cfg As Dictionary(Of String, Object)
    Private Monitor As NetworkMonitor
    Private LogLineCount As Integer = 0
    Private LastStatus As Nullable(Of Boolean) = Nothing
    Private LastSchool As Nullable(Of Boolean) = Nothing
    Private NotifiedConnection As Boolean = False
    Private _MonitorHandlersAttached As Boolean = False

    Private Sub Page_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Cfg = SharedCfg

        Dim Interval As Integer = GetDictInt(GetFunctionDict(Cfg), ConfigKeys.ReconnectInterval, 5)
        If Interval < 1 Then Interval = 1
        If Interval > 99 Then Interval = 99
        TxtInterval.Text = Interval.ToString()
        LabInterval.Text = Interval & " 秒"

        ChkAutoReconnect.Checked = GetDictBool(GetFunctionDict(Cfg), ConfigKeys.AutoReconnect, False)

        ChkAutoStart.Checked = IsEnabled()

        RunInNewThread(Sub() CleanOldLogs(7), "LogCleaner", ThreadPriority.Lowest)
        StartMonitor()
    End Sub

    ''' <summary>
    ''' 页面被卸载：真正解除订阅。
    ''' 若只清标志不 RemoveHandler，本页会被应用级的 NetworkMonitor 长期持有，
    ''' 且重新加载后会**重复订阅**（日志与状态刷新成倍触发）。
    ''' </summary>
    Private Sub Page_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        DetachMonitorHandlers()
        Monitor = Nothing
    End Sub

#Region "监控"

    ''' <summary>
    ''' 记录当前实际订阅的监控实例。
    ''' RemoveHandler 必须拿到**同一个** Monitor 对象，因此不能只依赖字段 Monitor
    ''' （它在 StopMonitor 里会被清空）；也不记录委托本身 ——
    ''' 每次 AddHandler 传的都是新 Lambda，委托不相等，无法据此解除。
    ''' 改为用具名方法订阅，这样 Add/Remove 成对、可重复、不累积。
    ''' </summary>
    Private _SubscribedMonitor As NetworkMonitor

    Private Sub StartMonitor()
        Dim MainWin = TryCast(Application.Current?.MainWindow, FormMain)
        If MainWin IsNot Nothing Then
            Monitor = MainWin.BgMonitor
            NotifiedConnection = MainWin.BgNotified
        End If
        If Monitor Is Nothing Then Return

        ' 幂等：已经订阅过同一个实例就不再重复订阅
        If _SubscribedMonitor Is Monitor Then Return

        ' 换了监控实例（或首次）先解除旧订阅，避免旧实例继续回调本页
        DetachMonitorHandlers()

        Dim Snap = Monitor.GetSnapshot()

        UpdateStatus(Snap.Connected)

        If Snap.SchoolReachable.HasValue Then
            UpdateSchoolStatus(Snap.SchoolReachable.Value)
        End If

        For Each msg In Snap.Logs
            AppendLog(msg, writeToFile:=False)
        Next

        AddHandler Monitor.LogMessage, AddressOf OnMonitorLogMessage
        AddHandler Monitor.StatusChanged, AddressOf OnMonitorStatusChanged
        AddHandler Monitor.SchoolStatusChanged, AddressOf OnMonitorSchoolStatusChanged
        _SubscribedMonitor = Monitor
        _MonitorHandlersAttached = True
    End Sub

    ''' <summary>
    ''' 解除所有监控订阅。
    ''' 原实现只把 _MonitorHandlersAttached 置 False、Monitor 置 Nothing，
    ''' **从不调用 RemoveHandler** —— 订阅关系仍然挂在 NetworkMonitor 上，
    ''' 而 NetworkMonitor 是应用级长生命周期对象，于是：
    '''   · 页面被卸载后仍在接收并处理日志/状态事件
    '''   · 再次 Loaded 时守卫已被重置，会**重复订阅**，日志与状态刷新成倍触发
    ''' 现在把三处订阅按名解除。
    ''' </summary>
    Private Sub DetachMonitorHandlers()
        If _SubscribedMonitor Is Nothing Then
            _MonitorHandlersAttached = False
            Return
        End If
        Try
            RemoveHandler _SubscribedMonitor.LogMessage, AddressOf OnMonitorLogMessage
            RemoveHandler _SubscribedMonitor.StatusChanged, AddressOf OnMonitorStatusChanged
            RemoveHandler _SubscribedMonitor.SchoolStatusChanged, AddressOf OnMonitorSchoolStatusChanged
        Catch ex As Exception
            Log(ex, "[PageStatus] 解除监控订阅失败")
        End Try
        _SubscribedMonitor = Nothing
        _MonitorHandlersAttached = False
    End Sub

#End Region

#Region "监控事件处理（具名方法，便于成对 RemoveHandler）"

    Private Sub OnMonitorLogMessage(msg As String)
        RunInUi(Sub() AppendLog(msg, writeToFile:=False))
    End Sub

    Private Sub OnMonitorStatusChanged(connected As Boolean)
        RunInUi(Sub() UpdateStatus(connected))
    End Sub

    Private Sub OnMonitorSchoolStatusChanged(reachable As Boolean)
        RunInUi(Sub() UpdateSchoolStatus(reachable))
    End Sub

    ''' <summary>供自检使用：当前实际生效的订阅数量（0 或 3）。</summary>
    Public ReadOnly Property MonitorHandlerCount As Integer
        Get
            Return If(_SubscribedMonitor Is Nothing, 0, 3)
        End Get
    End Property

    ''' <summary>
    ''' 自检：强制「解除订阅 → 重新订阅」若干轮，验证订阅数不累积。
    ''' 只被 --ui-test 调用。刻意直接调用成对方法，而不是依赖 Unloaded 事件 ——
    ''' WPF 中把 Frame.Content 置空并不保证同步触发 Unloaded，用它做断言不可靠。
    ''' </summary>
    Public Function SelfTestSubscriptionCycle(Rounds As Integer) As String
        Dim Sb As New Text.StringBuilder()
        For R = 1 To Rounds
            DetachMonitorHandlers()
            Dim AfterDetach As Integer = MonitorHandlerCount
            StartMonitor()
            Dim AfterAttach As Integer = MonitorHandlerCount
            Dim Ok As Boolean = (AfterDetach = 0 AndAlso AfterAttach = 3)
            Sb.AppendLine("  第 " & R & " 轮  解除后=" & AfterDetach & "  重新订阅后=" & AfterAttach &
                          If(Ok, "   OK", "   <<< 异常"))
        Next
        Return Sb.ToString()
    End Function

#End Region

#Region "状态刷新"

    Private Sub UpdateStatus(connected As Boolean)
        LastStatus = connected
        If connected Then
            ShapeStatusDot.Fill = New SolidColorBrush(Color.FromRgb(&H4C, &HAF, &H50))
            LabStatus.Text = "已连接"
            If Application.IsBackgroundStart AndAlso Not NotifiedConnection Then
                NotifiedConnection = True
                Dim MainWin = TryCast(Application.Current?.MainWindow, FormMain)
                If MainWin IsNot Nothing Then
                    MainWin.ShowTrayNotification("连接成功", "网络已连接")
                End If
            End If
        Else
            ShapeStatusDot.Fill = New SolidColorBrush(Color.FromRgb(&HF4, &H43, &H36))
            LabStatus.Text = "未连接"
        End If
    End Sub

    Private Sub UpdateSchoolStatus(reachable As Boolean)
        LastSchool = reachable
        LabSchoolStatus.Text = If(reachable, "可达", "不可达")
    End Sub

#End Region

#Region "按钮事件"

    Private IsConnecting As Boolean = False

    Private Sub BtnConnect_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnConnect.Click
        If IsConnecting Then Return
        IsConnecting = True
        BtnConnect.Text = "连接中…"
        BtnConnect.IsEnabled = False
        BtnDisconnect.IsEnabled = False
        RunInNewThread(
            Sub()
                Try
                    ' 先做本地配置检查：不合格就直接提示，不发无效 HTTP 请求
                    Dim Context = BuildRuntimeAuthContext()
                    If Not Context.IsValid Then
                        RunInUi(Sub()
                                    AppendLog("[" & GetTimeNow() & "] " & Context.ErrorMessage)
                                    MessageBox.Show(Context.ErrorMessage, "配置不完整",
                                                    MessageBoxButton.OK, MessageBoxImage.Warning)
                                End Sub)
                        Return
                    End If

                    RunInUi(Sub() AppendLog("[" & GetTimeNow() & "] 正在获取门户认证参数…"))
                    ' Dynamic ePortal authentication：发现 → ModAuth → ModCrypto → LoginPayload
                    Dim Result = Authenticate(Context.Account, Context.ProbeUrl, Context.Server,
                                              Context.Timeout, Context.BindAddress)
                    RunInUi(Sub() HandleAuthResult(Result))
                Catch ex As Exception
                    RunInUi(Sub()
                                AppendLog("[" & GetTimeNow() & "] 连接异常: " & ex.Message)
                            End Sub)
                Finally
                    RunInUi(Sub()
                                BtnConnect.Text = "连接"
                                BtnConnect.IsEnabled = True
                                BtnDisconnect.IsEnabled = True
                                IsConnecting = False
                            End Sub)
                End Try
            End Sub, "Connect")
    End Sub

    Private Sub BtnDisconnect_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnDisconnect.Click
        BtnConnect.IsEnabled = False
        BtnDisconnect.Text = "断开中…"
        BtnDisconnect.IsEnabled = False
        RunInNewThread(
            Sub()
                Try
                    ' 断开只依赖登录成功时拿到的 userIndex（门户协议实测如此）
                    Dim Result = LogoutAuthenticated()
                    RunInUi(Sub()
                                HandleLogoutResult(Result)
                                BtnDisconnect.Text = "断开"
                                BtnConnect.IsEnabled = True
                                BtnDisconnect.IsEnabled = True
                            End Sub)
                Catch ex As Exception
                    RunInUi(Sub()
                                AppendLog("[" & GetTimeNow() & "] 断开异常: " & ex.Message)
                                BtnDisconnect.Text = "断开"
                                BtnConnect.IsEnabled = True
                                BtnDisconnect.IsEnabled = True
                            End Sub)
                End Try
            End Sub, "Disconnect")
    End Sub

    ''' <summary>处理动态认证结果（三种情形：已联网 / 登录成功 / 认证失败）。</summary>
    Private Sub HandleAuthResult(result As AuthenticationResult)
        Dim Ts As String = GetTimeNow()
        If result.Success Then
            ' Payload 为 Nothing 表示 AlreadyOnline：当前无需认证
            AppendLog("[" & Ts & "] " & result.Message)
            UpdateStatus(True)
        Else
            Dim Msg As String = DescribeFailure(result)
            AppendLog("[" & Ts & "] 连接失败: " & Msg)
            UpdateStatus(False)
            MessageBox.Show(Msg, "认证失败", MessageBoxButton.OK, MessageBoxImage.Error)
        End If
    End Sub

    ''' <summary>处理断开结果（成功 / 失败 / 无可用会话）。</summary>
    Private Sub HandleLogoutResult(result As LogoutResult)
        Dim Ts As String = GetTimeNow()
        Select Case result.Status
            Case PortalLogoutStatus.Success
                AppendLog("[" & Ts & "] " & result.Message)
                UpdateStatus(False)
            Case PortalLogoutStatus.NoSession
                AppendLog("[" & Ts & "] " & result.Message)
                MessageBox.Show(result.Message, "无法断开", MessageBoxButton.OK, MessageBoxImage.Warning)
            Case Else
                AppendLog("[" & Ts & "] 断开失败: " & result.Message)
                MessageBox.Show("断开失败: " & result.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error)
        End Select
    End Sub

#End Region

#Region "设置"

    ''' <summary>
    ''' 「开机自启」开关。
    ''' 状态以**实际的自启动机制**为准（计划任务，或回退态下的旧 Run 键），
    ''' 不再只是读一个注册表值。
    ''' </summary>
    Private Sub ChkAutoStart_Change(sender As Object, user As Boolean) Handles ChkAutoStart.Change
        If Not user Then Return

        If ChkAutoStart.Checked Then
            Select Case Enable()          ' ModStartup.Enable()
                Case EnableResult.AlreadyOk, EnableResult.Registered
                    ' 成功：无需打扰
                Case EnableResult.ElevationPending
                    MessageBox.Show("已请求管理员权限以完成开机自启设置。" & vbCrLf & vbCrLf &
                                    "请在出现的提示框中允许，随后重新打开本程序确认开关状态。",
                                    "开机自启", MessageBoxButton.OK, MessageBoxImage.Information)
                    ' 提权进程可能还在跑，勾选状态稍后会与实际对齐
                Case EnableResult.FallbackToRun
                    MessageBox.Show("创建计划任务失败，已回退为使用注册表启动项。" & vbCrLf & vbCrLf &
                                    "开机自启仍然可用，但启动时机可能比预期晚一些。",
                                    "开机自启", MessageBoxButton.OK, MessageBoxImage.Warning)
                Case Else
                    MessageBox.Show("无法设置开机自启。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning)
                    ChkAutoStart.Checked = False
            End Select
        Else
            If Not Disable() Then
                MessageBox.Show("无法关闭开机自启。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning)
            End If
            ' 以实际状态回写，避免开关与实际不一致
            ChkAutoStart.Checked = IsEnabled()
        End If
    End Sub

    Private Sub ChkAutoReconnect_Change(sender As Object, user As Boolean) Handles ChkAutoReconnect.Change
        If Not user Then Return
        Dim FunctionCfg = GetFunctionDict(Cfg)
        If FunctionCfg IsNot Nothing Then
            FunctionCfg(ConfigKeys.AutoReconnect) = ChkAutoReconnect.Checked
            WriteCfg(Cfg)
        End If
    End Sub

    Private Sub BtnApplyInterval_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnApplyInterval.Click
        Dim Val As Integer
        If Not Integer.TryParse(TxtInterval.Text, Val) OrElse Val < 1 OrElse Val > 99 Then
            MessageBox.Show("检测间隔必须为 1 到 99 之间的整数。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning)
            Dim FunctionCfg2 = GetFunctionDict(Cfg)
            If FunctionCfg2 IsNot Nothing AndAlso FunctionCfg2.ContainsKey(ConfigKeys.ReconnectInterval) Then
                TxtInterval.Text = FunctionCfg2(ConfigKeys.ReconnectInterval).ToString()
            End If
            Return
        End If
        Dim FunctionCfg3 = GetFunctionDict(Cfg)
        If FunctionCfg3 IsNot Nothing Then
            FunctionCfg3(ConfigKeys.ReconnectInterval) = Val
            WriteCfg(Cfg)
        End If
        LabInterval.Text = Val & " 秒"
        AppendLog("[" & GetTimeNow() & "] 检测间隔已更新为 " & Val & " 秒")
    End Sub

#End Region

#Region "日志"

    Private Sub AppendLog(msg As String, Optional writeToFile As Boolean = True)
        LabLog.Text &= msg & vbLf
        If writeToFile Then RunInNewThread(Sub() DailyWrite(msg & vbCrLf), "LogWriter", ThreadPriority.Lowest)
        LogLineCount += 1
        If LogLineCount > 500 Then
            Dim Lines = LabLog.Text.Split(vbLf)
            If Lines.Length > 100 Then
                LabLog.Text = String.Join(vbLf, Lines.Skip(100))
                LogLineCount -= 100
            End If
        End If
    End Sub

    Private Sub BtnClearLog_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnClearLog.Click
        LabLog.Text = ""
        LogLineCount = 0
    End Sub

    Private Sub BtnOpenLog_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnOpenLog.Click
        Dim LogsDir = GetLogsDir()
        Try
            If Not IO.Directory.Exists(LogsDir) Then IO.Directory.CreateDirectory(LogsDir)
        Catch
        End Try
        Diagnostics.Process.Start(LogsDir)
    End Sub

#End Region

End Class
