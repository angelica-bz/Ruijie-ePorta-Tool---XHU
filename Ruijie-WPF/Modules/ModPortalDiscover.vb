Imports System.Net
Imports Microsoft.VisualBasic

''' <summary>
''' 西华大学 ePortal 门户参数自动发现。
'''
''' 职责边界（与 ModCrypto / ModNetwork 严格分工）：
'''   ModPortalDiscover —— 发现 queryString、公钥、服务列表、验证码状态
'''   ModCrypto          —— 明文密码 → password 密文（本模块不复制其加密逻辑）
'''   ModNetwork         —— 底层 HTTP（PostJson / EncodeFormData / BuildHeaders）
'''
''' 关于 queryString 的编码约定（非常重要，勿改）：
'''   本模块输出的 QueryString 一律是【302 Location 里的原始 query 部分】，不做任何
'''   URL 编码。门户 JavaScript 的写法是
'''       content = "queryString=" + encodeURIComponent(getQueryString());
'''       thePost.send(content);          // XHR 不会再编码
'''   而本项目 ModNetwork.EncodeFormData 会对 value 执行一次 Uri.EscapeDataString。
'''   因此把【原始 query】交给 PostJson 正好等价于门户的行为；
'''   若在此处提前编码，最终会变成双重编码而登录失败。
'''
''' 行为依据（第二阶段实测，唯一准则）：
'''   %TEMP%\ruijie-eportal-investigation\flow1.pageInfo.json     真实 pageInfo 响应
'''   %TEMP%\ruijie-eportal-investigation\getServices.html        真实 getServices 响应
'''   %TEMP%\ruijie-eportal-investigation\login_bch.js            调用方式与参数拼接
'''   %TEMP%\ruijie-eportal-investigation\AuthInterFace.js        接口路径与请求构造
''' </summary>
Public Module ModPortalDiscover

#Region "常量与默认值"

    ''' <summary>门户默认基地址（西华大学）。调用方可以覆盖。</summary>
    Public Const DefaultServer As String = "http://202.115.144.51"

    ''' <summary>登录页路径。</summary>
    Public Const IndexPath As String = "/eportal/index.jsp"

    ''' <summary>pageInfo 接口路径（提供公钥、passwordEncrypt、validCodeUrl、服务表）。</summary>
    Public Const PageInfoPath As String = "/eportal/InterFace.do?method=pageInfo"

    ''' <summary>getServices 接口路径（提供服务/运营商列表与默认服务）。</summary>
    Public Const ServicesPath As String = "/eportal/InterFace.do?method=getServices"

    ''' <summary>
    ''' 触发 BRAS 门户劫持的探测地址。与 ModNetwork.TestInternet 默认探测目标同源，
    ''' 未认证时会被校园网门户 302 劫持到 eportal/index.jsp。
    ''' </summary>
    Public Const DefaultProbeUrl As String = "http://www.msftconnecttest.com/redirect"

    ''' <summary>
    ''' 判定“这是门户重定向”的必需参数。二者同时出现才认为是门户劫持，
    ''' 从而把门户 302 与普通 302（例如已联网时微软自己的跳转）区分开。
    ''' </summary>
    Private ReadOnly RequiredRedirectParams As String() = {"wlanuserip", "nasip"}

    ''' <summary>Location 里出现这些片段说明它指向门户登录页。</summary>
    Private ReadOnly PortalLocationHints As String() = {"eportal", "index.jsp"}

    Private Const DefaultTimeoutSeconds As Integer = 10

#End Region

