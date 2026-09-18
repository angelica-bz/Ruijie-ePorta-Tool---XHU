Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModAuthTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModAuthTests

#Region "ModAuth 测试用例"

    Private Sub Test_AuthOperatorMapping()
        ' 映射集中在这一个入口，UI/配置/认证参数都从这里取
        AssertEqual("office", GetServiceCode(PortalOperator.Office), "Office → office")
        AssertEqual("96301", GetServiceCode(PortalOperator.Telecom), "Telecom → 96301")
        AssertEqual("cmccgx", GetServiceCode(PortalOperator.Mobile), "Mobile → cmccgx")
        AssertEqual("unicom", GetServiceCode(PortalOperator.Unicom), "Unicom → unicom")
        AssertEqual("xhu", GetServiceCode(PortalOperator.Xhu), "Xhu → xhu")

        AssertEqual("办公网", GetDisplayName(PortalOperator.Office), "Office 显示名")
        AssertEqual("电信网", GetDisplayName(PortalOperator.Telecom), "Telecom 显示名")
        AssertEqual("移动网", GetDisplayName(PortalOperator.Mobile), "Mobile 显示名")
        AssertEqual("联通网", GetDisplayName(PortalOperator.Unicom), "Unicom 显示名")
        AssertEqual("校内网", GetDisplayName(PortalOperator.Xhu), "Xhu 显示名")

        AssertEqual(GetServiceCode(PortalOperator.Telecom), GetServiceName(PortalOperator.Telecom),
                    "GetServiceName 与 GetServiceCode 应为同一入口")

        AssertEqual(5, GetAllOperators().Length, "共 5 个运营商")

        Dim Op As PortalOperator = PortalOperator.Office
        AssertTrue(TryParseOperator("telecom", Op), "应按枚举名解析")
        AssertEqual(PortalOperator.Telecom, Op, "解析结果应为 Telecom")
        AssertTrue(TryParseOperator("96301", Op), "应按 service 值解析")
        AssertEqual(PortalOperator.Telecom, Op, "解析结果应为 Telecom")
        AssertTrue(TryParseOperator("电信网", Op), "应按中文显示名解析")
        AssertEqual(PortalOperator.Telecom, Op, "解析结果应为 Telecom")
        AssertFalse(TryParseOperator("不存在的运营商", Op), "未知名称应解析失败")
        AssertFalse(TryParseOperator("", Op), "空串应解析失败")
        AssertFalse(TryParseOperator(Nothing, Op), "Nothing 应解析失败")
    End Sub

    Private Sub Test_AuthTelecom()
        Dim payload = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                            MakeDiscovery(Services:=ParseServices(TestServicesJson)))

        AssertEqual("1234567890123", payload.UserId, "userId 必须是学号原文")
        AssertFalse(payload.UserId.Contains("@"), "userId 绝不能包含 @ 后缀")
        AssertEqual("96301", payload.Service, "电信对应 service 96301")
        AssertEqual(256, payload.Password.Length, "password 应为 256 位十六进制密文")
        AssertTrue(IsLowerHexOnly(payload.Password), "password 应为合法小写十六进制")
        AssertTrue(payload.PasswordEncrypt, "门户要求加密密码")
        AssertEqual("true", payload.ToLoginData()("passwordEncrypt"), "login_data 中 passwordEncrypt 为小写 true")
    End Sub

    Private Sub Test_AuthMobile()
        Dim payload = Build(NewAccount("1234567890123", "test1234", PortalOperator.Mobile),
                            MakeDiscovery(Services:=ParseServices(TestServicesJson)))
        AssertEqual("cmccgx", payload.Service, "移动对应 service cmccgx")
        AssertFalse(payload.UserId.Contains("@"), "userId 绝不能包含 @ 后缀")
        AssertEqual("1234567890123", payload.UserId, "userId 必须保持学号原文")
    End Sub

    Private Sub Test_AuthUnicom()
        Dim payload = Build(NewAccount("1234567890123", "test1234", PortalOperator.Unicom),
                            MakeDiscovery(Services:=ParseServices(TestServicesJson)))
        AssertEqual("unicom", payload.Service, "联通对应 service unicom")
        AssertFalse(payload.UserId.Contains("@"), "userId 绝不能包含 @ 后缀")
        AssertEqual("1234567890123", payload.UserId, "userId 必须保持学号原文")
    End Sub

    Private Sub Test_AuthOffice()
        Dim payload = Build(NewAccount("1234567890123", "test1234", PortalOperator.Office),
                            MakeDiscovery(Services:=ParseServices(TestServicesJson)))
        AssertEqual("office", payload.Service, "办公网对应 service office")
        AssertFalse(payload.UserId.Contains("@"), "userId 绝不能包含 @ 后缀")
    End Sub

    Private Sub Test_AuthXhu()
        Dim payload = Build(NewAccount("1234567890123", "test1234", PortalOperator.Xhu),
                            MakeDiscovery(Services:=ParseServices(TestServicesJson)))
        AssertEqual("xhu", payload.Service, "校内网对应 service xhu")
        AssertFalse(payload.UserId.Contains("@"), "userId 绝不能包含 @ 后缀")
    End Sub

    Private Sub Test_AuthLeadingZeroUserId()
        Dim payload = Build(NewAccount("001234567890", "test1234", PortalOperator.Telecom), MakeDiscovery())
        AssertEqual("001234567890", payload.UserId, "前导零学号必须原样保留")
        AssertEqual("001234567890", payload.ToLoginData()("userId"), "login_data 中同样要保留前导零")

        ' 门户 JS 对用户名执行 username.trim()
        Dim trimmed = Build(NewAccount("  1234567890123  ", "test1234", PortalOperator.Telecom), MakeDiscovery())
        AssertEqual("1234567890123", trimmed.UserId, "学号两侧空白应被去除（与门户 JS 一致）")
    End Sub

    Private Sub Test_AuthPasswordGoldenVector()
        ' ModAuth → ModCrypto 的端到端结果必须等于第三阶段用真实 security.js 生成的黄金向量
        Dim payload = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                            MakeDiscovery(Mac:="111111111"))
        AssertEqual(GoldenBasic, payload.Password, "test1234 + mac 111111111 应得到已验证的黄金密文")
    End Sub

    Private Sub Test_AuthMacDefault()
        ' query 中没有 mac → Redirect.Mac 为 Nothing → 由 ModCrypto 回退到 111111111
        Dim withoutMac = MakeDiscovery()
        AssertTrue(withoutMac.Redirect.Mac Is Nothing, "前置条件：没有 mac 参数时应为 Nothing")
        Dim a = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom), withoutMac)

        Dim withMac = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                            MakeDiscovery(Mac:="111111111"))

        AssertEqual(withMac.Password, a.Password, "mac 缺失必须与显式 111111111 完全一致")
        AssertEqual(GoldenBasic, a.Password, "缺失 mac 时应得到黄金密文")
    End Sub

    Private Sub Test_AuthRealDiscovery()
        ' 用第四阶段的真实夹具拼出完整 PortalDiscoveryResult，跑一遍端到端
        Dim discovery As New ModPortalDiscover.PortalDiscoveryResult With {
            .Status = ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication,
            .Redirect = ParseRedirectLocation(TestPortalRedirect),
            .PageInfo = ParsePageInfo(TestPageInfoJson),
            .Services = ParseServices(TestServicesJson)
        }
        Dim payload = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom), discovery)

        AssertEqual("1234567890123", payload.UserId, "userId 为学号原文")
        AssertEqual("96301", payload.Service, "service 为 96301")
        AssertEqual(discovery.Redirect.QueryString, payload.QueryString, "queryString 应原样透传")
        AssertEqual(GoldenBasic, payload.Password, "test1234 + 默认 mac 应得到黄金密文")
        AssertTrue(payload.PasswordEncrypt, "真实门户 passwordEncrypt 为 true")
        AssertEqual("", payload.ValidCode, "真实门户 validCodeUrl 为空，不需要验证码")
        AssertEqual("", payload.OperatorPwd, "普通校园网认证 operatorPwd 固定为空")
        AssertEqual("", payload.OperatorUserId, "普通校园网认证 operatorUserId 固定为空")
    End Sub

    Private Sub Test_AuthValidCode()
        ' 1) 门户不要求验证码
        Dim noCode = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                           MakeDiscovery(ValidCodeUrl:=""))
        AssertEqual("", noCode.ValidCode, "不需要验证码时应为空串")

        ' 即使调用方塞了验证码，门户不要求时也应清空
        Dim ignored = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom, "9999"),
                            MakeDiscovery(ValidCodeUrl:=""))
        AssertEqual("", ignored.ValidCode, "门户不要求验证码时不应提交验证码")

        ' 2) 门户要求且用户已提供
        Dim withCode = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom, "1234"),
                             MakeDiscovery(ValidCodeUrl:="http://202.115.144.51/eportal/validCode.jpg"))
        AssertEqual("1234", withCode.ValidCode, "应带上用户输入的验证码")

        ' 3) 门户要求但用户未提供 → 明确错误
        Dim Discovery = MakeDiscovery(ValidCodeUrl:="http://202.115.144.51/eportal/validCode.jpg")
        Dim Payload As AuthPayload = Nothing
        Dim Failure As AuthFailure = AuthFailure.None
        Dim Message As String = ""
        Dim Ok As Boolean = TryBuild(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                     Discovery, Payload, Failure, Message)
        AssertFalse(Ok, "缺少验证码应失败")
        AssertEqual(AuthFailure.ValidCodeRequired, Failure, "应给出 ValidCodeRequired")
        AssertTrue(Message.Contains("验证码"), "错误信息应能直接展示给用户")
        AssertTrue(Payload Is Nothing, "失败时不应产出 payload")
    End Sub

    Private Sub Test_AuthServiceNotAvailable()
        ' 门户当前只提供 office / xhu，没有 96301
        Dim Services = MakeServices("office", "xhu")
        Dim Payload As AuthPayload = Nothing
        Dim Failure As AuthFailure = AuthFailure.None
        Dim Message As String = ""
        Dim Ok As Boolean = TryBuild(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                     MakeDiscovery(Services:=Services), Payload, Failure, Message)
        AssertFalse(Ok, "门户没有该服务时应失败，而不是继续发送错误 payload")
        AssertEqual(AuthFailure.ServiceNotAvailable, Failure, "应为 ServiceNotAvailable")
        AssertTrue(Message.Contains("96301"), "错误信息应指出具体的 service")
        AssertTrue(Payload Is Nothing, "失败时不应产出 payload")

        ' 服务列表不可用时跳过校验（此时无法判断，不应误报）
        Dim noList = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                           MakeDiscovery(Services:=Nothing))
        AssertEqual("96301", noList.Service, "没有服务列表时应跳过校验继续构造")
    End Sub

    Private Sub Test_AuthQueryStringNotEncoded()
        Dim Q As String = "wlanuserip=10.20.1.2&url=http%3A%2F%2Fexample.com"
        Dim payload = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                            MakeDiscovery(QueryString:=Q))
        AssertEqual(Q, payload.QueryString, "queryString 必须与输入完全一致")
        AssertEqual(Q, payload.ToLoginData()("queryString"), "login_data 中也保持原样")
        AssertTrue(payload.QueryString.Contains("%3A%2F%2F"), "不得提前解码")
        AssertFalse(payload.QueryString.Contains("://"), "不得解码后再传出")
    End Sub

    Private Sub Test_AuthLoginDataShape()
        Dim payload = Build(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                            MakeDiscovery(Services:=ParseServices(TestServicesJson)))
        Dim Data = payload.ToLoginData()

        AssertEqual(LoginDataKeys.Length, Data.Count, "login_data 应恰好包含门户需要的 8 个字段")
        For Each KeyName In LoginDataKeys
            AssertTrue(Data.ContainsKey(KeyName), "缺少字段：" & KeyName)
            AssertTrue(TypeOf Data(KeyName) Is String, "字段值必须是 String：" & KeyName)
        Next
        ' 不该混入其它字段
        AssertFalse(Data.ContainsKey("cookie"), "不应混入 cookie")
        AssertFalse(Data.ContainsKey("headers"), "不应混入 headers")

        AssertEqual("1234567890123", Data("userId"), "userId")
        AssertEqual("96301", Data("service"), "service")
        AssertEqual("", Data("operatorPwd"), "operatorPwd 本阶段固定为空串")
        AssertEqual("", Data("operatorUserId"), "operatorUserId 本阶段固定为空串")
        AssertEqual("", Data("validcode"), "无验证码时为空串")
        AssertEqual("true", Data("passwordEncrypt"), "passwordEncrypt 为小写字符串")
        AssertTrue(Data("password").Length > 0, "password 不应为空")
    End Sub

    Private Sub Test_AuthErrorCases()
        Dim Account = NewAccount("1234567890123", "test1234", PortalOperator.Telecom)

        ' 账号
        AssertAuthFailure(Nothing, MakeDiscovery(), AuthFailure.MissingUserId, "账号为 Nothing")
        AssertAuthFailure(NewAccount("", "test1234", PortalOperator.Telecom), MakeDiscovery(),
                          AuthFailure.MissingUserId, "学号为空")
        AssertAuthFailure(NewAccount("   ", "test1234", PortalOperator.Telecom), MakeDiscovery(),
                          AuthFailure.MissingUserId, "学号纯空白")
        AssertAuthFailure(NewAccount("1234567890123", "", PortalOperator.Telecom), MakeDiscovery(),
                          AuthFailure.MissingPassword, "密码为空")
        AssertAuthFailure(NewAccount("1234567890123", Nothing, PortalOperator.Telecom), MakeDiscovery(),
                          AuthFailure.MissingPassword, "密码为 Nothing")
        AssertAuthFailure(NewAccount("1234567890123", "test1234", CType(99, PortalOperator)), MakeDiscovery(),
                          AuthFailure.InvalidOperator, "非法运营商")

        ' 门户发现结果
        AssertAuthFailure(Account, Nothing, AuthFailure.MissingDiscovery, "discovery 为 Nothing")
        Dim NoRedirect = MakeDiscovery()
        NoRedirect.Redirect = Nothing
        AssertAuthFailure(Account, NoRedirect, AuthFailure.MissingQueryString, "缺 Redirect")
        AssertAuthFailure(Account, MakeDiscovery(QueryString:=""), AuthFailure.MissingQueryString, "queryString 为空")

        ' 公钥
        Dim NoPageInfo = MakeDiscovery()
        NoPageInfo.PageInfo = Nothing
        AssertAuthFailure(Account, NoPageInfo, AuthFailure.MissingPublicKey, "缺 pageInfo")
        Dim EmptyKey = MakeDiscovery()
        EmptyKey.PageInfo.PublicKeyModulus = ""
        AssertAuthFailure(Account, EmptyKey, AuthFailure.MissingPublicKey, "modulus 为空")
        Dim EmptyExp = MakeDiscovery()
        EmptyExp.PageInfo.PublicKeyExponent = ""
        AssertAuthFailure(Account, EmptyExp, AuthFailure.MissingPublicKey, "exponent 为空")
        Dim BadKey = MakeDiscovery()
        BadKey.PageInfo.PublicKeyModulus = "not-hex"
        AssertAuthFailure(Account, BadKey, AuthFailure.InvalidPublicKey, "modulus 非法")

        ' 密码加密失败（越界字符会让 ModCrypto 拒绝）
        AssertAuthFailure(NewAccount("1234567890123", "A" & ChrW(&H4E2D) & "B", PortalOperator.Telecom),
                          MakeDiscovery(), AuthFailure.PasswordEncryptFailed, "密码含门户无法加密的字符")

        ' Build 抛出式接口上的异常类型与 Failure 一致
        Try
            Build(NewAccount("", "test1234", PortalOperator.Telecom), MakeDiscovery())
            Throw New Exception("断言失败: Build 应当抛出 AuthException")
        Catch ex As AuthException
            AssertEqual(AuthFailure.MissingUserId, ex.Failure, "AuthException.Failure 应为 MissingUserId")
            AssertTrue(ex.Message.Length > 0, "AuthException.Message 应可直接展示")
        End Try
    End Sub


