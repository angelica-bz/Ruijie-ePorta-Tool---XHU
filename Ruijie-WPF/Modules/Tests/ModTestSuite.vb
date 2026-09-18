''' <summary>
''' 离线测试总入口：--test 调用的就是这里。
'''
''' 只负责「按顺序把各模块的用例跑一遍」，不含任何用例本身。
''' 新增一个测试模块时，在下面加一行 Section 即可。
'''
''' 退出码由 App.xaml.vb 依据 <see cref="ModTestFramework.HasFailures"/> 决定：
'''   全部通过 → 0；有任一失败 → 1。
''' </summary>
Public Module ModTestSuite

    ''' <summary>执行全部离线测试并打印结果。不访问网络。</summary>
    Public Sub RunAllTests()
        ModTestFramework.ResetStatistics()

        ModTestFramework.Section("NetworkMonitor", AddressOf ModNetworkMonitorTests.RegisterAll, First:=True)
        ModTestFramework.Section("ModCrypto", AddressOf ModCryptoTests.RegisterAll)
        ModTestFramework.Section("ModPortalDiscover", AddressOf ModPortalDiscoverTests.RegisterAll)
        ModTestFramework.Section("ModAuth", AddressOf ModAuthTests.RegisterAll)
        ModTestFramework.Section("ModAuthentication", AddressOf ModAuthenticationTests.RegisterAll)
        ModTestFramework.Section("ModCredential", AddressOf ModCredentialTests.RegisterAll)
        ModTestFramework.Section("ModConfig", AddressOf ModConfigTests.RegisterAll)
        ModTestFramework.Section("RuntimeAuthentication", AddressOf ModRuntimeAuthenticationTests.RegisterAll)
        ModTestFramework.Section("PageConfig", AddressOf ModPageConfigTests.RegisterAll)
        ModTestFramework.Section("失败分类", AddressOf ModFailureClassificationTests.RegisterAll)
        ModTestFramework.Section("验收仪表", AddressOf ModAuthTraceTests.RegisterAll)
        ModTestFramework.Section("出口地址解析", AddressOf ModBindAddressTests.RegisterAll)

        ModTestFramework.WriteStatistics()
    End Sub

    Public ReadOnly Property TotalTests As Integer
        Get
            Return ModTestFramework.TotalTests
        End Get
    End Property

    Public ReadOnly Property PassedTests As Integer
        Get
            Return ModTestFramework.PassedTests
        End Get
    End Property

    Public ReadOnly Property FailedTests As Integer
        Get
            Return ModTestFramework.FailedTests
        End Get
    End Property

    ''' <summary>是否存在失败；--test 据此返回退出码。</summary>
    Public ReadOnly Property HasFailures As Boolean
        Get
            Return ModTestFramework.HasFailures
        End Get
    End Property

End Module