#Region "枚举与数据模型"

    ''' <summary>门户发现的状态。前两项为正常结果，其余为可区分的失败原因。</summary>
    Public Enum PortalDiscoveryStatus
        ''' <summary>情况 A：未认证，已取得门户重定向参数。</summary>
        NeedAuthentication
        ''' <summary>情况 B：已认证，没有门户劫持，当前无需 Portal 参数。</summary>
        AlreadyOnline
        ''' <summary>未检测到门户重定向。</summary>
        NoRedirect
        ''' <summary>
        ''' 外部探测请求本身失败（DNS / 连接 / 被中间设备打断等）。
        ''' 注意：这表示「探测这条网络路径不通」，**不等于**认证服务器不可达。
        ''' </summary>
        ProbeFailed
        ''' <summary>
        ''' 本机所有候选出口地址都试过了，没有一个能完成探测。
        ''' 含义是「找不到可用的校园网接口」，不是「门户挂了」，也不是「密码错了」。
        ''' </summary>
        NoUsableInterface
        ''' <summary>
        ''' 门户服务器本身不可达（TCP 都连不上）。与 <see cref="ProbeFailed"/> 严格区分。
        ''' </summary>
        Unreachable
        ''' <summary>
        ''' 门户服务器可达，但接口请求没有得到可用响应（例如空响应 / 非 JSON）。
        ''' 语义是「认证参数获取失败」，不是「服务器不可达」。
        ''' </summary>
        PortalRequestFailed
        ''' <summary>HTTP 请求超时。</summary>
        Timeout
        ''' <summary>Location 无效。</summary>
        InvalidLocation
        ''' <summary>Location 不包含认证参数。</summary>
        MissingParameters
        ''' <summary>其它响应异常。</summary>
        BadResponse
    End Enum

    ''' <summary>门户发现失败时抛出的异常，Status 可区分具体原因。</summary>
    Public Class PortalDiscoveryException
        Inherits Exception

        Public ReadOnly Property Status As PortalDiscoveryStatus

        Public Sub New(status As PortalDiscoveryStatus, message As String)
            MyBase.New(message)
            Me.Status = status
        End Sub
    End Class

    ''' <summary>从 302 Location 解析出的门户重定向信息。</summary>
    Public Class PortalRedirectInfo
        ''' <summary>去掉 query 与 fragment 后的门户地址，例如 http://202.115.144.51/eportal/index.jsp。</summary>
        Public Property PortalUrl As String
        ''' <summary>原始 query 部分（不含前导 '?'，未解码、未编码）。可直接交给 PostJson。</summary>
        Public Property QueryString As String
        ''' <summary>已解码的参数表（键不区分大小写）。</summary>
        Public Property Parameters As Dictionary(Of String, String)

        ''' <summary>queryString 中的 mac 参数；不存在时返回 Nothing（默认值由 ModCrypto 决定）。</summary>
        Public ReadOnly Property Mac As String
            Get
                Return GetParameter("mac")
            End Get
        End Property

        ''' <summary>取参数值；不存在时返回 Nothing。</summary>
        Public Function GetParameter(Name As String) As String
            If Parameters Is Nothing OrElse Name Is Nothing Then Return Nothing
            Dim Value As String = Nothing
            If Parameters.TryGetValue(Name, Value) Then Return Value
            Return Nothing
        End Function
    End Class

    ''' <summary>单个服务（运营商）。</summary>
    Public Class PortalService
        ''' <summary>service 字段值，例如 96301 / cmccgx / unicom / xhu / office。</summary>
        Public Property Name As String
        ''' <summary>显示名，例如 电信网 / 移动网 / 联通网 / 校内网 / 办公网。</summary>
        Public Property DisplayName As String
        ''' <summary>是否为门户默认服务。</summary>
        Public Property IsDefault As Boolean
        ''' <summary>门户的 domainName 标记（本版本门户 Web 认证不使用用户名@服务名，仅保留原始信息）。</summary>
        Public Property DomainName As Boolean
    End Class

    ''' <summary>服务列表及其默认项。</summary>
    Public Class PortalServices
        Public Property Items As New List(Of PortalService)
        ''' <summary>默认服务名（来自 getServices 的 net_access_type，或 pageInfo 的 serviceDefault）。</summary>
        Public Property DefaultName As String

        ''' <summary>默认服务对象；找不到时返回 Nothing。</summary>
        Public ReadOnly Property DefaultService As PortalService
            Get
                If Items Is Nothing OrElse Items.Count = 0 Then Return Nothing
                If Not String.IsNullOrEmpty(DefaultName) Then
                    Dim ByName = FindByName(DefaultName)
                    If ByName IsNot Nothing Then Return ByName
                End If
                For Each Item In Items
                    If Item.IsDefault Then Return Item
                Next
                Return Items(0)
            End Get
        End Property

        ''' <summary>按 service 名查找；找不到返回 Nothing。</summary>
        Public Function FindByName(Name As String) As PortalService
            If Items Is Nothing OrElse String.IsNullOrEmpty(Name) Then Return Nothing
            For Each Item In Items
                If String.Equals(Item.Name, Name, StringComparison.OrdinalIgnoreCase) Then Return Item
            Next
            Return Nothing
        End Function
    End Class

    ''' <summary>pageInfo 的解析结果（只保留认证需要的字段，其余原始数据保留在 Raw 中）。</summary>
    Public Class PortalPageInfo
        ''' <summary>门户公钥指数，例如 "10001"。交给 ModCrypto.EncryptPassword 使用。</summary>
        Public Property PublicKeyExponent As String
        ''' <summary>门户公钥模数（十六进制）。交给 ModCrypto.EncryptPassword 使用。</summary>
        Public Property PublicKeyModulus As String
        ''' <summary>门户是否要求加密密码（本门户实测为 True）。</summary>
        Public Property PasswordEncrypt As Boolean
        ''' <summary>验证码图片地址。为空字符串表示当前不需要验证码。</summary>
        Public Property ValidCodeUrl As String
        ''' <summary>pageInfo.service 解析出的服务表（可能为空）。</summary>
        Public Property Services As PortalServices
        ''' <summary>未加工的全部原始字段，供后续按需取用，避免把响应塞进无定义的 Dictionary 到处传。</summary>
        Public Property Raw As Dictionary(Of String, Object)

        ''' <summary>当前是否需要验证码。</summary>
        Public ReadOnly Property RequiresValidCode As Boolean
            Get
                Return Not String.IsNullOrEmpty(ValidCodeUrl)
            End Get
        End Property
    End Class

    ''' <summary>一次完整发现的结果。</summary>
    Public Class PortalDiscoveryResult
        Public Property Status As PortalDiscoveryStatus = PortalDiscoveryStatus.NoRedirect
        ''' <summary>可读的说明（正常时为简要描述，失败时为原因）。</summary>
        Public Property Message As String = ""
        ''' <summary>重定向信息；仅 NeedAuthentication / AlreadyOnline 时可能非空。</summary>
        Public Property Redirect As PortalRedirectInfo
        ''' <summary>pageInfo 解析结果；仅 NeedAuthentication 且探测成功时非空。</summary>
        Public Property PageInfo As PortalPageInfo
        ''' <summary>服务列表；优先取 getServices，失败时回退到 pageInfo.service。</summary>
        Public Property Services As PortalServices

        Public ReadOnly Property IsSuccess As Boolean
            Get
                Return Status = PortalDiscoveryStatus.NeedAuthentication OrElse
                       Status = PortalDiscoveryStatus.AlreadyOnline
            End Get
        End Property

        Public ReadOnly Property NeedsAuthentication As Boolean
            Get
                Return Status = PortalDiscoveryStatus.NeedAuthentication
            End Get
        End Property
    End Class

#End Region

