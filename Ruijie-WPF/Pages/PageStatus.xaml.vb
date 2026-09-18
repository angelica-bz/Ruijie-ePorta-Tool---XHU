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

        ChkAutoStart.Checked = IsAutoStartEnabled()

        RunInNewThread(Sub() CleanOldLogs(7), "LogCleaner", ThreadPriority.Lowest)
        StartMonitor()
    End Sub

    Private Sub Page_Unloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        StopMonitor()
    End Sub

#Region "监控"

    Private Sub StartMonitor()
        Dim MainWin = TryCast(Application.Current?.MainWindow, FormMain)
        If MainWin IsNot Nothing Then
            Monitor = MainWin.BgMonitor
            NotifiedConnection = MainWin.BgNotified
        End If
        If Monitor Is Nothing Then Return

        If Not _MonitorHandlersAttached Then
            Dim Snap = Monitor.GetSnapshot()

            UpdateStatus(Snap.Connected)

            If Snap.SchoolReachable.HasValue Then
                UpdateSchoolStatus(Snap.SchoolReachable.Value)
            End If

            For Each msg In Snap.Logs
                AppendLog(msg, writeToFile:=False)
            Next

            _MonitorHandlersAttached = True
            AddHandler Monitor.LogMessage, Sub(msg) RunInUi(Sub() AppendLog(msg, writeToFile:=False))
            AddHandler Monitor.StatusChanged, Sub(connected) RunInUi(Sub() UpdateStatus(connected))
            AddHandler Monitor.SchoolStatusChanged, Sub(reachable) RunInUi(Sub() UpdateSchoolStatus(reachable))
        End If
    End Sub

    Private Sub StopMonitor()
        _MonitorHandlersAttached = False
        Monitor = Nothing
    End Sub

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

    Private Sub ChkAutoStart_Change(sender As Object, user As Boolean) Handles ChkAutoStart.Change
        If Not user Then Return
        Dim Ok = SetAutoStart(ChkAutoStart.Checked)
        If Not Ok AndAlso ChkAutoStart.Checked Then
            MessageBox.Show("无法设置开机启动。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning)
            ChkAutoStart.Checked = False
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
