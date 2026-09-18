Imports Microsoft.VisualBasic

''' <summary>
''' 动态校园网 Web 认证链路的总入口。
'''
''' 调用图（下一阶段改配置与 UI 的基础）：
'''
'''   Authenticate()
'''       │
'''       ├─ ModPortalDiscover.Discover()
'''       │      ├─ DiscoverRedirect()      读 BRAS 302 拿 queryString / mac
'''       │      ├─ FetchPageInfo()         拿公钥、passwordEncrypt、validCodeUrl
'''       │      └─ FetchServices()         拿运营商服务列表
'''       │
'''       ├─ ModAuth.Build()
'''       │      └─ ModCrypto.EncryptPassword()   明文密码 → 门户密文
'''       │
'''       ├─ AuthPayload.ToLoginData()
'''       │
'''       ├─ ModNetwork.LoginPayload()
'''       │      └─ PostJson() / EncodeFormData()   只编码一次
'''       │
'''       └─ PortalLoginResult
'''
''' 与旧路径的关系（两条路并存，互不影响）：
'''   旧：config.yml 的 login_data（抓包透传） → ModNetwork.Login(Cfg, Headers)
'''   新：用户只需学号/密码/运营商          → ModAuthentication.Authenticate(...)
''' 两条路径最终都落到同一个 PostJson，因此编码行为完全一致。
'''
''' 敏感数据约定：password 明文/密文、完整 queryString、userIndex 一律不得进入日志。
''' 需要输出时请使用 <see cref="DescribePayloadSafely"/>，不要自行拼接。
''' </summary>
Public Module ModAuthentication

#Region "结果模型"

    ''' <summary>
    ''' 门户 login 接口返回的强类型结果，字段与真实响应一一对应。
    ''' 成功判定严格只看 result=="success"，不看 HTTP 状态码或 userIndex。
    ''' </summary>
    Public Class PortalLoginResult
        ''' <summary>"success" / "fail" / "error"（"error" 为本项目 HTTP 层失败时的标记）。</summary>
        Public Property Result As String
        ''' <summary>门户返回的可读信息，失败时可直接展示。</summary>
        Public Property Message As String
        ''' <summary>在线会话标识。属敏感数据，不要写日志。</summary>
        Public Property UserIndex As String
        ''' <summary>门户字段名就是 forwordurl（门户自身的拼写）。</summary>
        Public Property ForwardUrl As String
        Public Property KeepaliveInterval As Integer
        ''' <summary>非空表示门户开始要求验证码。</summary>
        Public Property ValidCodeUrl As String

        ''' <summary>严格按门户协议判定成功。</summary>
        Public ReadOnly Property IsSuccess As Boolean
            Get
                Return String.Equals(Result, "success", StringComparison.Ordinal)
            End Get
        End Property

    End Class

    ''' <summary>
    ''' 认证链路的统一返回。UI 只需要处理这一个对象。
    ''' 拆到 Dictionary 里会让上层重新做类型判断，因此这里全部强类型。
    ''' </summary>
    Public Class AuthenticationResult
        ''' <summary>
        ''' 是否成功。
        ''' 注意：当 <see cref="Payload"/> 非空而 <see cref="LoginResult"/> 为 Nothing 时，
        ''' 表示“认证参数已就绪但尚未提交登录请求”（由 PreparePayload 返回）。
        ''' </summary>
        Public Property Success As Boolean
        ''' <summary>可直接展示给用户的说明。</summary>
        Public Property Message As String
        ''' <summary>门户发现结果（无论成功失败都尽量保留，便于排查）。</summary>
        Public Property Discovery As ModPortalDiscover.PortalDiscoveryResult
        ''' <summary>构造好的认证参数；未走到该步骤时为 Nothing。</summary>
        Public Property Payload As AuthPayload
        ''' <summary>登录响应（强类型）；未提交登录时为 Nothing。</summary>
        Public Property LoginResult As PortalLoginResult
        ''' <summary>登录响应的原始 JSON（仅用于调试，消费方请用 LoginResult）。</summary>
        Public Property Response As Dictionary(Of String, Object)
        ''' <summary>门户发现状态原样保留。</summary>
        Public Property DiscoveryStatus As ModPortalDiscover.PortalDiscoveryStatus
        ''' <summary>认证层失败原因；未失败时为 None。</summary>
        Public Property Failure As AuthFailure = AuthFailure.None
    End Class

    ''' <summary>诊断链路上的单个阶段（用于快速定位问题出在哪一层）。</summary>
    Public Class PipelineStage
        Public Property Name As String
        Public Property Ok As Boolean
        ''' <summary>该阶段本轮不适用（例如已联网时无需检查公钥/服务）。</summary>
        Public Property Skipped As Boolean
        Public Property Detail As String
    End Class

#End Region