#Region "1. Location 解析（纯函数，可离线测试）"

    ''' <summary>
    ''' 从完整 URL 中取出原始 query 部分。
    ''' 不解码、不编码；正确处理 '?' 与 '#'（先切 fragment 再找 '?'，避免 "路径#片段?x=1" 误判）。
    ''' 没有 query 时返回 ""。
    ''' </summary>
    Public Function ExtractQueryString(Url As String) As String
        If String.IsNullOrEmpty(Url) Then Return ""
        Dim Work As String = Url
        Dim Hash As Integer = Work.IndexOf("#"c)
        If Hash >= 0 Then Work = Work.Substring(0, Hash)
        Dim Question As Integer = Work.IndexOf("?"c)
        If Question < 0 Then Return ""
        Return Work.Substring(Question + 1)
    End Function

    ''' <summary>把 query 拆成已解码的参数表（键不区分大小写，保留空值参数）。</summary>
    Public Function ParseQueryParameters(Query As String) As Dictionary(Of String, String)
        Dim Result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        If String.IsNullOrEmpty(Query) Then Return Result
        For Each Pair As String In Query.Split("&"c)
            If Pair.Length = 0 Then Continue For
            Dim Equal As Integer = Pair.IndexOf("="c)
            Dim RawKey As String, RawValue As String
            If Equal < 0 Then
                RawKey = Pair
                RawValue = ""
            Else
                RawKey = Pair.Substring(0, Equal)
                RawValue = Pair.Substring(Equal + 1)
            End If
            Dim Key As String = SafeUnescape(RawKey)
            If Key.Length = 0 Then Continue For
            Result(Key) = SafeUnescape(RawValue)
        Next
        Return Result
    End Function

    ''' <summary>
    ''' 解析 HTTP 302 的 Location，得到 PortalUrl / QueryString / Parameters。
    ''' 解析失败时抛出 <see cref="PortalDiscoveryException"/>。
    ''' </summary>
    Public Function ParseRedirectLocation(Location As String) As PortalRedirectInfo
        Dim Info As PortalRedirectInfo = Nothing
        Dim Reason As String = ""
        Dim Status As PortalDiscoveryStatus = PortalDiscoveryStatus.InvalidLocation
        If Not TryParseRedirectLocation(Location, Info, Status, Reason) Then
            Throw New PortalDiscoveryException(Status, Reason)
        End If
        Return Info
    End Function

    ''' <summary>非抛出式解析。失败时返回 False 并通过 Status/ErrorMessage 说明原因。</summary>
    Public Function TryParseRedirectLocation(Location As String,
                                             ByRef Info As PortalRedirectInfo,
                                             ByRef Status As PortalDiscoveryStatus,
                                             ByRef ErrorMessage As String) As Boolean
        Info = Nothing
        Status = PortalDiscoveryStatus.InvalidLocation
        ErrorMessage = ""

        If Location Is Nothing OrElse Location.Trim().Length = 0 Then
            ErrorMessage = "Location 为空，无法解析门户重定向。"
            Return False
        End If

        Dim Raw As String = Location.Trim()

        ' PortalUrl：去掉 query 与 fragment
        Dim Cut As Integer = Raw.Length
        Dim Hash As Integer = Raw.IndexOf("#"c)
        If Hash >= 0 AndAlso Hash < Cut Then Cut = Hash
        Dim Question As Integer = Raw.IndexOf("?"c)
        If Question >= 0 AndAlso Question < Cut Then Cut = Question
        Dim PortalUrl As String = Raw.Substring(0, Cut)
        If PortalUrl.Length = 0 Then
            ErrorMessage = "Location 不含有效地址：" & Raw
            Return False
        End If

        Info = New PortalRedirectInfo With {
            .PortalUrl = PortalUrl,
            .QueryString = ExtractQueryString(Raw),
            .Parameters = ParseQueryParameters(ExtractQueryString(Raw))
        }
        Return True
    End Function

    ''' <summary>
    ''' 判断一个 Location 是否属于门户登录页重定向，并给出状态。
    ''' 必需参数齐全 → NeedAuthentication；像门户页但缺参数 → MissingParameters；否则 → AlreadyOnline。
    ''' </summary>
    Public Function ClassifyRedirect(Location As String) As PortalDiscoveryStatus
        If String.IsNullOrEmpty(Location) Then Return PortalDiscoveryStatus.NoRedirect

        Dim Info As PortalRedirectInfo = Nothing
        If Not TryParseRedirectLocation(Location, Info, PortalDiscoveryStatus.InvalidLocation, "") Then
            Return PortalDiscoveryStatus.InvalidLocation
        End If

        Dim HasAllParams As Boolean = True
        For Each Name In RequiredRedirectParams
            If Info.GetParameter(Name) Is Nothing Then
                HasAllParams = False
                Exit For
            End If
        Next
        If HasAllParams Then Return PortalDiscoveryStatus.NeedAuthentication

        ' 看起来像门户登录页，但缺少认证参数
        For Each Hint In PortalLocationHints
            If Location.IndexOf(Hint, StringComparison.OrdinalIgnoreCase) >= 0 Then
                Return PortalDiscoveryStatus.MissingParameters
            End If
        Next

        ' 普通 302（例如已联网时微软自己的跳转）
        Return PortalDiscoveryStatus.AlreadyOnline
    End Function

    ''' <summary>取 URL 的 scheme://host[:port]；失败返回 ""。</summary>
    Public Function GetOrigin(Url As String) As String
        If String.IsNullOrEmpty(Url) Then Return ""
        Try
            Dim Parsed As New Uri(Url)
            Return Parsed.Scheme & "://" & Parsed.Authority
        Catch
            Return ""
        End Try
    End Function

#End Region

