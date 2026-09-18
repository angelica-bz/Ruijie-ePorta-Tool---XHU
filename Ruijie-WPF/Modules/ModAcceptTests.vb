Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' 开发期**真实环境验收**（--accept）。
'''
''' 与另外两个入口的区别：
'''   --test     纯离线，125 项单元测试，不碰网络。
'''   --diagnose **只读**：探测 + 门户 TCP + 发现分类，不改变认证状态。
'''   --e2e       真实端到端：会登出、会登录，但只给一个笼统的 PASS/FAIL。
'''   --accept    真实端到端，且**按阶段**给出 PASS/FAIL，
'''               并把每个关键动态量（queryString 参数存在性、公钥长度、
'''               密文长度、userId 后缀、自动重连的三项计数）单独核对。
'''
''' 设计原则：
'''   1. 分阶段判定。任一阶段失败就停在原地并报告该阶段，不做后续破坏性操作，
'''      绝不把「门户没劫持」「密码错了」「验证码要填」混成同一句错误。
'''   2. 敏感值一律不输出。密码明文/密文、queryString 真实取值、userIndex、
'''      Cookie、wlanuserip 全部只以 configured / FOUND / obtained / Changed 表示。
'''   3. 不改用户配置。自动重连需要开关打开，但**只在内存字典里打开**，
'''      不写回 bin/config.yml。
'''
''' 退出码：
'''   0 = 全部阶段 PASS
'''   1 = 有阶段 FAIL
'''   2 = 配置未就绪（不会改变网络状态）
'''   3 = 需要验证码，无法自动完成（已停在安全位置）
''' </summary>
Public Module ModAcceptTests

#Region "退出码"

    Public Const ExitPass As Integer = 0
    Public Const ExitFail As Integer = 1
    Public Const ExitBlocked As Integer = 2
    Public Const ExitNeedValidCode As Integer = 3

#End Region

#Region "阶段台账"

    ' §二十 要求的固定阶段顺序。未执行到的阶段一律显示 SKIPPED。
    Private ReadOnly StageOrder As String() = {
        "Configuration",
        "Portal Redirect",
        "QueryString",
        "PageInfo",
        "PublicKey",
        "Services",
        "PasswordEncryption",
        "Payload",
        "Login",
        "Internet",
        "Logout",
        "Second Login",
        "Auto Reconnect"}

    ''' <summary>供离线测试核对阶段顺序（§二十 要求的固定 13 项）。只读，不参与运行。</summary>
    Public ReadOnly Property StageOrderForTest As String()
        Get
            Return StageOrder
        End Get
    End Property

    Private ReadOnly Results As New Dictionary(Of String, String)
    Private ReadOnly Details As New Dictionary(Of String, String)

    ''' <summary>本次验收使用的绑定地址。恢复网络时必须沿用，否则可能因 VPN/TUN 抢路由而恢复不了。</summary>
    Private _BindAddress As String = ""

    ' 阶段结论只有四种，含义必须明确：
    '   PASS    —— 本阶段真实验证通过
    '   FAIL    —— 本阶段验证失败，已停在原地
    '   SKIP    —— 本次运行主动跳过（例如 --accept-no-reconnect）
    '   BLOCKED —— 因前置阶段失败/需要验证码而未执行
    Public Const OutcomePass As String = "PASS"
    Public Const OutcomeFail As String = "FAIL"
    Public Const OutcomeSkip As String = "SKIP"
    Public Const OutcomeBlocked As String = "BLOCKED"

    Private Sub Verdict(Name As String, Outcome As String, Optional Detail As String = "")
        Results(Name) = Outcome
        Details(Name) = Detail
        Console.WriteLine("    >>> " & Name.PadRight(20) & " " & Outcome &
                          If(String.IsNullOrEmpty(Detail), "", "  —— " & Detail))
        Console.WriteLine()
    End Sub

    ''' <summary>本阶段还没跑到：因为前面某阶段已经失败，或整轮在更早处停下。</summary>
    Private Function OutcomeOf(Name As String) As String
        If Results.ContainsKey(Name) Then Return Results(Name)
        Return OutcomeBlocked
    End Function

    Private Function AllPassed() As Boolean
        For Each Name In StageOrder
            If OutcomeOf(Name) <> OutcomePass Then Return False
        Next
        Return True
    End Function

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
        Return If(Value, "FOUND", "MISSING")
    End Function

    Private Function OkFail(Value As Boolean) As String
        Return If(Value, "OK", "FAIL")
    End Function

    ''' <summary>失败时的统一收尾：报告失败阶段 + 相关状态，不再做破坏性动作。</summary>
    Private Function Abort(StageName As String, Reason As String,
                           Optional Extra As String = "") As Integer
        Verdict(StageName, OutcomeFail, Reason)
        Console.WriteLine("=== ACCEPT FAIL ===")
        Console.WriteLine("Failed Stage : " & StageName)
        Console.WriteLine("Reason       : " & Reason)
        If Not String.IsNullOrEmpty(Extra) Then Console.WriteLine(Extra)
        Console.WriteLine()

        ' 诊断已经出完，接下来只做一件事：尽量别把用户的网断着。
        TryRestore()

        WriteSummary()
        Return If(Results("Configuration") = OutcomePass, ExitFail, ExitBlocked)
    End Function

    ''' <summary>
    ''' 尽力把校园网恢复到已认证状态。
    ''' 验收过程中只要登出过，失败退出时机器就会断网；这里补一次认证，
    ''' 用的是与正常「连接」完全相同的链路。失败也不影响已经给出的诊断结论。
    ''' </summary>
    Private Sub TryRestore()
        Console.WriteLine("NETWORK STATE AFTER ACCEPT:")
        If SafeInternet() Then
            Console.WriteLine("  Internet   : connected（仍处于已认证状态）")
            Console.WriteLine()
            Return
        End If

        Console.WriteLine("  Internet   : unauthenticated（验收过程中已登出，正在尽力恢复…）")
        Try
            Dim Cfg As AppConfig = ModConfig.LoadAppConfig()
            If Cfg IsNot Nothing AndAlso Not Cfg.PasswordNeedsReentry AndAlso
               Not String.IsNullOrEmpty(Cfg.User.UserId) Then
                Dim Context As RuntimeAuthContext = ModAuthentication.BuildRuntimeAuthContext(
                    Cfg, "", _BindAddress)
                If Context.IsValid Then
                    Dim Restore As AuthenticationResult = ModAuthentication.Authenticate(Context.Account)
                    Console.WriteLine("  Restore    : " & If(Restore.Success, "success", "failed —— " & ModAuthentication.DescribeFailure(Restore)))
                    Console.WriteLine("  Internet   : " & If(SafeInternet(), "connected", "still unauthenticated"))
                Else
                    Console.WriteLine("  Restore    : skipped（" & Context.ErrorMessage & "）")
                End If
            Else
                Console.WriteLine("  Restore    : skipped（配置未就绪）")
            End If
        Catch ex As Exception
            Console.WriteLine("  Restore    : exception —— " & ex.Message)
        End Try
        Console.WriteLine()
    End Sub

    Private Function SafeInternet() As Boolean
        Try
            Return ModNetwork.TestInternet(Timeout:=3)
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 登录成功后确认外网真的通了 —— **带一个有限的重试窗口**。
    '''
    ''' 门户返回 result=success 与 BRAS 真正放行流量之间可能差几百毫秒到几秒
    ''' （实测遇到过：Login=PASS 但紧接着单次探测仍然不通，几秒后自行恢复）。
    ''' 只看一次会把「刚登录还没生效」误判成 Internet 失败，所以这里给窗口重试。
    ''' 反过来，窗口内始终不通才是真的有问题。
    ''' </summary>
    Private Function WaitForInternet(Optional Seconds As Integer = 20) As Boolean
        Dim Deadline As DateTime = DateTime.Now.AddSeconds(Seconds)
        Do
            If SafeInternet() Then Return True
            Thread.Sleep(1000)
        Loop While DateTime.Now < Deadline
        Return False
    End Function

    Private Sub WriteSummary()
        Console.WriteLine("=== REAL E2E RESULT ===")
        Console.WriteLine()
        For Each Name In StageOrder
            Dim Detail As String = ""
            If Details.ContainsKey(Name) AndAlso Not String.IsNullOrEmpty(Details(Name)) Then
                Detail = "   (" & Details(Name) & ")"
            End If
            Console.WriteLine(Name & ":" & vbTab & OutcomeOf(Name) & Detail)
        Next
        Console.WriteLine()
    End Sub