#Region "统一认证入口"

    ''' <summary>
    ''' 完整动态认证：发现门户 → 构造 payload → 提交登录 → 解析结果。
    ''' 任何一步失败都返回 Success=False 并带可展示的 Message，不抛异常给 UI。
    '''
    ''' 带并发保护：手动连接与自动重连共用这一道闸门，同一时刻只会发出一个 login 请求。
    ''' </summary>
    Public Function Authenticate(Account As PortalAccount,
                                 Optional ProbeUrl As String = ModPortalDiscover.DefaultProbeUrl,
                                 Optional Server As String = "",
                                 Optional Timeout As Integer = 10,
                                 Optional BindAddress As String = "",
                                 Optional Cookie As String = "") As AuthenticationResult

        If Not TryBeginAuthentication() Then
            Return New AuthenticationResult With {
                .Success = False,
                .Message = "已有认证正在进行中，请稍候。",
                .DiscoveryStatus = ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication
            }
        End If

        Try
            ' ---- 1. 门户发现（可能抛 PortalDiscoveryException，这里统一转成结果对象）----
            Dim Discovery As ModPortalDiscover.PortalDiscoveryResult = Nothing
            Try
                Discovery = ModPortalDiscover.Discover(ProbeUrl, Server, Timeout, BindAddress)
            Catch ex As ModPortalDiscover.PortalDiscoveryException
                Return New AuthenticationResult With {
                    .Success = False,
                    .Message = ex.Message,
                    .DiscoveryStatus = ex.Status,
                    .Failure = AuthFailure.MissingDiscovery
                }
            Catch ex As Exception
                Return New AuthenticationResult With {
                    .Success = False,
                    .Message = "门户探测失败：" & ex.Message,
                    .DiscoveryStatus = ModPortalDiscover.PortalDiscoveryStatus.BadResponse,
                    .Failure = AuthFailure.MissingDiscovery
                }
            End Try

            Return AuthenticateWithDiscovery(Account, Discovery, Server, Timeout, Cookie)
        Finally
            EndAuthentication()
        End Try
    End Function

    ''' <summary>
    ''' 已经有 PortalDiscoveryResult 时继续完成认证（供测试与复用调用）。
    ''' 发现结果为 AlreadyOnline 时直接成功返回，不会发出登录请求。
    ''' </summary>
    Public Function AuthenticateWithDiscovery(Account As PortalAccount,
                                              Discovery As ModPortalDiscover.PortalDiscoveryResult,
                                              Optional Server As String = "",
                                              Optional Timeout As Integer = 10,
                                              Optional Cookie As String = "") As AuthenticationResult

        Dim Prepared As AuthenticationResult = PreparePayload(Account, Discovery)
        If Not Prepared.Success Then Return Prepared
        ' Payload 为空表示无需登录（已联网），直接返回
        If Prepared.Payload Is Nothing Then Return Prepared

        ' ---- 提交登录 ----
        Dim EffectiveServer As String = ResolveServer(Server, Discovery)
        Dim Headers As Dictionary(Of String, String) =
            BuildLoginHeaders(EffectiveServer, Prepared.Payload.QueryString, Cookie)

        Dim Response As Dictionary(Of String, Object) = Nothing
        Try
            Response = ModNetwork.LoginPayload(Prepared.Payload, EffectiveServer, Headers, Timeout)
        Catch ex As Exception
            Prepared.Success = False
            Prepared.Message = "登录请求发送失败：" & ex.Message
            Prepared.Failure = AuthFailure.MissingDiscovery
            Return Prepared
        End Try

        Prepared.Response = Response
        Prepared.LoginResult = ParseLoginResult(Response)
        Prepared.Success = Prepared.LoginResult.IsSuccess
        ' 成功判定严格只看 result；失败时把门户的 message 原样交给上层
        Prepared.Message = If(Prepared.Success,
                              If(String.IsNullOrEmpty(Prepared.LoginResult.Message), "认证成功", Prepared.LoginResult.Message),
                              If(String.IsNullOrEmpty(Prepared.LoginResult.Message), "认证失败", Prepared.LoginResult.Message))

        ' 登录成功后记住会话，供“断开”使用（只存内存，不写配置、不写日志）
        If Prepared.Success Then
            RememberSession(Prepared.LoginResult.UserIndex, EffectiveServer)
        End If
        Return Prepared
    End Function

    ''' <summary>
    ''' 只构造认证参数，不发任何网络请求。<see cref="AuthenticateWithDiscovery"/> 的第一步。
    '''
    ''' 返回值语义：
    '''   Success=False                        —— 失败，Message 说明原因，Failure 给出分类
    '''   Success=True 且 Payload=Nothing      —— 无需认证（当前已联网）
    '''   Success=True 且 Payload 非空         —— 参数就绪，可以提交登录
    ''' </summary>
    Public Function PreparePayload(Account As PortalAccount,
                                   Discovery As ModPortalDiscover.PortalDiscoveryResult) As AuthenticationResult
        Dim Result As New AuthenticationResult With {
            .Discovery = Discovery,
            .DiscoveryStatus = If(Discovery Is Nothing,
                                  ModPortalDiscover.PortalDiscoveryStatus.NoRedirect,
                                  Discovery.Status)
        }

        If Discovery Is Nothing Then
            Result.Success = False
            Result.Failure = AuthFailure.MissingDiscovery
            Result.Message = "缺少门户发现结果，无法认证。"
            Return Result
        End If

        ' ---- 情况 B：已经联网，不需要认证 ----
        If Discovery.Status = ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline Then
            Result.Success = True
            Result.Message = "当前已经联网"
            Return Result
        End If

        ' ---- 其余非 NeedAuthentication 状态一律失败，且不构造 login payload ----
        If Discovery.Status <> ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication Then
            Result.Success = False
            Result.Failure = AuthFailure.MissingDiscovery
            Result.Message = If(String.IsNullOrEmpty(Discovery.Message),
                                "门户发现失败（" & Discovery.Status.ToString() & "）。",
                                Discovery.Message)
            Return Result
        End If

        ' ---- 构造 payload（ModAuth 内部会做公钥、服务、验证码、加密等全部校验）----
        Dim Payload As AuthPayload = Nothing
        Dim Failure As AuthFailure = AuthFailure.None
        Dim Message As String = ""
        If Not ModAuth.TryBuild(Account, Discovery, Payload, Failure, Message) Then
            Result.Success = False
            Result.Failure = Failure
            Result.Message = Message
            Return Result
        End If

        Result.Success = True
        Result.Payload = Payload
        Result.Message = "认证参数已就绪。"
        Return Result
    End Function

