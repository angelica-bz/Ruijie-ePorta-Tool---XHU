Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModPortalDiscoverTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModPortalDiscoverTests

#Region "ModPortalDiscover 测试夹具"

    ' 真实 pageInfo 响应的压缩摘录：只保留 ModPortalDiscover 解析的字段，值均为原样。
    ' 完整响应：%TEMP%\ruijie-eportal-investigation\flow1.pageInfo.json

    Friend Const TestPageInfoJson As String =
        "{""passwordEncrypt"":""true"",""publicKeyExponent"":""10001"",""publicKeyModulus"":""94dd2a8675fb779e6b9f7103698634cd400f27a154afa67af6166a43fc26417222a79506d34cacc7641946abda1785b7acf9910ad6a0978c91ec84d40b71d2891379af19ffb333e7517e390bd26ac312fe940c340466b4a5d4af1d65c3b5944078f96a1a51a5a53e4bc302818b7c9f63c4a1b07bd7d874cef1c3d4b2f5eb7871""," &
        """validCodeUrl"":"""",""successPage"":"""",""isAutoLogin"":""false"",""prefixName"":""false""," &
        """prefixValue"":"""",""isToCasPage"":""false"",""isCheckSmsAuth"":""true"",""loginText"":""发现校园网的\""无线\""精彩""," &
        """selfUrl"":""http://202.115.144.52:8080/selfservice/"",""checkSourceIdClient"":null,""service"":{""96301"":{""serviceName"":""96301""," &
        """serviceShowName"":""电信网"",""serviceDefault"":""false"",""aceNotShow"":""false"",""operatorDefault"":""""," &
        """domainName"":""true""},""xhu"":{""serviceName"":""xhu"",""serviceShowName"":""校内网"",""serviceDefault"":""false""," &
        """aceNotShow"":""false"",""operatorDefault"":"""",""domainName"":""true""},""cmccgx"":{""serviceName"":""cmccgx""," &
        """serviceShowName"":""移动网"",""serviceDefault"":""false"",""aceNotShow"":""false"",""operatorDefault"":""""," &
        """domainName"":""true""},""unicom"":{""serviceName"":""unicom"",""serviceShowName"":""联通网""," &
        """serviceDefault"":""false"",""aceNotShow"":""false"",""operatorDefault"":"""",""domainName"":""true""}," &
        """office"":{""serviceName"":""office"",""serviceShowName"":""办公网"",""serviceDefault"":""true""," &
        """aceNotShow"":""false"",""operatorDefault"":"""",""domainName"":""true""}}}"

    ' 真实 getServices 响应的压缩摘录。
    ' 完整响应：%TEMP%\ruijie-eportal-investigation\getServices.html

    Friend Const TestServicesJson As String =
        "{""defaultService"":""<div id=\""selectDisname\"" tabIndex=\""0\"">办公网</div><input name=\""net_access_type\"" id=\""net_access_type\"" value='office' type=\""hidden\""/><input name=\""isNoOperatorPwd\"" id=\""isNoOperatorPwd\"" value='' type=\""hidden\""/><input name=\""isNoDomainName\"" id=\""isNoDomainName\"" value='true' type=\""hidden\""/>""," &
        """isService"":""true"",""services"":""[{\""aceNotShow\"":\""false\"",\""domainName\"":\""true\""," &
        "\""operatorDefault\"":\""\"",\""serviceDefault\"":\""true\"",\""serviceName\"":\""office\""," &
        "\""serviceShowName\"":\""办公网\""},{\""aceNotShow\"":\""false\"",\""domainName\"":\""true\""," &
        "\""operatorDefault\"":\""\"",\""serviceDefault\"":\""false\"",\""serviceName\"":\""96301\""," &
        "\""serviceShowName\"":\""电信网\""},{\""aceNotShow\"":\""false\"",\""domainName\"":\""true\""," &
        "\""operatorDefault\"":\""\"",\""serviceDefault\"":\""false\"",\""serviceName\"":\""cmccgx\""," &
        "\""serviceShowName\"":\""移动网\""},{\""aceNotShow\"":\""false\"",\""domainName\"":\""true\""," &
        "\""operatorDefault\"":\""\"",\""serviceDefault\"":\""false\"",\""serviceName\"":\""unicom\""," &
        "\""serviceShowName\"":\""联通网\""},{\""aceNotShow\"":\""false\"",\""domainName\"":\""true\""," &
        "\""operatorDefault\"":\""\"",\""serviceDefault\"":\""false\"",\""serviceName\"":\""xhu\""," &
        "\""serviceShowName\"":\""校内网\""}]"",""serviceJson"":""[{\""aceNotShow\"":\""false\"",\""domainName\"":\""true\""," &
        "\""operatorDefault\"":\""\"",\""serviceDefault\"":\""true\"",\""serviceName\"":\""office\""," &
        "\""serviceShowName\"":\""办公网\""},{\""aceNotShow\"":\""false\"",\""domainName\"":\""true\""," &
        "\""operatorDefault\"":\""\"",\""serviceDefault\"":\""false\"",\""serviceName\"":\""96301\""," &
        "\""serviceShowName\"":\""电信网\""},{\""aceNotShow\"":\""false\"",\""domainName\"":\""true\""," &
        "\""operatorDefault\"":\""\"",\""serviceDefault\"":\""false\"",\""serviceName\"":\""cmccgx\""," &
        "\""serviceShowName\"":\""移动网\""},{\""aceNotShow\"":\""false\"",\""domainName\"":\""true\""," &
        "\""operatorDefault\"":\""\"",\""serviceDefault\"":\""false\"",\""serviceName\"":\""unicom\""," &
        "\""serviceShowName\"":\""联通网\""},{\""aceNotShow\"":\""false\"",\""domainName\"":\""true\""," &
        "\""operatorDefault\"":\""\"",\""serviceDefault\"":\""false\"",\""serviceName\"":\""xhu\""," &
        "\""serviceShowName\"":\""校内网\""}]""}"

    ' 第二阶段实测：开发机处于已认证状态时，探测地址返回的是微软自己的普通 302
    Friend Const TestNormalRedirect As String =
        "http://go.microsoft.com/fwlink/?LinkID=219472&clcid=0x409"

    ' 未认证时应被 BRAS 劫持成的形态（沿用第二阶段抓包结构，IP 换成示例值）
    Friend Const TestPortalRedirect As String =
        "http://202.115.144.51/eportal/index.jsp?wlanuserip=10.20.1.2&wlanacname=&nasip=1.2.3.4" &
        "&wlanparameter=02-00-00-00-00-01&url=http%3A%2F%2Fwww.msftconnecttest.com%2Fredirect" &
        "&userlocation=ethtrunk%2F1%3A1000.0"

