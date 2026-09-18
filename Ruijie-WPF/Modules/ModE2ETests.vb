Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' 开发期真实环境端到端认证测试（--e2e）。
'''
''' 与 --test 的区别：
'''   --test  纯离线，117 项单元测试，不碰网络。
'''   --e2e   真实网络：会真的登出、真的登录、真的改变校园网认证状态。
'''
''' 退出码：
'''   0 = E2E 完整成功（结束时保持已认证）
'''   1 = E2E 实际测试失败
'''   2 = 配置未就绪（不会改变网络状态）
'''
''' 安全约定：所有敏感值（密码明文/密文、queryString、userIndex、Cookie、参数真实取值）
''' 一律不输出，只以 configured / FOUND / OK / obtained 之类的状态词表示。
''' </summary>
Public Module ModE2ETests

#Region "退出码"

    Public Const ExitSuccess As Integer = 0
    Public Const ExitFailed As Integer = 1
    Public Const ExitBlocked As Integer = 2

#End Region

#Region "输出小工具"

    Private Sub Head(Index As Integer, Title As String)
        Console.WriteLine("[" & Index & "] " & Title)
    End Sub

    Private Sub Item(Name As String, Value As String)
        Console.WriteLine("    " & Name.PadRight(18) & ": " & Value)
    End Sub

    Private Sub Blank()
        Console.WriteLine()
    End Sub

    Private Function YesNo(Value As Boolean) As String
        Return If(Value, "YES", "NO")
    End Function

    Private Function Found(Value As Boolean) As String
        Return If(Value, "FOUND", "NOT FOUND")
    End Function

    Private Function OkFail(Value As Boolean) As String
        Return If(Value, "OK", "FAIL")
    End Function

    ''' <summary>配置未就绪：明确说明并返回退出码 2，绝不改变网络状态。</summary>
    Private Function Blocked(Reason As String) As Integer
        Console.WriteLine("E2E BLOCKED")
        Console.WriteLine(Reason)
        Console.WriteLine()
        Console.WriteLine("=== E2E BLOCKED (exit 2) ===")
        Return ExitBlocked
    End Function

#End Region