#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("运营商映射与解析", AddressOf Test_AuthOperatorMapping)
        RunTest("电信: userId 纯学号 + service 96301", AddressOf Test_AuthTelecom)
        RunTest("移动: service cmccgx", AddressOf Test_AuthMobile)
        RunTest("联通: service unicom", AddressOf Test_AuthUnicom)
        RunTest("办公网: service office", AddressOf Test_AuthOffice)
        RunTest("校内网: service xhu", AddressOf Test_AuthXhu)
        RunTest("前导零学号原样保留", AddressOf Test_AuthLeadingZeroUserId)
        RunTest("密码黄金向量 (test1234 / 111111111)", AddressOf Test_AuthPasswordGoldenVector)
        RunTest("mac 缺失等价于默认值", AddressOf Test_AuthMacDefault)
        RunTest("真实 discovery 数据端到端", AddressOf Test_AuthRealDiscovery)
        RunTest("验证码三种情形", AddressOf Test_AuthValidCode)
        RunTest("所选 service 不存在时报错", AddressOf Test_AuthServiceNotAvailable)
        RunTest("queryString 原样透传不提前编码", AddressOf Test_AuthQueryStringNotEncoded)
        RunTest("ToLoginData 字段形状", AddressOf Test_AuthLoginDataShape)
        RunTest("各类非法输入给出明确错误", AddressOf Test_AuthErrorCases)
    End Sub

#End Region

End Module