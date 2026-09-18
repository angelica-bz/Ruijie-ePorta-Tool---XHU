Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModBindAddressTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModBindAddressTests

#Region "出口地址解析测试用例（不依赖测试机真实 IP）"

    ''' <summary>造一个候选，便于测试。Excluded 由 ExplainExclusionFor 现算，保证与生产同一套规则。</summary>
    Private Function MakeCandidate(Name As String, Type As String, Address As String) As ModNetwork.BindCandidate
        Return New ModNetwork.BindCandidate With {
            .Address = Address,
            .InterfaceName = Name,
            .InterfaceType = Type,
            .ExcludedReason = ModNetwork.ExplainExclusionFor(Name, Name, Type, Address)
        }
    End Function

    ''' <summary>校园网有线网卡必须被判为可用 —— 这正是 GUI 连接失败的根因所在。</summary>
    Private Sub Test_BindCampusEthernet()
        Dim C = MakeCandidate("以太网", "Ethernet", "10.88.12.34")
        AssertTrue(C.Usable, "校园网有线网卡应可用，实际被排除：" & C.ExcludedReason)

        Dim C2 = MakeCandidate("Ethernet", "Ethernet", "172.16.5.20")
        AssertTrue(C2.Usable, "其它校园内网段也应可用：" & C2.ExcludedReason)
    End Sub

    Private Sub Test_BindHomeAndWifi()
        AssertTrue(MakeCandidate("以太网", "Ethernet", "192.168.1.100").Usable, "家庭网有线应可用")
        AssertTrue(MakeCandidate("WLAN", "Wireless80211", "192.168.1.50").Usable, "无线网卡应可用")
        AssertTrue(MakeCandidate("WLAN 2", "Wireless80211", "10.88.12.200").Usable, "校园无线应可用")
    End Sub

    ''' <summary>
    ''' 会抢默认路由的虚拟网卡必须被排除 —— Clash TUN 就是这个原因让探测在未认证时失败。
    ''' </summary>
    Private Sub Test_BindVirtualExcluded()
        ' Clash Verge 的 TUN：198.18.0.0/15 是 RFC 2544 保留段
        Dim Tun = MakeCandidate("Meta", "Tunnel", "198.18.0.1")
        AssertFalse(Tun.Usable, "Clash TUN 必须被排除")
        AssertTrue(Tun.ExcludedReason.Length > 0, "应给出排除原因")

        ' 按名字识别的 VPN
        For Each Name In New String() {"Radmin VPN", "Tailscale", "WireGuard Tunnel",
                                       "OpenVPN TAP-Windows6", "ZeroTier One", "Wintun Userspace Tunnel"}
            Dim C = MakeCandidate(Name, "Ethernet", "10.9.9.9")
            AssertFalse(C.Usable, Name & " 应被排除")
        Next

        ' Tunnel 类型即使地址看着正常也应排除
        AssertFalse(MakeCandidate("SomeTunnel", "Tunnel", "10.1.1.1").Usable, "Tunnel 类型应被排除")
    End Sub

    Private Sub Test_BindLoopbackApipa()
        AssertFalse(MakeCandidate("Loopback Pseudo-Interface 1", "Loopback", "127.0.0.1").Usable,
                    "Loopback 应被排除")
        AssertFalse(MakeCandidate("以太网 4", "Ethernet", "169.254.1.1").Usable,
                    "APIPA 地址应被排除（没拿到 DHCP 的网卡不能用）")
        AssertFalse(MakeCandidate("任意", "Ethernet", "127.0.0.5").Usable, "127/8 整段应被排除")
    End Sub

    ''' <summary>排序必须稳定：可用的排在前面，同为可用时有线优先。</summary>
    Private Sub Test_BindRanking()
        Dim List As New List(Of ModNetwork.BindCandidate) From {
            MakeCandidate("Meta", "Tunnel", "198.18.0.1"),
            MakeCandidate("WLAN", "Wireless80211", "192.168.1.50"),
            MakeCandidate("Radmin VPN", "Ethernet", "26.1.2.3"),
            MakeCandidate("以太网", "Ethernet", "10.88.12.34"),
            MakeCandidate("Loopback Pseudo-Interface 1", "Loopback", "127.0.0.1")
        }

        Dim Ranked = ModNetwork.RankCandidates(List)
        AssertEqual(5, Ranked.Count, "候选数量不应变化")
        AssertTrue(Ranked(0).Usable, "第一个必须是可用候选")
        AssertEqual("10.88.12.34", Ranked(0).Address, "有线可用地址应排第一")
        AssertEqual("192.168.1.50", Ranked(1).Address, "无线可用地址排第二")
        AssertFalse(Ranked(2).Usable, "从第三个起都是被排除的")

        ' 排序必须稳定（同输入同输出）
        Dim Again = ModNetwork.RankCandidates(List)
        For I As Integer = 0 To Ranked.Count - 1
            AssertEqual(Ranked(I).Address, Again(I).Address, "第 " & I & " 位排序应稳定")
        Next

        Dim Best = ModNetwork.PickBestCandidate(List)
        AssertEqual("10.88.12.34", Best.Address, "PickBestCandidate 应选出有线地址")
    End Sub

    ''' <summary>全是虚拟网卡时不能返回空 —— 退回一个非保留地址总比完全不绑强。</summary>
    Private Sub Test_BindFallback()
        Dim List As New List(Of ModNetwork.BindCandidate) From {
            MakeCandidate("Meta", "Tunnel", "198.18.0.1"),
            MakeCandidate("Radmin VPN", "Ethernet", "26.1.2.3")
        }
        AssertEqual(0, ModNetwork.RankCandidates(List).Where(Function(C) C.Usable).Count(),
                    "这份输入里不应有可用候选")

        Dim Info = ModNetwork.ResolveBindAddressInfo("", List)
        AssertEqual("Fallback", Info.Source, "应走 Fallback")
        AssertTrue(Info.Address.Length > 0, "Fallback 也必须给出一个地址")
        AssertFalse(Info.Address.StartsWith("127."), "Fallback 不能是 loopback")
    End Sub

    Private Sub Test_BindNone()
        Dim List As New List(Of ModNetwork.BindCandidate) From {
            MakeCandidate("Loopback Pseudo-Interface 1", "Loopback", "127.0.0.1")
        }
        Dim Info = ModNetwork.ResolveBindAddressInfo("", List)
        AssertEqual("None", Info.Source, "没有任何可用地址时应为 None")
        AssertEqual("", Info.Address, "不应给出地址")
        AssertTrue(Info.Note.Length > 0, "应说明原因")

        ' 空列表同样安全
        Dim Empty = ModNetwork.ResolveBindAddressInfo("", New List(Of ModNetwork.BindCandidate)())
        AssertEqual("None", Empty.Source, "空候选应为 None")
        AssertEqual(0, Empty.Candidates, "候选数应为 0")
    End Sub

    ''' <summary>
    ''' §十五 回归锁定：GUI「连接」与 --accept 必须用**同一套**出口解析。
    ''' 以前 GUI 传的是空 BindAddress，于是请求走默认路由被 TUN 掐断，
    ''' 界面报「网络探测失败」；--accept 传了 --bind 所以能成功。
    ''' </summary>
    Private Sub Test_BindGuiAndAcceptShareResolution()
        Dim Resolved As String = ModNetwork.ResolveBindAddress(ModConfig.SchoolServer)

        ' GUI 路径：不带任何参数构造上下文（PageStatus.BtnConnect_Click 就是这么调的）
        Dim GuiContext As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom))

        ' --accept 路径：也是同一个构造器，只是显式传了地址
        Dim AcceptContext As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom), "", Resolved)

        If Resolved.Length > 0 Then
            AssertEqual(Resolved, GuiContext.BindAddress,
                        "GUI 上下文应自动解析出与 --accept 相同的出口地址")
            AssertEqual(Resolved, AcceptContext.BindAddress, "--accept 显式传入的地址应被原样保留")
            AssertEqual(GuiContext.BindAddress, AcceptContext.BindAddress,
                        "GUI 与 --accept 必须落在同一个 BindAddress 上")
        End If

        ' 显式传入时绝不能被自动解析覆盖
        Dim Forced As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom), "", "10.99.99.99")
        AssertEqual("10.99.99.99", Forced.BindAddress, "显式指定的出口不得被覆盖")
    End Sub

    ''' <summary>认证上下文在未指定出口时必须自己解析出非空地址 —— 这是本次修复的核心断言。</summary>
    Private Sub Test_BindContextAutoResolves()
        Dim Context As RuntimeAuthContext = BuildRuntimeAuthContext(
            MakeAppConfig("1234567890123", "test1234", PortalOperator.Telecom))
        AssertTrue(Context.IsValid, "上下文应有效：" & Context.ErrorMessage)

        Dim Info = ModNetwork.ResolveBindAddressInfo(ModConfig.SchoolServer)
        If Info.Source = "None" Then
            ' 测试机确实没有可用出口：此时为空是诚实的结果，不算失败
            AssertEqual("", Context.BindAddress, "无可用出口时应为空")
        Else
            AssertEqual(Info.Address, Context.BindAddress, "应解析出解析器给出的地址")
            AssertTrue(Context.BindAddress.Length > 0, "有可用出口时 BindAddress 不能为空")
        End If
    End Sub


#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("校园网有线网卡可用", AddressOf Test_BindCampusEthernet)
        RunTest("普通家庭网 / 无线网卡可用", AddressOf Test_BindHomeAndWifi)
        RunTest("TUN / VPN / 虚拟网卡被排除", AddressOf Test_BindVirtualExcluded)
        RunTest("Loopback 与 APIPA 被排除", AddressOf Test_BindLoopbackApipa)
        RunTest("多候选排序：可用优先、有线优先", AddressOf Test_BindRanking)
        RunTest("全部不可用时退回非保留地址", AddressOf Test_BindFallback)
        RunTest("没有任何地址时明确报无可用出口", AddressOf Test_BindNone)
        RunTest("GUI 与 --accept 共用同一套解析", AddressOf Test_BindGuiAndAcceptShareResolution)
        RunTest("未指定出口时认证上下文自动解析出地址", AddressOf Test_BindContextAutoResolves)
    End Sub

#End Region

End Module