#End Region

#Region "请求头构造"

    ''' <summary>
    ''' 构造动态认证所需的请求头。
    ''' Referer 由当前 queryString 动态生成，避免沿用旧配置里过期的那一条。
    ''' </summary>
    Public Function BuildLoginHeaders(Server As String,
                                      QueryString As String,
                                      Optional Cookie As String = "") As Dictionary(Of String, String)
        Dim Base As String = If(Server, "").TrimEnd("/"c)
        Dim Headers As New Dictionary(Of String, String) From {
            {"Content-Type", "application/x-www-form-urlencoded; charset=UTF-8"},
            {"Accept", "*/*"},
            {"User-Agent", ModNetwork.DefaultUserAgent},
            {"Origin", Base}
        }

        ' queryString 为空时不要拼出一个悬空的 '?'；调用方本应在此之前就已失败
        If String.IsNullOrEmpty(QueryString) Then
            Headers("Referer") = Base & ModPortalDiscover.IndexPath
        Else
            Headers("Referer") = Base & ModPortalDiscover.IndexPath & "?" & QueryString
        End If

        ' Cookie 是旧机制的遗留项，动态链路正常不需要，需要时由调用方传入
        If Not String.IsNullOrEmpty(Cookie) Then Headers("Cookie") = Cookie

        Return Headers
    End Function

    Private Function ResolveServer(Server As String, Discovery As ModPortalDiscover.PortalDiscoveryResult) As String
        If Not String.IsNullOrEmpty(Server) Then Return Server.TrimEnd("/"c)
        If Discovery IsNot Nothing AndAlso Discovery.Redirect IsNot Nothing Then
            Dim FromRedirect As String = ModPortalDiscover.GetOrigin(Discovery.Redirect.PortalUrl)
            If Not String.IsNullOrEmpty(FromRedirect) Then Return FromRedirect
        End If
        Return ModPortalDiscover.DefaultServer
    End Function

#End Region

#Region "登录响应解析"

    ''' <summary>
    ''' 把 login 的原始 JSON 转成强类型结果。
    ''' 注意门户字段名是 "forwordurl"（门户自身的拼写），不要“纠正”成 forwardurl。
    ''' </summary>
    Public Function ParseLoginResult(Response As Dictionary(Of String, Object)) As PortalLoginResult
        Dim Result As New PortalLoginResult()
        If Response Is Nothing Then
            Result.Result = "error"
            Result.Message = "登录请求没有返回任何内容。"
            Return Result
        End If

        Result.Result = GetDictStr(Response, "result")
        Result.Message = GetDictStr(Response, "message")
        Result.UserIndex = GetDictStr(Response, "userIndex")
        Result.ForwardUrl = GetDictStr(Response, "forwordurl")
        Result.KeepaliveInterval = GetDictInt(Response, "keepaliveInterval", 0)
        Result.ValidCodeUrl = GetDictStr(Response, "validCodeUrl")
        Return Result
    End Function

#End Region