#End Region

#Region "主流程"

    ''' <summary>
    ''' 执行一次完整的真实验收。
    ''' </summary>
    ''' <param name="BindAddress">需要绕过 VPN/TUN 时绑定的本机地址；普通情况留空。</param>
    ''' <param name="Timeout">单次网络请求超时（秒）。</param>
    ''' <param name="WithReconnect">是否包含自动重连实测（会真的制造一次认证丢失）。</param>
    Public Function Run(Optional BindAddress As String = "",
                        Optional Timeout As Integer = 15,
                        Optional WithReconnect As Boolean = True) As Integer

        Results.Clear()
        Details.Clear()
        _BindAddress = If(BindAddress, "")

        Console.WriteLine("=== Ruijie ePorta ACCEPT（真实环境验收）===" & vbCrLf)

        ' ============================================================
        ' [1] Configuration
        ' ============================================================
        Dim Cfg As AppConfig = Nothing
        Try
            Cfg = ModConfig.LoadAppConfig()
        Catch ex As Exception
            Head(1, "Configuration")
            Item("Result", "读取失败：" & ex.Message)
            Blank()
            Verdict("Configuration", OutcomeFail, "无法读取配置文件")
            WriteSummary()
            Return ExitBlocked
        End Try

        Dim Missing As New List(Of String)
        If Cfg.Version <> ModConfig.CurrentConfigVersion Then
            Missing.Add("配置版本 v" & Cfg.Version & "，需要 v" & ModConfig.CurrentConfigVersion)
        End If
        If String.IsNullOrEmpty(Cfg.User.UserId) Then Missing.Add("没有保存学号")
        If ModConfig.ReadStoredProtectedPassword().Length = 0 Then Missing.Add("没有保存密码")
        If Cfg.PasswordNeedsReentry Then Missing.Add("已保存的密码无法解密（DPAPI）")
        If Cfg.User.[Operator] = PortalOperator.Unknown Then Missing.Add("没有有效的网络类型")

        Head(1, "Configuration")
        Item("Version", "v" & Cfg.Version)
        Item("User", If(String.IsNullOrEmpty(Cfg.User.UserId), "missing", "configured"))
        Item("Password", If(String.IsNullOrEmpty(Cfg.User.Password), "missing", "configured"))
        Item("Operator", ModConfig.GetOperatorToken(Cfg.User.[Operator]))
        Item("AutoReconnect(config)", Cfg.[Function].AutoReconnect.ToString().ToLower())
        Item("Ready", YesNo(Missing.Count = 0))
        Blank()

        If Missing.Count > 0 Then
            Verdict("Configuration", OutcomeFail, String.Join("；", Missing.ToArray()))
            WriteSummary()
            Console.WriteLine("=== ACCEPT BLOCKED (exit 2) ===")
            Return ExitBlocked
        End If
        Verdict("Configuration", OutcomePass)

        Dim Context As RuntimeAuthContext = ModAuthentication.BuildRuntimeAuthContext(Cfg, "", BindAddress, Timeout)
        If Not Context.IsValid Then
            Verdict("Configuration", OutcomeFail, Context.ErrorMessage)
            WriteSummary()
            Return ExitBlocked
        End If
        Dim Account As PortalAccount = Context.Account

        ' 本次验收实际使用的出口地址：BuildRuntimeAuthContext 已经自动解析过。
        ' 不带 --bind 时这里就是自动解析的结果 —— 与 GUI「连接」走的是同一条路。
        Dim EffectiveBind As String = Context.BindAddress
        _BindAddress = EffectiveBind
        Item("BindAddress", If(String.IsNullOrEmpty(EffectiveBind), "(默认路由)", EffectiveBind))

        ' ============================================================
        ' [2] 进入未认证状态
        ' ============================================================
        Head(2, "进入未认证状态")
        Dim OnlineBefore As Boolean = SafeInternet()
        Item("Internet (before)", If(OnlineBefore, "connected", "already unauthenticated"))

        If OnlineBefore Then
            ' 先确认有没有现成的 userIndex；没有就现查一次
            Dim UserIndex As String = ModAuthentication.TryDiscoverCurrentUserIndex(Context.Server, Timeout)
            Item("UserIndex", If(String.IsNullOrEmpty(UserIndex), "not obtained", "obtained"))

            Dim LogoutFirst As ModAuthentication.LogoutResult =
                ModAuthentication.LogoutAuthenticated(Context.Server, Timeout)
            Item("Logout", LogoutFirst.Status.ToString().ToLower())
            If LogoutFirst.Status <> ModAuthentication.PortalLogoutStatus.Success Then
                Item("Message", LogoutFirst.Message)
            End If
            ModAuthentication.ClearSession()
            Blank()

            If LogoutFirst.Status <> ModAuthentication.PortalLogoutStatus.Success Then
                Return Abort("Portal Redirect",
                             "无法进入未认证状态（logout=" & LogoutFirst.Status.ToString() & "：" & LogoutFirst.Message & "）")
            End If

            ' 等 BRAS 真正把会话摘掉
            Thread.Sleep(2500)
            Dim StillOnline As Boolean = SafeInternet()
            Item("Internet (after logout)", If(StillOnline, "still connected（门户未立即生效）", "unauthenticated"))
            Blank()
            If StillOnline Then
                Return Abort("Portal Redirect",
                             "登出后仍能访问外网，无法取得未认证状态下的门户重定向")
            End If
        Else
            Item("Result", "当前已是未认证状态")
            Blank()
        End If

        ' ---- 记录门户参数基线（后续判断自动重连是否换了参数） ----
        ModAuthTrace.Reset()

        ' ============================================================
        ' [3] Portal Redirect —— 关键验证一
        ' ============================================================
        Head(3, "Portal Redirect（BRAS 是否真的 302 到门户）")
        Dim Redirect As ModPortalDiscover.PortalDiscoveryResult = Nothing
        Try
            Redirect = ModPortalDiscover.DiscoverRedirect(Context.ProbeUrl, Timeout, EffectiveBind)
        Catch ex As Exception
            Return Abort("Portal Redirect", "探测异常：" & ex.Message)
        End Try

        Item("Status", Redirect.Status.ToString())
        Item("NeedsAuthentication", YesNo(Redirect.NeedsAuthentication))
        Item("PortalUrl", Found(Redirect.Redirect IsNot Nothing AndAlso
                                Not String.IsNullOrEmpty(Redirect.Redirect.PortalUrl)))
        Item("QueryString", Found(Redirect.Redirect IsNot Nothing AndAlso
                                  Redirect.Redirect.QueryString.Length > 0))
        Item("Mac", Found(Redirect.Redirect IsNot Nothing AndAlso
                          Not String.IsNullOrEmpty(Redirect.Redirect.Mac)))
        Item("Parameters", If(Redirect.Redirect Is Nothing, "0",
                              Redirect.Redirect.Parameters.Count.ToString()))
        If Not String.IsNullOrEmpty(Redirect.Message) Then Item("Message", Redirect.Message)
        Blank()

        If Not Redirect.NeedsAuthentication Then
            Return Abort("Portal Redirect",
                         "预期 NeedAuthentication，实际 " & Redirect.Status.ToString() &
                         "（若为 AlreadyOnline，说明有别的程序抢先认证了）")
        End If
        If Redirect.Redirect Is Nothing OrElse Redirect.Redirect.QueryString.Length = 0 Then
            Return Abort("Portal Redirect", "取得门户重定向但没有 queryString")
        End If
        Verdict("Portal Redirect", OutcomePass)

        Dim QueryStringValue As String = Redirect.Redirect.QueryString
        ModAuthTrace.ObserveQueryString(QueryStringValue)

        ' ============================================================
        ' [4] QueryString 参数存在性（只报存在与否）
        ' ============================================================
        Head(4, "QueryString 参数存在性")
        Dim ParamNames As String() = {"wlanuserip", "wlanacname", "nasip",
                                      "wlanparameter", "userlocation", "url"}
        Dim AllParamsPresent As Boolean = True
        For Each Name In ParamNames
            Dim Present As Boolean = Redirect.Redirect.GetParameter(Name) IsNot Nothing
            ' url 允许缺失：门户有默认跳转
            If Not Present AndAlso Name <> "url" Then AllParamsPresent = False
            Item(Name, Found(Present))
        Next
        Item("mac", Found(Not String.IsNullOrEmpty(Redirect.Redirect.Mac)))
        Blank()

        If Not AllParamsPresent Then
            Return Abort("QueryString", "queryString 缺少校园网必需参数")
        End If
        Verdict("QueryString", OutcomePass)

        ' ============================================================
        ' [5] PageInfo —— 动态公钥
        ' ============================================================
        Head(5, "PageInfo（动态公钥）")
        Dim Info As ModPortalDiscover.PortalPageInfo = Nothing
        Try
            Info = ModPortalDiscover.FetchPageInfo(QueryStringValue, Context.Server, Timeout, EffectiveBind)
        Catch ex As Exception
            Return Abort("PageInfo", "pageInfo 请求失败：" & ex.Message)
        End Try

        Dim HttpStatus As Integer = ModAuthTrace.LastHttpStatus
        Dim BodyLength As Integer = ModAuthTrace.LastBodyLength

        Item("HTTP status", HttpStatus.ToString())
        Item("Body length", BodyLength.ToString() & If(BodyLength > 0, " (非空)", " (空响应！)"))
        Item("PublicKeyModulus", Found(Info IsNot Nothing AndAlso Info.PublicKeyModulus.Length > 0))
        Item("PublicKeyExponent", Found(Info IsNot Nothing AndAlso Info.PublicKeyExponent.Length > 0))
        Item("PasswordEncrypt", If(Info Is Nothing, "(n/a)", Info.PasswordEncrypt.ToString().ToLower()))
        Item("ValidCodeUrl", If(Info Is Nothing OrElse String.IsNullOrEmpty(Info.ValidCodeUrl),
                                "(空，无需验证码)", "(非空，门户要求验证码)"))
        Blank()

        If Info Is Nothing OrElse Info.PublicKeyModulus.Length = 0 OrElse Info.PublicKeyExponent.Length = 0 Then
            Return Abort("PageInfo", "pageInfo 未返回可用公钥")
        End If
        Verdict("PageInfo", OutcomePass, "HTTP " & HttpStatus & " / 正文 " & BodyLength & " 字节")

        Item("Modulus hex length", Info.PublicKeyModulus.Length.ToString())
        Item("Exponent", Info.PublicKeyExponent)
        Blank()
        If Info.PublicKeyModulus.Length <> 256 Then
            Verdict("PublicKey", OutcomeFail, "modulus 十六进制长度应为 256，实际 " & Info.PublicKeyModulus.Length)
            WriteSummary()
            Return ExitFail
        End If
        Verdict("PublicKey", OutcomePass, "modulus 256 hex / exponent " & Info.PublicKeyExponent)

        ' ============================================================
        ' [6] Services
        ' ============================================================
        Head(6, "Services（运营商服务表）")
        Dim Services As ModPortalDiscover.PortalServices = Nothing
        Try
            Services = ModPortalDiscover.FetchServices(QueryStringValue, Context.Server, Timeout, EffectiveBind)
        Catch ex As Exception
            Return Abort("Services", "getServices 请求失败：" & ex.Message)
        End Try

        Item("Services count", If(Services Is Nothing, "0", Services.Items.Count.ToString()))
        Item("Default", If(Services Is Nothing, "(n/a)", Services.DefaultName))
        Blank()

        If Services Is Nothing OrElse Services.Items.Count = 0 Then
            Return Abort("Services", "服务列表为空")
        End If

        Console.WriteLine("    五个运营商（按 service code 逐个核对）:")
        Dim Expected As PortalOperator() = ModAuth.GetAllOperators()
        Dim AllOperatorsFound As Boolean = True
        For Each Op In Expected
            Dim Code As String = ModAuth.GetServiceCode(Op)
            Dim Hit As ModPortalDiscover.PortalService = Services.FindByName(Code)
            If Hit Is Nothing Then AllOperatorsFound = False
            Console.WriteLine("      " & ModAuth.GetDisplayName(Op).PadRight(8) & " -> " &
                              Code.PadRight(10) & ": " & Found(Hit IsNot Nothing))
        Next
        Blank()

        Dim CurrentCode As String = ModAuth.GetServiceCode(Account.Operator)
        Dim CurrentHit As ModPortalDiscover.PortalService = Services.FindByName(CurrentCode)
        Item("Current operator", ModAuth.GetDisplayName(Account.Operator) & " -> " & CurrentCode)
        Item("Current in list", Found(CurrentHit IsNot Nothing))
        Item("Current display", If(CurrentHit Is Nothing, "(n/a)", CurrentHit.DisplayName))
        Blank()

        If CurrentHit Is Nothing Then
            Return Abort("Services", "当前运营商 " & CurrentCode & " 不在门户服务表中")
        End If
        If Not AllOperatorsFound Then
            ' 门户只下发本校实际开放的运营商，缺一个不算致命，但必须如实标注
            Verdict("Services", OutcomePass, "当前运营商命中；并非五个都在门户服务表中（属正常）")
        Else
            Verdict("Services", OutcomePass, "五个运营商全部命中")
        End If

        ' ============================================================
        ' 组装 Discovery（供 ModAuth / ModAuthentication 使用）
        ' ============================================================
        Dim Discovery As New ModPortalDiscover.PortalDiscoveryResult With {
            .Status = ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication,
            .Message = "验收：由 DiscoverRedirect + FetchPageInfo + FetchServices 组装",
            .Redirect = Redirect.Redirect,
            .PageInfo = Info,
            .Services = Services
        }

        ' ============================================================
        ' [7] PasswordEncryption —— 用本次动态公钥加密真实密码
        ' ============================================================
        Head(7, "PasswordEncryption（动态公钥 + 真实密码）")
        Dim MacValue As String = If(String.IsNullOrEmpty(Redirect.Redirect.Mac),
                                    ModCrypto.DefaultMac, Redirect.Redirect.Mac)
        Item("Mac source", If(String.IsNullOrEmpty(Redirect.Redirect.Mac),
                              "default（门户未下发 mac）", "portal（门户下发）"))

        Dim Cipher As String = ""
        Try
            Cipher = ModCrypto.EncryptPassword(Account.Password, MacValue,
                                               Info.PublicKeyModulus, Info.PublicKeyExponent)
        Catch ex As Exception
            Return Abort("PasswordEncryption", "加密失败：" & ex.Message)
        End Try

        Dim CipherHexLength As Integer = If(Cipher, "").Replace(" ", "").Length
        Item("PasswordEncryption", If(CipherHexLength > 0, "SUCCESS", "FAIL"))
        Item("CipherLength", CipherHexLength.ToString() & " hex（" & (CipherHexLength \ 2) & " 字节）")
        Blank()

        If CipherHexLength <> 256 Then
            Verdict("PasswordEncryption", OutcomeFail,
                    "密文十六进制长度应为 256，实际 " & CipherHexLength)
            WriteSummary()
            Return ExitFail
        End If
        Verdict("PasswordEncryption", OutcomePass, "256 hex / 128 字节")

        ' ============================================================
        ' [8] Payload（ModAuth.Build）
        ' ============================================================
        Head(8, "Payload（ModAuth.Build）")
        Dim Payload As AuthPayload = Nothing
        Try
            Payload = ModAuth.Build(Account, Discovery)
        Catch ex As ModAuth.AuthException
            Return Abort("Payload", "构造失败：" & ex.Message & "（Failure=" & ex.Failure.ToString() & "）")
        Catch ex As Exception
            Return Abort("Payload", "构造异常：" & ex.Message)
        End Try

        ' userId 必须始终是纯学号
        Dim Suffixes As String() = {"@96301", "@cmccgx", "@unicom", "@office", "@xhu"}
        Dim HasSuffix As Boolean = False
        For Each Suffix In Suffixes
            If Payload.UserId.Contains(Suffix) Then HasSuffix = True
        Next

        Item("userId", If(String.IsNullOrEmpty(Payload.UserId), "missing", "configured / pure student ID"))
        Item("UserIdHasSuffix", HasSuffix.ToString())
        Item("service", Payload.Service)
        Item("service expected", CurrentCode)
        Item("queryString", Found(Not String.IsNullOrEmpty(Payload.QueryString)))
        Item("password", If(String.IsNullOrEmpty(Payload.Password), "missing", "generated"))
        Item("passwordEncrypt", Payload.PasswordEncrypt.ToString().ToLower())
        Blank()

        If HasSuffix Then
            Verdict("Payload", OutcomeFail, "userId 含 @ 运营商后缀，违反 Web 认证规则")
            WriteSummary()
            Return ExitFail
        End If
        If Payload.Service <> CurrentCode Then
            Verdict("Payload", OutcomeFail, "service 应为 " & CurrentCode & "，实际 " & Payload.Service)
            WriteSummary()
            Return ExitFail
        End If
        If String.IsNullOrEmpty(Payload.QueryString) OrElse String.IsNullOrEmpty(Payload.Password) Then
            Verdict("Payload", OutcomeFail, "queryString 或 password 为空")
            WriteSummary()
            Return ExitFail
        End If
        Verdict("Payload", OutcomePass, "service=" & Payload.Service & " / UserIdHasSuffix=False")

        ' ---- 验证码分支：门户要求就先停下，不猜 ----
        If Info.PasswordEncrypt AndAlso Not String.IsNullOrEmpty(Info.ValidCodeUrl) Then
            Console.WriteLine("E2E = NEED_VALID_CODE")
            Console.WriteLine("门户下发了 validCodeUrl，本入口不会自动识别验证码。")
            Blank()
            Verdict("Login", OutcomeBlocked, "需要验证码")
            Verdict("Internet", OutcomeBlocked, "需要验证码")
            Verdict("Logout", OutcomeBlocked, "需要验证码")
            Verdict("Second Login", OutcomeBlocked, "需要验证码")
            Verdict("Auto Reconnect", OutcomeBlocked, "需要验证码")
            WriteSummary()
            Console.WriteLine("=== ACCEPT NEED_VALID_CODE (exit 3) ===")
            Return ExitNeedValidCode
        End If

        ' ============================================================
        ' [9] Login —— 真实登录
        ' ============================================================
        Head(9, "Login（真实提交）")
        Dim Login As AuthenticationResult = ModAuthentication.Authenticate(
            Account, Context.ProbeUrl, Context.Server, Timeout, EffectiveBind)

        Item("Result", If(Login.LoginResult Is Nothing, "(无登录响应)", Login.LoginResult.Result))
        Item("Message", Login.Message)
        Item("UserIndex", If(Login.LoginResult IsNot Nothing AndAlso Login.LoginResult.UserIndex.Length > 0,
                             "obtained", "none"))
        If Login.LoginResult IsNot Nothing Then
            Item("KeepaliveInterval", Login.LoginResult.KeepaliveInterval.ToString())
        End If
        Blank()

        If Not Login.Success Then
            Console.WriteLine("    失败分类（不猜，直接给结构化字段）:")
            Console.WriteLine("      DiscoveryStatus : " & Login.DiscoveryStatus.ToString())
            Console.WriteLine("      AuthFailure     : " & Login.Failure.ToString())
            Console.WriteLine("      门户 message    : " & Login.Message)
            Console.WriteLine("      可读说明        : " & ModAuthentication.DescribeFailure(Login))
            Blank()
            Return Abort("Login", "门户返回 " & If(Login.LoginResult Is Nothing, "无响应", Login.LoginResult.Result) &
                                  "（" & Login.Failure.ToString() & "）")
        End If
        Verdict("Login", OutcomePass)

        ' ============================================================
        ' [10] Internet
        ' ============================================================
        Head(10, "Internet")
        Console.WriteLine("    门户已返回成功，等待 BRAS 放行（最多 20 秒）…")
        Dim Online As Boolean = WaitForInternet(20)
        Item("Result", If(Online, "CONNECTED", "NOT CONNECTED"))
        Blank()
        If Not Online Then
            Return Abort("Internet", "登录返回成功，但 20 秒内外网始终不通")
        End If
        Verdict("Internet", OutcomePass)

        ' ============================================================
        ' [11] Logout
        ' ============================================================
        Head(11, "Logout")
        Dim LogoutResult As ModAuthentication.LogoutResult =
            ModAuthentication.LogoutAuthenticated(Context.Server, Timeout)
        Item("Logout", LogoutResult.Status.ToString().ToLower())
        If LogoutResult.Status <> ModAuthentication.PortalLogoutStatus.Success Then
            Item("Message", LogoutResult.Message)
        End If
        Blank()

        If LogoutResult.Status <> ModAuthentication.PortalLogoutStatus.Success Then
            Return Abort("Logout", "登出失败：" & LogoutResult.Message)
        End If
        ModAuthentication.ClearSession()

        Thread.Sleep(2500)
        Dim AfterLogout As Boolean = SafeInternet()
        Item("Internet", If(AfterLogout, "still connected", "unauthenticated"))
        Blank()
        If AfterLogout Then
            Return Abort("Logout", "登出后仍能访问外网")
        End If
        Verdict("Logout", OutcomePass)

        ' ============================================================
        ' [12] Second Login —— 全链路重跑（不重启程序）
        ' ============================================================
        Head(12, "Second Login（不重启程序，完整重跑一遍）")
        Dim Second As AuthenticationResult = ModAuthentication.Authenticate(
            Account, Context.ProbeUrl, Context.Server, Timeout, EffectiveBind)

        Item("DiscoveryStatus", Second.DiscoveryStatus.ToString())
        Item("Result", If(Second.LoginResult Is Nothing, Second.Message, Second.LoginResult.Result))
        Item("UserIndex", If(Second.LoginResult IsNot Nothing AndAlso Second.LoginResult.UserIndex.Length > 0,
                             "obtained", "none"))
        Blank()

        If Not Second.Success Then
            Return Abort("Second Login", "第二次登录失败：" & ModAuthentication.DescribeFailure(Second))
        End If

        Dim SecondOnline As Boolean = WaitForInternet(20)
        Item("Internet", If(SecondOnline, "CONNECTED", "NOT CONNECTED"))
        Blank()
        If Not SecondOnline Then
            Return Abort("Second Login", "第二次登录返回成功，但 20 秒内外网始终不通")
        End If
        Verdict("Second Login", OutcomePass)

        ' ============================================================
        ' [13] Auto Reconnect
        ' ============================================================
        If Not WithReconnect Then
            Verdict("Auto Reconnect", OutcomeSkip, "本次未启用")
            WriteSummary()
            Return If(AllPassed(), ExitPass, ExitFail)
        End If

        Dim ReconnectCode As Integer = RunReconnectAccept(Context, Timeout)
        ' 自动重连失败时机器可能仍处于未认证状态，统一在收尾处补一次恢复
        If ReconnectCode <> ExitPass AndAlso Not SafeInternet() Then TryRestore()
        WriteSummary()
        Return ReconnectCode
    End Function

