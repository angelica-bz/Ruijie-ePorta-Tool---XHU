Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModRuntimeAuthenticationTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModRuntimeAuthenticationTests

#Region "RuntimeAuthentication 测试用例"

    Private Sub Test_RtConfigToAccount()
        Dim Cfg As AppConfig = MakeAppConfig("001234567890", "test1234", PortalOperator.Telecom)
        Dim Account As PortalAccount = Cfg.ToPortalAccount()

        AssertTrue(Account IsNot Nothing, "应能转换出 PortalAccount")
        AssertEqual("001234567890", Account.UserId, "UserId 应一致（含前导零）")
        AssertEqual("test1234", Account.Password, "Password 应一致")
        AssertEqual(PortalOperator.Telecom, Account.[Operator], "Operator 应一致")
        AssertEqual("", Account.ValidCode, "默认无验证码")

        ' 学号始终按字符串处理
        Dim Long17 As AppConfig = MakeAppConfig("12345678901234567", "p", PortalOperator.Mobile)
        AssertEqual("12345678901234567", Long17.ToPortalAccount().UserId, "17 位学号不得被改写")

        ' 验证码可透传
        AssertEqual("4321", Cfg.ToPortalAccount("4321").ValidCode, "验证码应透传")
    End Sub

    Private Sub Test_RtPasswordDecrypt()
        ' 落盘 → 读回 → DPAPI 解密 → PortalAccount.Password
        Dim Path_ As String = CfgTempFile("rt-password.yml")
        Dim Saved As AppConfig = MakeAppConfig("1234567890123", "Test中abc", PortalOperator.Unicom)
        SaveAppConfigTo(Path_, Saved)

        Dim Loaded As AppConfig = LoadAppConfigFrom(Path_)
        AssertFalse(Loaded.PasswordNeedsReentry, "已保存的密码应能解密")
        AssertEqual("Test中abc", Loaded.User.Password, "解密后明文应一致")

        Dim Context As RuntimeAuthContext = BuildRuntimeAuthContext(Loaded)
        AssertTrue(Context.IsValid, "上下文应有效：" & Context.ErrorMessage)
        AssertEqual("Test中abc", Context.Account.Password, "PortalAccount 应拿到明文密码")

        ' 解密失败（伪造一段无法解密的保护串）→ 明确要求重新输入
        Dim BadPath As String = CfgTempFile("rt-password-bad.yml")
        Dim Raw As String = IO.File.ReadAllText(Path_)
        Dim Broken As String = System.Text.RegularExpressions.Regex.Replace(
            Raw, "(?m)^(\s*password_protected:\s*')[^']*(')",
            "$1" & Convert.ToBase64String(New Byte() {1, 2, 3, 4, 5, 6, 7, 8}) & "$2")
        IO.File.WriteAllText(BadPath, Broken, Text.Encoding.UTF8)

        Dim BadCfg As AppConfig = LoadAppConfigFrom(BadPath)
        AssertTrue(BadCfg.PasswordNeedsReentry, "无法解密时应标记需要重新输入")
        AssertEqual("", BadCfg.User.Password, "无法解密时不应有明文密码")
        AssertTrue(BadCfg.NotReadyReason.Contains("无法解密"), "应给出明确原因：" & BadCfg.NotReadyReason)
    End Sub

    Private Sub Test_RtTelecomService()
        Dim Cfg As AppConfig = MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom)
        Dim Context As RuntimeAuthContext = BuildRuntimeAuthContext(Cfg)
        AssertTrue(Context.IsValid, Context.ErrorMessage)

        Dim Prepared As AuthenticationResult = PreparePayload(
            Context.Account, MakeDiscovery(Services:=ParseServices(TestServicesJson)))
        AssertTrue(Prepared.Success, Prepared.Message)
        AssertEqual("1234567890123", Prepared.Payload.UserId, "userId 必须是纯学号")
        AssertFalse(Prepared.Payload.UserId.Contains("@"), "userId 不得含 @")
        AssertEqual("96301", Prepared.Payload.Service, "电信 → 96301")
    End Sub

    Private Sub Test_RtMobileService()
        Dim Context As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Mobile))
        Dim Prepared As AuthenticationResult = PreparePayload(
            Context.Account, MakeDiscovery(Services:=ParseServices(TestServicesJson)))
        AssertTrue(Prepared.Success, Prepared.Message)
        AssertEqual("cmccgx", Prepared.Payload.Service, "移动 → cmccgx")
        AssertFalse(Prepared.Payload.UserId.Contains("@"), "userId 不得含 @")
    End Sub

    Private Sub Test_RtUnicomService()
        Dim Context As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Unicom))
        Dim Prepared As AuthenticationResult = PreparePayload(
            Context.Account, MakeDiscovery(Services:=ParseServices(TestServicesJson)))
        AssertTrue(Prepared.Success, Prepared.Message)
        AssertEqual("unicom", Prepared.Payload.Service, "联通 → unicom")
        AssertFalse(Prepared.Payload.UserId.Contains("@"), "userId 不得含 @")
    End Sub

    Private Sub Test_RtAlreadyOnlineSkipLogin()
        Dim Context As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom))

        Dim Discovery = MakeStatusOnly(ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline, "当前已联网")
        Dim Result As AuthenticationResult = AuthenticateWithDiscovery(Context.Account, Discovery)

        AssertTrue(Result.Success, "已联网应视为成功")
        AssertEqual("当前已经联网", Result.Message, "应明确说明")
        AssertTrue(Result.Payload Is Nothing, "不应构造 login payload")
        AssertTrue(Result.LoginResult Is Nothing, "不应发出 login 请求（无网络访问）")
    End Sub

    Private Sub Test_RtDiscoveryFailureSkipLogin()
        Dim Context As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom))

        For Each Status In New ModPortalDiscover.PortalDiscoveryStatus() {
                ModPortalDiscover.PortalDiscoveryStatus.Unreachable,
                ModPortalDiscover.PortalDiscoveryStatus.Timeout,
                ModPortalDiscover.PortalDiscoveryStatus.MissingParameters,
                ModPortalDiscover.PortalDiscoveryStatus.BadResponse}
            Dim Result As AuthenticationResult = AuthenticateWithDiscovery(
                Context.Account, MakeStatusOnly(Status, "模拟失败"))
            AssertFalse(Result.Success, Status.ToString() & " 应失败")
            AssertTrue(Result.Payload Is Nothing, Status.ToString() & " 不应构造 payload")
            AssertTrue(Result.LoginResult Is Nothing, Status.ToString() & " 不应发出 login 请求")
            AssertTrue(DescribeFailure(Result).Length > 0, "应给出可读的失败说明")
        Next
    End Sub

    Private Sub Test_RtValidCodeRequired()
        Dim Context As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom))
        Dim Discovery = MakeDiscovery(ValidCodeUrl:="http://202.115.144.51/eportal/validCode.jpg",
                                      Services:=ParseServices(TestServicesJson))

        Dim Result As AuthenticationResult = PreparePayload(Context.Account, Discovery)
        AssertFalse(Result.Success, "缺验证码应失败")
        AssertEqual(AuthFailure.ValidCodeRequired, Result.Failure, "应进入 ValidCodeRequired")

        ' 带上验证码重新构造上下文即可通过
        Dim WithCode As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom), ValidCode:="1234")
        Dim Ok As AuthenticationResult = PreparePayload(WithCode.Account, Discovery)
        AssertTrue(Ok.Success, Ok.Message)
        AssertEqual("1234", Ok.Payload.ValidCode, "应带上验证码")
    End Sub

    Private Sub Test_RtFullChain()
        ' AppConfig → 解密 → PortalAccount → Discovery fixture → ModAuth → ModCrypto → LoginData
        Dim Path_ As String = CfgTempFile("rt-fullchain.yml")
        SaveAppConfigTo(Path_, MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom,
                                             AutoReconnect:=True, Interval:=8))

        Dim Loaded As AppConfig = LoadAppConfigFrom(Path_)
        Dim Context As RuntimeAuthContext = BuildRuntimeAuthContext(Loaded)
        AssertTrue(Context.IsValid, Context.ErrorMessage)
        AssertEqual(ModConfig.SchoolServer, Context.Server, "服务器应取学校固定常量")
        AssertEqual(True, Loaded.[Function].AutoReconnect, "功能开关应一并读出")

        Dim Discovery = MakeDiscovery(Services:=ParseServices(TestServicesJson))
        Dim Prepared As AuthenticationResult = PreparePayload(Context.Account, Discovery)
        AssertTrue(Prepared.Success, Prepared.Message)

        Dim Data As Dictionary(Of String, String) = Prepared.Payload.ToLoginData()
        AssertEqual(8, Data.Count, "login_data 应为 8 个字段")
        AssertEqual("1234567890123", Data("userId"), "userId 为纯学号")
        AssertEqual("96301", Data("service"), "service 为 96301")
        AssertEqual(Discovery.Redirect.QueryString, Data("queryString"), "queryString 原样透传")
        AssertEqual("", Data("operatorPwd"), "operatorPwd 为空")
        AssertEqual("", Data("operatorUserId"), "operatorUserId 为空")
        AssertEqual("", Data("validcode"), "无验证码时为空")
        AssertEqual("true", Data("passwordEncrypt"), "passwordEncrypt 为 true")
        AssertEqual(GoldenBasic, Data("password"), "密码应与第三阶段黄金向量一致")
    End Sub

    Private Sub Test_RtNotReadyReasons()
        ' 学号为空
        Dim NoId As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("", "test1234", PortalOperator.Telecom))
        AssertFalse(NoId.IsValid, "学号为空应不可用")
        AssertTrue(NoId.ErrorMessage.Contains("学号"), "应指出学号：" & NoId.ErrorMessage)

        ' 密码未保存
        Dim NoPwd As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "", PortalOperator.Telecom))
        AssertFalse(NoPwd.IsValid, "密码为空应不可用")
        AssertTrue(NoPwd.ErrorMessage.Contains("密码"), "应指出密码：" & NoPwd.ErrorMessage)

        ' 运营商未选择
        Dim NoOp As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Unknown))
        AssertFalse(NoOp.IsValid, "运营商未知应不可用")
        AssertTrue(NoOp.ErrorMessage.Contains("运营商"), "应指出运营商：" & NoOp.ErrorMessage)

        ' 迁移遗留的未知 service 应提示重新选择
        Dim Legacy As AppConfig = MakeAppConfig("1234567890123", "test1234", PortalOperator.Unknown)
        Legacy.UnknownServiceRaw = "some-old-service"
        Dim LegacyCtx As RuntimeAuthContext = BuildRuntimeAuthContext(Legacy)
        AssertFalse(LegacyCtx.IsValid, "未知 service 应不可用")
        AssertTrue(LegacyCtx.ErrorMessage.Contains("some-old-service"), "应带上原始 service：" & LegacyCtx.ErrorMessage)

        ' 齐全时可用
        AssertTrue(BuildRuntimeAuthContext(MakeAppConfig("1234567890123", "test1234",
                                                         PortalOperator.Telecom)).IsValid, "齐全时应可用")
    End Sub

    Private Sub Test_RtConfigChangeTakesEffect()
        Dim Path_ As String = CfgTempFile("rt-config-change.yml")

        SaveAppConfigTo(Path_, MakeAppConfig("1111111111111", "pw-one", PortalOperator.Telecom))
        Dim First As RuntimeAuthContext = BuildRuntimeAuthContext(LoadAppConfigFrom(Path_))
        AssertTrue(First.IsValid, First.ErrorMessage)
        AssertEqual("1111111111111", First.Account.UserId, "第一次学号")
        AssertEqual(PortalOperator.Telecom, First.Account.[Operator], "第一次运营商")

        ' 改配置（无需重启进程）
        SaveAppConfigTo(Path_, MakeAppConfig("2222222222222", "pw-two", PortalOperator.Mobile))
        Dim Second As RuntimeAuthContext = BuildRuntimeAuthContext(LoadAppConfigFrom(Path_))
        AssertTrue(Second.IsValid, Second.ErrorMessage)
        AssertEqual("2222222222222", Second.Account.UserId, "改配置后应立刻读到新学号")
        AssertEqual("pw-two", Second.Account.Password, "改配置后应立刻读到新密码")
        AssertEqual(PortalOperator.Mobile, Second.Account.[Operator], "改配置后应立刻读到新运营商")
    End Sub

    Private Sub Test_RtConcurrencyGuard()
        ' 正常情况下应能占住闸门
        AssertTrue(TryBeginAuthentication(), "第一次应能占住认证闸门")
        AssertTrue(IsAuthenticating, "占住后 IsAuthenticating 应为 True")
        ' 占住期间第二次认证应被拒绝，且不会发出请求
        Dim Blocked As AuthenticationResult = Authenticate(
            NewAccount("1234567890123", "test1234", PortalOperator.Telecom))
        AssertFalse(Blocked.Success, "并发认证应被拒绝")
        AssertTrue(Blocked.Message.Contains("正在进行"), "应说明原因：" & Blocked.Message)
        AssertTrue(Blocked.Payload Is Nothing, "被拒绝时不应构造 payload")

        EndAuthentication()
        AssertFalse(IsAuthenticating, "释放后应为 False")
        AssertTrue(TryBeginAuthentication(), "释放后应能再次占住")
        EndAuthentication()
    End Sub

    Private Sub Test_HttpLoginRequestShape()
        ' §二十：不真正登录校园网，只校验最终请求的构成
        Dim Discovery = MakeDiscovery(Mac:="111111111", Services:=ParseServices(TestServicesJson))
        Dim Prepared As AuthenticationResult = PreparePayload(
            NewAccount("1234567890123", "test1234", PortalOperator.Telecom), Discovery)
        AssertTrue(Prepared.Success, Prepared.Message)

        Dim QueryString As String = Prepared.Payload.QueryString
        Dim Headers As Dictionary(Of String, String) =
            BuildLoginHeaders(ModConfig.SchoolServer, QueryString, "")

        Dim Request As ModNetwork.LoginRequest =
            ModNetwork.BuildLoginRequest(Prepared.Payload, ModConfig.SchoolServer, Headers)

        ' method / path
        AssertEqual("POST", Request.Method, "必须是 POST")
        AssertEqual("/eportal/InterFace.do?method=login", Request.RequestPath, "路径必须与门户契约一致")
        AssertEqual(ModConfig.SchoolServer & "/eportal/InterFace.do?method=login", Request.Url, "完整 URL")

        ' headers
        AssertEqual("application/x-www-form-urlencoded; charset=UTF-8", Request.Headers("Content-Type"), "Content-Type")
        AssertEqual(ModConfig.SchoolServer, Request.Headers("Origin"), "Origin")
        AssertEqual(ModConfig.SchoolServer & "/eportal/index.jsp?" & QueryString, Request.Headers("Referer"),
                    "Referer 由当前 queryString 动态生成")
        AssertEqual(ModNetwork.DefaultUserAgent, Request.Headers("User-Agent"), "User-Agent 复用项目默认值")

        ' body：逐个字段核对（EncodeFormData 只编码一次）
        Dim Body As String = Request.Body
        For Each KeyName In LoginDataKeys
            AssertTrue(Body.Contains(Uri.EscapeDataString(KeyName) & "="), "正文应包含字段：" & KeyName)
        Next
        AssertTrue(Body.Contains("userId=1234567890123"), "userId 无特殊字符时原样出现")
        AssertTrue(Body.Contains("service=96301"), "service 应为 96301")
        AssertTrue(Body.Contains("passwordEncrypt=true"), "passwordEncrypt 应为小写 true")
        AssertTrue(Body.Contains("operatorPwd="), "operatorPwd 应为空值")
        AssertTrue(Body.Contains("validcode="), "validcode 应为空值")

        ' queryString 在正文里只被编码一次
        AssertTrue(Body.Contains("queryString=" & Uri.EscapeDataString(QueryString)), "queryString 恰好编码一次")
        AssertFalse(Body.Contains(Uri.EscapeDataString(Uri.EscapeDataString(QueryString))), "绝不能被编码两次")

        ' 绝不能把明文密码写进正文
        AssertFalse(Body.Contains("test1234"), "正文里不得出现密码明文")
        AssertEqual(GoldenBasic, Prepared.Payload.Password, "正文里的 password 应是黄金密文")
        AssertTrue(Body.Contains(Uri.EscapeDataString(GoldenBasic)), "正文应携带密文")
    End Sub