#Region "诊断与安全输出"

    ''' <summary>
    ''' 逐段体检认证链路，用来快速判断问题出在
    ''' PortalDiscovery / 公钥 / 服务列表 / 密码加密 / Payload 中的哪一层。
    ''' 完全离线：只用传入的 Discovery，不发网络请求、不做真实登录。
    ''' </summary>
    Public Function DiagnosePipeline(Account As PortalAccount,
                                     Discovery As ModPortalDiscover.PortalDiscoveryResult) As List(Of PipelineStage)
        Dim Stages As New List(Of PipelineStage)

        ' 已联网时不需要（也无法）检查公钥/服务/加密：这些阶段标记为 Skipped 而不是失败
        Dim AlreadyOnline As Boolean = Discovery IsNot Nothing AndAlso
            Discovery.Status = ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline
        Const SkipDetail As String = "已联网，无需检查"

        ' ---- Discovery ----
        Dim DiscoveryOk As Boolean = Discovery IsNot Nothing AndAlso
            (Discovery.Status = ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication OrElse
             Discovery.Status = ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline)
        Stages.Add(New PipelineStage With {
            .Name = "Discovery",
            .Ok = DiscoveryOk,
            .Detail = If(Discovery Is Nothing, "无发现结果", Discovery.Status.ToString())
        })

        ' ---- 公钥 ----
        Dim PublicKeyOk As Boolean = False
        Dim PublicKeyDetail As String = "无 pageInfo"
        Dim PublicKeySkipped As Boolean = False
        If Discovery IsNot Nothing AndAlso Discovery.PageInfo IsNot Nothing Then
            Dim PageInfo = Discovery.PageInfo
            If Not PageInfo.PasswordEncrypt Then
                PublicKeyOk = True
                PublicKeyDetail = "门户不要求加密，无需公钥"
            Else
                Dim Reason As String = ""
                PublicKeyOk = ModPortalDiscover.ValidatePublicKey(PageInfo.PublicKeyExponent, PageInfo.PublicKeyModulus, Reason)
                PublicKeyDetail = If(PublicKeyOk,
                                     "exponent=" & PageInfo.PublicKeyExponent & " modulus=" & PageInfo.PublicKeyModulus.Length & " hex",
                                     Reason)
            End If
        ElseIf AlreadyOnline Then
            PublicKeyOk = True
            PublicKeySkipped = True
            PublicKeyDetail = SkipDetail
        End If
        Stages.Add(New PipelineStage With {.Name = "PublicKey", .Ok = PublicKeyOk,
                                           .Skipped = PublicKeySkipped, .Detail = PublicKeyDetail})

        ' ---- Services ----
        Dim ServicesOk As Boolean = Discovery IsNot Nothing AndAlso
                                    Discovery.Services IsNot Nothing AndAlso
                                    Discovery.Services.Items IsNot Nothing AndAlso
                                    Discovery.Services.Items.Count > 0
        Dim ServicesSkipped As Boolean = False
        Dim ServicesDetail As String = If(ServicesOk,
                                          Discovery.Services.Items.Count & " 项, 默认=" & If(Discovery.Services.DefaultName, "(无)"),
                                          "服务列表不可用")
        If Not ServicesOk AndAlso AlreadyOnline Then
            ServicesOk = True
            ServicesSkipped = True
            ServicesDetail = SkipDetail
        End If
        Stages.Add(New PipelineStage With {.Name = "Services", .Ok = ServicesOk,
                                           .Skipped = ServicesSkipped, .Detail = ServicesDetail})

        ' ---- PasswordEncryption ----
        Dim EncryptOk As Boolean = False
        Dim EncryptDetail As String = "未执行"
        Dim EncryptSkipped As Boolean = False
        If Discovery IsNot Nothing AndAlso Discovery.PageInfo IsNot Nothing AndAlso
           Discovery.PageInfo.PasswordEncrypt AndAlso PublicKeyOk AndAlso Account IsNot Nothing Then
            Try
                Dim Cipher = ModCrypto.EncryptPassword(Account.Password,
                                                       If(Discovery.Redirect Is Nothing, Nothing, Discovery.Redirect.Mac),
                                                       Discovery.PageInfo.PublicKeyModulus,
                                                       Discovery.PageInfo.PublicKeyExponent)
                EncryptOk = Not String.IsNullOrEmpty(Cipher)
                EncryptDetail = "密文长度 " & Cipher.Length
            Catch ex As Exception
                EncryptDetail = ex.Message
            End Try
        ElseIf Discovery IsNot Nothing AndAlso Discovery.PageInfo IsNot Nothing AndAlso Not Discovery.PageInfo.PasswordEncrypt Then
            EncryptOk = True
            EncryptDetail = "门户不要求加密（明文提交）"
        ElseIf AlreadyOnline Then
            EncryptOk = True
            EncryptSkipped = True
            EncryptDetail = SkipDetail
        End If
        Stages.Add(New PipelineStage With {.Name = "PasswordEncryption", .Ok = EncryptOk,
                                           .Skipped = EncryptSkipped, .Detail = EncryptDetail})

        ' ---- Payload（只构造，不提交）----
        Dim PayloadOk As Boolean = False
        Dim PayloadDetail As String = "未执行"
        Dim Prepared = PreparePayload(Account, Discovery)
        If Prepared.Success AndAlso Prepared.Payload Is Nothing Then
            PayloadOk = True
            PayloadDetail = "无需认证（已联网）"
        ElseIf Prepared.Success Then
            PayloadOk = True
            PayloadDetail = DescribePayloadSafely(Prepared.Payload)
        Else
            PayloadDetail = Prepared.Failure.ToString() & ": " & Prepared.Message
        End If
        Stages.Add(New PipelineStage With {.Name = "Payload", .Ok = PayloadOk, .Detail = PayloadDetail})

        Return Stages
    End Function

    ''' <summary>学号打码：保留首 4 末 4，其余用 *** 代替；过短则整体打码。</summary>
    Public Function MaskUserId(UserId As String) As String
        If String.IsNullOrEmpty(UserId) Then Return "(空)"
        If UserId.Length <= 8 Then Return New String("*"c, UserId.Length)
        Return UserId.Substring(0, 4) & "***" & UserId.Substring(UserId.Length - 4)
    End Function

    ''' <summary>
    ''' 生成可安全写入日志的 payload 摘要。
    ''' 不含 password（明文或密文）、不含完整 queryString、不含 userIndex。
    ''' </summary>
    Public Function DescribePayloadSafely(Payload As AuthPayload) As String
        If Payload Is Nothing Then Return "Payload=(无)"
        Return "UserId=" & MaskUserId(Payload.UserId) &
               " Service=" & If(Payload.Service, "(空)") &
               " Password=<redacted>" &
               " PasswordEncrypt=" & Payload.PasswordEncrypt &
               " ValidCode=" & If(String.IsNullOrEmpty(Payload.ValidCode), "(空)", "<redacted>") &
               " QueryString=<redacted:" & If(Payload.QueryString, "").Length & " chars>"
    End Function

