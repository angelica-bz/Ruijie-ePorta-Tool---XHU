Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModAuthenticationTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModAuthenticationTests

#Region "ModAuthentication 测试用例"

    Private Sub Test_AuthnPrepareFullChain()
        Dim discovery = MakeRealDiscovery()
        Dim prepared = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Telecom), discovery)

        AssertTrue(prepared.Success, "认证参数应就绪：" & prepared.Message)
        AssertTrue(prepared.Payload IsNot Nothing, "应产出 payload")
        AssertTrue(prepared.LoginResult Is Nothing, "PreparePayload 不得提交登录请求")
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication, prepared.DiscoveryStatus, "状态应保留")

        Dim payload = prepared.Payload
        AssertEqual("1234567890123", payload.UserId, "userId 为学号原文")
        AssertFalse(payload.UserId.Contains("@"), "userId 不得含 @")
        AssertEqual("96301", payload.Service, "service 为 96301")
        AssertEqual(discovery.Redirect.QueryString, payload.QueryString, "queryString 原样透传")
        AssertEqual(GoldenBasic, payload.Password, "test1234 + 默认 mac 应得到黄金密文")
        AssertTrue(payload.PasswordEncrypt, "passwordEncrypt 来自门户")
        AssertEqual("true", payload.ToLoginData()("passwordEncrypt"), "login_data 中为小写 true")
    End Sub

    Private Sub Test_AuthnTelecom()
        Dim prepared = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                      MakeRealDiscovery())
        AssertTrue(prepared.Success, prepared.Message)
        AssertEqual("1234567890123", prepared.Payload.UserId, "userId 必须是纯学号")
        AssertEqual("96301", prepared.Payload.Service, "电信 → 96301")
    End Sub

    Private Sub Test_AuthnMobile()
        Dim prepared = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Mobile),
                                      MakeRealDiscovery())
        AssertTrue(prepared.Success, prepared.Message)
        AssertEqual("cmccgx", prepared.Payload.Service, "移动 → cmccgx")
        AssertFalse(prepared.Payload.UserId.Contains("@"), "userId 必须仍是纯学号")
    End Sub

    Private Sub Test_AuthnUnicom()
        Dim prepared = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Unicom),
                                      MakeRealDiscovery())
        AssertTrue(prepared.Success, prepared.Message)
        AssertEqual("unicom", prepared.Payload.Service, "联通 → unicom")
        AssertFalse(prepared.Payload.UserId.Contains("@"), "userId 必须仍是纯学号")
    End Sub

    Private Sub Test_AuthnAlreadyOnline()
        Dim discovery = MakeStatusOnly(ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline,
                                       "未检测到门户重定向，当前已联网。")
        Dim result = AuthenticateWithDiscovery(NewAccount("1234567890123", "test1234", PortalOperator.Telecom), discovery)

        AssertTrue(result.Success, "已联网应视为成功")
        AssertEqual("当前已经联网", result.Message, "应给出明确说明")
        AssertTrue(result.Payload Is Nothing, "不应构造 login payload")
        AssertTrue(result.LoginResult Is Nothing, "不应发出登录请求（无网络访问）")
        AssertTrue(result.Discovery IsNot Nothing, "应保留 Discovery，让调用者知道为何没有登录")
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline, result.DiscoveryStatus, "状态应保留")
    End Sub

    Private Sub Test_AuthnDiscoveryFailures()
        Dim Failures = New ModPortalDiscover.PortalDiscoveryStatus() {
            ModPortalDiscover.PortalDiscoveryStatus.Unreachable,
            ModPortalDiscover.PortalDiscoveryStatus.Timeout,
            ModPortalDiscover.PortalDiscoveryStatus.InvalidLocation,
            ModPortalDiscover.PortalDiscoveryStatus.MissingParameters,
            ModPortalDiscover.PortalDiscoveryStatus.BadResponse,
            ModPortalDiscover.PortalDiscoveryStatus.NoRedirect
        }
        For Each Status In Failures
            Dim Reason As String = "原因-" & Status.ToString()
            Dim result = AuthenticateWithDiscovery(
                NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                MakeStatusOnly(Status, Reason))

            AssertFalse(result.Success, Status.ToString() & " 应判定为失败")
            AssertEqual(Reason, result.Message, Status.ToString() & " 应原样带出门户发现的消息")
            AssertTrue(result.Payload Is Nothing, Status.ToString() & " 不应继续构造 payload")
            AssertEqual(Status, result.DiscoveryStatus, Status.ToString() & " 应保留原始状态")
            AssertTrue(result.Failure <> AuthFailure.None, Status.ToString() & " 应给出失败分类")
        Next
    End Sub

    Private Sub Test_AuthnValidCode()
        Dim discovery = MakeDiscovery(ValidCodeUrl:="http://202.115.144.51/eportal/validCode.jpg",
                                      Services:=ParseServices(TestServicesJson))

        ' 门户要求验证码但未提供
        Dim noCode = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Telecom), discovery)
        AssertFalse(noCode.Success, "缺验证码应失败")
        AssertEqual(AuthFailure.ValidCodeRequired, noCode.Failure, "应为 ValidCodeRequired")
        AssertTrue(noCode.Message.Contains("验证码"), "信息应可直接展示给用户")
        AssertTrue(noCode.Payload Is Nothing, "不应产出 payload")

        ' 提供验证码后可用
        Dim withCode = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Telecom, "1234"), discovery)
        AssertTrue(withCode.Success, "提供验证码后应就绪：" & withCode.Message)
        AssertEqual("1234", withCode.Payload.ValidCode, "应带上验证码")
        AssertEqual("1234", withCode.Payload.ToLoginData()("validcode"), "login_data 中同样带上")
    End Sub

    Private Sub Test_AuthnServiceNotAvailable()
        ' 门户只提供 office / xhu，没有 96301
        Dim discovery = MakeDiscovery(Services:=MakeServices("office", "xhu"))
        Dim result = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Telecom), discovery)

        AssertFalse(result.Success, "门户没有该服务时应失败")
        AssertEqual(AuthFailure.ServiceNotAvailable, result.Failure, "应为 ServiceNotAvailable")
        AssertTrue(result.Payload Is Nothing, "不应产出 payload")
        AssertTrue(result.Message.Contains("96301"), "信息应指出具体 service")
    End Sub

    Private Sub Test_AuthnPasswordGoldenVector()
        Dim prepared = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                      MakeDiscovery(Mac:="111111111", Services:=ParseServices(TestServicesJson)))
        AssertTrue(prepared.Success, prepared.Message)
        AssertEqual(GoldenBasic, prepared.Payload.Password,
                    "ModAuthentication → ModAuth → ModCrypto 必须得到第三阶段黄金向量")
    End Sub

    Private Sub Test_AuthnQueryStringEncodingChain()
        ' ---- 情形 1：query 本身已是 %3A%2F%2F 形态 ----
        Dim Encoded As String = "wlanuserip=10.20.1.2&url=http%3A%2F%2Fexample.com"
        Dim r1 = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                MakeDiscovery(QueryString:=Encoded, Services:=ParseServices(TestServicesJson)))
        AssertTrue(r1.Success, r1.Message)
        AssertEqual(Encoded, r1.Payload.QueryString, "认证层必须原样透传")
        AssertTrue(r1.Payload.QueryString.Contains("%3A%2F%2F"), "应保留 %3A%2F%2F")
        AssertFalse(r1.Payload.QueryString.Contains("%253A"), "认证层不得提前变成 %253A")
        AssertFalse(r1.Payload.QueryString.Contains("://"), "认证层不得提前解码成 :")

        ' EncodeFormData 恰好作用一次，且绝不能作用两次
        Dim Wire1 As String = ModNetwork.EncodeFormData(r1.Payload.ToLoginData())
        AssertTrue(Wire1.Contains("queryString=" & Uri.EscapeDataString(Encoded)), "wire 上应是编码一次的结果")
        AssertFalse(Wire1.Contains(Uri.EscapeDataString(Uri.EscapeDataString(Encoded))), "绝不能被编码两次")

        ' ---- 情形 2：query 是原始形态（BRAS Location 的真实形态）----
        Dim RawQuery As String = "wlanuserip=10.20.1.2&url=http://example.com"
        Dim r2 = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                MakeDiscovery(QueryString:=RawQuery, Services:=ParseServices(TestServicesJson)))
        AssertTrue(r2.Success, r2.Message)
        AssertEqual(RawQuery, r2.Payload.QueryString, "原始 query 同样原样透传")

        Dim Wire2 As String = ModNetwork.EncodeFormData(r2.Payload.ToLoginData())
        AssertTrue(Wire2.Contains("url%3Dhttp%3A%2F%2Fexample.com"),
                   "原始 query 编码一次 → url%3Dhttp%3A%2F%2F，与浏览器 encodeURIComponent 结果一致")
    End Sub

    Private Sub Test_AuthnParseLoginResult()
        Dim Ok = ParseLoginResult(ModJson.ParseJsonResponse(
            "{""result"":""success"",""userIndex"":""test"",""message"":""ok"",""keepaliveInterval"":0}"))
        AssertTrue(Ok.IsSuccess, "result=success 应判为成功")
        AssertEqual("test", Ok.UserIndex, "UserIndex 应解析出来")
        AssertEqual("ok", Ok.Message, "Message")
        AssertEqual(0, Ok.KeepaliveInterval, "KeepaliveInterval")

        Dim Fail = ParseLoginResult(ModJson.ParseJsonResponse("{""result"":""fail"",""message"":""用户名不能为空""}"))
        AssertFalse(Fail.IsSuccess, "result=fail 应判为失败")
        AssertEqual("用户名不能为空", Fail.Message, "Message 应原样保留")

        ' 成功判定只看 result，不能凭 userIndex / forwordurl
        Dim NoResult = ParseLoginResult(ModJson.ParseJsonResponse(
            "{""userIndex"":""x"",""forwordurl"":""http://y""}"))
        AssertFalse(NoResult.IsSuccess, "没有 result=success 就不能算成功")
        AssertEqual("x", NoResult.UserIndex, "但 UserIndex 仍应解析出来")

        ' 门户字段名就是 forwordurl（门户自身拼写）
        Dim Fwd = ParseLoginResult(ModJson.ParseJsonResponse(
            "{""result"":""success"",""forwordurl"":""http://example.com""}"))
        AssertEqual("http://example.com", Fwd.ForwardUrl, "应读取门户拼写 forwordurl")

        ' HTTP 层失败时 PostJson 返回 result=error
        Dim Transport = ParseLoginResult(Dict("result", "error", "message", "HTTP 500: 服务器错误"))
        AssertFalse(Transport.IsSuccess, "传输错误应判为失败")
        AssertEqual("HTTP 500: 服务器错误", Transport.Message, "应带出传输错误信息")

        ' 响应完全为空
        AssertFalse(ParseLoginResult(Nothing).IsSuccess, "空响应应判为失败")
    End Sub

    Private Sub Test_AuthnLoginHeaders()
        Dim Headers = BuildLoginHeaders("http://202.115.144.51/", "a=1", "JSESSIONID=xyz")
        AssertEqual("application/x-www-form-urlencoded; charset=UTF-8", Headers("Content-Type"), "Content-Type")
        AssertEqual("http://202.115.144.51", Headers("Origin"), "Origin（Server 末尾斜杠应被去掉）")
        AssertEqual("http://202.115.144.51/eportal/index.jsp?a=1", Headers("Referer"), "Referer 应按当前 queryString 动态生成")
        AssertEqual(ModNetwork.DefaultUserAgent, Headers("User-Agent"), "User-Agent 复用项目默认值")
        AssertEqual("JSESSIONID=xyz", Headers("Cookie"), "Cookie 由调用方传入")

        ' 不传 Cookie 时不应出现该头
        AssertFalse(BuildLoginHeaders("http://202.115.144.51", "a=1").ContainsKey("Cookie"),
                    "未传 Cookie 时不应出现 Cookie 头")
        ' queryString 为空时不要留下悬空的 '?'
        AssertEqual("http://202.115.144.51/eportal/index.jsp",
                    BuildLoginHeaders("http://202.115.144.51", "")("Referer"), "无 query 时不拼 '?'")

        ' 登录路径与门户契约一致
        AssertEqual("/eportal/InterFace.do?method=login", ModNetwork.PortalLoginPath, "登录接口路径")
    End Sub

    Private Sub Test_AuthnDiagnosePipeline()
        Dim Stages = DiagnosePipeline(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                      MakeRealDiscovery())

        AssertEqual(5, Stages.Count, "应有 5 个体检阶段")
        Dim Names As New List(Of String)
        For Each Stage In Stages
            Names.Add(Stage.Name)
            AssertTrue(Stage.Ok, "阶段 " & Stage.Name & " 应通过：" & Stage.Detail)
        Next
        AssertEqual("Discovery,PublicKey,Services,PasswordEncryption,Payload",
                    String.Join(",", Names.ToArray()), "阶段顺序应固定")

        ' 故意抽掉服务列表与公钥，验证能定位到具体层
        Dim BrokenServices = MakeDiscovery()          ' Services 默认为 Nothing
        Dim Staged1 = DiagnosePipeline(NewAccount("1234567890123", "test1234", PortalOperator.Telecom), BrokenServices)
        AssertFalse(Staged1(2).Ok, "Services 阶段应被标红")
        AssertTrue(Staged1(1).Ok, "PublicKey 阶段仍应通过")

        Dim BrokenKey = MakeDiscovery(Modulus:="not-hex", Services:=ParseServices(TestServicesJson))
        Dim Staged2 = DiagnosePipeline(NewAccount("1234567890123", "test1234", PortalOperator.Telecom), BrokenKey)
        AssertFalse(Staged2(1).Ok, "PublicKey 阶段应被标红")
        AssertFalse(Staged2(3).Ok, "PasswordEncryption 阶段也应标红")

        ' 发现阶段失败
        Dim Staged3 = DiagnosePipeline(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                       MakeStatusOnly(ModPortalDiscover.PortalDiscoveryStatus.Unreachable, "不可达"))
        AssertFalse(Staged3(0).Ok, "Discovery 阶段应被标红")

        ' 已联网时，公钥/服务/加密三段不适用，应标记为 Skipped 而不是失败
        Dim Staged4 = DiagnosePipeline(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                       MakeStatusOnly(ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline, "已联网"))
        For Each Stage In Staged4
            AssertTrue(Stage.Ok, "已联网时所有阶段都不应报错：" & Stage.Name)
        Next
        AssertTrue(Staged4(1).Skipped, "PublicKey 阶段应标记为跳过")
        AssertTrue(Staged4(2).Skipped, "Services 阶段应标记为跳过")
        AssertTrue(Staged4(3).Skipped, "PasswordEncryption 阶段应标记为跳过")
        AssertFalse(Staged4(0).Skipped, "Discovery 阶段不应被标记为跳过")
    End Sub

    Private Sub Test_AuthnSafeLogging()
        Dim prepared = PreparePayload(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                      MakeRealDiscovery())
        AssertTrue(prepared.Success, prepared.Message)
        Dim ConfigText As String = DescribePayloadSafely(prepared.Payload)

        AssertTrue(ConfigText.Contains("Service=96301"), "应包含 service")
        AssertTrue(ConfigText.Contains("Password=<redacted>"), "password 必须打码")
        AssertFalse(ConfigText.Contains(prepared.Payload.Password), "绝不得出现密码密文")
        AssertFalse(ConfigText.Contains(prepared.Payload.QueryString), "绝不得出现完整 queryString")
        AssertTrue(ConfigText.Contains("QueryString=<redacted:"), "queryString 只保留长度")
        AssertFalse(ConfigText.Contains("1234567890123"), "绝不得出现完整学号")

        AssertEqual("1234***0123", MaskUserId("1234567890123"), "学号打码保留首4末4")
        AssertEqual("********", MaskUserId("12345678"), "过短学号整体打码")
        AssertEqual("(空)", MaskUserId(""), "空学号")
        AssertEqual("(空)", MaskUserId(Nothing), "Nothing")

        ' 空 payload 不应抛异常
        AssertEqual("Payload=(无)", DescribePayloadSafely(Nothing), "空 payload 的安全描述")
    End Sub


