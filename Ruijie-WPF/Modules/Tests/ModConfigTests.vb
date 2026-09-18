Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModConfigTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModConfigTests

#Region "ModConfig 测试用例"

    Private Sub Test_CfgMigrateSyntheticV3()
        Dim Path_ As String = CfgTempFile("migrate-synthetic.yml")
        IO.File.WriteAllText(Path_, SyntheticV3Config(), Text.Encoding.UTF8)

        Dim Result As MigrationResult = MigrateToV4(Path_)
        AssertTrue(Result.Migrated, "应完成迁移：" & Result.Message)
        AssertEqual(3, Result.FromVersion, "来源版本应为 v3")
        AssertEqual(4, Result.ToVersion, "目标版本应为 v4")

        Dim Cfg As AppConfig = Result.Config
        AssertTrue(Cfg IsNot Nothing, "应返回迁移后的配置")
        AssertEqual("001234567890", Cfg.User.UserId, "学号必须原样迁移（含前导零）")
        AssertEqual(PortalOperator.Telecom, Cfg.User.[Operator], "service 96301 应映射为 Telecom")
        AssertTrue(Cfg.[Function].AutoReconnect, "auto_reconnect 应迁移")
        AssertEqual(5, Cfg.[Function].ReconnectInterval, "reconnect_interval 应迁移")
        AssertTrue(Cfg.PasswordNeedsReentry, "旧密码是门户密文，无法还原，必须要求重新输入")
        AssertEqual("", Cfg.User.Password, "迁移后不应有明文密码")

        ' 旧字段不得出现在新的 v4 模型里
        Dim ConfigText As String = IO.File.ReadAllText(Path_)
        AssertFalse(HasYamlKey(ConfigText, "url"), "v4 不应再有 url 段")
        AssertFalse(HasYamlKey(ConfigText, "cookie"), "v4 不应再有 cookie")
        AssertFalse(HasYamlKey(ConfigText, "login_data"), "v4 不应再有 login_data")
        AssertFalse(HasYamlKey(ConfigText, "headers"), "v4 不应再有 headers")
        AssertFalse(HasYamlKey(ConfigText, "logout_data"), "v4 不应再有 logout_data")
        AssertFalse(ConfigText.Contains("deadbeef"), "不得把旧密码密文写进新配置")
        AssertFalse(ConfigText.Contains("wlanuserip"), "不得把旧 queryString 写进新配置")
        AssertFalse(ConfigText.Contains("test-cookie"), "不得把旧 cookie 写进新配置")
        AssertFalse(ConfigText.Contains("example.test"), "不得把旧 Referer 写进新配置")
    End Sub

    Private Sub Test_CfgMigrateBackup()
        Dim Path_ As String = CfgTempFile("migrate-backup.yml")
        Dim Original As String = SyntheticV3Config()
        IO.File.WriteAllText(Path_, Original, Text.Encoding.UTF8)

        Dim Result As MigrationResult = MigrateToV4(Path_)
        AssertTrue(Result.Migrated, "应完成迁移：" & Result.Message)

        Dim BackupPath As String = GetLegacyBackupPath(Path_)
        AssertEqual(Path_ & LegacyBackupSuffix, BackupPath, "备份路径应为 config.yml.v3.bak 形式")
        AssertTrue(IO.File.Exists(BackupPath), "必须生成 v3 备份")
        AssertEqual(Original, IO.File.ReadAllText(BackupPath), "备份内容应与迁移前的旧配置完全一致")

        ' 备份已存在时不覆盖，保留最初的旧配置
        IO.File.WriteAllText(BackupPath, "PRISTINE", Text.Encoding.UTF8)
        Dim Second As MigrationResult = MigrateToV4(Path_)
        AssertEqual("PRISTINE", IO.File.ReadAllText(BackupPath), "已有备份不应被覆盖")

        ' 备份被删除后仍可再次迁移（幂等）
        IO.File.Delete(BackupPath)
        IO.File.WriteAllText(Path_, Original, Text.Encoding.UTF8)
        Dim Third As MigrationResult = MigrateToV4(Path_)
        AssertTrue(Third.Migrated, "重复迁移应仍然成功")
        AssertTrue(IO.File.Exists(BackupPath), "备份应重新生成")
    End Sub

    Private Sub Test_CfgMigrateUnknownService()
        Dim Path_ As String = CfgTempFile("migrate-unknown.yml")
        IO.File.WriteAllText(Path_, SyntheticV3Config().Replace("""96301""", """unknown-service-x"""), Text.Encoding.UTF8)

        Dim Result As MigrationResult = MigrateToV4(Path_)
        AssertTrue(Result.Migrated, "应完成迁移：" & Result.Message)
        AssertEqual(PortalOperator.Unknown, Result.Config.User.[Operator],
                    "未知 service 必须映射为 Unknown，绝不能静默变成 Telecom")
        AssertEqual("unknown-service-x", Result.Config.UnknownServiceRaw, "应保留原始 service 供 UI 提示")

        ' 再次读写也要保持 Unknown
        Dim Reloaded As AppConfig = LoadAppConfigFrom(Path_)
        AssertEqual(PortalOperator.Unknown, Reloaded.User.[Operator], "重新读取后仍应为 Unknown")
    End Sub

    Private Sub Test_CfgMigrateAlreadyV4()
        Dim Path_ As String = CfgTempFile("migrate-already-v4.yml")
        Dim Cfg As AppConfig = GetDefaultAppConfig()
        Cfg.User.UserId = "1234567890123"
        Cfg.User.[Operator] = PortalOperator.Mobile
        SaveAppConfigTo(Path_, Cfg)

        Dim Result As MigrationResult = MigrateToV4(Path_)
        AssertFalse(Result.Migrated, "已是 v4 不应再次迁移")
        AssertTrue(Result.Message.Contains("无需迁移"), "应给出明确说明：" & Result.Message)
        AssertFalse(IO.File.Exists(GetLegacyBackupPath(Path_)), "不应产生多余的备份")
    End Sub

    Private Sub Test_CfgRoundTrip()
        Dim Path_ As String = CfgTempFile("roundtrip.yml")

        Dim Original As AppConfig = GetDefaultAppConfig()
        Original.User.UserId = "001234567890"          ' 前导零
        Original.User.Password = "test1234"
        Original.User.[Operator] = PortalOperator.Unicom
        Original.[Function].AutoReconnect = True
        Original.[Function].ReconnectInterval = 7
        SaveAppConfigTo(Path_, Original)

        Dim Loaded As AppConfig = LoadAppConfigFrom(Path_)
        AssertEqual("001234567890", Loaded.User.UserId, "前导零学号必须一致")
        AssertEqual("test1234", Loaded.User.Password, "DPAPI 密码应能解回明文")
        AssertEqual(PortalOperator.Unicom, Loaded.User.[Operator], "运营商应一致")
        AssertTrue(Loaded.[Function].AutoReconnect, "auto_reconnect 应一致")
        AssertEqual(7, Loaded.[Function].ReconnectInterval, "reconnect_interval 应一致")
        AssertFalse(Loaded.PasswordNeedsReentry, "密码已保存时不应要求重新输入")
        AssertTrue(Loaded.IsReadyForAuthentication, "配置齐全时应可直接认证")

        ' 17 位学号
        Dim Path2 As String = CfgTempFile("roundtrip-17.yml")
        Dim Long17 As AppConfig = GetDefaultAppConfig()
        Long17.User.UserId = "12345678901234567"
        Long17.User.Password = "p"
        Long17.User.[Operator] = PortalOperator.Telecom
        SaveAppConfigTo(Path2, Long17)
        AssertEqual("12345678901234567", LoadAppConfigFrom(Path2).User.UserId,
                    "17 位学号不得被数字精度改写")
    End Sub

    Private Sub Test_CfgUserIdStaysString()
        ' YAML 里 user_id 必须带引号写出，读回来必须是 String 而不是数字
        Dim Path_ As String = CfgTempFile("userid-type.yml")
        Dim Cfg As AppConfig = GetDefaultAppConfig()
        Cfg.User.UserId = "12345678901234567"
        SaveAppConfigTo(Path_, Cfg)

        Dim ConfigText As String = IO.File.ReadAllText(Path_)
        AssertTrue(ConfigText.Contains("user_id: '12345678901234567'"),
                   "user_id 必须以带引号的字符串形式写出，避免被 YAML 当数字读回")

        AssertEqual("12345678901234567", LoadAppConfigFrom(Path_).User.UserId, "17 位学号应完整保留")

        ' 前导零同样不能被吃掉
        Dim Path2 As String = CfgTempFile("userid-zeros.yml")
        Dim Cfg2 As AppConfig = GetDefaultAppConfig()
        Cfg2.User.UserId = "001234567890"
        SaveAppConfigTo(Path2, Cfg2)
        AssertEqual("001234567890", LoadAppConfigFrom(Path2).User.UserId, "前导零应完整保留")

        ' 纯数字但很短的学号也不应被当数字
        Dim Path3 As String = CfgTempFile("userid-short.yml")
        Dim Cfg3 As AppConfig = GetDefaultAppConfig()
        Cfg3.User.UserId = "2021000001"
        SaveAppConfigTo(Path3, Cfg3)
        AssertEqual("2021000001", LoadAppConfigFrom(Path3).User.UserId, "普通学号应保持一致")
    End Sub

    Private Sub Test_CfgFormatStability()
        Dim Path_ As String = CfgTempFile("stability.yml")
        Dim Cfg As AppConfig = GetDefaultAppConfig()
        Cfg.User.UserId = "001234567890"
        Cfg.User.Password = "test1234"
        Cfg.User.[Operator] = PortalOperator.Telecom
        Cfg.[Function].AutoReconnect = True
        Cfg.[Function].ReconnectInterval = 9

        ' 写 → 读 → 写：关键字段语义必须稳定
        SaveAppConfigTo(Path_, Cfg)
        Dim First As String = IO.File.ReadAllText(Path_)

        Dim Loaded As AppConfig = LoadAppConfigFrom(Path_)
        SaveAppConfigTo(Path_, Loaded)
        Dim Second As String = IO.File.ReadAllText(Path_)

        ' DPAPI 每次加密都带新的随机盐，password_protected 密文必然不同，
        ' 因此比对「除密码行以外的整份文件」必须逐字节一致 —— 其余字段不得漂移。
        AssertEqual(StripPasswordLine(First), StripPasswordLine(Second),
                    "除 password_protected 外，写→读→写 必须逐字节一致（不得因类型推断漂移）")
        AssertTrue(First.Contains("password_protected:"), "应当写入了密码字段")
        AssertFalse(First.Contains("test1234"), "任何情况下都不得写出密码明文")

        Dim Again As AppConfig = LoadAppConfigFrom(Path_)
        AssertEqual("001234567890", Again.User.UserId, "往返后学号不变")
        AssertEqual(PortalOperator.Telecom, Again.User.[Operator], "往返后运营商不变")
        AssertTrue(Again.[Function].AutoReconnect, "往返后 auto_reconnect 不变")
        AssertEqual(9, Again.[Function].ReconnectInterval, "往返后间隔不变")
    End Sub

    Private Sub Test_CfgNoLegacyKeysWritten()
        Dim Path_ As String = CfgTempFile("no-legacy.yml")
        Dim Cfg As AppConfig = GetDefaultAppConfig()
        Cfg.User.UserId = "1234567890123"
        Cfg.User.Password = "test1234"
        Cfg.User.[Operator] = PortalOperator.Telecom
        SaveAppConfigTo(Path_, Cfg)

        Dim ConfigText As String = IO.File.ReadAllText(Path_)
        AssertTrue(HasYamlKey(ConfigText, "main"), "应保留 main 段")
        AssertTrue(HasYamlKey(ConfigText, "account"), "应有 account 段")
        AssertTrue(HasYamlKey(ConfigText, "auth"), "应有 auth 段")
        AssertTrue(HasYamlKey(ConfigText, "function"), "应保留 function 段")

        AssertFalse(HasYamlKey(ConfigText, "url"), "不应有 url 段")
        AssertFalse(HasYamlKey(ConfigText, "cookie"), "不应有 cookie")
        AssertFalse(HasYamlKey(ConfigText, "login_data"), "不应有 login_data")
        AssertFalse(HasYamlKey(ConfigText, "logout_data"), "不应有 logout_data")
        AssertFalse(HasYamlKey(ConfigText, "headers"), "不应有 headers")

        AssertFalse(ConfigText.Contains("test1234"), "配置文件里绝不能出现密码明文")
        AssertTrue(ConfigText.Contains("password_protected"), "应写入 password_protected 字段")
        AssertTrue(ConfigText.Contains("operator: 'telecom'"), "运营商应以配置 token 形式保存")
    End Sub

    Private Sub Test_CfgVersionTooNew()
        Dim Path_ As String = CfgTempFile("version-too-new.yml")
        IO.File.WriteAllText(Path_,
            "main:" & vbCrLf & "  version: 99" & vbCrLf &
            "account:" & vbCrLf & "  user_id: '1234567890123'" & vbCrLf, Text.Encoding.UTF8)

        Dim Cfg As AppConfig = LoadAppConfigFrom(Path_)
        AssertTrue(Cfg.Warnings.Count > 0, "应给出告警")
        Dim Found As Boolean = False
        For Each Warning In Cfg.Warnings
            If Warning.Contains("请更新本程序") Then Found = True
        Next
        AssertTrue(Found, "应明确提示配置来自更新版本：" & String.Join(" / ", Cfg.Warnings.ToArray()))
    End Sub

    Private Sub Test_CfgLegacyCompatibility()
        ' 兼容层：旧代码仍然能从 ReadCfg 形状的字典里取到它需要的字段
        Dim Cfg As AppConfig = GetDefaultAppConfig()
        Cfg.User.UserId = "1234567890123"
        Cfg.User.[Operator] = PortalOperator.Telecom
        Cfg.[Function].AutoReconnect = True
        Cfg.[Function].ReconnectInterval = 3

        Dim Legacy As Dictionary(Of String, Object) = ToLegacyDictionary(Cfg)

        ' NetworkMonitor 需要 url.server 与 function.*
        AssertEqual(SchoolServer, GetDictStr(GetUrlDict(Legacy), ConfigKeys.Server), "兼容层应提供学校服务器地址")
        AssertTrue(GetDictBool(GetFunctionDict(Legacy), ConfigKeys.AutoReconnect, False), "兼容层应提供 auto_reconnect")
        AssertEqual(3, GetDictInt(GetFunctionDict(Legacy), ConfigKeys.ReconnectInterval, 5), "兼容层应提供间隔")

        ' PageConfig 需要 login_data.*
        Dim LoginData = GetSubDict(Legacy, ConfigKeys.LoginData)
        AssertTrue(LoginData IsNot Nothing, "兼容层应提供 login_data")
        AssertEqual("1234567890123", GetDictStr(LoginData, "userId"), "兼容层应带出学号")
        AssertEqual("96301", GetDictStr(LoginData, "service"), "兼容层应带出 service")

        ' v4 原生字段也可读
        AssertEqual("1234567890123", GetDictStr(GetSubDict(Legacy, ConfigKeys.Account), ConfigKeys.UserId), "account.user_id")
        AssertEqual("telecom", GetDictStr(GetSubDict(Legacy, ConfigKeys.Auth), ConfigKeys.OperatorKey), "auth.operator")

        ' 运营商 token 往返
        AssertEqual("telecom", GetOperatorToken(PortalOperator.Telecom), "Telecom token")
        AssertEqual("mobile", GetOperatorToken(PortalOperator.Mobile), "Mobile token")
        AssertEqual("unicom", GetOperatorToken(PortalOperator.Unicom), "Unicom token")
        AssertEqual("office", GetOperatorToken(PortalOperator.Office), "Office token")
        AssertEqual("xhu", GetOperatorToken(PortalOperator.Xhu), "Xhu token")
        AssertEqual("unknown", GetOperatorToken(PortalOperator.Unknown), "Unknown token")
    End Sub


#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("v3 → v4 迁移（合成配置）", AddressOf Test_CfgMigrateSyntheticV3)
        RunTest("迁移生成备份且保留原内容", AddressOf Test_CfgMigrateBackup)
        RunTest("未知 service 不静默变成 telecom", AddressOf Test_CfgMigrateUnknownService)
        RunTest("已是 v4 时不重复迁移", AddressOf Test_CfgMigrateAlreadyV4)
        RunTest("v4 往返: 学号/密码/运营商/功能开关", AddressOf Test_CfgRoundTrip)
        RunTest("学号始终按字符串处理", AddressOf Test_CfgUserIdStaysString)
        RunTest("写→读→写 语义稳定", AddressOf Test_CfgFormatStability)
        RunTest("v4 文件不含任何旧字段", AddressOf Test_CfgNoLegacyKeysWritten)
        RunTest("更高版本配置给出明确提示", AddressOf Test_CfgVersionTooNew)
        RunTest("旧结构兼容层仍可用", AddressOf Test_CfgLegacyCompatibility)
    End Sub

#End Region

End Module