#End Region

#Region "运行时认证上下文"

    ''' <summary>
    ''' 一次认证所需的运行期参数集合。由 AppConfig 构造，完全不依赖旧的 login_data。
    ''' UI / NetworkMonitor / 诊断入口都从这里取参数。
    ''' </summary>
    Public Class RuntimeAuthContext
        Public Property Account As PortalAccount
        ''' <summary>门户基地址（西华大学固定常量）。</summary>
        Public Property Server As String = ModPortalDiscover.DefaultServer
        ''' <summary>用于触发 BRAS 门户劫持的探测地址。</summary>
        Public Property ProbeUrl As String = ModPortalDiscover.DefaultProbeUrl
        Public Property Timeout As Integer = 10
        ''' <summary>需要绕过 VPN/TUN 时绑定的本机地址；普通用户留空。</summary>
        Public Property BindAddress As String = ""
        ''' <summary>非空表示配置不可用于认证，内容可直接展示给用户。</summary>
        Public Property ErrorMessage As String = ""

        Public ReadOnly Property IsValid As Boolean
            Get
                Return ErrorMessage.Length = 0 AndAlso Account IsNot Nothing
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 从 AppConfig 构造运行时认证上下文。
    ''' 配置不完整（学号为空 / 密码未保存或无法解密 / 运营商无效）时通过 Error 给出明确原因，
    ''' 而不是等发出无效 HTTP 请求后才失败。
    ''' </summary>
    Public Function BuildRuntimeAuthContext(Optional Config As AppConfig = Nothing,
                                            Optional ValidCode As String = "",
                                            Optional BindAddress As String = "",
                                            Optional Timeout As Integer = 10) As RuntimeAuthContext
        Dim Result As New RuntimeAuthContext With {
            .Server = ModConfig.SchoolServer,
            .ProbeUrl = ModPortalDiscover.DefaultProbeUrl,
            .Timeout = Timeout,
            .BindAddress = If(BindAddress, "")
        }

        ' 没有显式指定出口时自动解析。
        ' 这一步是必须的：系统默认路由经常被 VPN / Clash TUN 抢走，
        ' 已认证时看不出问题，未认证时请求会被虚拟网卡掐断，
        ' 表现成「网络探测失败」，从而把整个认证卡死。
        ' GUI「连接」与 NetworkMonitor 自动重连都经过这里，因此只需在这一处修。
        If Result.BindAddress.Length = 0 Then
            Try
                Result.BindAddress = ModNetwork.ResolveBindAddress(Result.Server)
            Catch
                ' 解析失败就保持为空，退回默认路由 —— 至少还能试一次
            End Try
        End If

        Dim Effective As AppConfig = If(Config, ModConfig.LoadAppConfig())
        If Effective Is Nothing Then
            Result.ErrorMessage = "配置为空，请重新填写。"
            Return Result
        End If

        Dim Reason As String = Effective.NotReadyReason
        If Reason.Length > 0 Then
            Result.ErrorMessage = Reason
            Return Result
        End If

        Result.Account = Effective.ToPortalAccount(ValidCode)
        Return Result
    End Function

#End Region

#Region "并发保护"

    ' 手动连接与自动重连可能同时触发。这里用模块级互斥保证同一时刻只发出一个 login 请求。
    Private ReadOnly AuthGate As New Object()
    Private _Authenticating As Boolean = False

    ''' <summary>当前是否有认证正在进行。</summary>
    Public ReadOnly Property IsAuthenticating As Boolean
        Get
            SyncLock AuthGate
                Return _Authenticating
            End SyncLock
        End Get
    End Property

    ''' <summary>尝试占住认证闸门；返回 False 表示已有认证在进行。</summary>
    Public Function TryBeginAuthentication() As Boolean
        SyncLock AuthGate
            If _Authenticating Then Return False
            _Authenticating = True
            Return True
        End SyncLock
    End Function

    ''' <summary>释放认证闸门。</summary>
    Public Sub EndAuthentication()
        SyncLock AuthGate
            _Authenticating = False
        End SyncLock
    End Sub

#End Region

