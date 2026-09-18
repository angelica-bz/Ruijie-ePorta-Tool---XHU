Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModTestFixtures：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModTestFixtures

#Region "ModAuth 测试夹具"

    ''' <summary>测试用查询串；mac 由参数决定是否出现。</summary>
    Friend Function MakeQuery(Optional Mac As String = Nothing) As String
        Dim Q As String =
            "wlanuserip=10.20.1.2&wlanacname=&nasip=1.2.3.4&wlanparameter=02-00-00-00-00-01" &
            "&url=http%3A%2F%2Fwww.msftconnecttest.com%2Fredirect&userlocation=ethtrunk%2F1%3A1000.0"
        If Mac IsNot Nothing Then Q &= "&mac=" & Mac
        Return Q
    End Function

    Friend Function MakeDiscovery(Optional Mac As String = Nothing,
                                   Optional QueryString As String = Nothing,
                                   Optional Modulus As String = TestModulus,
                                   Optional Exponent As String = TestExponent,
                                   Optional PasswordEncrypt As Boolean = True,
                                   Optional ValidCodeUrl As String = "",
                                   Optional Services As ModPortalDiscover.PortalServices = Nothing) As ModPortalDiscover.PortalDiscoveryResult
        Dim EffectiveQuery As String = If(QueryString IsNot Nothing, QueryString, MakeQuery(Mac))
        Return New ModPortalDiscover.PortalDiscoveryResult With {
            .Status = ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication,
            .Message = "测试用发现结果",
            .Redirect = New ModPortalDiscover.PortalRedirectInfo With {
                .PortalUrl = "http://202.115.144.51/eportal/index.jsp",
                .QueryString = EffectiveQuery,
                .Parameters = ModPortalDiscover.ParseQueryParameters(EffectiveQuery)
            },
            .PageInfo = New ModPortalDiscover.PortalPageInfo With {
                .PublicKeyModulus = Modulus,
                .PublicKeyExponent = Exponent,
                .PasswordEncrypt = PasswordEncrypt,
                .ValidCodeUrl = ValidCodeUrl
            },
            .Services = Services
        }
    End Function

    Friend Function NewAccount(UserId As String, Password As String,
                               Op As PortalOperator,
                               Optional ValidCode As String = "") As PortalAccount
        Return New PortalAccount With {
            .UserId = UserId,
            .Password = Password,
            .[Operator] = Op,
            .ValidCode = ValidCode
        }
    End Function

    Friend Function MakeServices(ParamArray Names As String()) As ModPortalDiscover.PortalServices
        Dim Result As New ModPortalDiscover.PortalServices()
        For Each ItemName In Names
            Result.Items.Add(New ModPortalDiscover.PortalService With {.Name = ItemName, .DisplayName = ItemName})
        Next
        Return Result
    End Function

    Friend Sub AssertAuthFailure(Account As PortalAccount,
                                  Discovery As ModPortalDiscover.PortalDiscoveryResult,
                                  Expected As AuthFailure,
                                  Optional Scenario As String = "")
        Dim Payload As AuthPayload = Nothing
        Dim Failure As AuthFailure = AuthFailure.None
        Dim Message As String = ""
        Dim Ok As Boolean = TryBuild(Account, Discovery, Payload, Failure, Message)
        AssertFalse(Ok, "应失败：" & Expected.ToString() & If(Scenario = "", "", " (" & Scenario & ")"))
        AssertEqual(Expected, Failure, "失败原因应为 " & Expected.ToString() & If(Scenario = "", "", " (" & Scenario & ")"))
        AssertTrue(Message.Length > 0, "失败时必须给出可展示给用户的信息")
        AssertTrue(Payload Is Nothing, "失败时不应产出 payload")
    End Sub


#End Region

#Region "ModAuthentication 测试夹具"

    ''' <summary>用第四阶段的真实夹具拼出完整的、可继续认证的 PortalDiscoveryResult。</summary>
    Friend Function MakeRealDiscovery() As ModPortalDiscover.PortalDiscoveryResult
        Return New ModPortalDiscover.PortalDiscoveryResult With {
            .Status = ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication,
            .Message = "测试用",
            .Redirect = ParseRedirectLocation(TestPortalRedirect),
            .PageInfo = ParsePageInfo(TestPageInfoJson),
            .Services = ParseServices(TestServicesJson)
        }
    End Function

    ''' <summary>只带状态与说明的发现结果（模拟探测阶段的各种失败）。</summary>
    Friend Function MakeStatusOnly(Status As ModPortalDiscover.PortalDiscoveryStatus,
                                    Message As String) As ModPortalDiscover.PortalDiscoveryResult
        Return New ModPortalDiscover.PortalDiscoveryResult With {
            .Status = Status,
            .Message = Message
        }
    End Function


#End Region

#Region "ModCredential / ModConfig 测试夹具"

    ''' <summary>测试专用临时目录，绝不触碰真实的 bin\config.yml。</summary>
    Friend Function CfgTempDir() As String
        Dim Dir As String = IO.Path.Combine(IO.Path.GetTempPath(), "ruijie-cfg-tests")
        If Not IO.Directory.Exists(Dir) Then IO.Directory.CreateDirectory(Dir)
        Return Dir
    End Function

    ''' <summary>取一个干净的临时配置文件路径（同时清掉同名备份）。</summary>
    Friend Function CfgTempFile(Name As String) As String
        Dim P As String = IO.Path.Combine(CfgTempDir(), Name)
        For Each Candidate In New String() {P, P & LegacyBackupSuffix}
            If IO.File.Exists(Candidate) Then IO.File.Delete(Candidate)
        Next
        Return P
    End Function

    ''' <summary>§十七 给出的合成 v3 配置。</summary>
    Friend Function SyntheticV3Config() As String
        Dim Lines As String() = {
            "main:",
            "  version: 3",
            "",
            "function:",
            "  auto_reconnect: true",
            "  reconnect_interval: 5",
            "",
            "url:",
            "  server: http://202.115.144.51",
            "  login: /eportal/InterFace.do?method=login",
            "  logout: /eportal/InterFace.do?method=logout",
            "",
            "cookie: test-cookie",
            "",
            "login_data:",
            "  userId: ""001234567890""",
            "  password: ""deadbeef""",
            "  service: ""96301""",
            "  queryString: ""wlanuserip=10.20.1.2""",
            "  operatorPwd: """"",
            "  operatorUserId: """"",
            "  validcode: """"",
            "  passwordEncrypt: true",
            "",
            "headers:",
            "  Referer: http://example.test/"
        }
        Return String.Join(vbCrLf, Lines) & vbCrLf
    End Function

    ''' <summary>去掉 password_protected 行，用于比对“除密文外”的格式稳定性。</summary>
    Friend Function StripPasswordLine(ConfigText As String) As String
        Dim Kept As New List(Of String)
        For Each Line In ConfigText.Split(New String() {vbCrLf, vbLf}, StringSplitOptions.None)
            If Line.TrimStart().StartsWith(ConfigKeys.PasswordProtected & ":") Then Continue For
            Kept.Add(Line)
        Next
        Return String.Join(vbLf, Kept.ToArray())
    End Function

    ''' <summary>配置文本里是否存在某个顶层 YAML 键（忽略注释与空行）。</summary>
    Friend Function HasYamlKey(Text As String, Key As String) As Boolean
        For Each Line In Text.Split(New String() {vbCrLf, vbLf}, StringSplitOptions.None)
            Dim Trimmed As String = Line.Trim()
            If Trimmed.Length = 0 OrElse Trimmed.StartsWith("#") Then Continue For
            If Trimmed.StartsWith(Key & ":") Then Return True
        Next
        Return False
    End Function


#End Region

#Region "RuntimeAuthentication 测试夹具"

    ''' <summary>在内存里造一份可用于认证的 AppConfig（不落盘）。</summary>
    Friend Function MakeAppConfig(UserId As String, Password As String, Op As PortalOperator,
                                   Optional AutoReconnect As Boolean = False,
                                   Optional Interval As Integer = 5) As AppConfig
        Dim Cfg As AppConfig = GetDefaultAppConfig()
        Cfg.User.UserId = UserId
        Cfg.User.Password = Password
        Cfg.User.[Operator] = Op
        Cfg.[Function].AutoReconnect = AutoReconnect
        Cfg.[Function].ReconnectInterval = Interval
        Cfg.PasswordNeedsReentry = String.IsNullOrEmpty(Password)
        Return Cfg
    End Function


#End Region

#Region "PageConfig 测试夹具"

    ''' <summary>建立一份带可用密码的 v4 测试配置，返回其路径。</summary>
    Friend Function WritePcConfig(Name As String, UserId As String, Password As String,
                                   Op As PortalOperator) As String
        Dim Path_ As String = CfgTempFile(Name)
        SaveAppConfigTo(Path_, MakeAppConfig(UserId, Password, Op))
        Return Path_
    End Function

    ''' <summary>构造一个「密码框留空」的表单。</summary>
    Friend Function BlankPasswordForm(UserId As String, Op As PortalOperator) As ModAccountForm.AccountForm
        Return New ModAccountForm.AccountForm With {
            .UserId = UserId,
            .[Operator] = Op,
            .NewPassword = "",
            .ClearStoredPassword = False
        }
    End Function


#End Region

End Module
