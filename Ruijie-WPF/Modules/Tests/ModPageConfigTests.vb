Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModPageConfigTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModPageConfigTests

#Region "PageConfig 测试用例"

    Private Sub Test_PcLoadExistingPassword()
        Dim Path_ As String = WritePcConfig("pc-load.yml", "1234567890123", "OldTest123", PortalOperator.Telecom)

        Dim Cfg As AppConfig = LoadAppConfigFrom(Path_)
        Dim State As ModAccountForm.FormState = ModAccountForm.DescribeState(Cfg, Path_)
        Dim Form As ModAccountForm.AccountForm = ModAccountForm.BuildForm(Cfg)

        AssertEqual("1234567890123", Form.UserId, "学号应加载出来")
        AssertEqual(PortalOperator.Telecom, Form.[Operator], "运营商应加载出来")
        AssertEqual("", Form.NewPassword, "密码框初值必须为空（不回填明文）")
        AssertFalse(Cfg.PasswordNeedsReentry, "已保存且可解密的密码不应要求重新输入")
        AssertEqual(ModAccountForm.PasswordUiState.Saved, State.PasswordState, "密码状态应为已保存")
        AssertEqual("已保存密码，留空保持不变", State.PasswordHint, "应提示留空保持不变")
        AssertTrue(State.CanClearPassword, "已有密码时应能清除")
        AssertTrue(State.StatusReady, "配置完整时状态应为可认证")
        AssertEqual("配置完整，可以认证", State.StatusText, "状态文案")
    End Sub

    Private Sub Test_PcKeepPasswordWhenBlank()
        ' 本阶段关键用例：留空保存绝不能把已有密码丢掉
        Dim Path_ As String = WritePcConfig("pc-keep.yml", "1234567890123", "OldTest123", PortalOperator.Telecom)
        Dim BeforeBlob As String = ModConfig.ReadStoredProtectedPassword(Path_)
        AssertTrue(BeforeBlob.Length > 0, "前置条件：磁盘上已有密文")

        Dim Outcome As ModAccountForm.SaveOutcome =
            ModAccountForm.ApplyAndSave(BlankPasswordForm("1234567890123", PortalOperator.Telecom), Path_)
        AssertTrue(Outcome.Success, "留空保存应成功：" & Outcome.ValidationError)

        ' 密文逐字符不变（没有做无谓的重新加密）
        AssertEqual(BeforeBlob, ModConfig.ReadStoredProtectedPassword(Path_), "password_protected 必须保持原值")

        Dim Loaded As AppConfig = LoadAppConfigFrom(Path_)
        AssertEqual("OldTest123", Loaded.User.Password, "明文密码应保持不变")
        AssertFalse(Loaded.PasswordNeedsReentry, "密码应仍然可用")
    End Sub

    Private Sub Test_PcSetNewPassword()
        Dim Path_ As String = WritePcConfig("pc-newpw.yml", "1234567890123", "OldTest123", PortalOperator.Telecom)
        Dim BeforeBlob As String = ModConfig.ReadStoredProtectedPassword(Path_)

        Dim Form As ModAccountForm.AccountForm = BlankPasswordForm("1234567890123", PortalOperator.Telecom)
        Form.NewPassword = "NewTest123"
        Dim Outcome As ModAccountForm.SaveOutcome = ModAccountForm.ApplyAndSave(Form, Path_)
        AssertTrue(Outcome.Success, Outcome.ValidationError)

        Dim AfterBlob As String = ModConfig.ReadStoredProtectedPassword(Path_)
        AssertTrue(AfterBlob.Length > 0, "应写入新的密文")
        AssertFalse(AfterBlob = BeforeBlob, "新密码应产生不同的密文")
        AssertFalse(AfterBlob.Contains("NewTest123"), "密文里不得出现明文")

        Dim Loaded As AppConfig = LoadAppConfigFrom(Path_)
        AssertEqual("NewTest123", Loaded.User.Password, "解出来必须是新密码")
    End Sub

    Private Sub Test_PcRequirePasswordWhenNone()
        Dim Path_ As String = CfgTempFile("pc-nopw.yml")
        SaveAppConfigTo(Path_, MakeAppConfig("1234567890123", "", PortalOperator.Telecom))
        AssertEqual("", ModConfig.ReadStoredProtectedPassword(Path_), "前置条件：没有保存过密码")

        Dim Cfg As AppConfig = LoadAppConfigFrom(Path_)
        Dim State As ModAccountForm.FormState = ModAccountForm.DescribeState(Cfg, Path_)
        AssertEqual(ModAccountForm.PasswordUiState.NotSaved, State.PasswordState, "应为未保存状态")
        AssertFalse(State.CanClearPassword, "没有密码时不应显示清除入口")

        Dim Outcome As ModAccountForm.SaveOutcome =
            ModAccountForm.ApplyAndSave(BlankPasswordForm("1234567890123", PortalOperator.Telecom), Path_)
        AssertFalse(Outcome.Success, "没有密码且留空时不应保存成功")
        AssertEqual("请输入密码", Outcome.ValidationError, "应提示请输入密码")
    End Sub

    Private Sub Test_PcChangeUserIdKeepsPassword()
        Dim Path_ As String = WritePcConfig("pc-uid.yml", "1234567890123", "OldTest123", PortalOperator.Telecom)

        Dim Outcome As ModAccountForm.SaveOutcome =
            ModAccountForm.ApplyAndSave(BlankPasswordForm("001234567890", PortalOperator.Telecom), Path_)
        AssertTrue(Outcome.Success, Outcome.ValidationError)

        Dim Loaded As AppConfig = LoadAppConfigFrom(Path_)
        AssertEqual("001234567890", Loaded.User.UserId, "学号应立即生效（含前导零）")
        AssertEqual("OldTest123", Loaded.User.Password, "只改学号不应影响密码")
        AssertFalse(Loaded.PasswordNeedsReentry, "密码仍应可用")

        ' 17 位学号同样不被数字类型改写
        Dim Path2 As String = WritePcConfig("pc-uid17.yml", "1234567890123", "OldTest123", PortalOperator.Mobile)
        AssertTrue(ModAccountForm.ApplyAndSave(
            BlankPasswordForm("12345678901234567", PortalOperator.Mobile), Path2).Success, "17 位学号应可保存")
        AssertEqual("12345678901234567", LoadAppConfigFrom(Path2).User.UserId, "17 位学号不得被改写")
    End Sub

    Private Sub Test_PcOperatorMapping()
        ' 五种网络类型的显示名与保存后的枚举
        AssertEqual("中国电信", ModAuth.GetUiDisplayName(PortalOperator.Telecom), "Telecom 显示名")
        AssertEqual("中国移动", ModAuth.GetUiDisplayName(PortalOperator.Mobile), "Mobile 显示名")
        AssertEqual("中国联通", ModAuth.GetUiDisplayName(PortalOperator.Unicom), "Unicom 显示名")
        AssertEqual("办公网", ModAuth.GetUiDisplayName(PortalOperator.Office), "Office 显示名")
        AssertEqual("校内网", ModAuth.GetUiDisplayName(PortalOperator.Xhu), "Xhu 显示名")

        ' 下拉框顺序必须覆盖全部运营商（防止将来新增枚举被静默漏掉）
        Dim UiOrder As PortalOperator() = ModAccountForm.GetUiOperatorOrder()
        AssertEqual(ModAuth.GetAllOperators().Length, UiOrder.Length, "下拉框项数应等于全部运营商数")
        For Each Op In ModAuth.GetAllOperators()
            Dim Found As Boolean = False
            For Each Item In UiOrder
                If Item = Op Then Found = True
            Next
            AssertTrue(Found, "下拉框应包含 " & Op.ToString())
        Next
        AssertEqual(PortalOperator.Telecom, UiOrder(0), "三大运营商应排在前面")

        ' 逐个保存并回读
        Dim Index As Integer = 0
        For Each Op In UiOrder
            Index += 1
            Dim Path_ As String = CfgTempFile("pc-op-" & Index & ".yml")
            SaveAppConfigTo(Path_, MakeAppConfig("1234567890123", "OldTest123", Op))
            AssertEqual(Op, LoadAppConfigFrom(Path_).User.[Operator], Op.ToString() & " 应正确往返")
        Next
    End Sub

    Private Sub Test_PcUnknownService()
        Dim Path_ As String = CfgTempFile("pc-unknown.yml")
        Dim Cfg As AppConfig = MakeAppConfig("1234567890123", "OldTest123", PortalOperator.Unknown)
        Cfg.UnknownServiceRaw = "some-old-service"
        SaveAppConfigTo(Path_, Cfg)

        Dim Loaded As AppConfig = LoadAppConfigFrom(Path_)
        Dim State As ModAccountForm.FormState = ModAccountForm.DescribeState(Loaded, Path_)
        AssertTrue(State.UnknownServiceHint.Contains("无法识别"), "应提示网络类型无法识别：" & State.UnknownServiceHint)
        AssertTrue(State.UnknownServiceHint.Contains("重新选择"), "应提示重新选择")

        ' 下拉框默认落到一个有效项，而不是 Unknown
        Dim Form As ModAccountForm.AccountForm = ModAccountForm.BuildForm(Loaded)
        AssertTrue(Form.[Operator] <> PortalOperator.Unknown, "下拉框应默认选中有效项")

        ' 用户重新选择并保存后，未知标记被清空
        Dim Outcome As ModAccountForm.SaveOutcome =
            ModAccountForm.ApplyAndSave(BlankPasswordForm("1234567890123", PortalOperator.Unicom), Path_)
        AssertTrue(Outcome.Success, Outcome.ValidationError)

        Dim After As AppConfig = LoadAppConfigFrom(Path_)
        AssertEqual(PortalOperator.Unicom, After.User.[Operator], "应保存新的网络类型")
        AssertEqual("", After.UnknownServiceRaw, "未知标记应被清空")
        AssertEqual("", ModAccountForm.DescribeState(After, Path_).UnknownServiceHint, "提示应消失")
    End Sub

    Private Sub Test_PcMigrationState()
        ' 模拟迁移刚完成：密码待重新输入 + 存在 v3 备份
        Dim Path_ As String = CfgTempFile("pc-migrated.yml")
        IO.File.WriteAllText(Path_ & LegacyBackupSuffix, "main:" & vbCrLf & "  version: 3" & vbCrLf, Text.Encoding.UTF8)
        SaveAppConfigTo(Path_, MakeAppConfig("1234567890123", "", PortalOperator.Telecom))

        Dim Cfg As AppConfig = LoadAppConfigFrom(Path_)
        AssertTrue(Cfg.PasswordNeedsReentry, "密码应处于待重新输入状态")

        Dim State As ModAccountForm.FormState = ModAccountForm.DescribeState(Cfg, Path_)
        AssertTrue(State.MigrationHint.Length > 0, "应显示迁移提示")
        AssertTrue(State.MigrationHint.Contains("旧版本配置"), "提示应说明检测到旧配置")
        AssertTrue(State.MigrationHint.Contains("重新输入校园网密码"), "提示应要求重新输入密码")
        AssertFalse(State.StatusReady, "未设置密码时状态不应为可认证")
        AssertTrue(State.StatusText.Contains("密码"), "状态应指出缺少密码：" & State.StatusText)

        ' 设置密码后迁移提示消失
        Dim Form As ModAccountForm.AccountForm = BlankPasswordForm("1234567890123", PortalOperator.Telecom)
        Form.NewPassword = "AfterMigrate1"
        AssertTrue(ModAccountForm.ApplyAndSave(Form, Path_).Success, "设置密码应成功")
        Dim After As AppConfig = LoadAppConfigFrom(Path_)
        AssertEqual("", ModAccountForm.DescribeState(After, Path_).MigrationHint, "设置密码后不应再提示迁移")
        AssertTrue(ModAccountForm.DescribeState(After, Path_).StatusReady, "此后应可认证")
    End Sub

    Private Sub Test_PcFullSaveRoundTrip()
        Dim Path_ As String = CfgTempFile("pc-full.yml")
        SaveAppConfigTo(Path_, MakeAppConfig("", "", PortalOperator.Unknown))

        Dim Form As ModAccountForm.AccountForm = BlankPasswordForm("001234567890", PortalOperator.Mobile)
        Form.NewPassword = "FullRound1"
        Dim Outcome As ModAccountForm.SaveOutcome = ModAccountForm.ApplyAndSave(Form, Path_)
        AssertTrue(Outcome.Success, Outcome.ValidationError)

        ' 回读完全一致
        Dim Loaded As AppConfig = LoadAppConfigFrom(Path_)
        AssertEqual("001234567890", Loaded.User.UserId, "学号一致（含前导零）")
        AssertEqual(PortalOperator.Mobile, Loaded.User.[Operator], "运营商一致")
        AssertEqual("FullRound1", Loaded.User.Password, "密码一致")
        AssertTrue(Loaded.IsReadyForAuthentication, "应可直接认证")
        AssertEqual("配置已保存，可以进行校园网认证。", Outcome.Message, "保存后应给出可认证提示")

        ' 保存后不再触碰旧的 login_data / url / cookie / headers
        Dim ConfigText As String = IO.File.ReadAllText(Path_)
        AssertFalse(HasYamlKey(ConfigText, "url"), "不应写入 url")
        AssertFalse(HasYamlKey(ConfigText, "cookie"), "不应写入 cookie")
        AssertFalse(HasYamlKey(ConfigText, "login_data"), "不应写入 login_data")
        AssertFalse(HasYamlKey(ConfigText, "headers"), "不应写入 headers")
        AssertTrue(HasYamlKey(ConfigText, "account"), "应写入 account")
        AssertTrue(HasYamlKey(ConfigText, "auth"), "应写入 auth")
    End Sub

    Private Sub Test_PcNoSensitiveOutput()
        Dim Path_ As String = WritePcConfig("pc-safe.yml", "1234567890123", "SecretPw123", PortalOperator.Telecom)
        Dim Blob As String = ModConfig.ReadStoredProtectedPassword(Path_)
        Dim Cfg As AppConfig = LoadAppConfigFrom(Path_)
        Dim State As ModAccountForm.FormState = ModAccountForm.DescribeState(Cfg, Path_)

        ' 页面上会显示的全部文字
        Dim Shown As String = State.PasswordHint & "|" & State.MigrationHint & "|" &
                              State.UnknownServiceHint & "|" & State.StatusText

        AssertFalse(Shown.Contains("SecretPw123"), "提示文字不得出现密码明文")
        AssertFalse(Shown.Contains(Blob), "提示文字不得出现 password_protected 密文")
        AssertFalse(Shown.Contains("JSESSIONID"), "不得出现 JSESSIONID")
        AssertFalse(Shown.Contains("EPORTAL_COOKIE"), "不得出现 Cookie")
        AssertFalse(Shown.Contains("wlanuserip"), "不得出现 queryString 内容")

        ' 保存成功的提示同样不得泄露
        Dim Outcome As ModAccountForm.SaveOutcome =
            ModAccountForm.ApplyAndSave(BlankPasswordForm("1234567890123", PortalOperator.Telecom), Path_)
        AssertTrue(Outcome.Success, Outcome.ValidationError)
        AssertFalse(Outcome.Message.Contains("SecretPw123"), "保存提示不得出现密码明文")
        AssertFalse(Outcome.Message.Contains(ModConfig.ReadStoredProtectedPassword(Path_)), "保存提示不得出现密文")

        ' 校验失败提示也不含敏感内容
        Dim Bad As New ModAccountForm.AccountForm With {
            .UserId = "", .[Operator] = PortalOperator.Telecom, .NewPassword = "SecretPw123"
        }
        Dim BadOutcome As ModAccountForm.SaveOutcome = ModAccountForm.ApplyAndSave(Bad, Path_)
        AssertFalse(BadOutcome.Success, "空学号应失败")
        AssertFalse(BadOutcome.ValidationError.Contains("SecretPw123"), "校验提示不得出现密码明文")
    End Sub

    Private Sub Test_PcClearPassword()
        Dim Path_ As String = WritePcConfig("pc-clear.yml", "1234567890123", "OldTest123", PortalOperator.Telecom)
        AssertTrue(ModConfig.ReadStoredProtectedPassword(Path_).Length > 0, "前置条件：已有密码")

        ' 清除 + 同时输入新密码 → 新密码生效
        Dim Form As ModAccountForm.AccountForm = BlankPasswordForm("1234567890123", PortalOperator.Telecom)
        Form.ClearStoredPassword = True
        Form.NewPassword = "AfterClear1"
        AssertTrue(ModAccountForm.ApplyAndSave(Form, Path_).Success, "清除并设置新密码应成功")
        AssertEqual("AfterClear1", LoadAppConfigFrom(Path_).User.Password, "应为新密码")

        ' 清除且不输入新密码 → 校验失败（不允许把配置存成没有密码的状态）
        Dim ClearOnly As ModAccountForm.AccountForm = BlankPasswordForm("1234567890123", PortalOperator.Telecom)
        ClearOnly.ClearStoredPassword = True
        Dim Outcome As ModAccountForm.SaveOutcome = ModAccountForm.ApplyAndSave(ClearOnly, Path_)
        AssertFalse(Outcome.Success, "只清除不输入新密码应被拒绝")
        AssertEqual("请输入密码", Outcome.ValidationError, "应提示请输入密码")
    End Sub

    Private Sub Test_PcSaveMessages()
        Dim Ready As AppConfig = MakeAppConfig("1234567890123", "OldTest123", PortalOperator.Telecom)
        AssertEqual("配置已保存，可以进行校园网认证。", ModAccountForm.DescribeSaveResult(Ready), "配置完整时的文案")

        Dim NoPw As AppConfig = MakeAppConfig("1234567890123", "", PortalOperator.Telecom)
        AssertTrue(ModAccountForm.DescribeSaveResult(NoPw).Contains("尚未设置密码"), "缺密码时的文案")

        Dim NoOp As AppConfig = MakeAppConfig("1234567890123", "OldTest123", PortalOperator.Unknown)
        AssertTrue(ModAccountForm.DescribeSaveResult(NoOp).Contains("配置已保存"), "其它不完整情况仍应确认已保存")
    End Sub

    Private Sub Test_PcValidationErrors()
        Dim Path_ As String = WritePcConfig("pc-validate.yml", "1234567890123", "OldTest123", PortalOperator.Telecom)
        Dim Cfg As AppConfig = LoadAppConfigFrom(Path_)
        Dim State As ModAccountForm.FormState = ModAccountForm.DescribeState(Cfg, Path_)

        ' 学号为空
        Dim NoId As New ModAccountForm.AccountForm With {
            .UserId = "   ", .[Operator] = PortalOperator.Telecom, .NewPassword = ""
        }
        AssertEqual("请输入学号", ModAccountForm.Validate(NoId, State), "空学号提示")
        AssertEqual("请输入学号", ModAccountForm.ApplyAndSave(NoId, Path_).ValidationError, "空学号不应落盘")

        ' 运营商无效
        Dim NoOp As New ModAccountForm.AccountForm With {
            .UserId = "1234567890123", .[Operator] = PortalOperator.Unknown, .NewPassword = ""
        }
        AssertEqual("请选择网络类型", ModAccountForm.Validate(NoOp, State), "未选网络类型提示")

        ' 无可用密码
        Dim NoStored As ModAccountForm.FormState = ModAccountForm.DescribeState(
            MakeAppConfig("1234567890123", "", PortalOperator.Telecom), CfgTempFile("pc-validate2.yml"))
        Dim NoPw As New ModAccountForm.AccountForm With {
            .UserId = "1234567890123", .[Operator] = PortalOperator.Telecom, .NewPassword = ""
        }
        AssertEqual("请输入密码", ModAccountForm.Validate(NoPw, NoStored), "无密码提示")

        ' 学习号会被 trim 后保存
        Dim Spaced As New ModAccountForm.AccountForm With {
            .UserId = "  1234567890123  ", .[Operator] = PortalOperator.Telecom, .NewPassword = ""
        }
        AssertEqual("", ModAccountForm.Validate(Spaced, State), "带空白的学号应通过校验")
        AssertTrue(ModAccountForm.ApplyAndSave(Spaced, Path_).Success, "带空白学号应可保存")
        AssertEqual("1234567890123", LoadAppConfigFrom(Path_).User.UserId, "保存时应去掉两侧空白")
    End Sub