#Region "会话与断开"

    ' 登录成功后的会话信息，只存在于内存，不写入配置、不写日志。
    Private ReadOnly SessionGate As New Object()
    Private _SessionUserIndex As String = ""
    Private _SessionServer As String = ""

    ''' <summary>当前会话的 userIndex；为空表示本进程尚未建立会话。属敏感数据，勿写日志。</summary>
    Public ReadOnly Property CurrentUserIndex As String
        Get
            SyncLock SessionGate
                Return _SessionUserIndex
            End SyncLock
        End Get
    End Property

    ''' <summary>是否持有可用于断开的会话。</summary>
    Public ReadOnly Property HasSession As Boolean
        Get
            Return CurrentUserIndex.Length > 0
        End Get
    End Property

    ''' <summary>清除会话。</summary>
    Public Sub ClearSession()
        SyncLock SessionGate
            _SessionUserIndex = ""
            _SessionServer = ""
        End SyncLock
    End Sub

    Private Sub RememberSession(UserIndex As String, Server As String)
        If String.IsNullOrEmpty(UserIndex) Then Return
        SyncLock SessionGate
            _SessionUserIndex = UserIndex
            _SessionServer = Server
        End SyncLock
    End Sub

    ''' <summary>断开结果分类。</summary>
    Public Enum PortalLogoutStatus
        ''' <summary>门户接受了断开请求。</summary>
        Success
        ''' <summary>门户拒绝了断开请求。</summary>
        Failed
        ''' <summary>本进程没有可用会话，无法断开。</summary>
        NoSession
    End Enum

    Public Class LogoutResult
        Public Property Status As PortalLogoutStatus = PortalLogoutStatus.NoSession
        Public Property Message As String = ""
        ''' <summary>门户原始响应（调试用）。</summary>
        Public Property Raw As Dictionary(Of String, Object)

        Public ReadOnly Property Success As Boolean
            Get
                Return Status = PortalLogoutStatus.Success
            End Get
        End Property
    End Class

    ''' <summary>门户断开接口路径（相对于 Server）。</summary>
    Public Const PortalLogoutPath As String = "/eportal/InterFace.do?method=logout"

    ''' <summary>
    ''' 用当前运行时会话断开连接。
    '''
    ''' 门户协议依据（AuthInterFace.js 实测）：
    '''     logout : function(userIndex, callback) {
    '''         var content = "userIndex=" + userIndex;
    '''         post(ePortalUrl + "logout", content, callback);
    '''     }
    ''' 即 **logout 只需要 userIndex**，不需要 queryString / password / service。
    ''' 旧的 logout_data 一直是空字典，所以旧断开始终发出的是无效请求。
    ''' </summary>
    Public Function LogoutAuthenticated(Optional Server As String = "",
                                        Optional Timeout As Integer = 10,
                                        Optional Cookie As String = "") As LogoutResult
        Dim Result As New LogoutResult()
        Dim UserIndex As String = CurrentUserIndex
        Dim EffectiveServer As String = If(String.IsNullOrEmpty(Server), _SessionServer, Server)
        If String.IsNullOrEmpty(EffectiveServer) Then EffectiveServer = ModConfig.SchoolServer
        EffectiveServer = EffectiveServer.TrimEnd("/"c)

        If UserIndex.Length = 0 Then
            ' 本进程没登录过；尝试从门户当前会话问出 userIndex（程序重启后仍可用）
            UserIndex = TryDiscoverCurrentUserIndex(EffectiveServer, Timeout)
        End If

        If UserIndex.Length = 0 Then
            Result.Status = PortalLogoutStatus.NoSession
            Result.Message = "当前没有可用的登录会话，无法断开。请先连接，或在浏览器中退出认证。"
            Return Result
        End If

        Dim Headers As New Dictionary(Of String, String) From {
            {"Content-Type", "application/x-www-form-urlencoded; charset=UTF-8"},
            {"Accept", "*/*"},
            {"User-Agent", ModNetwork.DefaultUserAgent},
            {"Origin", EffectiveServer}
        }
        If Not String.IsNullOrEmpty(Cookie) Then Headers("Cookie") = Cookie

        Dim Response As Dictionary(Of String, Object) = Nothing
        Try
            Response = ModNetwork.PostJson(EffectiveServer & PortalLogoutPath,
                                           New Dictionary(Of String, String) From {{"userIndex", UserIndex}},
                                           Headers, Timeout)
        Catch ex As Exception
            Result.Status = PortalLogoutStatus.Failed
            Result.Message = "断开请求发送失败：" & ex.Message
            Return Result
        End Try

        Result.Raw = Response
        Dim Parsed As PortalLoginResult = ParseLoginResult(Response)
        If Parsed.IsSuccess Then
            Result.Status = PortalLogoutStatus.Success
            Result.Message = If(String.IsNullOrEmpty(Parsed.Message), "断开成功", Parsed.Message)
            ClearSession()
        Else
            Result.Status = PortalLogoutStatus.Failed
            Result.Message = If(String.IsNullOrEmpty(Parsed.Message), "断开失败", Parsed.Message)
        End If
        Return Result
    End Function

    ''' <summary>
    ''' 从门户当前的认证成功页问出本机会话的 userIndex（程序重启后仍可用）。
    ''' 已联网时 GET /eportal/redirectortosuccess.jsp 会 302 到
    ''' success.jsp?userIndex=...（第二阶段实测行为）。
    ''' </summary>
    Public Function TryDiscoverCurrentUserIndex(Server As String, Optional Timeout As Integer = 10) As String
        Try
            Dim Url As String = Server.TrimEnd("/"c) & "/eportal/redirectortosuccess.jsp"
            Dim Request As Net.HttpWebRequest = CType(Net.WebRequest.Create(Url), Net.HttpWebRequest)
            Request.Method = "GET"
            Request.AllowAutoRedirect = False
            Request.Timeout = Math.Max(1, Timeout) * 1000
            Request.UserAgent = ModNetwork.DefaultUserAgent

            Using Response As Net.HttpWebResponse = CType(Request.GetResponse(), Net.HttpWebResponse)
                Return ExtractUserIndex(If(Response.Headers("Location"), ""))
            End Using
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>从 success.jsp?userIndex=... 形式的 URL 中取出 userIndex。属敏感数据，勿写日志。</summary>
    Public Function ExtractUserIndex(Url As String) As String
        If String.IsNullOrEmpty(Url) Then Return ""
        Dim Match = System.Text.RegularExpressions.Regex.Match(Url, "userIndex=([^&\s]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        If Match.Success Then Return Match.Groups(1).Value
        Return ""
    End Function

#End Region

#Region "运行时诊断"

    ''' <summary>
    ''' 面向真实环境的完整诊断：
    ''' 配置 → 门户发现 → 公钥 → 服务列表 → 认证参数 → 密码加密 → 登录。
    ''' 输出中所有敏感值都以 已配置 / 已发现 / OK / FAIL 表示，不含明文。
    ''' </summary>
    Public Function DiagnoseRuntime(Optional BindAddress As String = "",
                                    Optional Timeout As Integer = 10,
                                    Optional SkipLogin As Boolean = False,
                                    Optional ConfigPath As String = "") As List(Of PipelineStage)
        Dim Stages As New List(Of PipelineStage)

        ' ---- 配置 ----
        Dim Cfg As AppConfig = Nothing
        Try
            ' ConfigPath 为空时使用程序目录下的 config.yml；显式传入便于诊断工具与测试
            Cfg = If(String.IsNullOrEmpty(ConfigPath), ModConfig.LoadAppConfig(), ModConfig.LoadAppConfigFrom(ConfigPath))
        Catch ex As Exception
            Stages.Add(New PipelineStage With {.Name = "配置", .Ok = False, .Detail = "读取失败：" & ex.Message})
            Return Stages
        End Try

        Stages.Add(New PipelineStage With {
            .Name = "配置",
            .Ok = True,
            .Detail = "学号=" & If(String.IsNullOrEmpty(Cfg.User.UserId), "未配置", "已配置") &
                      " 密码=" & If(String.IsNullOrEmpty(Cfg.User.Password), "未配置", "已配置") &
                      " 运营商=" & ModConfig.GetOperatorToken(Cfg.User.[Operator]) &
                      " 自动重连=" & Cfg.[Function].AutoReconnect
        })

        ' 配置不完整时单独成一项：不要把「账户/配置问题」伪装成「门户发现失败」
        Dim Reason As String = Cfg.NotReadyReason
        If Reason.Length > 0 Then
            Stages.Add(New PipelineStage With {.Name = "可认证性", .Ok = False, .Detail = Reason})
            Stages.Add(New PipelineStage With {.Name = "门户发现", .Ok = True, .Skipped = True, .Detail = "配置未就绪，未执行"})
            Stages.Add(New PipelineStage With {.Name = "登录", .Ok = True, .Skipped = True, .Detail = "配置未就绪，未执行"})
            Return Stages
        End If

        ' ---- 门户发现 ----
        Dim Discovery As ModPortalDiscover.PortalDiscoveryResult = Nothing
        Try
            Discovery = ModPortalDiscover.Discover(ModPortalDiscover.DefaultProbeUrl,
                                                   ModConfig.SchoolServer, Timeout, BindAddress)
        Catch ex As ModPortalDiscover.PortalDiscoveryException
            Stages.Add(New PipelineStage With {.Name = "门户发现", .Ok = False,
                                               .Detail = ex.Status.ToString() & "：" & ex.Message})
            Return Stages
        Catch ex As Exception
            Stages.Add(New PipelineStage With {.Name = "门户发现", .Ok = False, .Detail = "异常：" & ex.Message})
            Return Stages
        End Try

        Dim AlreadyOnline As Boolean = Discovery IsNot Nothing AndAlso
            Discovery.Status = ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline
        Stages.Add(New PipelineStage With {
            .Name = "门户发现",
            .Ok = Discovery IsNot Nothing AndAlso Discovery.IsSuccess,
            .Skipped = AlreadyOnline,
            .Detail = If(Discovery Is Nothing, "无结果", Discovery.Status.ToString()) &
                      " QueryString=" & If(Discovery IsNot Nothing AndAlso Discovery.Redirect IsNot Nothing AndAlso
                                           Discovery.Redirect.QueryString.Length > 0, "已发现", "未发现") &
                      " 公钥=" & If(Discovery IsNot Nothing AndAlso Discovery.PageInfo IsNot Nothing AndAlso
                                    Discovery.PageInfo.PublicKeyModulus.Length > 0, "OK", "无") &
                      " 服务=" & If(Discovery IsNot Nothing AndAlso Discovery.Services IsNot Nothing, "OK", "无")
        })
        If Discovery Is Nothing OrElse Not Discovery.IsSuccess Then Return Stages

        ' 其余阶段复用离线诊断（跳过它自己的 Discovery 项）
        For Each Stage In DiagnosePipeline(Cfg.ToPortalAccount(), Discovery)
            If Stage.Name <> "Discovery" Then Stages.Add(Stage)
        Next

        ' ---- 登录 ----
        If SkipLogin OrElse AlreadyOnline Then
            Stages.Add(New PipelineStage With {.Name = "登录", .Ok = True, .Skipped = True,
                                               .Detail = If(AlreadyOnline, "未执行（已联网）", "未执行（调用方要求跳过）")})
        Else
            Dim LoginResult As AuthenticationResult = AuthenticateWithDiscovery(
                Cfg.ToPortalAccount(), Discovery, ModConfig.SchoolServer, Timeout)
            Stages.Add(New PipelineStage With {
                .Name = "登录",
                .Ok = LoginResult.Success,
                .Detail = If(LoginResult.Message, "") &
                          If(LoginResult.LoginResult Is Nothing, "",
                             " [result=" & If(LoginResult.LoginResult.Result, "") & "]")
            })
        End If

        Return Stages
    End Function

#End Region

#Region "失败分类（供 UI 与监控日志使用）"

    ''' <summary>
    ''' 把认证结果映射成可读的失败说明，并区分四类问题：
    ''' 网络问题 / 认证参数问题 / 账户问题 / 门户问题。
    ''' 不包含任何敏感值，可直接写入日志或展示给用户。
    ''' </summary>
    Public Function DescribeFailure(Result As AuthenticationResult) As String
        If Result Is Nothing Then Return "认证失败：没有结果。"
        If Result.Success Then Return "认证成功。"

        ' 先看门户发现阶段的问题（网络层）
        Select Case Result.DiscoveryStatus
            Case ModPortalDiscover.PortalDiscoveryStatus.NoUsableInterface
                Return "没有找到可访问校园认证门户的网络接口，请确认已连接校园网（有线/无线）。"
            Case ModPortalDiscover.PortalDiscoveryStatus.ProbeFailed
                Return "当前网络探测失败，请检查校园网连接。"
            Case ModPortalDiscover.PortalDiscoveryStatus.Unreachable
                Return "校园认证门户当前不可达，请稍后重试或确认已接入校园网。"
            Case ModPortalDiscover.PortalDiscoveryStatus.PortalRequestFailed
                Return "门户认证参数获取失败：" & Result.Message
            Case ModPortalDiscover.PortalDiscoveryStatus.Timeout
                Return "网络探测超时，请检查当前网络连接。"
            Case ModPortalDiscover.PortalDiscoveryStatus.NoRedirect
                Return "网络问题：未检测到门户重定向。"
            Case ModPortalDiscover.PortalDiscoveryStatus.MissingParameters
                Return "认证参数问题：门户登录页缺少必需参数。"
            Case ModPortalDiscover.PortalDiscoveryStatus.InvalidLocation
                Return "认证参数问题：门户重定向地址无效。"
            Case ModPortalDiscover.PortalDiscoveryStatus.BadResponse
                Return "门户问题：门户响应格式异常。"
        End Select

        ' 再看认证层的问题（账户 / 参数 / 门户）
        Select Case Result.Failure
            Case AuthFailure.MissingUserId
                Return "账户问题：本地没有保存学号，请在配置页填写。"
            Case AuthFailure.MissingPassword
                Return "账户问题：本地没有保存密码，请在配置页填写。"
            Case AuthFailure.InvalidOperator
                Return "账户问题：运营商无效，请在配置页重新选择。"
            Case AuthFailure.PasswordEncryptFailed
                Return "账户问题：" & Result.Message
            Case AuthFailure.ServiceNotAvailable
                Return "门户问题：" & Result.Message
            Case AuthFailure.ValidCodeRequired
                Return "门户问题：" & Result.Message
            Case AuthFailure.MissingPublicKey, AuthFailure.InvalidPublicKey
                Return "门户问题：" & Result.Message
            Case AuthFailure.MissingQueryString
                Return "认证参数问题：未能取得认证参数（queryString）。"
            Case AuthFailure.MissingDiscovery
                Return "网络问题：" & Result.Message
        End Select

        ' 门户明确返回的业务失败（例如密码错误、用户名不能为空）
        If Result.LoginResult IsNot Nothing AndAlso Not String.IsNullOrEmpty(Result.LoginResult.Message) Then
            Return "门户返回：" & Result.LoginResult.Message
        End If
        Return If(String.IsNullOrEmpty(Result.Message), "认证失败：未知原因。", Result.Message)
    End Function

#End Region

End Module