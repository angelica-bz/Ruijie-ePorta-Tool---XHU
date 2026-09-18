Imports Microsoft.VisualBasic

''' <summary>
''' 认证业务层：把「用户身份 + 运营商选择 + 门户发现结果」组装成 ePortal 登录接口
''' 需要的 login_data。
'''
''' 三层职责严格分离（勿混）：
'''   用户配置        —— 学号 / 明文密码 / 运营商          （PortalAccount）
'''   运行期门户数据  —— queryString / mac / 公钥 / 验证码开关 （ModPortalDiscover 的 PortalDiscoveryResult）
'''   认证 Payload    —— userId/password/service/...        （本模块的 AuthPayload）
'''
''' 本模块不访问 UI、配置文件、网络或 SharedCfg，只做纯数据组装，因此
''' PageConfig、NetworkMonitor、命令行测试都可以调用同一套逻辑。
'''
''' 关键约定（均有第二阶段实测依据）：
'''   userId —— 校园网 Web 认证下就是学号原文，绝不拼接 @运营商后缀；
'''             运营商信息只通过 service 字段传递。
'''   service —— 来自本模块唯一的运营商映射表，并用门户实际返回的服务列表校验。
'''   queryString —— 原样透传 discovery.Redirect.QueryString，不在本层做任何编解码；
'''                  最终由 ModNetwork.EncodeFormData 统一编码一次。
'''   passwordEncrypt —— 由门户决定（PortalPageInfo.PasswordEncrypt），UI 不得干预。
'''   mac 默认值 —— 由 ModCrypto 负责，本模块不复制该逻辑。
'''
''' ============================================================================
''' 为什么 userId 不带 @ 后缀 —— 与宽带拨号的区别（改代码前务必读完）
''' ----------------------------------------------------------------------------
''' 校园网有两种互不相同的接入与认证模型，用户名写法也完全不同：
'''
'''   1) ePortal Web 认证（**本程序采用的就是这一种**）
'''        浏览器被 BRAS 劫持到门户页面，提交表单完成认证。
'''        用户名 = 纯学号；运营商单独放在 service 字段里。
'''            userId  = "2023123456789"
'''            service = "96301"
'''        把运营商拼进用户名（"2023123456789@96301"）会被门户当成
'''        一个不存在的账号，直接认证失败。
'''
'''   2) 宽带拨号 PPPoE（**不是本程序**，也未被本程序实现）
'''        由操作系统或路由器发起 PPPoE 拨号，用户名形如
'''            "2023123456789@96301"   "2023123456789@cmccgx"   "2023123456789@unicom"
'''        这里的 @ 后缀是**拨号用户名的一部分**，由 PPPoE 服务端解析，
'''        与 ePortal 的 service 字段没有任何关系。
'''
''' 之所以要专门写清楚：这两种写法外观相似、含义相反。
''' 见到 @96301 / @cmccgx / @unicom 时不要「顺手」把它拼到 userId 上 ——
''' 那是把拨号的写法搬进了 Web 认证。getServices 返回的 service 字段
''' （office / 96301 / cmccgx / unicom / xhu）才是 Web 认证该用的东西。
'''
''' 对应的回归测试：ModFailureClassificationTests / ModAuthTests 中断言
''' Payload.UserId 不含 "@"，以及 ModAcceptTests 中的 UserIdHasSuffix = False。
''' ============================================================================
''' </summary>
Public Module ModAuth

#Region "运营商模型"

    ''' <summary>
    ''' 校园网 ePortal 的运营商/服务。枚举名是逻辑概念，
    ''' 具体 service 值由 <see cref="GetServiceCode"/> 唯一给出。
    ''' </summary>
    Public Enum PortalOperator
        ''' <summary>
        ''' 未知/无法识别的服务。用于配置迁移时保留旧数据里不认识的 service 值，
        ''' 绝不要把未知值静默归为某一个真实运营商。
        ''' </summary>
        Unknown = -1
        ''' <summary>办公网  office</summary>
        Office = 0
        ''' <summary>电信网  96301</summary>
        Telecom = 1
        ''' <summary>移动网  cmccgx</summary>
        Mobile = 2
        ''' <summary>联通网  unicom</summary>
        Unicom = 3
        ''' <summary>校内网  xhu</summary>
        Xhu = 4
    End Enum

    ''' <summary>
    ''' 运营商映射的唯一入口：逻辑运营商 → 门户 service 值。
    ''' 门户实测（第二阶段 getServices / pageInfo）：
    '''   office → 办公网（门户默认）   96301 → 电信网   cmccgx → 移动网
    '''   unicom → 联通网               xhu   → 校内网
    ''' 注意：这些是 service 字段值，不是 userId 后缀。
    ''' </summary>
    Public Function GetServiceCode(Op As PortalOperator) As String
        Select Case Op
            Case PortalOperator.Office : Return "office"
            Case PortalOperator.Telecom : Return "96301"
            Case PortalOperator.Mobile : Return "cmccgx"
            Case PortalOperator.Unicom : Return "unicom"
            Case PortalOperator.Xhu : Return "xhu"
            Case Else
                Throw New AuthException(AuthFailure.InvalidOperator, "未知的运营商：" & Op.ToString())
        End Select
    End Function

    ''' <summary>与 <see cref="GetServiceCode"/> 同义，供偏好 "service name" 叫法的调用方使用。</summary>
    Public Function GetServiceName(Op As PortalOperator) As String
        Return GetServiceCode(Op)
    End Function

    ''' <summary>运营商的显示名。与门户 serviceShowName 实测值一致。</summary>
    Public Function GetDisplayName(Op As PortalOperator) As String
        Select Case Op
            Case PortalOperator.Office : Return "办公网"
            Case PortalOperator.Telecom : Return "电信网"
            Case PortalOperator.Mobile : Return "移动网"
            Case PortalOperator.Unicom : Return "联通网"
            Case PortalOperator.Xhu : Return "校内网"
            Case Else
                Throw New AuthException(AuthFailure.InvalidOperator, "未知的运营商：" & Op.ToString())
        End Select
    End Function

    ''' <summary>
    ''' 面向普通用户的显示名。与 <see cref="GetDisplayName"/> 的区别：
    '''   GetDisplayName   —— 门户 serviceShowName（"电信网"），用于日志与协议对照
    '''   GetUiDisplayName —— 用户熟悉的名称（"中国电信"），用于配置界面
    ''' 两者都只在本模块维护，UI 不要另建映射表。
    ''' </summary>
    Public Function GetUiDisplayName(Op As PortalOperator) As String
        Select Case Op
            Case PortalOperator.Telecom : Return "中国电信"
            Case PortalOperator.Mobile : Return "中国移动"
            Case PortalOperator.Unicom : Return "中国联通"
            Case PortalOperator.Office : Return "办公网"
            Case PortalOperator.Xhu : Return "校内网"
            Case Else : Return "未知网络类型"
        End Select
    End Function

    ''' <summary>全部运营商（顺序与枚举声明一致），供 UI 生成下拉框。</summary>
    Public Function GetAllOperators() As PortalOperator()
        Return New PortalOperator() {
            PortalOperator.Office,
            PortalOperator.Telecom,
            PortalOperator.Mobile,
            PortalOperator.Unicom,
            PortalOperator.Xhu
        }
    End Function

    ''' <summary>
    ''' 宽松解析运营商，依次尝试：枚举名、service 值、中文显示名（均忽略大小写）。
    ''' 便于配置迁移与 UI 回填。
    ''' </summary>
    Public Function TryParseOperator(Text As String, ByRef Result As PortalOperator) As Boolean
        Result = PortalOperator.Office
        If String.IsNullOrEmpty(Text) Then Return False
        Dim Clean As String = Text.Trim()
        If Clean.Length = 0 Then Return False

        For Each Op In GetAllOperators()
            If String.Equals(Op.ToString(), Clean, StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(GetServiceCode(Op), Clean, StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(GetDisplayName(Op), Clean, StringComparison.OrdinalIgnoreCase) Then
                Result = Op
                Return True
            End If
        Next
        Return False
    End Function

#End Region

#Region "数据模型"

    ''' <summary>用户配置层：只包含用户真正需要提供的信息。</summary>
    Public Class PortalAccount
        ''' <summary>学号。始终按字符串处理，绝不经过 Integer/Long/Double 转换。</summary>
        Public Property UserId As String
        ''' <summary>用户输入的明文密码。</summary>
        Public Property Password As String
        ''' <summary>用户选择的运营商。</summary>
        Public Property [Operator] As PortalOperator
        ''' <summary>验证码；仅当门户要求时才需要。</summary>
        Public Property ValidCode As String
    End Class

    ''' <summary>认证 Payload 层：与门户 login 接口字段一一对应。</summary>
    Public Class AuthPayload
        ''' <summary>学号原文（不含任何 @ 后缀）。</summary>
        Public Property UserId As String
        ''' <summary>最终提交的 password（门户要求加密时为密文，否则为明文）。</summary>
        Public Property Password As String
        ''' <summary>门户 service 值。</summary>
        Public Property Service As String
        ''' <summary>原始 queryString（未编码）。</summary>
        Public Property QueryString As String
        ''' <summary>运营商子账号密码，本阶段固定为 ""。</summary>
        Public Property OperatorPwd As String
        ''' <summary>运营商子账号，本阶段固定为 ""。</summary>
        Public Property OperatorUserId As String
        ''' <summary>验证码；门户未要求时为空串。</summary>
        Public Property ValidCode As String
        ''' <summary>是否加密密码，由门户 pageInfo 决定。</summary>
        Public Property PasswordEncrypt As Boolean

        ''' <summary>
        ''' 转换为门户 login 接口的字段字典。
        ''' 字段名与顺序对齐浏览器 Form Data；最终由 ModNetwork.EncodeFormData 编码一次。
        ''' </summary>
        Public Function ToLoginData() As Dictionary(Of String, String)
            Return New Dictionary(Of String, String) From {
                {"userId", If(UserId, "")},
                {"password", If(Password, "")},
                {"service", If(Service, "")},
                {"queryString", If(QueryString, "")},
                {"operatorPwd", If(OperatorPwd, "")},
                {"operatorUserId", If(OperatorUserId, "")},
                {"validcode", If(ValidCode, "")},
                {"passwordEncrypt", If(PasswordEncrypt, "true", "false")}
            }
        End Function
    End Class

    ''' <summary>login_data 的字段名（顺序即浏览器 Form Data 的顺序）。</summary>
    Public ReadOnly LoginDataKeys As String() = {
        "userId", "password", "service", "queryString",
        "operatorPwd", "operatorUserId", "validcode", "passwordEncrypt"
    }

#End Region

#Region "错误模型"

    ''' <summary>认证层可区分的失败原因，供 UI 直接映射为提示文案。</summary>
    Public Enum AuthFailure
        None = 0
        ''' <summary>学号为空。</summary>
        MissingUserId
        ''' <summary>密码为空。</summary>
        MissingPassword
        ''' <summary>运营商无效。</summary>
        InvalidOperator
        ''' <summary>PortalDiscoveryResult 不存在。</summary>
        MissingDiscovery
        ''' <summary>公钥缺失。</summary>
        MissingPublicKey
        ''' <summary>公钥非法。</summary>
        InvalidPublicKey
        ''' <summary>门户要求验证码但未提供。</summary>
        ValidCodeRequired
        ''' <summary>所选 service 当前不存在于门户服务列表。</summary>
        ServiceNotAvailable
        ''' <summary>密码加密失败。</summary>
        PasswordEncryptFailed
        ''' <summary>queryString 缺失。</summary>
        MissingQueryString
    End Enum

    ''' <summary>认证层异常。Message 可直接展示给用户。</summary>
    Public Class AuthException
        Inherits Exception

        Public ReadOnly Property Failure As AuthFailure

        Public Sub New(failure As AuthFailure, message As String)
            MyBase.New(message)
            Me.Failure = failure
        End Sub
    End Class

    Private Function Fail(ByRef Failure As AuthFailure, ByRef Message As String,
                          Code As AuthFailure, Text As String) As Boolean
        Failure = Code
        Message = Text
        Return False
    End Function

#End Region

#Region "核心：构造认证 Payload"

    ''' <summary>
    ''' 组装认证 Payload。失败时抛出 <see cref="AuthException"/>，其 Message 可直接展示。
    ''' </summary>
    Public Function Build(Account As PortalAccount, Discovery As PortalDiscoveryResult) As AuthPayload
        Dim Payload As AuthPayload = Nothing
        Dim Failure As AuthFailure = AuthFailure.None
        Dim Message As String = ""
        If TryBuild(Account, Discovery, Payload, Failure, Message) Then Return Payload
        Throw New AuthException(Failure, Message)
    End Function

    ''' <summary>非抛出式组装。失败时返回 False，并通过 Failure/Message 说明原因。</summary>
    Public Function TryBuild(Account As PortalAccount,
                             Discovery As PortalDiscoveryResult,
                             ByRef Payload As AuthPayload,
                             ByRef Failure As AuthFailure,
                             ByRef Message As String) As Boolean
        Payload = Nothing
        Failure = AuthFailure.None
        Message = ""

        ' ---- 1. 用户配置层校验 ----
        If Account Is Nothing Then
            Return Fail(Failure, Message, AuthFailure.MissingUserId, "账号信息为空，请填写学号与密码。")
        End If

        ' 门户 JavaScript 对用户名执行 username.trim()，对密码不 trim；此处保持一致。
        Dim UserId As String = If(Account.UserId, "").Trim()
        If UserId.Length = 0 Then
            Return Fail(Failure, Message, AuthFailure.MissingUserId, "学号不能为空。")
        End If

        If String.IsNullOrEmpty(Account.Password) Then
            Return Fail(Failure, Message, AuthFailure.MissingPassword, "密码不能为空。")
        End If

        ' ---- 2. 运行期门户数据校验 ----
        If Discovery Is Nothing Then
            Return Fail(Failure, Message, AuthFailure.MissingDiscovery,
                        "缺少门户发现结果，请先完成门户探测后再认证。")
        End If
        If Discovery.Redirect Is Nothing Then
            Return Fail(Failure, Message, AuthFailure.MissingQueryString,
                        "门户发现结果缺少重定向信息（queryString 不可用）。")
        End If

        Dim QueryString As String = If(Discovery.Redirect.QueryString, "")
        If QueryString.Length = 0 Then
            Return Fail(Failure, Message, AuthFailure.MissingQueryString,
                        "门户未提供 queryString，无法完成认证。")
        End If

        If Discovery.PageInfo Is Nothing Then
            Return Fail(Failure, Message, AuthFailure.MissingPublicKey,
                        "门户发现结果缺少 pageInfo，无法确定公钥与密码加密方式。")
        End If
        Dim PageInfo = Discovery.PageInfo

        ' ---- 3. 运营商映射 + 门户服务列表校验 ----
        Dim ServiceCode As String = ""
        Try
            ServiceCode = GetServiceCode(Account.[Operator])
        Catch ex As AuthException
            Return Fail(Failure, Message, ex.Failure, ex.Message)
        End Try

        If Not IsServiceAvailable(Discovery.Services, ServiceCode) Then
            Return Fail(Failure, Message, AuthFailure.ServiceNotAvailable,
                        "门户当前不提供所选运营商的服务（" & GetDisplayName(Account.[Operator]) &
                        " / " & ServiceCode & "）。门户提供的服务：" & DescribeServices(Discovery.Services))
        End If

        ' ---- 4. 验证码 ----
        Dim ValidCode As String = If(Account.ValidCode, "")
        If PageInfo.RequiresValidCode AndAlso ValidCode.Length = 0 Then
            Return Fail(Failure, Message, AuthFailure.ValidCodeRequired,
                        "门户当前需要验证码，请填写后重试。" &
                        If(String.IsNullOrEmpty(PageInfo.ValidCodeUrl), "",
                           "（验证码地址：" & PageInfo.ValidCodeUrl & "）"))
        End If
        If Not PageInfo.RequiresValidCode Then ValidCode = ""

        ' ---- 5. 密码处理（加密与否由门户决定） ----
        Dim FinalPassword As String = Account.Password
        If PageInfo.PasswordEncrypt Then
            Dim Reason As String = ""
            If String.IsNullOrEmpty(PageInfo.PublicKeyModulus) OrElse String.IsNullOrEmpty(PageInfo.PublicKeyExponent) Then
                Return Fail(Failure, Message, AuthFailure.MissingPublicKey,
                            "门户要求加密密码，但没有下发公钥。")
            End If
            If Not ModPortalDiscover.ValidatePublicKey(PageInfo.PublicKeyExponent, PageInfo.PublicKeyModulus, Reason) Then
                Return Fail(Failure, Message, AuthFailure.InvalidPublicKey,
                            "门户下发的公钥不可用：" & Reason)
            End If
            Try
                ' mac 的默认值由 ModCrypto 决定（Nothing / "" → "111111111"），本层不重复该逻辑。
                ModAuthTrace.CountPasswordEncryption()
                FinalPassword = ModCrypto.EncryptPassword(Account.Password,
                                                          Discovery.Redirect.Mac,
                                                          PageInfo.PublicKeyModulus,
                                                          PageInfo.PublicKeyExponent)
            Catch ex As Exception
                Return Fail(Failure, Message, AuthFailure.PasswordEncryptFailed,
                            "密码加密失败：" & ex.Message)
            End Try
        End If

        ' ---- 6. 组装 ----
        Payload = New AuthPayload With {
            .UserId = UserId,
            .Password = FinalPassword,
            .Service = ServiceCode,
            .QueryString = QueryString,
            .OperatorPwd = "",
            .OperatorUserId = "",
            .ValidCode = ValidCode,
            .PasswordEncrypt = PageInfo.PasswordEncrypt
        }
        Return True
    End Function

    ''' <summary>
    ''' 用门户实际返回的服务列表校验 service。
    ''' 列表不可用时跳过校验（此时无法判断），列表可用但缺少该服务时返回 False。
    ''' </summary>
    Private Function IsServiceAvailable(Services As PortalServices, ServiceCode As String) As Boolean
        If Services Is Nothing OrElse Services.Items Is Nothing OrElse Services.Items.Count = 0 Then Return True
        Return Services.FindByName(ServiceCode) IsNot Nothing
    End Function

    Private Function DescribeServices(Services As PortalServices) As String
        If Services Is Nothing OrElse Services.Items Is Nothing OrElse Services.Items.Count = 0 Then
            Return "(未知)"
        End If
        Dim Parts As New List(Of String)
        For Each Item In Services.Items
            Parts.Add(Item.Name & "/" & Item.DisplayName)
        Next
        Return String.Join("、", Parts.ToArray())
    End Function

#End Region

End Module