#End Region

#Region "自动重连实测（§十七 / §十八）"

    ''' <summary>
    ''' 用「登出」制造一次真实的认证丢失，观察 NetworkMonitor 是否通过新认证链自动恢复。
    ''' 全程不动网卡，因此不会把机器弄成物理断网。
    '''
    ''' 自动重连开关与间隔**只在内存字典里改**，不写回 bin/config.yml。
    ''' </summary>
    Private Function RunReconnectAccept(Context As RuntimeAuthContext, Timeout As Integer) As Integer
        Head(13, "Auto Reconnect（自动重连 + 动态参数）")

        ' ---- 只清零计数，保留 §3 那次 queryString 观测作为基线 ----
        ModAuthTrace.ResetCounters()

        ' 兼容层字典：NetworkMonitor 从这里读开关与间隔
        Dim MonitorCfg As Dictionary(Of String, Object) = ModConfig.ReadCfg()
        Dim Func As Dictionary(Of String, Object) = Nothing
        If MonitorCfg.ContainsKey(ConfigKeys.FunctionSection) Then
            Func = TryCast(MonitorCfg(ConfigKeys.FunctionSection), Dictionary(Of String, Object))
        End If
        If Func Is Nothing Then
            Func = New Dictionary(Of String, Object)()
            MonitorCfg(ConfigKeys.FunctionSection) = Func
        End If
        Func(ConfigKeys.AutoReconnect) = True
        Func(ConfigKeys.ReconnectInterval) = 2
        Item("AutoReconnect(in-memory)", "true（不写回配置文件）")
        Item("ReconnectInterval", "2s")

        Dim Monitor As New NetworkMonitor(MonitorCfg, Context.BindAddress)
        Monitor.Start()
        Thread.Sleep(2000)

        Dim Snapshot = Monitor.GetSnapshot()
        Item("Monitor started", YesNo(True))
        Item("Monitor sees", If(Snapshot.Connected, "Connected", "Disconnected"))
        Blank()

        If Not Snapshot.Connected Then
            Monitor.Stop()
            Verdict("Auto Reconnect", OutcomeFail, "监控启动时未处于已连接状态，无法观测断线事件")
            Return ExitFail
        End If

        ' ---- 制造断线：登出 = 真实认证丢失 ----
        Console.WriteLine("    制造断线：登出（真实认证丢失）")
        Dim Loss As ModAuthentication.LogoutResult =
            ModAuthentication.LogoutAuthenticated(Context.Server, Timeout)
        Item("Logout to simulate loss", Loss.Status.ToString().ToLower())
        ModAuthentication.ClearSession()
        Blank()

        ' ---- 等监控发现断线并自动重连 ----
        Dim Deadline As DateTime = DateTime.Now.AddSeconds(75)
        Dim Recovered As Boolean = False
        While DateTime.Now < Deadline
            Thread.Sleep(1000)
            If SafeInternet() Then
                Recovered = True
                Exit While
            End If
        End While

        ' 让监控把「网络已恢复」也写进日志
        Thread.Sleep(2500)
        Monitor.Stop()
        Thread.Sleep(500)

        Item("Auto reconnect", OkFail(Recovered))
        Item("Internet", If(Recovered, "CONNECTED", "NOT CONNECTED"))
        Blank()

        ' ---- 监控日志（判断到底是谁恢复的）----
        Dim Final = Monitor.GetSnapshot()
        Console.WriteLine("    Monitor logs（节选）:")
        For Each Line In Final.Logs
            Console.WriteLine("      " & Line)
        Next
        Blank()

        ' ---- §十八 三项计数 ----
        Console.WriteLine("    动态参数统计（ModAuthTrace，只报次数与是否变化）:")
        Item("Reconnect Count", ModAuthTrace.ReconnectCount.ToString())
        Item("Discovery Count", ModAuthTrace.DiscoveryCount.ToString())
        Item("PageInfo Count", ModAuthTrace.PageInfoCount.ToString())
        Item("PasswordEncryption", ModAuthTrace.PasswordEncryptionCount.ToString())
        Item("Login Count", ModAuthTrace.LoginCount.ToString())
        Item("ReconnectUsedDynamicDiscovery", ModAuthTrace.ReconnectUsedDynamicDiscovery.ToString())
        Item("WlanUserIp observations", ModAuthTrace.WlanUserIpObservations.ToString())
        Item("WlanUserIp", If(ModAuthTrace.WlanUserIpChanged, "Changed", "Unchanged"))
        Item("PublicKey", If(ModAuthTrace.PublicKeyChanged, "Changed", "Unchanged"))
        Blank()

        If Not Recovered Then
            Verdict("Auto Reconnect", OutcomeFail, "未能在 75 秒内恢复连接")
            Return ExitFail
        End If

        ' 必须确实是**监控自己重新认证**恢复的，而不是外部程序
        Dim DidReconnect As Boolean = False
        For Each Line In Final.Logs
            If Line.Contains("自动重连成功") Then DidReconnect = True
        Next
        If Not DidReconnect Then
            Verdict("Auto Reconnect", OutcomeFail,
                    "网络恢复了，但日志里没有「自动重连成功」——可能是外部程序抢先恢复的，不能算本项目成果")
            Return ExitFail
        End If

        ' 必须确实重新走过 Discovery / pageInfo / login
        If Not ModAuthTrace.ReconnectUsedDynamicDiscovery Then
            Verdict("Auto Reconnect", OutcomeFail,
                    "自动重连没有完整重跑动态链（Reconnect=" & ModAuthTrace.ReconnectCount &
                    " / Discovery=" & ModAuthTrace.DiscoveryCount &
                    " / PageInfo=" & ModAuthTrace.PageInfoCount &
                    " / PasswordEncryption=" & ModAuthTrace.PasswordEncryptionCount &
                    " / Login=" & ModAuthTrace.LoginCount & "）")
            Return ExitFail
        End If

        If ModAuthTrace.WlanUserIpObservations < 2 Then
            Verdict("Auto Reconnect", OutcomeFail, "没有观测到两次 queryString，无法确认参数是重新取的")
            Return ExitFail
        End If

        Verdict("Auto Reconnect", OutcomePass,
                "Discovery=" & ModAuthTrace.DiscoveryCount &
                " / PageInfo=" & ModAuthTrace.PageInfoCount &
                " / PasswordEncryption=" & ModAuthTrace.PasswordEncryptionCount &
                " / Login=" & ModAuthTrace.LoginCount &
                " / WlanUserIp=" & If(ModAuthTrace.WlanUserIpChanged, "Changed", "Unchanged"))
        Return If(AllPassed(), ExitPass, ExitFail)
    End Function

#End Region

End Module