#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("AppConfig → PortalAccount 字段一致", AddressOf Test_RtConfigToAccount)
        RunTest("DPAPI 密码解密后进入 PortalAccount", AddressOf Test_RtPasswordDecrypt)
        RunTest("电信: 纯学号 + service 96301", AddressOf Test_RtTelecomService)
        RunTest("移动: service cmccgx", AddressOf Test_RtMobileService)
        RunTest("联通: service unicom", AddressOf Test_RtUnicomService)
        RunTest("已联网时不发送登录请求", AddressOf Test_RtAlreadyOnlineSkipLogin)
        RunTest("门户发现失败时不发送登录请求", AddressOf Test_RtDiscoveryFailureSkipLogin)
        RunTest("验证码缺失进入 ValidCodeRequired", AddressOf Test_RtValidCodeRequired)
        RunTest("完整链路: 配置→解密→账号→认证→LoginData", AddressOf Test_RtFullChain)
        RunTest("配置不完整时给出明确原因", AddressOf Test_RtNotReadyReasons)
        RunTest("改配置无需重启即生效", AddressOf Test_RtConfigChangeTakesEffect)
        RunTest("并发保护: 同时只允许一个认证", AddressOf Test_RtConcurrencyGuard)
        RunTest("HTTP 发送层: method/path/headers/body", AddressOf Test_HttpLoginRequestShape)
    End Sub

#End Region

End Module