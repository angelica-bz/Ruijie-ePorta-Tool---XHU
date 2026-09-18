Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModFailureClassificationTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModFailureClassificationTests

#Region "失败分类测试用例（§七 离线矩阵）"

    ''' <summary>
    ''' ProbeFailed 表示「探测这条网络路径不通」，Unreachable 表示「认证门户 TCP 都连不上」。
    ''' 两者语义不同，说明文案也必须不同 —— 这正是「未认证后点连接却报认证服务器不可达」
    ''' 那次误判要防住的地方。
    ''' </summary>
    Private Sub Test_ClsProbeFailedNotUnreachable()
        Dim ProbeStatus = ModPortalDiscover.PortalDiscoveryStatus.ProbeFailed
        Dim PortalStatus = ModPortalDiscover.PortalDiscoveryStatus.Unreachable

        AssertFalse(ProbeStatus = PortalStatus, "两个状态不能是同一个枚举值")
        AssertFalse(CInt(ProbeStatus) = CInt(PortalStatus), "底层取值也必须不同")

        Dim ProbeMsg As String = DescribeFailure(
            AuthenticateWithDiscovery(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                      MakeStatusOnly(ProbeStatus, "模拟探测失败")))
        Dim PortalMsg As String = DescribeFailure(
            AuthenticateWithDiscovery(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                      MakeStatusOnly(PortalStatus, "模拟门户不可达")))

        AssertTrue(ProbeMsg.Length > 0 AndAlso PortalMsg.Length > 0, "两者都要有可读说明")
        AssertFalse(ProbeMsg = PortalMsg, "两者说明不能相同")
        AssertFalse(ProbeMsg.Contains("认证门户不可达"), "探测失败不得说成门户不可达")
        AssertFalse(ProbeMsg.Contains("认证服务器不可达"), "探测失败不得说成服务器不可达")
        AssertFalse(PortalMsg.Contains("网络探测失败"), "门户不可达不得说成探测失败")
    End Sub

    ''' <summary>八种失败状态都要有说明，且两两不同 —— 避免用户看到笼统的同一句话。</summary>
    Private Sub Test_ClsFailureMessagesDistinct()
        Dim Statuses = New ModPortalDiscover.PortalDiscoveryStatus() {
            ModPortalDiscover.PortalDiscoveryStatus.ProbeFailed,
            ModPortalDiscover.PortalDiscoveryStatus.Unreachable,
            ModPortalDiscover.PortalDiscoveryStatus.PortalRequestFailed,
            ModPortalDiscover.PortalDiscoveryStatus.Timeout,
            ModPortalDiscover.PortalDiscoveryStatus.NoRedirect,
            ModPortalDiscover.PortalDiscoveryStatus.MissingParameters,
            ModPortalDiscover.PortalDiscoveryStatus.InvalidLocation,
            ModPortalDiscover.PortalDiscoveryStatus.BadResponse}

        Dim Seen As New Dictionary(Of String, String)
        Dim Dump As New List(Of String)
        For Each Status In Statuses
            Dim Result = AuthenticateWithDiscovery(
                NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                MakeStatusOnly(Status, "标记-" & Status.ToString()))
            Dim Msg As String = DescribeFailure(Result)
            Dump.Add(Status.ToString() & " => " & Msg)

            AssertTrue(Msg.Length > 0, Status.ToString() & " 应有说明")
            AssertFalse(Msg = "认证成功。", Status.ToString() & " 不应被判成成功")
            If Seen.ContainsKey(Msg) Then
                Throw New Exception("断言失败: " & Status.ToString() & " 与 " & Seen(Msg) &
                                    " 的说明完全相同：" & Msg & vbCrLf & "  " & String.Join(vbCrLf & "  ", Dump))
            End If
            Seen(Msg) = Status.ToString()
        Next
        AssertEqual(Statuses.Length, Seen.Count, "八种状态应给出八句不同说明")
    End Sub

    ''' <summary>
    ''' 回归锁定：门户接口请求失败（HTTP 200 + 空正文）曾经被归成 Unreachable，
    ''' 于是界面显示「认证服务器不可达」。修正后应显示「认证参数获取失败」，
    ''' 并且必须带上底层原因，不能再说「不可达」。
    ''' </summary>
    Private Sub Test_ClsProbeFailureNotReportedUnreachable()
        Dim Result = AuthenticateWithDiscovery(
            NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
            MakeStatusOnly(ModPortalDiscover.PortalDiscoveryStatus.PortalRequestFailed,
                           "服务器返回了空响应"))
        Dim Msg As String = DescribeFailure(Result)

        AssertFalse(Result.Success, "接口失败应判定为失败")
        AssertFalse(Msg.Contains("不可达"), "接口失败不得再说「不可达」：" & Msg)
        AssertTrue(Msg.Contains("认证参数"), "应指向认证参数获取失败：" & Msg)
        AssertTrue(Msg.Contains("服务器返回了空响应"), "应保留底层原因便于排查：" & Msg)
    End Sub

    ''' <summary>
    ''' 核心回归：门户 InterFace.do 对没有 User-Agent 的请求返回「HTTP 200 + 空正文」，
    ''' 这是那次误判的真实根因。PostPortal 的请求头因此必须带 User-Agent。
    ''' </summary>
    Private Sub Test_ClsPortalHeadersCarryUserAgent()
        Dim Headers As Dictionary(Of String, String) = ModPortalDiscover.BuildPortalRequestHeaders()

        AssertTrue(Headers.ContainsKey("User-Agent"), "门户接口请求头必须包含 User-Agent")
        AssertTrue(Headers("User-Agent").Trim().Length > 0, "User-Agent 不能为空")
        AssertEqual(ModNetwork.DefaultUserAgent, Headers("User-Agent"), "应使用全局统一的 User-Agent")
        AssertTrue(Headers.ContainsKey("Content-Type"), "应声明表单编码")
        AssertTrue(Headers("Content-Type").Contains("x-www-form-urlencoded"), "应为表单编码")
    End Sub

    ''' <summary>
    ''' 三个真正发请求的入口（探测 GET 已内联同一常量 / 门户接口 / 登录接口）必须用同一个
    ''' User-Agent。历史上 ModPortalDiscover 是唯一漏掉的一处。
    ''' </summary>
    Private Sub Test_ClsAllEntryPointsCarryUserAgent()
        Dim Ua As String = ModNetwork.DefaultUserAgent
        AssertTrue(Ua.Trim().Length > 0, "全局 User-Agent 不能为空")

        Dim PortalHeaders As Dictionary(Of String, String) = ModPortalDiscover.BuildPortalRequestHeaders()
        AssertEqual(Ua, PortalHeaders("User-Agent"), "门户接口")

        Dim LoginHeaders As Dictionary(Of String, String) = BuildLoginHeaders(
            ModPortalDiscover.DefaultServer, "wlanuserip=1.2.3.4")
        AssertEqual(Ua, LoginHeaders("User-Agent"), "登录接口")

        Dim NetHeaders As Dictionary(Of String, String) = ModNetwork.BuildHeaders(GetDefaultConfig())
        AssertEqual(Ua, NetHeaders("User-Agent"), "通用请求头")
    End Sub

    ''' <summary>
    ''' 非 3xx 的探测响应（超时/ 5xx / 连不上等由上层判成 ProbeFailed）经 BuildRedirectResult
    ''' 处理时，结果只能是 NoRedirect 或 AlreadyOnline，绝不能是 Unreachable。
    ''' </summary>
    Private Sub Test_ClsNonRedirectNeverUnreachable()
        Dim UnreachableStatus = ModPortalDiscover.PortalDiscoveryStatus.Unreachable

        For Each Code In New Integer() {0, 204, 404, 500, 502, 503}
            Dim Result = ModPortalDiscover.BuildRedirectResult(Code, "")
            AssertFalse(Result.Status = UnreachableStatus,
                        "HTTP " & Code & " 不应被判成门户不可达")
            AssertFalse(Result.Status = ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication,
                        "HTTP " & Code & " 不应要求认证")
        Next

        ' 3xx 但缺 Location：属于响应异常，同样不是「不可达」
        Dim NoLocation = ModPortalDiscover.BuildRedirectResult(302, "")
        AssertEqual(ModPortalDiscover.PortalDiscoveryStatus.InvalidLocation, NoLocation.Status,
                    "302 缺 Location 应为 InvalidLocation")
    End Sub

    ''' <summary>
    ''' PortalRequestFailed 的语义是「服务器在，但认证参数拿不到」，
    ''' 与 ProbeFailed（探测路径不通）和 Unreachable（门户连不上）三足鼎立、互不相等。
    ''' </summary>
    Private Sub Test_ClsPortalRequestFailedSemantics()
        Dim Probe = ModPortalDiscover.PortalDiscoveryStatus.ProbeFailed
        Dim Portal = ModPortalDiscover.PortalDiscoveryStatus.Unreachable
        Dim Req = ModPortalDiscover.PortalDiscoveryStatus.PortalRequestFailed

        AssertFalse(Req = Probe, "接口失败 ≠ 探测失败")
        AssertFalse(Req = Portal, "接口失败 ≠ 门户不可达")

        Dim Values As New HashSet(Of Integer) From {CInt(Probe), CInt(Portal), CInt(Req)}
        AssertEqual(3, Values.Count, "三者底层取值应互不相同")

        Dim Msg As String = DescribeFailure(
            AuthenticateWithDiscovery(NewAccount("1234567890123", "test1234", PortalOperator.Telecom),
                                      MakeStatusOnly(Req, "接口异常")))
        AssertFalse(Msg.Contains("探测失败"), "接口失败不得说成探测失败：" & Msg)
        AssertFalse(Msg.Contains("门户不可达"), "接口失败不得说成门户不可达：" & Msg)
    End Sub

    ''' <summary>
    ''' 只是切换「自动重连」开关，不能动到学号 / 密码 / 网络类型。
    ''' </summary>
    Private Sub Test_ClsReconnectToggleKeepsAccount()
        Dim Path_ As String = CfgTempFile("cls-reconnect.yml")
        Try
            Dim Cfg As AppConfig = MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom)
            Cfg.Function.AutoReconnect = False
            Cfg.Function.ReconnectInterval = 1
            SaveAppConfigTo(Path_, Cfg)

            Dim Before As AppConfig = LoadAppConfigFrom(Path_)
            Dim CipherBefore As String = Before.User.PasswordProtectedRaw
            AssertTrue(CipherBefore.Length > 0, "首次保存应写入密文")
            AssertEqual("test1234", Before.User.Password, "首次保存后应可解密")

            ' 只改开关，密码框留空 → 密码必须原样保留
            Dim Reloaded As AppConfig = LoadAppConfigFrom(Path_)
            Reloaded.Function.AutoReconnect = True
            Reloaded.Function.ReconnectInterval = 30
            SaveAppConfigTo(Path_, Reloaded)

            Dim After As AppConfig = LoadAppConfigFrom(Path_)
            AssertEqual("1234567890123", After.User.UserId, "学号不应变")
            AssertEqual(PortalOperator.Telecom, After.User.Operator, "网络类型不应变")
            AssertEqual(CipherBefore, After.User.PasswordProtectedRaw, "只改开关时密文应逐字节不变")
            AssertTrue(After.Function.AutoReconnect, "开关应已生效")
            AssertEqual(30, After.Function.ReconnectInterval, "间隔应已生效")

            ' 密码仍然可用，且认证上下文可正常构造
            AssertFalse(After.PasswordNeedsReentry, "密码应仍可解密")
            AssertEqual("test1234", After.User.Password, "密码明文应一致")
            Dim Context As RuntimeAuthContext = BuildRuntimeAuthContext(After)
            AssertTrue(Context.IsValid, "上下文应有效：" & Context.ErrorMessage)
            AssertEqual("test1234", Context.Account.Password, "账号应拿到明文密码")
            AssertEqual("1234567890123", Context.Account.UserId, "账号学号应一致")
        Finally
            If IO.File.Exists(Path_) Then IO.File.Delete(Path_)
        End Try
    End Sub


#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("ProbeFailed 与 Unreachable 是两种不同错误", AddressOf Test_ClsProbeFailedNotUnreachable)
        RunTest("八种失败状态各有互不相同的说明", AddressOf Test_ClsFailureMessagesDistinct)
        RunTest("探测失败不会被说成认证服务器不可达", AddressOf Test_ClsProbeFailureNotReportedUnreachable)
        RunTest("门户接口请求头必须带 User-Agent", AddressOf Test_ClsPortalHeadersCarryUserAgent)
        RunTest("三个请求入口使用同一个 User-Agent", AddressOf Test_ClsAllEntryPointsCarryUserAgent)
        RunTest("非 3xx 探测响应不会被判成门户不可达", AddressOf Test_ClsNonRedirectNeverUnreachable)
        RunTest("门户接口失败时按 TCP 可达性二分", AddressOf Test_ClsPortalRequestFailedSemantics)
        RunTest("改自动重连开关不影响账号密码", AddressOf Test_ClsReconnectToggleKeepsAccount)
    End Sub

#End Region

End Module