#End Region

#Region "ModPortalDiscover 测试用例"

    Private Sub Test_PortalExtractQueryString()
        Dim info = ParseRedirectLocation("http://202.115.144.51/eportal/index.jsp?wlanuserip=10.20.1.2&wlanacname=&nasip=1.2.3.4")
        AssertEqual("wlanuserip=10.20.1.2&wlanacname=&nasip=1.2.3.4", info.QueryString, "QueryString 应为原始 query 部分")
        AssertEqual("http://202.115.144.51/eportal/index.jsp", info.PortalUrl, "PortalUrl 不应包含 query")
        AssertEqual("wlanuserip=10.20.1.2&wlanacname=&nasip=1.2.3.4",
                    ExtractQueryString("http://202.115.144.51/eportal/index.jsp?wlanuserip=10.20.1.2&wlanacname=&nasip=1.2.3.4"),
                    "ExtractQueryString 应与 ParseRedirectLocation 一致")
    End Sub

    Private Sub Test_PortalParseParameters()
        Dim info = ParseRedirectLocation(TestPortalRedirect)
        AssertEqual("10.20.1.2", info.GetParameter("wlanuserip"), "wlanuserip 应可取出")
        AssertEqual("1.2.3.4", info.GetParameter("nasip"), "nasip 应可取出")
        AssertEqual("02-00-00-00-00-01", info.GetParameter("wlanparameter"), "wlanparameter 应可取出")
        AssertEqual("ethtrunk/1:1000.0", info.GetParameter("userlocation"), "userlocation 应被解码后取出")
        AssertEqual("http://www.msftconnecttest.com/redirect", info.GetParameter("url"), "url 应被解码后取出")
        AssertTrue(info.GetParameter("wlanacname") IsNot Nothing, "空值参数也应存在（不能因为值为空就丢弃）")
        AssertEqual("", info.GetParameter("wlanacname"), "wlanacname 的值为空串")
        AssertTrue(info.GetParameter("not_exist") Is Nothing, "不存在的参数应返回 Nothing")
    End Sub

    Private Sub Test_PortalKeepsPercentEncoding()
        Dim info = ParseRedirectLocation(TestPortalRedirect)
        AssertTrue(info.QueryString.Contains("%3A%2F%2F"), "QueryString 必须保留原始 %3A%2F%2F")
        AssertFalse(info.QueryString.Contains("://"), "QueryString 不得被提前解码")
        AssertTrue(info.QueryString.Contains("wlanuserip=10.20.1.2"), "QueryString 应保持原始键值顺序与形态")
        ' 参数表提供解码后的值，两者分工明确
        AssertEqual("http://www.msftconnecttest.com/redirect", info.GetParameter("url"),
                    "Parameters 提供解码后的值，QueryString 保持原始编码")
    End Sub

    Private Sub Test_PortalStripsFragment()
        Dim info = ParseRedirectLocation("http://202.115.144.51/eportal/index.jsp?wlanuserip=10.20.1.2&nasip=1.2.3.4#frag?x=1")
        AssertEqual("wlanuserip=10.20.1.2&nasip=1.2.3.4", info.QueryString, "'#' 之后的内容应被剔除")
        AssertEqual("http://202.115.144.51/eportal/index.jsp", info.PortalUrl, "PortalUrl 不应包含 fragment")
        AssertEqual("", ExtractQueryString("http://202.115.144.51/eportal/index.jsp#frag?x=1"),
                    "fragment 中的 '?' 不应被误判为 query 起点")
    End Sub

    Private Sub Test_PortalNoQuery()
        AssertEqual("", ExtractQueryString("http://202.115.144.51/eportal/index.jsp"), "无 query 应返回空串")
        Dim info = ParseRedirectLocation("http://202.115.144.51/eportal/index.jsp")
        AssertEqual("", info.QueryString, "无 query 时 QueryString 为空串")
        AssertEqual(0, info.Parameters.Count, "无 query 时参数表为空")
    End Sub

    Private Sub Test_PortalInvalidLocation()
        AssertThrows(Sub() ParseRedirectLocation(""), GetType(ModPortalDiscover.PortalDiscoveryException), "空 Location 应被拒绝")
        AssertThrows(Sub() ParseRedirectLocation("   "), GetType(ModPortalDiscover.PortalDiscoveryException), "纯空白 Location 应被拒绝")
        AssertThrows(Sub() ParseRedirectLocation(Nothing), GetType(ModPortalDiscover.PortalDiscoveryException), "Nothing Location 应被拒绝")
    End Sub

    Private Sub Test_PortalMacPresent()
        Dim info = ParseRedirectLocation("http://202.115.144.51/eportal/index.jsp?wlanuserip=10.20.1.2&nasip=1.2.3.4&mac=abcdef")
        AssertEqual("abcdef", info.Mac, "mac 应被完整保留，供 ModCrypto 使用")
    End Sub

    Private Sub Test_PortalMacAbsent()
        Dim info = ParseRedirectLocation(TestPortalRedirect)
        AssertTrue(info.Mac Is Nothing, "没有 mac 参数时应返回 Nothing（默认值由 ModCrypto 负责）")
    End Sub

    Private Sub Test_PortalClassifyPortalRedirect()
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication,
                    ClassifyRedirect(TestPortalRedirect),
                    "同时含 wlanuserip 与 nasip 应判定为门户劫持（需要认证）")
    End Sub

    Private Sub Test_PortalClassifyNormalRedirect()
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline,
                    ClassifyRedirect(TestNormalRedirect),
                    "已联网时微软自己的 302 应判定为无需门户参数")
    End Sub

    Private Sub Test_PortalClassifyMissingParameters()
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.MissingParameters,
                    ClassifyRedirect("http://202.115.144.51/eportal/index.jsp"),
                    "指向门户登录页但缺认证参数应单独区分")
    End Sub

    Private Sub Test_PortalGetOrigin()
        AssertEqual("http://202.115.144.51", GetOrigin(ParseRedirectLocation(TestPortalRedirect).PortalUrl),
                    "应能从 PortalUrl 提取源地址")
        AssertEqual("http://202.115.144.51", GetOrigin("http://202.115.144.51/eportal/index.jsp"), "无 query 同样适用")
        AssertEqual("", GetOrigin("not a url"), "非法 URL 应返回空串")
    End Sub

    Private Sub Test_PortalBuildResultPortal()
        Dim result = BuildRedirectResult(302, TestPortalRedirect)
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication, result.Status,
                    "302 指向门户登录页应判定为需要认证")
        AssertTrue(result.NeedsAuthentication, "NeedsAuthentication 应为 True")
        AssertTrue(result.Redirect IsNot Nothing, "Redirect 应被填充")
        AssertEqual("10.20.1.2", result.Redirect.GetParameter("wlanuserip"), "应带出认证参数")
        AssertTrue(result.Message.Contains("门户"), "说明文字应表明检测到门户重定向")
    End Sub

    Private Sub Test_PortalBuildResultOnline()
        ' 回归用例：曾把分类结果作为 ByRef 实参传给解析函数而被其出参覆盖，
        ' 造成状态正确、文案却错报“Location 无效”。此用例锁死该行为。
        Dim result = BuildRedirectResult(302, TestNormalRedirect)
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline, result.Status,
                    "已联网时微软自己的普通 302 应判定为 AlreadyOnline")
        AssertTrue(result.IsSuccess, "已联网属于成功状态，不应视为错误")
        AssertFalse(result.NeedsAuthentication, "已联网时不需要认证")
        AssertFalse(result.Message.Contains("无效"), "已联网时文案不应报 Location 无效")
        AssertTrue(result.Redirect IsNot Nothing, "仍应带出普通跳转的 Location 信息")
    End Sub

    Private Sub Test_PortalBuildResultEdge()
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.InvalidLocation,
                    BuildRedirectResult(302, "").Status, "3xx 但没有 Location 应判为 InvalidLocation")
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.MissingParameters,
                    BuildRedirectResult(302, "http://202.115.144.51/eportal/index.jsp").Status,
                    "指向门户页但缺认证参数应单独区分")
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline,
                    BuildRedirectResult(200, "").Status, "2xx 应判定为已联网")
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.NoRedirect,
                    BuildRedirectResult(500, "").Status, "5xx 应判定为未检测到门户重定向")
    End Sub

    Private Sub Test_PortalPageInfoRealPublicKey()
        Dim info = ParsePageInfo(TestPageInfoJson)
        AssertEqual("10001", info.PublicKeyExponent, "exponent 应为 10001")
        AssertEqual(256, info.PublicKeyModulus.Length, "modulus 应为 256 个十六进制字符（1024 bit）")
        AssertTrue(IsLowerHexOnly(info.PublicKeyModulus), "modulus 应为合法小写十六进制")
        AssertTrue(info.PasswordEncrypt, "本门户 passwordEncrypt 为 true")
    End Sub

    Private Sub Test_PortalPageInfoRealServices()
        Dim info = ParsePageInfo(TestPageInfoJson)
        AssertTrue(info.Services IsNot Nothing, "pageInfo.service 应解析出服务表")
        AssertEqual(5, info.Services.Items.Count, "应有 5 个服务")
        AssertTrue(info.Services.FindByName("96301") IsNot Nothing, "96301 应存在")
        AssertEqual("电信网", info.Services.FindByName("96301").DisplayName, "96301 → 电信网")
        AssertEqual("办公网", info.Services.FindByName("office").DisplayName, "office → 办公网")
        AssertEqual("office", info.Services.DefaultName, "默认服务应为 office")
        AssertTrue(info.Services.FindByName("office").IsDefault, "office 应带 serviceDefault=true")
    End Sub

    Private Sub Test_PortalPageInfoValidCode()
        Dim info = ParsePageInfo(TestPageInfoJson)
        AssertEqual("", info.ValidCodeUrl, "本门户 validCodeUrl 为空串")
        AssertFalse(info.RequiresValidCode, "validCodeUrl 为空表示当前不需要验证码")
    End Sub

    Private Sub Test_PortalServicesReal()
        Dim services = ParseServices(TestServicesJson)
        AssertEqual(5, services.Items.Count, "应有 5 个服务")
        AssertTrue(services.FindByName("96301") IsNot Nothing, "96301 应存在")
        AssertEqual("电信网", services.FindByName("96301").DisplayName, "96301 → 电信网")
        AssertEqual("移动网", services.FindByName("cmccgx").DisplayName, "cmccgx → 移动网")
        AssertEqual("联通网", services.FindByName("unicom").DisplayName, "unicom → 联通网")
        AssertEqual("校内网", services.FindByName("xhu").DisplayName, "xhu → 校内网")
        AssertEqual("办公网", services.FindByName("office").DisplayName, "office → 办公网")
        AssertEqual("office", services.DefaultName, "默认服务应为 office（取自 net_access_type）")
        AssertEqual("office", services.DefaultService.Name, "DefaultService 应为 office")
    End Sub

    Private Sub Test_PortalDirtyMissingPublicKey()
        ' 声称要加密却没有公钥：必须在此明确报错，而不是等到登录时才失败
        AssertThrows(Sub() ParsePageInfo(Dict("passwordEncrypt", "true")),
                     GetType(ModPortalDiscover.PortalDiscoveryException), "要求加密但完全没有公钥字段")
        AssertThrows(Sub() ParsePageInfo(Dict("passwordEncrypt", "true", "publicKeyExponent", "10001", "publicKeyModulus", "")),
                     GetType(ModPortalDiscover.PortalDiscoveryException), "要求加密但 modulus 为空串")
        AssertThrows(Sub() ParsePageInfo(Dict("passwordEncrypt", "true", "publicKeyExponent", "", "publicKeyModulus", TestModulus)),
                     GetType(ModPortalDiscover.PortalDiscoveryException), "要求加密但 exponent 为空串")
    End Sub

    Private Sub Test_PortalDirtyBadPublicKey()
        Dim reason As String = ""

        AssertFalse(ValidatePublicKey("xyz", TestModulus, reason), "exponent 含非 hex 应失败")
        AssertTrue(reason.Length > 0, "失败时必须给出可读原因")

        AssertFalse(ValidatePublicKey("10001", "not-a-hex", reason), "modulus 含非 hex 应失败")
        AssertTrue(reason.Length > 0, "失败时必须给出可读原因")

        AssertFalse(ValidatePublicKey("10001", "abcd", reason), "modulus 过短应失败")
        AssertFalse(ValidatePublicKey("", TestModulus, reason), "exponent 为空应失败")
        AssertFalse(ValidatePublicKey("10001", "", reason), "modulus 为空应失败")
        AssertFalse(ValidatePublicKey(Nothing, TestModulus, reason), "exponent 为 Nothing 应失败")

        AssertTrue(ValidatePublicKey("10001", TestModulus, reason), "真实公钥应通过校验")
        AssertEqual("", reason, "通过校验时不应留下错误信息")
    End Sub

    Private Sub Test_PortalDirtyBadJson()
        AssertThrows(Sub() ParsePageInfo("{这不是合法 JSON"),
                     GetType(ModPortalDiscover.PortalDiscoveryException), "非法 JSON 应报错")
        AssertThrows(Sub() ParsePageInfo(""),
                     GetType(ModPortalDiscover.PortalDiscoveryException), "空响应应报错")
        AssertThrows(Sub() ParsePageInfo("[1,2,3]"),
                     GetType(ModPortalDiscover.PortalDiscoveryException), "JSON 数组不是 pageInfo 对象，应报错")
        AssertThrows(Sub() ParseServices("{这不是合法 JSON"),
                     GetType(ModPortalDiscover.PortalDiscoveryException), "getServices 非法 JSON 应报错")
    End Sub

    Private Sub Test_PortalDirtyEmptyServices()
        AssertThrows(Sub() ParseServices(Dict("services", "[]", "serviceJson", "[]")),
                     GetType(ModPortalDiscover.PortalDiscoveryException), "空服务列表应报错而不是返回空集合")
        AssertThrows(Sub() ParseServices(Dict("services", "这不是 JSON")),
                     GetType(ModPortalDiscover.PortalDiscoveryException), "服务列表非法应报错")
        AssertThrows(Sub() ParseServices(Dict("unrelated", "x")),
                     GetType(ModPortalDiscover.PortalDiscoveryException), "完全没有服务字段应报错")
    End Sub

    Private Sub Test_PortalDirtyUnknownService()
        Dim services = ParseServices(TestServicesJson)
        AssertTrue(services.FindByName("notexist") Is Nothing, "未知 service 名应返回 Nothing 而不是抛异常")
        AssertTrue(services.FindByName("") Is Nothing, "空名字应返回 Nothing")
        AssertTrue(services.FindByName(Nothing) Is Nothing, "Nothing 应返回 Nothing")
    End Sub

    Private Sub Test_PortalNoEncryptAllowsEmptyKey()
        ' passwordEncrypt=false 时门户不下发公钥（登录页静态 HTML 里就是空值），不应因此报错
        Dim info = ParsePageInfo(Dict("passwordEncrypt", "false",
                                      "publicKeyExponent", "",
                                      "publicKeyModulus", "",
                                      "validCodeUrl", ""))
        AssertFalse(info.PasswordEncrypt, "passwordEncrypt 应为 False")
        AssertEqual("", info.PublicKeyModulus, "无公钥时 modulus 为空串")
        AssertFalse(info.RequiresValidCode, "无验证码")
    End Sub