#Region "主流程"

    ''' <summary>
    ''' 执行一次真实端到端认证测试。
    ''' </summary>
    ''' <param name="BindAddress">需要绕过 VPN/TUN 时绑定的本机地址；普通情况留空。</param>
    ''' <param name="WithReconnect">是否包含自动重连实测（会真的制造一次断线）。</param>
    Public Function Run(Optional BindAddress As String = "",
                        Optional WithReconnect As Boolean = True,
                        Optional Timeout As Integer = 15) As Integer

        Console.WriteLine("=== Ruijie ePorta E2E ===" & vbCrLf)

        ' ================= [1] 配置 =================
        Dim Cfg As AppConfig = Nothing
        Try
            Cfg = ModConfig.LoadAppConfig()
        Catch ex As Exception
            Head(1, "Configuration")
            Item("Result", "读取失败：" & ex.Message)
            Blank()
            Return Blocked("无法读取配置文件。")
        End Try

        Dim Missing As New List(Of String)
        If Cfg.Version <> ModConfig.CurrentConfigVersion Then
            Missing.Add("配置文件版本为 v" & Cfg.Version & "，需要 v" & ModConfig.CurrentConfigVersion)
        End If
        If String.IsNullOrEmpty(Cfg.User.UserId) Then Missing.Add("没有保存学号")
        If ModConfig.ReadStoredProtectedPassword().Length = 0 Then Missing.Add("没有保存密码")
        If Cfg.PasswordNeedsReentry Then Missing.Add("已保存的密码无法解密")
        If Cfg.User.[Operator] = PortalOperator.Unknown Then Missing.Add("没有有效的网络类型")

        Head(1, "Configuration")
        Item("Version", Cfg.Version.ToString())
        Item("User", If(String.IsNullOrEmpty(Cfg.User.UserId), "missing", "configured"))
        Item("Password", If(String.IsNullOrEmpty(Cfg.User.Password), "missing", "configured"))
        Item("Operator", ModConfig.GetOperatorToken(Cfg.User.[Operator]))
        Item("Ready", YesNo(Missing.Count = 0))
        Blank()

        If Missing.Count > 0 Then
            Return Blocked("请先在配置页保存校园网密码并选择网络类型。" & vbCrLf &
                           "原因：" & String.Join("；", Missing.ToArray()))
        End If

        Dim Context As RuntimeAuthContext = ModAuthentication.BuildRuntimeAuthContext(Cfg, "", BindAddress, Timeout)
        If Not Context.IsValid Then Return Blocked(Context.ErrorMessage)
        Dim Account As PortalAccount = Context.Account

        ' ================= [2] 门户发现（登录前） =================
        Dim Discovery As ModPortalDiscover.PortalDiscoveryResult = Nothing
        Try
            Discovery = ModPortalDiscover.Discover(Context.ProbeUrl, Context.Server, Timeout, BindAddress)
        Catch ex As ModPortalDiscover.PortalDiscoveryException
            Head(2, "Portal Discovery")
            Item("Status", ex.Status.ToString())
            Item("Result", "FAIL：" & ex.Message)
            Blank()
            Console.WriteLine("=== E2E FAIL (exit 1) ===")
            Return ExitFailed
        End Try

        If Not PrintDiscovery(2, Discovery) Then
            Console.WriteLine("=== E2E FAIL (exit 1) ===")
            Return ExitFailed
        End If

        ' ================= 若已联网，先登出进入未认证状态 =================
        Dim WasOnline As Boolean = (Discovery.Status = ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline)
        If WasOnline Then
            Console.WriteLine("    （当前已联网，先登出以进入未认证状态）")
            Blank()

            Dim LogoutFirst As ModAuthentication.LogoutResult = ModAuthentication.LogoutAuthenticated(Context.Server, Timeout)
            If LogoutFirst.Status <> ModAuthentication.PortalLogoutStatus.Success Then
                Head(3, "Precondition Logout")
                Item("Result", LogoutFirst.Status.ToString())
                Item("Message", LogoutFirst.Message)
                Blank()
                Console.WriteLine("未能进入未认证状态，无法继续真实登录验证。")
                Console.WriteLine("=== E2E FAIL (exit 1) ===")
                Return ExitFailed
            End If

            ' 等门户会话真正失效
            Thread.Sleep(1500)

            ' ================= [3] 未认证状态下的门户发现（关键分支） =================
            Try
                Discovery = ModPortalDiscover.Discover(Context.ProbeUrl, Context.Server, Timeout, BindAddress)
            Catch ex As ModPortalDiscover.PortalDiscoveryException
                Head(3, "Portal Discovery (unauthenticated)")
                Item("Status", ex.Status.ToString())
                Item("Result", "FAIL：" & ex.Message)
                Blank()
                Console.WriteLine("=== E2E FAIL (exit 1) ===")
                Return ExitFailed
            End Try

            If Not PrintDiscovery(3, Discovery) Then
                Console.WriteLine("=== E2E FAIL (exit 1) ===")
                Return ExitFailed
            End If

            If Discovery.Status <> ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication Then
                Console.WriteLine("    预期 NeedAuthentication，实际 " & Discovery.Status.ToString())
                Blank()
                Console.WriteLine("=== E2E FAIL (exit 1) ===")
                Return ExitFailed
            End If

            ' 未认证时应能拿到完整认证参数
            If Not PrintRedirectParams(Discovery) Then
                Console.WriteLine("=== E2E FAIL (exit 1) ===")
                Return ExitFailed
            End If
            Blank()
        Else
            Console.WriteLine("    （当前未认证，跳过登出准备步骤）")
            Blank()
        End If

        ' ================= [4] 认证 Payload =================
        Dim Prepared As AuthenticationResult = ModAuthentication.PreparePayload(Account, Discovery)
        Head(4, "Payload")
        Item("UserId", If(String.IsNullOrEmpty(Account.UserId), "missing", "configured"))
        Item("Service", If(Prepared.Payload Is Nothing, "(n/a)", Prepared.Payload.Service))
        Item("PasswordEncrypt", If(Prepared.Payload Is Nothing, "(n/a)", Prepared.Payload.PasswordEncrypt.ToString().ToLower()))
        Item("QueryString", If(Discovery.Redirect IsNot Nothing AndAlso Discovery.Redirect.QueryString.Length > 0, "FOUND", "NOT FOUND"))
        Item("Password", If(Prepared.Payload Is Nothing, "(n/a)", If(String.IsNullOrEmpty(Prepared.Payload.Password), "missing", "generated")))
        Item("Payload", OkFail(Prepared.Success))
        If Not Prepared.Success Then Item("Message", Prepared.Message)
        Blank()

        If Not Prepared.Success Then
            If Prepared.Failure = AuthFailure.ValidCodeRequired Then
                Console.WriteLine("E2E = NEED_VALID_CODE")
                Console.WriteLine("门户要求验证码，本入口不会自动处理验证码，请人工完成一次认证后再试。")
                Console.WriteLine()
                Console.WriteLine("=== E2E NEED_VALID_CODE (exit 1) ===")
                Return ExitFailed
            End If
            Console.WriteLine("=== E2E FAIL (exit 1) ===")
            Return ExitFailed
        End If

        ' 学号必须始终是纯学号
        If Prepared.Payload.UserId.Contains("@") Then
            Console.WriteLine("    UserId 含 @ 后缀，违反校园网 Web 认证规则。")
            Blank()
            Console.WriteLine("=== E2E FAIL (exit 1) ===")
            Return ExitFailed
        End If

        ' ================= [5] 真实登录 =================
        Dim Login As AuthenticationResult = ModAuthentication.Authenticate(
            Account, Context.ProbeUrl, Context.Server, Timeout, BindAddress)

        Head(5, "Login")
        Item("Result", If(Login.LoginResult Is Nothing, Login.Message, Login.LoginResult.Result))
        Item("UserIndex", If(Login.LoginResult IsNot Nothing AndAlso Login.LoginResult.UserIndex.Length > 0,
                             "obtained", "none"))
        If Login.LoginResult IsNot Nothing Then
            Item("KeepaliveInterval", Login.LoginResult.KeepaliveInterval.ToString())
        End If
        Blank()

        If Not Login.Success Then
            Console.WriteLine("    Failure        : " & Login.Failure.ToString())
            Console.WriteLine("    DiscoveryStatus: " & Login.DiscoveryStatus.ToString())
            Console.WriteLine("    Message        : " & ModAuthentication.DescribeFailure(Login))
            Blank()
            Console.WriteLine("NETWORK STATE AFTER E2E:")
            Console.WriteLine(If(ModNetwork.TestInternet(Timeout:=3), "  Authenticated（登录失败但仍可上网）", "  Unauthenticated"))
            Console.WriteLine()
            Console.WriteLine("=== E2E FAIL (exit 1) ===")
            Return ExitFailed
        End If

        ' ================= [6] 互联网连通性 =================
        Dim Online As Boolean = ModNetwork.TestInternet(Timeout:=3)
        Head(6, "Internet")
        Item("Result", If(Online, "connected", "NOT connected"))
        Blank()

        If Not Online Then
            Console.WriteLine("    登录成功但外网仍不通，可能门户还有后续步骤。")
            Console.WriteLine("=== E2E FAIL (exit 1) ===")
            Return ExitFailed
        End If

        ' ================= [7] 自动重连（可选） =================
        If WithReconnect Then
            If Not RunReconnectCheck(Context, Timeout) Then
                Console.WriteLine("=== E2E FAIL (exit 1) ===")
                Return ExitFailed
            End If
        Else
            Head(7, "Auto-Reconnect")
            Item("Result", "skipped")
            Blank()
        End If

        ' ================= [8] 断开 =================
        Dim LogoutResult As ModAuthentication.LogoutResult = ModAuthentication.LogoutAuthenticated(Context.Server, Timeout)
        Head(8, "Logout")
        Item("Result", LogoutResult.Status.ToString().ToLower())
        If LogoutResult.Status <> ModAuthentication.PortalLogoutStatus.Success Then
            Item("Message", LogoutResult.Message)
        End If
        Blank()

        If LogoutResult.Status <> ModAuthentication.PortalLogoutStatus.Success Then
            Console.WriteLine("=== E2E FAIL (exit 1) ===")
            Return ExitFailed
        End If

        ' 确认确实回到未认证状态
        Thread.Sleep(1500)
        Dim AfterLogoutOnline As Boolean = ModNetwork.TestInternet(Timeout:=3)
        Console.WriteLine("    Internet after logout : " & If(AfterLogoutOnline, "still connected（门户可能未立即生效）", "unauthenticated"))
        Blank()

        ' ================= [9] 恢复：结束时保持已认证 =================
        Console.WriteLine("[9] Restore")
        Dim Restore As AuthenticationResult = ModAuthentication.Authenticate(
            Account, Context.ProbeUrl, Context.Server, Timeout, BindAddress)
        Item("Result", If(Restore.Success, "success", "FAIL：" & ModAuthentication.DescribeFailure(Restore)))
        Item("Internet", If(ModNetwork.TestInternet(Timeout:=3), "connected", "NOT connected"))
        Blank()

        If Not Restore.Success Then
            Console.WriteLine("E2E 结束后校园网处于未认证状态，请手动点击「连接」。")
            Console.WriteLine("=== E2E FAIL (exit 1) ===")
            Return ExitFailed
        End If

        Console.WriteLine("=== E2E PASS ===")
        Return ExitSuccess
    End Function

#End Region

#Region "阶段输出"

    Private Function PrintDiscovery(Index As Integer, Discovery As ModPortalDiscover.PortalDiscoveryResult) As Boolean
        Head(Index, "Portal Discovery")
        If Discovery Is Nothing Then
            Item("Status", "(无结果)")
            Blank()
            Return False
        End If

        Item("Status", Discovery.Status.ToString())
        Item("QueryString", Found(Discovery.Redirect IsNot Nothing AndAlso Discovery.Redirect.QueryString.Length > 0))
        Item("MAC", Found(Discovery.Redirect IsNot Nothing AndAlso Not String.IsNullOrEmpty(Discovery.Redirect.Mac)))
        Item("PageInfo", OkFail(Discovery.PageInfo IsNot Nothing))
        Item("PublicKey", OkFail(Discovery.PageInfo IsNot Nothing AndAlso
                                 Discovery.PageInfo.PublicKeyModulus.Length > 0 AndAlso
                                 Discovery.PageInfo.PublicKeyExponent.Length > 0))
        Item("Services", OkFail(Discovery.Services IsNot Nothing AndAlso Discovery.Services.Items.Count > 0))
        If Discovery.PageInfo IsNot Nothing Then
            Item("PasswordEncrypt", Discovery.PageInfo.PasswordEncrypt.ToString().ToLower())
            Item("ValidCodeUrl", If(String.IsNullOrEmpty(Discovery.PageInfo.ValidCodeUrl), "(空，无需验证码)", "(非空，门户要求验证码)"))
        End If
        If Not String.IsNullOrEmpty(Discovery.Message) Then Item("Message", Discovery.Message)
        Blank()

        If Discovery.Status = ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication Then
            Return Discovery.PageInfo IsNot Nothing AndAlso
                   Discovery.Services IsNot Nothing AndAlso
                   Discovery.Services.Items.Count > 0
        End If
        Return Discovery.IsSuccess
    End Function

    ''' <summary>未认证时逐个确认 queryString 里的校园网参数是否存在（不输出真实取值）。</summary>
    Private Function PrintRedirectParams(Discovery As ModPortalDiscover.PortalDiscoveryResult) As Boolean
        Console.WriteLine("    Redirect params (只显示是否存在):")
        If Discovery Is Nothing OrElse Discovery.Redirect Is Nothing Then
            Console.WriteLine("      (无重定向信息)")
            Return False
        End If

        Dim Names As String() = {"wlanuserip", "nasip", "wlanparameter", "userlocation", "url"}
        Dim AllPresent As Boolean = True
        For Each Name In Names
            Dim Present As Boolean = Discovery.Redirect.GetParameter(Name) IsNot Nothing
            If Not Present AndAlso Name <> "url" Then AllPresent = False
            Console.WriteLine("      " & Name.PadRight(16) & ": " & Found(Present))
        Next

        Dim MacValue As String = Discovery.Redirect.Mac
        Console.WriteLine("      " & "mac".PadRight(16) & ": " &
                          If(String.IsNullOrEmpty(MacValue), "NOT FOUND（ModCrypto 将使用默认值）", "FOUND（传给 ModCrypto）"))
        Return AllPresent
    End Function