#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("完整 Payload 链 (真实夹具)", AddressOf Test_AuthnPrepareFullChain)
        RunTest("电信完整链: service 96301", AddressOf Test_AuthnTelecom)
        RunTest("移动完整链: service cmccgx", AddressOf Test_AuthnMobile)
        RunTest("联通完整链: service unicom", AddressOf Test_AuthnUnicom)
        RunTest("已联网时直接成功且不发登录请求", AddressOf Test_AuthnAlreadyOnline)
        RunTest("门户发现失败时不构造 payload", AddressOf Test_AuthnDiscoveryFailures)
        RunTest("验证码: 缺失报错 / 提供后可用", AddressOf Test_AuthnValidCode)
        RunTest("服务不存在时报错", AddressOf Test_AuthnServiceNotAvailable)
        RunTest("密码黄金向量 end-to-end", AddressOf Test_AuthnPasswordGoldenVector)
        RunTest("queryString 编码链只编码一次", AddressOf Test_AuthnQueryStringEncodingChain)
        RunTest("登录结果解析", AddressOf Test_AuthnParseLoginResult)
        RunTest("登录请求头动态生成", AddressOf Test_AuthnLoginHeaders)
        RunTest("诊断入口逐段体检", AddressOf Test_AuthnDiagnosePipeline)
        RunTest("日志安全: 不泄露密码/学号/queryString", AddressOf Test_AuthnSafeLogging)
    End Sub

#End Region

End Module