#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("加载已有密码的配置", AddressOf Test_PcLoadExistingPassword)
        RunTest("已有密码 + 密码框留空 → 原密文不变", AddressOf Test_PcKeepPasswordWhenBlank)
        RunTest("已有密码 + 输入新密码 → 覆盖为新密码", AddressOf Test_PcSetNewPassword)
        RunTest("没有密码 + 密码框留空 → 提示请输入密码", AddressOf Test_PcRequirePasswordWhenNone)
        RunTest("只改学号且留空密码 → 密码保持", AddressOf Test_PcChangeUserIdKeepsPassword)
        RunTest("五种网络类型显示与保存", AddressOf Test_PcOperatorMapping)
        RunTest("未知网络类型提示与清除", AddressOf Test_PcUnknownService)
        RunTest("迁移后状态提示重新输入密码", AddressOf Test_PcMigrationState)
        RunTest("完整保存并回读一致", AddressOf Test_PcFullSaveRoundTrip)
        RunTest("状态与提示不含任何敏感值", AddressOf Test_PcNoSensitiveOutput)
        RunTest("清除已保存密码", AddressOf Test_PcClearPassword)
        RunTest("保存成功文案三态", AddressOf Test_PcSaveMessages)
        RunTest("表单校验三种提示", AddressOf Test_PcValidationErrors)
    End Sub

#End Region

End Module