#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("Location 提取原始 query", AddressOf Test_PortalExtractQueryString)
        RunTest("Location 参数解析", AddressOf Test_PortalParseParameters)
        RunTest("Location 保持百分号编码不提前解码", AddressOf Test_PortalKeepsPercentEncoding)
        RunTest("Location 正确剔除 # 片段", AddressOf Test_PortalStripsFragment)
        RunTest("Location 无 query 得到空串", AddressOf Test_PortalNoQuery)
        RunTest("Location 非法输入被拒绝", AddressOf Test_PortalInvalidLocation)
        RunTest("mac 存在时被完整保留", AddressOf Test_PortalMacPresent)
        RunTest("mac 不存在时为 Nothing", AddressOf Test_PortalMacAbsent)
        RunTest("门户重定向判定为 NeedAuthentication", AddressOf Test_PortalClassifyPortalRedirect)
        RunTest("普通 302 判定为 AlreadyOnline", AddressOf Test_PortalClassifyNormalRedirect)
        RunTest("门户页缺参数判定为 MissingParameters", AddressOf Test_PortalClassifyMissingParameters)
        RunTest("GetOrigin 提取门户源地址", AddressOf Test_PortalGetOrigin)
        RunTest("发现结果: 门户劫持 302", AddressOf Test_PortalBuildResultPortal)
        RunTest("发现结果: 已联网普通 302", AddressOf Test_PortalBuildResultOnline)
        RunTest("发现结果: 边界状态码", AddressOf Test_PortalBuildResultEdge)
        RunTest("真实 pageInfo: 公钥与密码加密开关", AddressOf Test_PortalPageInfoRealPublicKey)
        RunTest("真实 pageInfo: 服务表解析", AddressOf Test_PortalPageInfoRealServices)
        RunTest("真实 pageInfo: 验证码状态默认为关闭", AddressOf Test_PortalPageInfoValidCode)
        RunTest("真实 getServices: 五个运营商与默认项", AddressOf Test_PortalServicesReal)
        RunTest("脏数据: 要求加密但缺公钥应报错", AddressOf Test_PortalDirtyMissingPublicKey)
        RunTest("脏数据: 非法 modulus/exponent 被明确拒绝", AddressOf Test_PortalDirtyBadPublicKey)
        RunTest("脏数据: 非法 JSON 应报错", AddressOf Test_PortalDirtyBadJson)
        RunTest("脏数据: 空服务列表应报错", AddressOf Test_PortalDirtyEmptyServices)
        RunTest("脏数据: 未知 service 名返回 Nothing", AddressOf Test_PortalDirtyUnknownService)
        RunTest("passwordEncrypt=false 时不强制公钥", AddressOf Test_PortalNoEncryptAllowsEmptyKey)
    End Sub

#End Region

End Module