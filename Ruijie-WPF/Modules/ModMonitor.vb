Imports System.Collections.Generic
Imports System.IO
Imports System.Threading
Imports System.Windows
Imports Microsoft.VisualBasic

Public Module ModMonitor

#Region "格式化工具"

    Public Function FormatDuration(Dur As TimeSpan) As String
        Dim Total As Integer = CInt(Math.Floor(Dur.TotalSeconds))
        If Total < 60 Then
            Return Total & "s"
        ElseIf Total < 3600 Then
            Return (Total \ 60) & "m" & (Total Mod 60) & "s"
        Else
            Dim H As Integer = Total \ 3600
            Dim M As Integer = (Total Mod 3600) \ 60
            Return H & "h" & M & "m"
        End If
    End Function

#End Region

End Module

Public Class NetworkMonitor

    Public Event LogMessage(Msg As String)
    Public Event StatusChanged(Connected As Boolean)
    Public Event SchoolStatusChanged(Reachable As Boolean)

    Private ReadOnly _Cfg As Dictionary(Of String, Object)
    Private ReadOnly _StopEvent As New ManualResetEvent(False)
    Private _WasConnected As Nullable(Of Boolean) = Nothing
    Private _ConnectedSince As Nullable(Of DateTime) = Nothing
    Private _DisconnectTime As Nullable(Of DateTime) = Nothing
    Private _DisconnectSchoolReachable As Nullable(Of Boolean) = Nothing
    Private _SchoolReachable As Nullable(Of Boolean) = Nothing
    Private ReadOnly _RecentLogs As New Queue(Of String)
    Private ReadOnly _RecentLogsLock As New Object()
    Private _Thread As Thread

    Public ReadOnly Property IsCurrentlyConnected As Boolean
        Get
            Return _WasConnected.HasValue AndAlso _WasConnected.Value
        End Get
    End Property

    Public Function GetSnapshot() As (
        Connected As Boolean,
        SchoolReachable As Nullable(Of Boolean),
        Logs As String()
    )
        SyncLock _RecentLogsLock
            Return (_WasConnected.GetValueOrDefault(False), _SchoolReachable, _RecentLogs.ToArray())
        End SyncLock
    End Function

    ''' <summary>需要绕过 VPN/TUN 时绑定的本机地址；普通用户留空。</summary>
    Private ReadOnly _BindAddress As String

    Public Sub New(Cfg As Dictionary(Of String, Object), Optional BindAddress As String = "")
        _Cfg = Cfg
        _BindAddress = If(BindAddress, "")
    End Sub

    Public Sub Start()
        _StopEvent.Reset()
        _Thread = New Thread(AddressOf RunLoop) With {
            .Name = "NetworkMonitor",
            .IsBackground = True,
            .Priority = ThreadPriority.BelowNormal
        }
        _Thread.Start()
        Log("[Monitor] thread started")
    End Sub

    ''' <summary>
    ''' 请求停止，并**等待监控线程真正退出**。
    ''' 原来只 Set 停止标志就返回，线程可能还卡在一次网络探测里（最长数秒）；
    ''' 退出流程需要「停完再往下走」，否则会带着一个仍在运行的监控线程去 Shutdown。
    ''' </summary>
    Public Sub [Stop]()
        _StopEvent.Set()
        Dim Th = _Thread
        If Th IsNot Nothing AndAlso Th.IsAlive Then
            Try
                If Th.Join(8000) Then
                    Log("[Monitor] thread exit")
                Else
                    Log("[Monitor] stop timeout: thread still alive after 8s")
                End If
            Catch ex As Exception
                Log(ex, "[Monitor] join failed")
            End Try
        End If
        _Thread = Nothing
    End Sub

    Private Sub RunLoop()
        Dim ServerUrl As String = GetServerUrl()

        Do While Not _StopEvent.WaitOne(0)
            Try
                Dim IsConnected As Boolean = False
                Try
                    Dim CheckTimeout = Math.Max(1, Math.Min(3, GetReconnectInterval()))
                    IsConnected = TestInternet(Timeout:=CheckTimeout)
                Catch
                    IsConnected = False
                End Try

                Dim Now As DateTime = DateTime.Now
                Dim Ts As String = Now.ToString("HH:mm:ss")

                If Not _WasConnected.HasValue Then
                    _WasConnected = IsConnected
                    If IsConnected Then _ConnectedSince = Now
                    Dim Status As String = If(IsConnected, "已连接", "未连接")
                    RaiseLog("[" & Ts & "] 监控启动 - 网络" & Status)
                    RaiseStatus(IsConnected)
                    RaiseSchoolStatus(ServerUrl)

                    If Not IsConnected AndAlso GetAutoReconnect() Then
                        RaiseLog("[" & Ts & "] 启动时已断网，尝试连接…")
                        TryReconnect(ServerUrl, Now)
                    End If

                ElseIf _WasConnected.Value AndAlso Not IsConnected Then
                    RaiseLog("[" & Ts & "] ⚠ 网络已断开")
                    RaiseStatus(False)
                    _WasConnected = False
                    _DisconnectTime = Now

                    Dim SchoolReachable = CheckSchool(ServerUrl)
                    _DisconnectSchoolReachable = SchoolReachable
                    Dim OnlineDuration As String = If(_ConnectedSince.HasValue, FormatDuration(Now - _ConnectedSince.Value), "未知")
                    Dim Reason As String = If(SchoolReachable, "认证服务器可达,疑似认证丢失", "认证服务器不可达,疑似物理断网")
                    WriteDisconnectLog("[" & Now.ToString("HH:mm:ss") & "] ⬇ 中断开始 | 已在线 " & OnlineDuration & " | " & Reason)

                    If GetAutoReconnect() Then
                        RaiseLog("[" & Ts & "] 尝试自动重连…")
                        Dim Ok = TryReconnect(ServerUrl, Now)
                        If Ok Then
                            Dim Dur As String = If(_DisconnectTime.HasValue, FormatDuration(DateTime.Now - _DisconnectTime.Value), "未知")
                            Dim SchoolNow = CheckSchool(ServerUrl)
                            WriteDisconnectLog("[" & DateTime.Now.ToString("HH:mm:ss") & "] ⬆ 中断结束(自动重连) | 持续 " & Dur & " | 认证服务器: " & If(SchoolNow, "可达", "不可达"))
                        End If
                    End If

                ElseIf Not _WasConnected.Value AndAlso IsConnected Then
                    RaiseLog("[" & Ts & "] 网络已恢复")
                    RaiseStatus(True)
                    _WasConnected = True
                    _ConnectedSince = Now

                    Dim Dur As String = If(_DisconnectTime.HasValue, FormatDuration(Now - _DisconnectTime.Value), "未知")
                    Dim SchoolNow = CheckSchool(ServerUrl)
                    WriteDisconnectLog("[" & Now.ToString("HH:mm:ss") & "] ⬆ 中断结束(自行恢复) | 持续 " & Dur & " | 认证服务器: " & If(SchoolNow, "可达", "不可达"))

                ElseIf _WasConnected.Value AndAlso IsConnected Then
                    If Not _ConnectedSince.HasValue Then _ConnectedSince = Now
                End If

                Dim Interval As Integer = Math.Max(1, GetReconnectInterval())
                _StopEvent.WaitOne(Interval * 1000)

            Catch ex As Exception
                RaiseLog("[" & DateTime.Now.ToString("HH:mm:ss") & "] 监控线程异常: " & ex.ToString())
                _StopEvent.WaitOne(5000)
            End Try
        Loop
    End Sub

    ''' <summary>
    ''' 自动重连。与手动“连接”走完全相同的动态认证链：
    '''     读取最新 AppConfig → 解密密码 → PortalAccount
    '''     → ModAuthentication.Authenticate → ModPortalDiscover / ModAuth / ModCrypto → LoginPayload
    '''
    ''' 每次重连都会重新探测门户，因此不再依赖旧配置里那份过期的 queryString，
    ''' DHCP 换 IP 之后同样能拿到新的认证参数。
    ''' 由于每次都会重新读配置文件，改完学号/密码/运营商无需重启程序即可生效。
    ''' </summary>
    Private Function TryReconnect(ServerUrl As String, Now As DateTime) As Boolean
        Dim Ts As String = Now.ToString("HH:mm:ss")
        ModAuthTrace.CountReconnect()
        RaiseLog("[" & Ts & "] 正在获取门户认证参数并提交认证…")

        ' ---- 1. 读取最新配置并构造运行期账号 ----
        Dim Context As RuntimeAuthContext = Nothing
        Try
            Context = BuildRuntimeAuthContext(BindAddress:=ResolveBindAddressForReconnect(Ts))
        Catch ex As Exception
            Dim Msg0 As String = "读取配置异常 (" & ex.Message & ")"
            RaiseLog("[" & DateTime.Now.ToString("HH:mm:ss") & "] 自动重连失败: " & Msg0)
            WriteDisconnectLog("[" & DateTime.Now.ToString("HH:mm:ss") & "]   自动重连: 失败 (" & Msg0 & ")")
            Return False
        End Try

        If Not Context.IsValid Then
            ' 账户/配置问题：本地就能判定，不发无效请求
            RaiseLog("[" & DateTime.Now.ToString("HH:mm:ss") & "] 自动重连失败: " & Context.ErrorMessage)
            WriteDisconnectLog("[" & DateTime.Now.ToString("HH:mm:ss") & "]   自动重连: 失败 (" & Context.ErrorMessage & ")")
            Return False
        End If

        ' ---- 2. 走统一认证链 ----
        Dim AuthResult As AuthenticationResult = Nothing
        Try
            AuthResult = Authenticate(Context.Account, Context.ProbeUrl, Context.Server,
                                      Context.Timeout, Context.BindAddress)
        Catch ex As Exception
            Dim Msg1 As String = "认证异常 (" & ex.Message & ")"
            RaiseLog("[" & DateTime.Now.ToString("HH:mm:ss") & "] 自动重连失败: " & Msg1)
            WriteDisconnectLog("[" & DateTime.Now.ToString("HH:mm:ss") & "]   自动重连: 失败 (" & Msg1 & ")")
            Return False
        End Try

        Dim Now2 As DateTime = DateTime.Now
        Dim Ts2 As String = Now2.ToString("HH:mm:ss")
        If AuthResult IsNot Nothing AndAlso AuthResult.Success Then
            RaiseLog("[" & Ts2 & "] 自动重连成功")
            RaiseStatus(True)
            _WasConnected = True
            _ConnectedSince = Now2
            Return True
        End If

        ' 失败：按 网络 / 认证参数 / 账户 / 门户 四类给出说明
        Dim Msg As String = DescribeFailure(AuthResult)
        RaiseLog("[" & Ts2 & "] 自动重连失败: " & Msg)
        WriteDisconnectLog("[" & Now2.ToString("HH:mm:ss") & "]   自动重连: 失败 (" & Msg & ")")
        Return False
    End Function

    ''' <summary>
    ''' 自动重连前重新确定出口地址。
    '''
    ''' _BindAddress 是构造时记下的，一旦 DHCP 换 IP、换网卡或 Wi-Fi/有线切换就失效了。
    ''' 因此每次重连都校验一次：还在用就沿用，失效就重新解析。
    ''' 目标链路：断线 → ResolveBindAddress → Discovery → pageInfo → 加密 → Login。
    ''' </summary>
    Private Function ResolveBindAddressForReconnect(Ts As String) As String
        Try
            If Not String.IsNullOrEmpty(_BindAddress) AndAlso
               ModNetwork.IsLocalAddressPresent(_BindAddress) Then
                Return _BindAddress
            End If

            Dim Fresh As String = ModNetwork.ResolveBindAddress(GetServerUrl())
            If Not String.IsNullOrEmpty(_BindAddress) AndAlso Fresh <> _BindAddress Then
                RaiseLog("[" & Ts & "] 原出口地址已失效，自动切换到 " & If(Fresh = "", "(默认路由)", Fresh))
            End If
            Return Fresh
        Catch
            ' 解析失败退回构造时记下的值，让 BuildRuntimeAuthContext 再兜一次
            Return _BindAddress
        End Try
    End Function

    Private Sub RaiseLog(Msg As String)
        RaiseEvent LogMessage(Msg)
        DailyWrite(Msg & vbCrLf)
        SyncLock _RecentLogsLock
            _RecentLogs.Enqueue(Msg)
            While _RecentLogs.Count > 50
                _RecentLogs.Dequeue()
            End While
        End SyncLock
    End Sub

    Private Sub RaiseStatus(Connected As Boolean)
        RaiseEvent StatusChanged(Connected)
    End Sub

    Private Sub RaiseSchoolStatus(ServerUrl As String)
        Dim Reachable As Boolean = False
        Try
            Reachable = TcpProbe(ServerUrl)
        Catch
        End Try
        _SchoolReachable = Reachable
        RaiseEvent SchoolStatusChanged(Reachable)
    End Sub

    Private Function CheckSchool(ServerUrl As String) As Boolean
        Try
            Return TcpProbe(ServerUrl)
        Catch
            Return False
        End Try
    End Function

    Private Sub WriteDisconnectLog(Msg As String)
        DailyWrite(Msg & vbCrLf)
    End Sub

    Private Function GetServerUrl() As String
        Dim Server As String = GetDictStr(GetUrlDict(_Cfg), ConfigKeys.Server)
        If Server = "" Then Return "http://127.0.0.1"
        Return Server
    End Function

    Private Function GetAutoReconnect() As Boolean
        Return GetDictBool(GetFunctionDict(_Cfg), ConfigKeys.AutoReconnect, False)
    End Function

    Private Function GetReconnectInterval() As Integer
        Return GetDictInt(GetFunctionDict(_Cfg), ConfigKeys.ReconnectInterval, 5)
    End Function

End Class