#Region "2. 公钥校验"

    ''' <summary>
    ''' 校验门户公钥是否可用。不把当前 modulus 固定成常量，允许门户将来更换。
    ''' 规则：非空、纯十六进制、长度合理。
    ''' </summary>
    Public Function ValidatePublicKey(ExponentHex As String,
                                      ModulusHex As String,
                                      ByRef ErrorMessage As String) As Boolean
        ErrorMessage = ""

        If Not IsHexString(ExponentHex) Then
            ErrorMessage = "公钥 exponent 为空或含非十六进制字符：" & SafeText(ExponentHex)
            Return False
        End If
        Dim Exp As String = ExponentHex.Trim()
        If Exp.Length > 16 Then
            ErrorMessage = "公钥 exponent 长度异常（" & Exp.Length & " 个十六进制字符）。"
            Return False
        End If

        If Not IsHexString(ModulusHex) Then
            ErrorMessage = "公钥 modulus 为空或含非十六进制字符：" & SafeText(ModulusHex)
            Return False
        End If
        Dim Modu As String = ModulusHex.Trim()
        If Modu.Length < 32 OrElse Modu.Length > 2048 Then
            ErrorMessage = "公钥 modulus 长度异常（" & Modu.Length & " 个十六进制字符，" &
                           "合理范围 32–2048，即 128–8192 bit）。"
            Return False
        End If

        Return True
    End Function

    Private Function IsHexString(Value As String) As Boolean
        If Value Is Nothing Then Return False
        Dim Clean As String = Value.Trim()
        If Clean.Length = 0 Then Return False
        For Each Ch As Char In Clean
            If Not Uri.IsHexDigit(Ch) Then Return False
        Next
        Return True
    End Function

    Private Function SafeText(Value As String) As String
        If Value Is Nothing Then Return "(Nothing)"
        If Value.Length = 0 Then Return "(空)"
        If Value.Length > 40 Then Return Value.Substring(0, 40) & "…"
        Return Value
    End Function

#End Region