#End Region

#Region "自动重连实测"

    ''' <summary>
    ''' 用「登出」制造一次真实的认证丢失，观察 NetworkMonitor 是否通过新认证链自动恢复。
    ''' 全程不操作网卡，因此不会把机器弄成物理断网。
    ''' </summary>
    Private Function RunReconnectCheck(Context As RuntimeAuthContext, Timeout As Integer) As Boolean
        Head(7, "Auto-Reconnect")

        Dim FunctionCfg As RuntimeFunctionConfig = ModConfig.GetCurrentFunctionConfig()
        If Not FunctionCfg.AutoReconnect Then
            Item("Result", "skipped（配置里未开启自动重连）")
            Blank()
            Return True
        End If

        ' 用兼容层字典构造监控（NetworkMonitor 仍从 SharedCfg 形状读取开关与间隔）
        Dim Monitor As New NetworkMonitor(ModConfig.ReadCfg(), Context.BindAddress)
        Monitor.Start()
        Thread.Sleep(1500)
        Item("Monitor started", YesNo(True))

        ' 登出 = 真实认证丢失
        Dim Logout As ModAuthentication.LogoutResult = ModAuthentication.LogoutAuthenticated(Context.Server, Timeout)
        Item("Logout to simulate loss", Logout.Status.ToString().ToLower())
        ModAuthentication.ClearSession()

        ' 等监控发现断线并自动重连（间隔 + 探测 + 认证时间）
        Dim WaitMs As Integer = Math.Max(1000, FunctionCfg.ReconnectInterval) * 1000 + 20000
        Monitor.Stop()
        Monitor.Start()
        Dim Deadline As DateTime = DateTime.Now.AddMilliseconds(WaitMs)
        Dim Recovered As Boolean = False
        While DateTime.Now < Deadline
            Thread.Sleep(1000)
            If ModNetwork.TestInternet(Timeout:=3) Then
                Recovered = True
                Exit While
            End If
        End While

        Monitor.Stop()
        Item("Auto reconnect", OkFail(Recovered))
        Item("Internet", If(Recovered, "connected", "NOT connected"))
        Blank()

        If Not Recovered Then
            Console.WriteLine("    自动重连未能在 " & (WaitMs \ 1000) & " 秒内恢复连接。")
            Console.WriteLine("    请查看 logs 目录中的监控日志了解具体失败原因。")
        End If
        Return Recovered
    End Function

