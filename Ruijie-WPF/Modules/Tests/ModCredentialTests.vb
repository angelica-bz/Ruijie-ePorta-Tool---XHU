Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModCredentialTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModCredentialTests

#Region "ModCredential 测试用例"

    Private Sub Test_CredRoundTrip()
        Dim ProtectedText As String = ModCredential.ProtectPassword("test1234")
        AssertTrue(ProtectedText.Length > 0, "保护结果不应为空")
        AssertFalse(ProtectedText.Contains("test1234"), "保护结果中不得出现明文")

        Dim Plain As String = ""
        AssertTrue(ModCredential.TryUnprotectPassword(ProtectedText, Plain), "应当能解开")
        AssertEqual("test1234", Plain, "往返后必须与原文一致")
    End Sub

    Private Sub Test_CredEmptyPassword()
        ' 空密码 → 空保护串（表示“没有保存密码”），而不是一段无意义的 DPAPI 块
        AssertEqual("", ModCredential.ProtectPassword(""), "空密码应得到空保护串")

        Dim Plain As String = "未初始化"
        AssertTrue(ModCredential.TryUnprotectPassword("", Plain), "空保护串应视为正常状态")
        AssertEqual("", Plain, "空保护串对应空密码")

        Dim NothingPlain As String = "未初始化"
        AssertTrue(ModCredential.TryUnprotectPassword(Nothing, NothingPlain), "Nothing 同样应视为正常状态")
        AssertEqual("", NothingPlain, "Nothing 对应空密码")
    End Sub

    Private Sub Test_CredUnicode()
        Dim Original As String = "Test中abc"
        Dim ProtectedText As String = ModCredential.ProtectPassword(Original)
        Dim Plain As String = ""
        AssertTrue(ModCredential.TryUnprotectPassword(ProtectedText, Plain), "Unicode 密码应当能解开")
        AssertEqual(Original, Plain, "Unicode 往返必须一致")
    End Sub

    Private Sub Test_CredInvalidData()
        Dim Plain As String = "残留值"

        AssertFalse(ModCredential.TryUnprotectPassword("这不是合法 Base64 !!!", Plain),
                    "非法 Base64 应返回 False")
        AssertEqual("", Plain, "失败时明文应被清空")

        ' 合法 Base64 但不是本程序/本用户产出的 DPAPI 数据
        Dim RandomData As String = Convert.ToBase64String(New Byte() {1, 2, 3, 4, 5, 6, 7, 8})
        AssertFalse(ModCredential.TryUnprotectPassword(RandomData, Plain),
                    "随机数据应返回 False")
        AssertEqual("", Plain, "失败时明文应被清空")

        ' 短 Base64（DPAPI 块不可能这么短）
        AssertFalse(ModCredential.TryUnprotectPassword("AAAA", Plain), "过短的 Base64 应返回 False")
    End Sub

    Private Sub Test_CredCurrentUser()
        ' 当前用户加密 → 当前用户解密，必须成功
        Dim ProtectedText As String = ModCredential.ProtectPassword("current-user-check")
        Dim Plain As String = ""
        AssertTrue(ModCredential.TryUnprotectPassword(ProtectedText, Plain), "当前用户应能解开自己的密文")
        AssertEqual("current-user-check", Plain, "内容一致")

        ' 两次保护同一密码，DPAPI 结果不同（含随机盐），但都能解开
        Dim Second As String = ModCredential.ProtectPassword("current-user-check")
        Dim Plain2 As String = ""
        AssertTrue(ModCredential.TryUnprotectPassword(Second, Plain2), "第二次保护同样应能解开")
        AssertEqual("current-user-check", Plain2, "内容一致")

        ' 格式识别：必须校验 DPAPI blob 文件头，不能只看“是不是合法 Base64”
        ' （"test1234" 本身就是合法 Base64，属于反例）
        AssertTrue(ModCredential.LooksProtected(ProtectedText), "保护结果应被识别为保护数据")
        AssertFalse(ModCredential.LooksProtected("test1234"), "明文 test1234 恰好是合法 Base64，但不应被认作保护数据")
        AssertFalse(ModCredential.LooksProtected("cGFzc3dvcmQ="), "普通 Base64 文本不应被认作保护数据")
        AssertFalse(ModCredential.LooksProtected(""), "空串不应被识别为保护数据")
    End Sub


#End Region

#Region "验收前 DPAPI 自检用例"

    ''' <summary>
    ''' 验收前的 DPAPI 自检：用固定探针口令走一遍 Protect → Unprotect，
    ''' 确认「当前用户 + 当前机器」的 DPAPI 链路本身是通的。
    ''' 这一步与真实密码无关，因此可以安全地写死在测试里。
    ''' </summary>
    Private Sub Test_CredE2eProbe()
        Const Probe As String = "E2E-Test-Only-123"

        Dim ProtectedText As String = ModCredential.ProtectPassword(Probe)
        AssertTrue(ProtectedText.Length > 0, "Protect 应产出密文")
        AssertFalse(ProtectedText.Contains(Probe), "密文里不得出现明文")
        AssertTrue(ModCredential.LooksProtected(ProtectedText), "产物应能被识别为本程序的保护格式")

        Dim Plain As String = ""
        AssertTrue(ModCredential.TryUnprotectPassword(ProtectedText, Plain), "Unprotect 应成功")
        AssertEqual(Probe, Plain, "Protect → Unprotect 必须还原出原文")

        ' 两次加密同一口令应得到不同密文（DPAPI 带随机盐），但都能解回原文
        Dim Again As String = ModCredential.ProtectPassword(Probe)
        AssertFalse(Again = ProtectedText, "DPAPI 每次加密应带随机量")
        Dim Plain2 As String = ""
        AssertTrue(ModCredential.TryUnprotectPassword(Again, Plain2), "第二份密文也应可解")
        AssertEqual(Probe, Plain2, "第二份密文也应还原出同一原文")
    End Sub

    ''' <summary>
    ''' 读取**当前真实配置**里的 password_protected，确认在这台机器上仍然可解密。
    ''' 只输出状态，不输出明文或密文；配置不存在时跳过（离线测试不应依赖用户环境）。
    ''' </summary>
    Private Sub Test_CredRealConfigDecryptable()
        Dim Raw As String = ""
        Try
            Raw = ModConfig.ReadStoredProtectedPassword()
        Catch
            Return
        End Try

        If String.IsNullOrEmpty(Raw) Then Return   ' 没有真实配置：跳过，不算失败

        AssertTrue(ModCredential.LooksProtected(Raw), "真实配置里的密文格式应可识别")

        Dim Plain As String = ""
        AssertTrue(ModCredential.TryUnprotectPassword(Raw, Plain),
                   "真实配置里的密码应当可以解密（同一 Windows 用户）")
        AssertTrue(Plain.Length > 0, "解出的明文不应为空")
        ' 绝不断言明文内容，也绝不在失败信息里带出明文
    End Sub


#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("DPAPI 往返: test1234", AddressOf Test_CredRoundTrip)
        RunTest("空密码行为明确", AddressOf Test_CredEmptyPassword)
        RunTest("Unicode 密码往返", AddressOf Test_CredUnicode)
        RunTest("非法保护数据返回 False", AddressOf Test_CredInvalidData)
        RunTest("密文绑定当前用户且格式可识别", AddressOf Test_CredCurrentUser)
        RunTest("DPAPI 往返: E2E-Test-Only-123", AddressOf Test_CredE2eProbe)
        RunTest("真实配置的密文当前可解密", AddressOf Test_CredRealConfigDecryptable)
    End Sub

#End Region

End Module