#Region "3. pageInfo / getServices 解析（纯函数，可离线测试）"

    ''' <summary>解析 pageInfo JSON 文本。</summary>
    Public Function ParsePageInfo(Json As String) As PortalPageInfo
        Dim Value As Object = Nothing
        Dim Reason As String = ""
        If Not ModJson.TryParseJsonValue(Json, Value, Reason) Then
            Throw New PortalDiscoveryException(PortalDiscoveryStatus.BadResponse, "pageInfo 响应不是合法 JSON：" & Reason)
        End If
        Dim Data = TryCast(Value, Dictionary(Of String, Object))
        If Data Is Nothing Then
            Throw New PortalDiscoveryException(PortalDiscoveryStatus.BadResponse, "pageInfo 响应不是 JSON 对象。")
        End If
        Return ParsePageInfo(Data)
    End Function

    ''' <summary>解析已反序列化的 pageInfo 响应。</summary>
    Public Function ParsePageInfo(Data As Dictionary(Of String, Object)) As PortalPageInfo
        If Data Is Nothing Then
            Throw New PortalDiscoveryException(PortalDiscoveryStatus.BadResponse, "pageInfo 响应为空。")
        End If

        Dim Info As New PortalPageInfo With {
            .Raw = Data,
            .PasswordEncrypt = IsTrueValue(GetDictStr(Data, "passwordEncrypt")),
            .PublicKeyExponent = GetDictStr(Data, "publicKeyExponent").Trim(),
            .PublicKeyModulus = GetDictStr(Data, "publicKeyModulus").Trim(),
            .ValidCodeUrl = GetDictStr(Data, "validCodeUrl").Trim()
        }

        ' pageInfo.service 是 {serviceName -> {serviceShowName, serviceDefault, domainName, ...}} 对象
        Dim ServiceMap = GetSubDict(Data, "service")
        If ServiceMap IsNot Nothing Then
            Dim Built = BuildServicesFromMap(ServiceMap)
            ' 空服务表统一收敛为 Nothing，便于调用方用同一套判断
            If Built.Items.Count > 0 Then Info.Services = Built
        End If

        ' 需要加密密码时，公钥必须可用，否则后续无法构造登录请求
        If Info.PasswordEncrypt Then
            Dim Reason As String = ""
            If Not ValidatePublicKey(Info.PublicKeyExponent, Info.PublicKeyModulus, Reason) Then
                Throw New PortalDiscoveryException(
                    PortalDiscoveryStatus.BadResponse,
                    "门户要求加密密码（passwordEncrypt=true），但 " & Reason)
            End If
        End If

        Return Info
    End Function

    ''' <summary>解析 getServices JSON 文本。</summary>
    Public Function ParseServices(Json As String) As PortalServices
        Dim Value As Object = Nothing
        Dim Reason As String = ""
        If Not ModJson.TryParseJsonValue(Json, Value, Reason) Then
            Throw New PortalDiscoveryException(PortalDiscoveryStatus.BadResponse, "getServices 响应不是合法 JSON：" & Reason)
        End If
        Dim Data = TryCast(Value, Dictionary(Of String, Object))
        If Data Is Nothing Then
            Throw New PortalDiscoveryException(PortalDiscoveryStatus.BadResponse, "getServices 响应不是 JSON 对象。")
        End If
        Return ParseServices(Data)
    End Function

    ''' <summary>
    ''' 解析已反序列化的 getServices 响应。
    ''' 服务列表来源优先级：services（JSON 数组字符串）→ serviceJson → serviceContent 里的 services 脚本。
    ''' </summary>
    Public Function ParseServices(Data As Dictionary(Of String, Object)) As PortalServices
        If Data Is Nothing Then
            Throw New PortalDiscoveryException(PortalDiscoveryStatus.BadResponse, "getServices 响应为空。")
        End If

        Dim Result As PortalServices = Nothing

        For Each KeyName In New String() {"services", "serviceJson"}
            Dim JsonArray As String = GetDictStr(Data, KeyName).Trim()
            If JsonArray.Length > 0 Then
                Result = TryBuildServicesFromArray(JsonArray)
                If Result IsNot Nothing AndAlso Result.Items.Count > 0 Then Exit For
            End If
        Next

        If Result Is Nothing OrElse Result.Items.Count = 0 Then
            ' 退回 serviceContent：里面内嵌 <script>var services=[...]</script>
            Dim Content As String = GetDictStr(Data, "serviceContent")
            Dim StartAt As Integer = Content.IndexOf("var services=", StringComparison.OrdinalIgnoreCase)
            If StartAt >= 0 Then
                Dim Open = Content.IndexOf("["c, StartAt)
                Dim Close = Content.LastIndexOf("]"c)
                If Open >= 0 AndAlso Close > Open Then
                    Result = TryBuildServicesFromArray(Content.Substring(Open, Close - Open + 1))
                End If
            End If
        End If

        If Result Is Nothing OrElse Result.Items.Count = 0 Then
            Throw New PortalDiscoveryException(PortalDiscoveryStatus.BadResponse,
                                               "getServices 响应中没有可解析的服务列表。")
        End If

        ' 默认服务：net_access_type 的 value（HTML 片段里），否则用 serviceDefault 标记
        Result.DefaultName = ExtractDefaultServiceName(GetDictStr(Data, "defaultService"))
        If String.IsNullOrEmpty(Result.DefaultName) Then
            For Each Item In Result.Items
                If Item.IsDefault Then
                    Result.DefaultName = Item.Name
                    Exit For
                End If
            Next
        End If

        Return Result
    End Function

    ''' <summary>从 pageInfo 的 service 对象构造服务表。</summary>
    Private Function BuildServicesFromMap(Map As Dictionary(Of String, Object)) As PortalServices
        Dim Result As New PortalServices()
        For Each Pair In Map
            Dim Entry = TryCast(Pair.Value, Dictionary(Of String, Object))
            If Entry Is Nothing Then Continue For
            Result.Items.Add(New PortalService With {
                .Name = FirstNonEmpty(GetDictStr(Entry, "serviceName"), Pair.Key),
                .DisplayName = GetDictStr(Entry, "serviceShowName"),
                .IsDefault = IsTrueValue(GetDictStr(Entry, "serviceDefault")),
                .DomainName = IsTrueValue(GetDictStr(Entry, "domainName"))
            })
        Next
        For Each Item In Result.Items
            If Item.IsDefault Then
                Result.DefaultName = Item.Name
                Exit For
            End If
        Next
        Return Result
    End Function

    ''' <summary>从 services JSON 数组字符串构造服务表；解析失败返回 Nothing。</summary>
    Private Function TryBuildServicesFromArray(JsonArray As String) As PortalServices
        Dim Value As Object = Nothing
        Dim Reason As String = ""
        If Not ModJson.TryParseJsonValue(JsonArray, Value, Reason) Then Return Nothing

        Dim Items = TryCast(Value, Object())
        If Items Is Nothing Then Return Nothing

        Dim Result As New PortalServices()
        For Each Element In Items
            Dim Entry = TryCast(Element, Dictionary(Of String, Object))
            If Entry Is Nothing Then Continue For
            Dim Name As String = GetDictStr(Entry, "serviceName").Trim()
            If Name.Length = 0 Then Continue For
            Result.Items.Add(New PortalService With {
                .Name = Name,
                .DisplayName = GetDictStr(Entry, "serviceShowName"),
                .IsDefault = IsTrueValue(GetDictStr(Entry, "serviceDefault")),
                .DomainName = IsTrueValue(GetDictStr(Entry, "domainName"))
            })
        Next
        Return Result
    End Function

    ''' <summary>从 defaultService 的 HTML 片段里取出 net_access_type 的 value。</summary>
    Private Function ExtractDefaultServiceName(Html As String) As String
        If String.IsNullOrEmpty(Html) Then Return ""
        Dim Match = System.Text.RegularExpressions.Regex.Match(
            Html, "net_access_type[^>]*value\s*=\s*['""]([^'""]*)['""]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        If Match.Success Then Return Match.Groups(1).Value.Trim()
        Return ""
    End Function

#End Region

#Region "4. HTTP：302 探测与接口调用"

    ''' <summary>GET 探测的结果。</summary>
    Private Class ProbeResponse
        Public Property StatusCode As Integer = 0
        Public Property Location As String = ""
        Public Property FailureStatus As PortalDiscoveryStatus = PortalDiscoveryStatus.NoRedirect
        Public Property FailureMessage As String = ""
        Public ReadOnly Property Succeeded As Boolean
            Get
                Return FailureMessage.Length = 0
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 探测门户重定向：GET 一个普通 HTTP 地址，读取 BRAS 返回的 302 Location。
    '''
    ''' 注意：这里刻意【不复用 ModNetwork.BuildHeaders】—— 探测目标是外网站点
    ''' （如 www.msftconnecttest.com），带上门户的 Host / Origin / Referer / Cookie
    ''' 反而是错误的。仅使用最小请求头。
    ''' </summary>
    Public Function DiscoverRedirect(Optional ProbeUrl As String = DefaultProbeUrl,
                                     Optional Timeout As Integer = DefaultTimeoutSeconds,
                                     Optional BindAddress As String = "") As PortalDiscoveryResult
        Dim Result As New PortalDiscoveryResult()

        Dim Response As ProbeResponse = HttpGetWithoutRedirect(ProbeUrl, Timeout, BindAddress)
        If Not Response.Succeeded Then
            Result.Status = Response.FailureStatus
            Result.Message = Response.FailureMessage
            Return Result
        End If

        Return BuildRedirectResult(Response.StatusCode, Response.Location)
    End Function

    ''' <summary>
    ''' 由探测响应的状态码与 Location 构造发现结果（纯函数，可离线测试）。
    ''' 区分：门户劫持 → NeedAuthentication；普通跳转/2xx → AlreadyOnline；
    ''' 门户页但缺参数 → MissingParameters；3xx 无 Location → InvalidLocation。
    ''' </summary>
    Public Function BuildRedirectResult(StatusCode As Integer, Location As String) As PortalDiscoveryResult
        Dim Result As New PortalDiscoveryResult()

        ' ---- 3xx：读 Location 判断是否被门户劫持 ----
        If StatusCode >= 300 AndAlso StatusCode < 400 Then
            If String.IsNullOrEmpty(Location) Then
                Result.Status = PortalDiscoveryStatus.InvalidLocation
                Result.Message = "HTTP " & StatusCode & " 响应没有 Location 头。"
                Return Result
            End If

            Dim Classified As PortalDiscoveryStatus = ClassifyRedirect(Location)
            Result.Status = Classified

            ' 注意：不要把这个 Classified 作为 ByRef 实参传进去，否则会被解析函数的
            ' 出参覆盖（TryParseRedirectLocation 成功返回时不会回写状态）。
            Dim Info As PortalRedirectInfo = Nothing
            Dim ParseStatus As PortalDiscoveryStatus = PortalDiscoveryStatus.InvalidLocation
            Dim ParseError As String = ""
            TryParseRedirectLocation(Location, Info, ParseStatus, ParseError)

            Select Case Classified
                Case PortalDiscoveryStatus.NeedAuthentication
                    Result.Redirect = Info
                    Result.Message = "检测到校园网门户重定向，已取得认证参数。"
                Case PortalDiscoveryStatus.MissingParameters
                    Result.Redirect = Info
                    Result.Message = "检测到门户登录页重定向，但 Location 缺少必需认证参数（" &
                                     String.Join(" / ", RequiredRedirectParams) & "）。"
                Case PortalDiscoveryStatus.AlreadyOnline
                    Result.Redirect = Info
                    Result.Message = "未检测到门户重定向（普通 HTTP " & StatusCode & " 跳转），当前已联网。"
                Case Else
                    Result.Message = "Location 无效：" & ParseError
            End Select
            Return Result
        End If

        ' ---- 2xx：拿到真实内容，说明流量没有被劫持，当前已联网 ----
        If StatusCode >= 200 AndAlso StatusCode < 300 Then
            Result.Status = PortalDiscoveryStatus.AlreadyOnline
            Result.Message = "未检测到门户重定向，当前已联网（HTTP " & StatusCode & "）。"
            Return Result
        End If

        Result.Status = PortalDiscoveryStatus.NoRedirect
        Result.Message = "探测返回 HTTP " & StatusCode & "，未检测到门户重定向。"
        Return Result
    End Function

    ''' <summary>
    ''' 门户接口请求失败时判定最终状态，把两个概念严格分开：
    '''   TCP 都连不上      → Unreachable         （认证服务器确实不可达）
    '''   TCP 通但接口失败  → PortalRequestFailed （服务器在，但认证参数拿不到）
    ''' 非接口类状态原样保留。
    ''' </summary>
    Private Function ResolvePortalFailureStatus(Server As String, Fallback As PortalDiscoveryStatus) As PortalDiscoveryStatus
        If Fallback <> PortalDiscoveryStatus.PortalRequestFailed AndAlso
           Fallback <> PortalDiscoveryStatus.Unreachable Then
            Return Fallback
        End If

        Dim Reachable As Boolean = False
        Try
            Reachable = ModNetwork.TcpProbe(Server, 3)
        Catch
        End Try

        Return If(Reachable, PortalDiscoveryStatus.PortalRequestFailed, PortalDiscoveryStatus.Unreachable)
    End Function

    ''' <summary>
    ''' 组装本次探测要依次尝试的出口地址序列。
    '''
    ''' 传了 BindAddress：先用它，失败后再退到其它候选 —— 用户/脚本显式指定的地址
    ''' 不该成为唯一尝试，否则网卡换 IP 之后整个认证就死了。
    ''' 没传：用 ResolveBindAddress 选出的最佳出口打头。
    ''' </summary>
    Private Function BuildProbeCandidates(BindAddress As String) As List(Of String)
        Dim Result As New List(Of String)

        If Not String.IsNullOrEmpty(BindAddress) Then Result.Add(BindAddress.Trim())

        Dim Info As ModNetwork.BindResolution = ModNetwork.ResolveBindAddressInfo()
        If Info IsNot Nothing AndAlso Not String.IsNullOrEmpty(Info.Address) AndAlso
           Not Result.Contains(Info.Address) Then
            Result.Add(Info.Address)
        End If

        ' 其余可用物理网卡作为兜底。被排除的虚拟网卡不进候选 —— 它们正是问题来源。
        For Each C In ModNetwork.GetCandidateBindAddresses()
            If C.Usable AndAlso Not Result.Contains(C.Address) Then Result.Add(C.Address)
        Next

        Return Result
    End Function

    ''' <summary>
    ''' 门户接口请求头。
    '''
    ''' **User-Agent 是必需的**：门户的 InterFace.do 对没有 User-Agent 的请求会返回
    ''' 「HTTP 200 + 空正文」，在上层表现为「服务器返回了空响应」，
    ''' 并曾被错误地归类成「认证服务器不可达」。
    ''' 本项目其它所有请求（BuildHeaders / BuildLoginHeaders / 探测 GET / userIndex 查询）
    ''' 都带了 User-Agent，这里曾经是唯一漏掉的一处。
    ''' </summary>
    Public Function BuildPortalRequestHeaders() As Dictionary(Of String, String)
        Return New Dictionary(Of String, String) From {
            {"Content-Type", "application/x-www-form-urlencoded; charset=UTF-8"},
            {"Accept", "*/*"},
            {"User-Agent", ModNetwork.DefaultUserAgent}
        }
    End Function

    ''' <summary>POST pageInfo，取得公钥、passwordEncrypt、validCodeUrl 与服务表。</summary>
    Public Function FetchPageInfo(QueryString As String,
                                  Optional Server As String = DefaultServer,
                                  Optional Timeout As Integer = DefaultTimeoutSeconds,
                                  Optional BindAddress As String = "") As PortalPageInfo
        ModAuthTrace.CountPageInfo()
        Dim Body = PostPortal(Server, PageInfoPath, QueryString, Timeout, "pageInfo", BindAddress)
        Dim Info As PortalPageInfo = ParsePageInfo(Body)
        If Info IsNot Nothing Then
            ModAuthTrace.ObservePublicKey(Info.PublicKeyModulus, Info.PublicKeyExponent)
        End If
        Return Info
    End Function

    ''' <summary>POST getServices，取得服务/运营商列表。</summary>
    Public Function FetchServices(QueryString As String,
                                  Optional Server As String = DefaultServer,
                                  Optional Timeout As Integer = DefaultTimeoutSeconds,
                                  Optional BindAddress As String = "") As PortalServices
        Dim Body = PostPortal(Server, ServicesPath, QueryString, Timeout, "getServices", BindAddress)
        Return ParseServices(Body)
    End Function

    ''' <summary>
    ''' 一步完成：探测重定向 → pageInfo → getServices。
    ''' 已联网（没有门户劫持）时返回 AlreadyOnline，这不是错误。
    ''' </summary>
    Public Function Discover(Optional ProbeUrl As String = DefaultProbeUrl,
                             Optional Server As String = "",
                             Optional Timeout As Integer = DefaultTimeoutSeconds,
                             Optional BindAddress As String = "") As PortalDiscoveryResult
        ModAuthTrace.CountDiscovery()

        ' BindAddress 为空时自动解析本机出口，并准备好「失败就换下一个」的候选序列。
        Dim Candidates As List(Of String) = BuildProbeCandidates(BindAddress)

        Dim Result As PortalDiscoveryResult = Nothing
        Dim Tried As New List(Of String)

        For Each Candidate In Candidates
            Tried.Add(Candidate)
            Result = DiscoverRedirect(ProbeUrl, Timeout, Candidate)

            ' 探测请求本身失败才换下一个出口；AlreadyOnline / NeedAuthentication 都是有效结论
            If Result.Status <> PortalDiscoveryStatus.ProbeFailed Then
                BindAddress = Candidate
                Exit For
            End If
        Next

        If Result Is Nothing Then
            ' 连一个候选都没有：明确说「找不到可用接口」，不要说成门户挂了
            Return New PortalDiscoveryResult With {
                .Status = PortalDiscoveryStatus.NoUsableInterface,
                .Message = "本机没有任何可用的 IPv4 出口地址，无法访问认证门户。"
            }
        End If

        ' 所有候选都 ProbeFailed：这是「找不到能访问门户的网卡」，不是「门户不可达」
        If Result.Status = PortalDiscoveryStatus.ProbeFailed Then
            Result.Status = PortalDiscoveryStatus.NoUsableInterface
            Result.Message = "已尝试本机 " & Tried.Count & " 个网络接口，均无法完成探测。" & Result.Message
            Return Result
        End If

        If Not Result.NeedsAuthentication Then Return Result

        ' 记录本次动态参数（模块内部只留指纹），供验收比较「是否变化」
        If Result.Redirect IsNot Nothing Then ModAuthTrace.ObserveQueryString(Result.Redirect.QueryString)

        ' 门户地址优先取重定向里的 origin，其次用调用方给的，最后用默认值
        Dim EffectiveServer As String = Server
        If String.IsNullOrEmpty(EffectiveServer) AndAlso Result.Redirect IsNot Nothing Then
            EffectiveServer = GetOrigin(Result.Redirect.PortalUrl)
        End If
        If String.IsNullOrEmpty(EffectiveServer) Then EffectiveServer = DefaultServer

        Dim QueryString As String = Result.Redirect.QueryString

        Try
            ' BindAddress 必须一并传入：否则 pageInfo 会走默认路由（可能被 VPN/TUN 接管），
            ' 在未认证状态下拿不到响应，被误判成「认证服务器不可达」。
            Result.PageInfo = FetchPageInfo(QueryString, EffectiveServer, Timeout, BindAddress)
        Catch ex As PortalDiscoveryException
            Result.Status = ResolvePortalFailureStatus(EffectiveServer, ex.Status)
            Result.Message = "取得 pageInfo 失败：" & ex.Message
            Return Result
        End Try

        Try
            Result.Services = FetchServices(QueryString, EffectiveServer, Timeout, BindAddress)
        Catch ex As PortalDiscoveryException
            ' getServices 失败不致命：pageInfo.service 通常已够用
            If Result.PageInfo.Services IsNot Nothing AndAlso Result.PageInfo.Services.Items.Count > 0 Then
                Result.Services = Result.PageInfo.Services
                Result.Message = "portal 参数已取得；getServices 失败，已回退到 pageInfo.service（" & ex.Message & "）"
            Else
                Result.Status = ResolvePortalFailureStatus(EffectiveServer, ex.Status)
                Result.Message = "取得服务列表失败：" & ex.Message
                Return Result
            End If
        End Try

        If Result.Services Is Nothing Then Result.Services = Result.PageInfo.Services
        Result.Status = PortalDiscoveryStatus.NeedAuthentication
        Result.Message = "门户参数发现完成。"
        Return Result
    End Function

    ''' <summary>复用 ModNetwork.PostJson + EncodeFormData 提交门户接口。</summary>
    Private Function PostPortal(Server As String,
                                Path As String,
                                QueryString As String,
                                Timeout As Integer,
                                Action As String,
                                Optional BindAddress As String = "") As Dictionary(Of String, Object)
        If String.IsNullOrEmpty(Server) Then Server = DefaultServer
        Dim Url As String = Server.TrimEnd("/"c) & Path
        Dim Data As New Dictionary(Of String, String) From {
            {"queryString", If(QueryString, "")}
        }
        Dim Headers As Dictionary(Of String, String) = BuildPortalRequestHeaders()

        Dim Response As Dictionary(Of String, Object) = ModNetwork.PostJson(Url, Data, Headers, Timeout, BindAddress)

        ' PostJson 在传输/解析异常时返回 {result:"error", message:"…"}；
        ' 真实 pageInfo 一定包含 passwordEncrypt，用它区分。
        If Response IsNot Nothing AndAlso Response.ContainsKey("result") AndAlso
           String.Equals(GetDictStr(Response, "result"), "error", StringComparison.OrdinalIgnoreCase) AndAlso
           Not Response.ContainsKey("passwordEncrypt") Then
            ' 门户接口请求失败 ≠ 门户服务器不可达：这里先用 TCP 预检区分，
            ' 再由 ResolvePortalFailureStatus 统一决定最终状态。
            Throw New PortalDiscoveryException(
                ResolvePortalFailureStatus(Server, PortalDiscoveryStatus.PortalRequestFailed),
                Action & " 请求失败：" & GetDictStr(Response, "message"))
        End If

        Return Response
    End Function

    ''' <summary>
    ''' 最小请求头的 GET，禁用自动跳转以便读取 Location。
    ''' BindAddress 非空时把请求绑定到指定本机地址 —— 用于绕开 VPN/TUN 之类
    ''' 会抢走默认路由的虚拟网卡，确保探测真的走校园网出口。
    ''' </summary>
    Private Function HttpGetWithoutRedirect(Url As String, Timeout As Integer, Optional BindAddress As String = "") As ProbeResponse
        Dim Result As New ProbeResponse()
        Dim Request As HttpWebRequest = Nothing
        Try
            Request = CType(WebRequest.Create(Url), HttpWebRequest)
            Request.Method = "GET"
            Request.AllowAutoRedirect = False
            Request.Timeout = Math.Max(1, Timeout) * 1000
            Request.UserAgent = ModNetwork.DefaultUserAgent
            Request.Accept = "*/*"

            ModNetwork.BindRequestToLocalAddress(Request, BindAddress)

            Using Response As HttpWebResponse = CType(Request.GetResponse(), HttpWebResponse)
                Result.StatusCode = CInt(Response.StatusCode)
                Result.Location = If(Response.Headers("Location"), "")
            End Using
            Return Result

        Catch ex As WebException
            ' 部分 3xx 会以异常形式返回，此时响应里仍有 Location
            Dim WebResponse = TryCast(ex.Response, HttpWebResponse)
            If WebResponse IsNot Nothing Then
                Using WebResponse
                    Result.StatusCode = CInt(WebResponse.StatusCode)
                    Result.Location = If(WebResponse.Headers("Location"), "")
                End Using
                Return Result
            End If
            Select Case ex.Status
                Case WebExceptionStatus.Timeout
                    Result.FailureStatus = PortalDiscoveryStatus.Timeout
                    Result.FailureMessage = "网络探测请求超时（" & Timeout & " 秒）：" & Url
                Case WebExceptionStatus.NameResolutionFailure,
                     WebExceptionStatus.ConnectFailure,
                     WebExceptionStatus.ProxyNameResolutionFailure
                    ' 探测的是外网站点，失败说明这条探测路径不通，而不是认证门户不可达
                    Result.FailureStatus = PortalDiscoveryStatus.ProbeFailed
                    Result.FailureMessage = "网络探测失败（无法建立连接）：" & ex.Message
                Case Else
                    Result.FailureStatus = PortalDiscoveryStatus.ProbeFailed
                    Result.FailureMessage = "网络探测失败（" & ex.Status.ToString() & "）：" & ex.Message
            End Select
            Return Result

        Catch ex As Exception
            Result.FailureStatus = PortalDiscoveryStatus.ProbeFailed
            Result.FailureMessage = "网络探测异常：" & ex.Message
            Return Result
        End Try
    End Function

#End Region

#Region "5. 小工具"

    ''' <summary>门户所有布尔字段都以字符串 "true"/"false" 下发，这里统一判定。</summary>
    Private Function IsTrueValue(Value As String) As Boolean
        If Value Is Nothing Then Return False
        Return String.Equals(Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Function FirstNonEmpty(First As String, Second As String) As String
        If Not String.IsNullOrEmpty(First) Then Return First
        Return Second
    End Function

    ''' <summary>Uri.UnescapeDataString 对畸形转义序列不抛异常，这里再加一层保护。</summary>
    Private Function SafeUnescape(Value As String) As String
        If String.IsNullOrEmpty(Value) Then Return ""
        Try
            Return Uri.UnescapeDataString(Value)
        Catch
            Return Value
        End Try
    End Function

#End Region

End Module
