Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModAuthTraceTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModAuthTraceTests

#Region "验收仪表测试用例"

    ''' <summary>
    ''' 自动重连「确实重新走了动态链路」的判据必须是四项齐全：
    ''' Reconnect / Discovery / pageInfo / 密码加密 / login 缺一不可。
    ''' 只看到「重连成功」是不够的 —— 复用旧参数同样会成功。
    ''' </summary>
    Private Sub Test_TrcReconnectRequiresFourStages()
        ModAuthTrace.Reset()
        AssertEqual(0, ModAuthTrace.ReconnectCount, "初始 ReconnectCount")
        AssertEqual(0, ModAuthTrace.DiscoveryCount, "初始 DiscoveryCount")
        AssertEqual(0, ModAuthTrace.PageInfoCount, "初始 PageInfoCount")
        AssertEqual(0, ModAuthTrace.PasswordEncryptionCount, "初始 PasswordEncryptionCount")
        AssertEqual(0, ModAuthTrace.LoginCount, "初始 LoginCount")
        AssertFalse(ModAuthTrace.ReconnectUsedDynamicDiscovery, "全零时不得判为走了动态链路")

        ModAuthTrace.CountReconnect()
        AssertFalse(ModAuthTrace.ReconnectUsedDynamicDiscovery, "只有 Reconnect 不算")

        ModAuthTrace.CountDiscovery()
        AssertFalse(ModAuthTrace.ReconnectUsedDynamicDiscovery, "缺 pageInfo 不算")

        ModAuthTrace.CountPageInfo()
        AssertFalse(ModAuthTrace.ReconnectUsedDynamicDiscovery, "缺密码加密不算")

        ModAuthTrace.CountPasswordEncryption()
        AssertFalse(ModAuthTrace.ReconnectUsedDynamicDiscovery, "缺 login 不算")

        ModAuthTrace.CountLogin()
        AssertTrue(ModAuthTrace.ReconnectUsedDynamicDiscovery, "五项齐全才算真的重跑动态链路")

        AssertEqual(1, ModAuthTrace.ReconnectCount, "ReconnectCount 应为 1")
        AssertEqual(1, ModAuthTrace.LoginCount, "LoginCount 应为 1")
        ModAuthTrace.Reset()
    End Sub

    ''' <summary>ResetCounters 划统计窗口，但必须保留动态参数观测，否则无法比较「变没变」。</summary>
    Private Sub Test_TrcResetCountersKeepsObservations()
        ModAuthTrace.Reset()
        ModAuthTrace.CountDiscovery()
        ModAuthTrace.CountLogin()
        ModAuthTrace.ObserveQueryString("wlanuserip=10.1.1.1&nasip=1.1.1.1")
        AssertEqual(1, ModAuthTrace.WlanUserIpObservations, "应记录一次观测")

        ModAuthTrace.ResetCounters()
        AssertEqual(0, ModAuthTrace.DiscoveryCount, "计数应清零")
        AssertEqual(0, ModAuthTrace.LoginCount, "计数应清零")
        AssertEqual(1, ModAuthTrace.WlanUserIpObservations, "观测必须保留")

        ModAuthTrace.Reset()
        AssertEqual(0, ModAuthTrace.WlanUserIpObservations, "Reset 才清观测")
    End Sub

    ''' <summary>同一个 IP 重复观测不算变化；换了 IP 才算。</summary>
    Private Sub Test_TrcQueryStringChangeDetection()
        ModAuthTrace.Reset()

        ModAuthTrace.ObserveQueryString("wlanuserip=10.88.12.34&nasip=192.0.2.10")
        AssertFalse(ModAuthTrace.WlanUserIpChanged, "首次观测不算变化")

        ModAuthTrace.ObserveQueryString("wlanuserip=10.88.12.34&nasip=192.0.2.10")
        AssertFalse(ModAuthTrace.WlanUserIpChanged, "同一个 IP 不算变化")
        AssertEqual(2, ModAuthTrace.WlanUserIpObservations, "应有两次观测")

        ModAuthTrace.ObserveQueryString("wlanuserip=10.88.12.99&nasip=192.0.2.10")
        AssertTrue(ModAuthTrace.WlanUserIpChanged, "换了 IP 应判定为变化")

        ' 缺 wlanuserip 的 queryString 不参与统计
        Dim Before As Integer = ModAuthTrace.WlanUserIpObservations
        ModAuthTrace.ObserveQueryString("nasip=1.1.1.1&url=x")
        AssertEqual(Before, ModAuthTrace.WlanUserIpObservations, "缺 wlanuserip 时不应计数")

        ' 空值安全
        ModAuthTrace.ObserveQueryString("")
        ModAuthTrace.ObserveQueryString(Nothing)
        AssertEqual(Before, ModAuthTrace.WlanUserIpObservations, "空输入不应计数")

        ModAuthTrace.Reset()
    End Sub

    Private Sub Test_TrcPublicKeyChangeDetection()
        ModAuthTrace.Reset()

        ModAuthTrace.ObservePublicKey("AB" & New String("0"c, 254), "10001")
        AssertFalse(ModAuthTrace.PublicKeyChanged, "首次观测不算变化")

        ModAuthTrace.ObservePublicKey("ab" & New String("0"c, 254), "10001")
        AssertFalse(ModAuthTrace.PublicKeyChanged, "大小写不同不应算变化（已归一化）")

        ModAuthTrace.ObservePublicKey("ff" & New String("0"c, 254), "10001")
        AssertTrue(ModAuthTrace.PublicKeyChanged, "换公钥应判定为变化")

        Dim Before As Integer = ModAuthTrace.PublicKeyObservations
        ModAuthTrace.ObservePublicKey("", "10001")
        AssertEqual(Before, ModAuthTrace.PublicKeyObservations, "空 modulus 不应计数")

        ModAuthTrace.Reset()
    End Sub

    ''' <summary>仪表只留状态码与长度，不留正文 —— 正文里可能有会话信息。</summary>
    Private Sub Test_TrcHttpObservation()
        ModAuthTrace.Reset()
        AssertEqual(0, ModAuthTrace.LastHttpStatus, "初始状态码应为 0")

        ModAuthTrace.ObserveHttpResponse(200, 22634, "/eportal/InterFace.do?method=pageInfo")
        AssertEqual(200, ModAuthTrace.LastHttpStatus, "状态码应被记录")
        AssertEqual(22634, ModAuthTrace.LastBodyLength, "正文长度应被记录")
        AssertTrue(ModAuthTrace.LastMethod.Contains("pageInfo"), "应记录是哪个接口")

        ' 空正文必须能如实反映出来 —— 这正是那次「服务器返回了空响应」的特征
        ModAuthTrace.ObserveHttpResponse(200, 0, "/eportal/InterFace.do?method=pageInfo")
        AssertEqual(200, ModAuthTrace.LastHttpStatus, "空正文时状态码仍是 200")
        AssertEqual(0, ModAuthTrace.LastBodyLength, "空正文长度必须是 0")

        ModAuthTrace.Reset()
        AssertEqual(0, ModAuthTrace.LastHttpStatus, "Reset 应清空")
        AssertEqual(0, ModAuthTrace.LastBodyLength, "Reset 应清空")
    End Sub

    ''' <summary>§十六 要求阶段结论只有 PASS / FAIL / SKIP / BLOCKED，语义不能含糊。</summary>
    Private Sub Test_TrcOutcomeVocabulary()
        Dim Values As String() = {ModAcceptTests.OutcomePass, ModAcceptTests.OutcomeFail,
                                  ModAcceptTests.OutcomeSkip, ModAcceptTests.OutcomeBlocked}
        Dim Seen As New HashSet(Of String)
        For Each Value In Values
            AssertTrue(Not String.IsNullOrEmpty(Value), "结论取值不能为空")
            AssertTrue(Seen.Add(Value), "结论取值不能重复：" & Value)
        Next
        AssertEqual(4, Seen.Count, "应恰好四种结论")

        ' §二十 要求的固定阶段顺序必须完整
        AssertEqual(13, ModAcceptTests.StageOrderForTest.Length, "应有 13 个阶段")
        AssertEqual("Configuration", ModAcceptTests.StageOrderForTest(0), "首个阶段")
        AssertEqual("Auto Reconnect", ModAcceptTests.StageOrderForTest(12), "末个阶段")
    End Sub


#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("计数初值为零且四项齐全才判定动态链路", AddressOf Test_TrcReconnectRequiresFourStages)
        RunTest("ResetCounters 只清计数不清观测", AddressOf Test_TrcResetCountersKeepsObservations)
        RunTest("wlanuserip 变化检测", AddressOf Test_TrcQueryStringChangeDetection)
        RunTest("公钥变化检测", AddressOf Test_TrcPublicKeyChangeDetection)
        RunTest("HTTP 响应只留状态码与长度", AddressOf Test_TrcHttpObservation)
        RunTest("阶段结论四种取值互不相同", AddressOf Test_TrcOutcomeVocabulary)
    End Sub

#End Region

End Module