#End Region

#Region "只读诊断（--diagnose）"

    ''' <summary>
    ''' 只读运行时诊断：配置 / 路由 / 探测 / 门户 / 分类。
    ''' **绝不**执行 Logout、Login 或开启 NetworkMonitor，因此不会改变网络状态。
    ''' 退出码与 E2E 一致：0 正常 / 1 有失败 / 2 配置未就绪。
    ''' </summary>
    Public Function RunDiagnose(Optional BindAddress As String = "",
                                Optional Timeout As Integer = 10) As Integer
        Console.WriteLine("=== Runtime Diagnose ===" & vbCrLf)

        Dim Problems As Integer = 0

        ' ---------- 配置 ----------
        Dim Cfg As AppConfig = Nothing
        Try
            Cfg = ModConfig.LoadAppConfig()
        Catch ex As Exception
            Console.WriteLine("Config            : FAIL (" & ex.Message & ")")
            Console.WriteLine()
            Console.WriteLine("=== Diagnose BLOCKED (exit 2) ===")
            Return ExitBlocked
        End Try

        Console.WriteLine("Config            : OK (v" & Cfg.Version & ")")
        Console.WriteLine("User              : " & If(String.IsNullOrEmpty(Cfg.User.UserId), "missing", "configured"))
        Console.WriteLine("Password          : " & If(String.IsNullOrEmpty(Cfg.User.Password), "missing", "configured"))
        Console.WriteLine("Operator          : " & ModConfig.GetOperatorToken(Cfg.User.[Operator]))
        Console.WriteLine("AutoReconnect     : " & Cfg.[Function].AutoReconnect.ToString().ToLower() &
                          " (间隔 " & Cfg.[Function].ReconnectInterval & "s)")
        Console.WriteLine()

        If Not String.IsNullOrEmpty(Cfg.NotReadyReason) Then
            Console.WriteLine("Ready             : NO —— " & Cfg.NotReadyReason)
            Console.WriteLine()
            Console.WriteLine("=== Diagnose BLOCKED (exit 2) ===")
            Return ExitBlocked
        End If

        ' ---------- 绑定与路由 ----------
        ' 未显式指定时，报告自动解析的结果 —— 这正是 GUI「连接」实际会用的地址
        Dim Resolution As ModNetwork.BindResolution = ModNetwork.ResolveBindAddressInfo(ModConfig.SchoolServer)
        Dim Explicit As Boolean = Not String.IsNullOrEmpty(BindAddress)
        Dim EffectiveBind As String = If(Explicit, BindAddress, Resolution.Address)

        Console.WriteLine("BindAddress       : " & If(String.IsNullOrEmpty(EffectiveBind), "(无可用出口，走默认路由)", EffectiveBind))
        Console.WriteLine("Source            : " & If(Explicit, "命令行指定",
                                          If(String.IsNullOrEmpty(Resolution.Address), "未解析出可用出口",
                                             Resolution.Source & " / " & Resolution.InterfaceName)))
        Console.WriteLine("Candidates        : " & Resolution.Candidates & "（可用 " & Resolution.UsableCandidates & "）")
        If Not String.IsNullOrEmpty(Resolution.Note) Then
            Console.WriteLine("ResolveNote       : " & Resolution.Note)
        End If
        Console.WriteLine("Selected          : " & If(String.IsNullOrEmpty(EffectiveBind), "FAIL", "PASS"))

        ' 逐条列出候选，让「为什么没选它」一目了然
        For Each C In Resolution.All
            Console.WriteLine("    " & C.Address.PadRight(16) & " " & C.InterfaceName.PadRight(14) & " " &
                              If(C.Usable, "可用", "排除：" & C.ExcludedReason))
        Next

        Console.WriteLine("Routing           : " & DescribeBinding(EffectiveBind, Problems))
        If String.IsNullOrEmpty(EffectiveBind) Then Problems += 1
        Console.WriteLine("ProbeUrl          : " & ModPortalDiscover.DefaultProbeUrl)
        Console.WriteLine("Server            : " & ModConfig.SchoolServer)
        Console.WriteLine()

        ' ---------- 门户 TCP ----------
        Dim PortalTcp As Boolean = ModNetwork.TcpProbe(ModConfig.SchoolServer, 3, EffectiveBind)
        Console.WriteLine("Portal Server     : " & If(PortalTcp, "Reachable", "UNREACHABLE"))
        If Not PortalTcp Then Problems += 1
        Console.WriteLine()

        ' ---------- 探测 ----------
        Dim Probe As ModPortalDiscover.PortalDiscoveryResult =
            ModPortalDiscover.DiscoverRedirect(ModPortalDiscover.DefaultProbeUrl, Timeout, EffectiveBind)

        Dim ProbeFailed As Boolean = (Probe.Status = ModPortalDiscover.PortalDiscoveryStatus.ProbeFailed OrElse
                                      Probe.Status = ModPortalDiscover.PortalDiscoveryStatus.Timeout)
        Console.WriteLine("Probe             : " & If(ProbeFailed, "FAILED", "OK"))
        Console.WriteLine("Probe Result      : " & Probe.Status.ToString())
        Console.WriteLine("Probe Message     : " & Probe.Message)
        If ProbeFailed Then Problems += 1
        Console.WriteLine()

        ' ---------- 完整发现 ----------
        Dim Discovery As ModPortalDiscover.PortalDiscoveryResult = Nothing
        Try
            Discovery = ModPortalDiscover.Discover(ModPortalDiscover.DefaultProbeUrl,
                                                   ModConfig.SchoolServer, Timeout, EffectiveBind)
        Catch ex As ModPortalDiscover.PortalDiscoveryException
            Console.WriteLine("Discovery         : FAIL (" & ex.Status.ToString() & "：" & ex.Message & ")")
            Console.WriteLine()
            Console.WriteLine("=== Diagnose FAIL (exit 1) ===")
            Return ExitFailed
        End Try

        Dim DiscoveryOk As Boolean = Discovery.IsSuccess

        ' AlreadyOnline 表示「当前已经联网」，此时门户参数本来就不需要取，
        ' 缺失不是失败。必须与真正的 FAIL 区分开，否则正常状态会被打印成一串 FAIL。
        Dim NeedPortalParams As Boolean =
            (Discovery.Status <> ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline)
        Const NotNeeded As String = "n/a (已联网，无需门户参数)"

        Console.WriteLine("Discovery         : " & OkFail(DiscoveryOk) & " (" & Discovery.Status.ToString() & ")")
        Console.WriteLine("QueryString       : " &
                          If(NeedPortalParams,
                             Found(Discovery.Redirect IsNot Nothing AndAlso Discovery.Redirect.QueryString.Length > 0),
                             NotNeeded))
        Console.WriteLine("MAC               : " &
                          If(NeedPortalParams,
                             Found(Discovery.Redirect IsNot Nothing AndAlso Not String.IsNullOrEmpty(Discovery.Redirect.Mac)),
                             NotNeeded))
        Console.WriteLine("PageInfo          : " &
                          If(NeedPortalParams, OkFail(Discovery.PageInfo IsNot Nothing), NotNeeded))
        Console.WriteLine("PublicKey         : " &
                          If(NeedPortalParams,
                             OkFail(Discovery.PageInfo IsNot Nothing AndAlso
                                    Discovery.PageInfo.PublicKeyModulus.Length > 0),
                             NotNeeded))
        Console.WriteLine("Services          : " &
                          If(NeedPortalParams,
                             OkFail(Discovery.Services IsNot Nothing AndAlso
                                    Discovery.Services.Items.Count > 0),
                             NotNeeded))
        Console.WriteLine("Message           : " & Discovery.Message)
        If Not DiscoveryOk Then Problems += 1
        Console.WriteLine()

        ' ---------- 结论 ----------
        If Problems = 0 Then
            Console.WriteLine("=== Diagnose PASS (exit 0) ===")
            Return ExitSuccess
        End If
        Console.WriteLine("=== Diagnose FAIL (exit 1)，共 " & Problems & " 项异常 ===")
        Return ExitFailed
    End Function

    ''' <summary>描述当前绑定是否指向真实网卡；未指定时提示可能被 VPN/TUN 接管。</summary>
    Private Function DescribeBinding(BindAddress As String, ByRef Problems As Integer) As String
        If String.IsNullOrEmpty(BindAddress) Then
            Return "Default route（若装有 VPN/TUN，校园网请求可能被接管）"
        End If

        Dim FoundLocal As Boolean = False
        Try
            For Each Nic In Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                If Nic.OperationalStatus <> Net.NetworkInformation.OperationalStatus.Up Then Continue For
                For Each Addr In Nic.GetIPProperties().UnicastAddresses
                    If Addr.Address.AddressFamily = Net.Sockets.AddressFamily.InterNetwork AndAlso
                       Addr.Address.ToString() = BindAddress.Trim() Then
                        FoundLocal = True
                        Return "Bound to " & Nic.Name & " (" & BindAddress & ")"
                    End If
                Next
            Next
        Catch
        End Try

        If Not FoundLocal Then
            Problems += 1
            Return "BindAddress " & BindAddress & " 不是本机在用地址（绑定会失败，将退回默认路由）"
        End If
        Return BindAddress
    End Function

#End Region

End Module