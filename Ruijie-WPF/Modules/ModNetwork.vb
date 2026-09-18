Imports System.IO
Imports System.Net
Imports Microsoft.VisualBasic

Public Module ModNetwork

    ''' <summary>
    ''' 把请求绑定到指定本机地址（用于绕开 VPN/TUN 抢路由）。
    ''' 地址为空或非法时保持默认路由，不做任何改动。
    ''' </summary>
    Public Sub BindRequestToLocalAddress(Req As HttpWebRequest, BindAddress As String)
        If Req Is Nothing OrElse String.IsNullOrEmpty(BindAddress) Then Return
        Dim LocalAddress As Net.IPAddress = Nothing
        If Not Net.IPAddress.TryParse(BindAddress.Trim(), LocalAddress) Then Return
        Req.ServicePoint.BindIPEndPointDelegate =
            Function(servicePoint, remoteEndPoint, retryCount) New Net.IPEndPoint(LocalAddress, 0)
    End Sub

    ''' <summary>默认 User-Agent。公开给动态认证链路复用，避免两处各写一份。</summary>
    Public Const DefaultUserAgent As String =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " &
        "AppleWebKit/537.36 (KHTML, like Gecko) " &
        "Chrome/99.0.4844.51 Safari/537.36 Edg/99.0.1150.39"

#Region "网络检测"

    Public Function TestInternet(Optional Host As String = "http://connect.rom.miui.com/generate_204", Optional Timeout As Integer = 1) As Boolean
        Try
            Dim Req As HttpWebRequest = CType(WebRequest.Create(Host), HttpWebRequest)
            Req.Method = "HEAD"
            Req.Timeout = Timeout * 1000
            Req.AllowAutoRedirect = True

            Using Resp As HttpWebResponse = CType(Req.GetResponse(), HttpWebResponse)
                If Host.EndsWith("generate_204") Then
                    Return Resp.StatusCode = HttpStatusCode.NoContent
                End If
                Dim Code As Integer = Resp.StatusCode
                Return (Code >= 200 AndAlso Code <= 208) OrElse Code = 226
            End Using
        Catch ex As Exception
            Return False
        End Try
    End Function

#End Region

#Region "请求头构建"

    Private Function StripScheme(Url As String) As String
        Return Url.Replace("http://", "").Replace("https://", "")
    End Function

    Public Function BuildHeaders(Cfg As Dictionary(Of String, Object)) As Dictionary(Of String, String)
        Dim Server As String = GetDictStr(GetUrlDict(Cfg), ConfigKeys.Server)

        Dim Hostname As String = StripScheme(Server)

        Dim Headers As New Dictionary(Of String, String)
        Headers("Connection") = "keep-alive"
        Headers("User-Agent") = DefaultUserAgent
        Headers("Content-Type") = "application/x-www-form-urlencoded; charset=UTF-8"
        Headers("Accept") = "*/*"
        Headers("Accept-Encoding") = "gzip, deflate"
        Headers("Accept-Language") = "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7"
        Headers("Host") = Hostname
        Headers("Origin") = Server

        Dim Cookie As String = GetDictStr(Cfg, ConfigKeys.Cookie)
        Headers("Cookie") = Cookie

        Dim UserHeaders = GetSubDict(Cfg, ConfigKeys.Headers)
        If UserHeaders IsNot Nothing Then
            For Each Kvp In UserHeaders
                If Kvp.Value IsNot Nothing Then
                    Headers(Kvp.Key) = Kvp.Value.ToString()
                End If
            Next
        End If

        Return Headers
    End Function

#End Region

#Region "POST JSON"

    ''' <summary>
    ''' 把表单字段编码为 application/x-www-form-urlencoded 正文。
    ''' 公开以便测试直接验证“只编码一次”的契约；行为与原先完全一致。
    ''' </summary>
    Public Function EncodeFormData(Data As Dictionary(Of String, String)) As String
        Dim Parts As New List(Of String)
        For Each Kvp In Data
            Parts.Add(Uri.EscapeDataString(Kvp.Key) & "=" & Uri.EscapeDataString(If(Kvp.Value, "")))
        Next
        Return String.Join("&", Parts)
    End Function

    ''' <summary>
    ''' POST 表单并解析 JSON 响应。
    ''' BindAddress 非空时把请求绑定到指定本机地址，用于绕开会抢走默认路由的 VPN/TUN，
    ''' 确保请求真的从校园网出口发出。
    ''' </summary>
    Public Function PostJson(Url As String, Data As Dictionary(Of String, String),
                             Headers As Dictionary(Of String, String),
                             Optional Timeout As Integer = 10,
                             Optional BindAddress As String = "") As Dictionary(Of String, Object)
        Dim Result As New Dictionary(Of String, Object)

        Try
            Dim Req As HttpWebRequest = CType(WebRequest.Create(Url), HttpWebRequest)
            Req.Method = "POST"
            Req.Timeout = Timeout * 1000
            BindRequestToLocalAddress(Req, BindAddress)

            For Each Kvp In Headers
                Select Case Kvp.Key.ToLower()
                    Case "content-type"
                        Req.ContentType = Kvp.Value
                    Case "accept"
                        Req.Accept = Kvp.Value
                    Case "user-agent"
                        Req.UserAgent = Kvp.Value
                    Case "host"
                        ' Skip - set by framework
                    Case Else
                        Try
                            Req.Headers(Kvp.Key) = Kvp.Value
                        Catch
                        End Try
                End Select
            Next

            If Data IsNot Nothing AndAlso Data.Count > 0 Then
                Dim PostData As String = EncodeFormData(Data)
                Dim Bytes As Byte() = Text.Encoding.UTF8.GetBytes(PostData)
                Req.ContentLength = Bytes.Length
                Using ReqStream = Req.GetRequestStream()
                    ReqStream.Write(Bytes, 0, Bytes.Length)
                End Using
            Else
                Req.ContentLength = 0
            End If

            Using Resp As HttpWebResponse = CType(Req.GetResponse(), HttpWebResponse)
                Using Reader As New StreamReader(Resp.GetResponseStream(), Text.Encoding.UTF8)
                    Dim Body As String = Reader.ReadToEnd()
                    ' 只记录状态码与长度供验收核对，正文不落地
                    ModAuthTrace.ObserveHttpResponse(CInt(Resp.StatusCode), Text.Encoding.UTF8.GetByteCount(Body),
                                                     If(Url, ""))
                    Return ParseJsonResponse(Body)
                End Using
            End Using

        Catch ex As WebException
            If ex.Response IsNot Nothing Then
                Dim Resp = CType(ex.Response, HttpWebResponse)
                ModAuthTrace.ObserveHttpResponse(CInt(Resp.StatusCode), 0, If(Url, ""))
                Result("result") = "error"
                Result("message") = "HTTP " & CType(Resp.StatusCode, Integer) & ": " & Resp.StatusDescription
            Else
                Result("result") = "error"
                Result("message") = ex.Message
            End If
        Catch ex As Exception
            Result("result") = "error"
            Result("message") = ex.Message
        End Try

        Return Result
    End Function

#End Region

#Region "登录与断网"

    ''' <summary>新动态认证链路的登录路径（相对于 Server）。</summary>
    Public Const PortalLoginPath As String = "/eportal/InterFace.do?method=login"

    ''' <summary>
    ''' 登录入口：直接用 <see cref="AuthPayload"/> 提交。
    '''
    ''' 历史上这里还有一条「旧路径」—— Login(Cfg, Headers)，读 config.yml 里抓包得到的
    ''' login_data 原样透传。那条路径已随动态认证链路上线一并删除：
    ''' 现在 queryString / 公钥 / 密文全部在运行时从门户动态取得，配置文件里不再保存这些。
    ''' 无论如何都走同一个 PostJson / EncodeFormData。
    ''' </summary>
    Public Function LoginPayload(Payload As AuthPayload,
                                 Server As String,
                                 Headers As Dictionary(Of String, String),
                                 Optional Timeout As Integer = 10) As Dictionary(Of String, Object)
        Dim Request As LoginRequest = BuildLoginRequest(Payload, Server, Headers)
        ModAuthTrace.CountLogin()
        Return PostJson(Request.Url, Payload.ToLoginData(), Headers, Timeout)
    End Function

    ''' <summary>
    ''' 一次登录请求的完整描述（不发送）。
    ''' 有了它，测试就能在不真正登录校园网的前提下校验 method / path / headers / 表单正文，
    ''' 而 <see cref="LoginPayload"/> 走的正是同一份构造结果，所以测试对真实请求有约束力。
    ''' </summary>
    Public Class LoginRequest
        Public Property Method As String = "POST"
        Public Property Url As String = ""
        ''' <summary>已按 application/x-www-form-urlencoded 编码好的正文。</summary>
        Public Property Body As String = ""
        Public Property Headers As Dictionary(Of String, String)

        ''' <summary>请求路径（含 query），便于测试断言。</summary>
        Public ReadOnly Property RequestPath As String
            Get
                Try
                    Return New Uri(Url).PathAndQuery
                Catch
                    Return Url
                End Try
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 构造登录请求但不发送。URL 拼接与表单编码与 <see cref="LoginPayload"/> 完全一致。
    ''' </summary>
    Public Function BuildLoginRequest(Payload As AuthPayload,
                                      Server As String,
                                      Headers As Dictionary(Of String, String)) As LoginRequest
        If Payload Is Nothing Then Throw New ArgumentNullException("Payload")
        Dim Base As String = If(Server, "").TrimEnd("/"c)
        Return New LoginRequest With {
            .Method = "POST",
            .Url = Base & PortalLoginPath,
            .Body = EncodeFormData(Payload.ToLoginData()),
            .Headers = Headers
        }
    End Function

#End Region

#Region "TCP探针"

    Public Function TcpProbe(Url As String, Optional Timeout As Integer = 2,
                             Optional BindAddress As String = "") As Boolean
        Try
            Dim Host As String = StripScheme(Url)
            Dim Port As Integer = 80
            Dim ColonIdx As Integer = Host.IndexOf(":"c)
            If ColonIdx > 0 Then
                Integer.TryParse(Host.Substring(ColonIdx + 1), Port)
                Host = Host.Substring(0, ColonIdx)
            End If
            Dim SlashIdx As Integer = Host.IndexOf("/"c)
            If SlashIdx > 0 Then Host = Host.Substring(0, SlashIdx)

            Using Client As New Net.Sockets.TcpClient()
                ' 绑定本地地址：不绑的话会走默认路由，可能被 VPN/TUN 接管
                Dim Local As Net.IPAddress = Nothing
                If Not String.IsNullOrEmpty(BindAddress) AndAlso
                   Net.IPAddress.TryParse(BindAddress.Trim(), Local) Then
                    Try
                        Client.Client.Bind(New Net.IPEndPoint(Local, 0))
                    Catch
                        ' 绑不上就退回默认路由，不要因此判成「不可达」
                    End Try
                End If

                Dim Ar = Client.BeginConnect(Host, Port, Nothing, Nothing)
                If Ar.AsyncWaitHandle.WaitOne(Timeout * 1000) Then
                    Client.EndConnect(Ar)
                    Return True
                End If
                Return False
            End Using
        Catch
            Return False
        End Try
    End Function

#End Region

#Region "本地出口地址解析"

    ''' <summary>
    ''' 一个候选出口地址及其来源，供解析与诊断共用。
    ''' </summary>
    Public Class BindCandidate
        Public Property Address As String = ""
        Public Property InterfaceName As String = ""
        Public Property InterfaceType As String = ""
        ''' <summary>该网卡默认路由的 metric，越小越优先。没有默认路由时为 Integer.MaxValue。</summary>
        Public Property RouteMetric As Integer = Integer.MaxValue
        ''' <summary>被排除的原因；为空表示是可用的物理网卡。</summary>
        Public Property ExcludedReason As String = ""

        Public ReadOnly Property Usable As Boolean
            Get
                Return ExcludedReason.Length = 0
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 列出本机所有 IPv4 候选出口地址，并标注哪些是可用的物理网卡、哪些被排除及原因。
    '''
    ''' 之所以要做这一步：校园网客户端默认走系统默认路由，而 VPN / Clash TUN 之类的
    ''' 虚拟网卡常常把默认路由抢走（metric 更小）。**已认证时看不出问题**，
    ''' 一旦未认证，请求会被虚拟网卡掐断，表现为「网络探测失败」。
    ''' 因此认证前必须显式挑一个真实的校园网出口。
    ''' </summary>
    Public Function GetCandidateBindAddresses() As List(Of BindCandidate)
        Dim Result As New List(Of BindCandidate)
        Try
            For Each Nic In Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                If Nic.OperationalStatus <> Net.NetworkInformation.OperationalStatus.Up Then Continue For

                For Each Addr In Nic.GetIPProperties().UnicastAddresses
                    If Addr.Address.AddressFamily <> Net.Sockets.AddressFamily.InterNetwork Then Continue For

                    Dim C As New BindCandidate With {
                        .Address = Addr.Address.ToString(),
                        .InterfaceName = Nic.Name,
                        .InterfaceType = Nic.NetworkInterfaceType.ToString()
                    }
                    C.ExcludedReason = ExplainExclusion(Nic, C.Address)
                    Result.Add(C)
                Next
            Next
        Catch
        End Try

        ' 有线的真实网卡最可能是校园网出口；同类型按地址排序，保证多次调用顺序一致
        Return RankCandidates(Result)
    End Function

    ''' <summary>
    ''' 候选排序：可用优先 → 有线优先 → 地址字典序。
    ''' 纯函数，不读真实网卡，因此离线测试可以直接喂合成数据。
    ''' </summary>
    Public Function RankCandidates(Candidates As IEnumerable(Of BindCandidate)) As List(Of BindCandidate)
        If Candidates Is Nothing Then Return New List(Of BindCandidate)
        Return Candidates.OrderBy(Function(C) If(C.Usable, 0, 1)) _
                         .ThenBy(Function(C) TypeRank(C.InterfaceType)) _
                         .ThenBy(Function(C) If(C.Address, ""), StringComparer.Ordinal) _
                         .ToList()
    End Function

    ''' <summary>从候选里挑出最佳出口；全都不可用时返回 Nothing。</summary>
    Public Function PickBestCandidate(Candidates As IEnumerable(Of BindCandidate)) As BindCandidate
        Return RankCandidates(Candidates).FirstOrDefault(Function(C) C.Usable)
    End Function

    ''' <summary>有线优先，其次无线，其余最后。校园网通常是有线。</summary>
    Private Function TypeRank(InterfaceType As String) As Integer
        Select Case InterfaceType
            Case "Ethernet", "GigabitEthernet", "FastEthernetFx", "FastEthernetT"
                Return 0
            Case "Wireless80211"
                Return 1
            Case Else
                Return 2
        End Select
    End Function

    ''' <summary>
    ''' 判断某个地址是否该被排除，返回原因；返回空串表示可用。
    ''' 规则都是通用的（保留地址段 + 虚拟网卡特征），不针对某一所学校。
    ''' </summary>
    Private Function ExplainExclusion(Nic As Net.NetworkInformation.NetworkInterface, Address As String) As String
        Return ExplainExclusionFor(Nic.Name, Nic.Description,
                                   Nic.NetworkInterfaceType.ToString(), Address)
    End Function

    ''' <summary>
    ''' 排除规则的纯函数版本：只看「接口名 / 描述 / 类型 / 地址」，不碰真实网卡。
    ''' 离线测试直接喂合成数据即可，不依赖测试机的实际 IP。
    ''' </summary>
    Public Function ExplainExclusionFor(InterfaceName As String, Description As String,
                                        InterfaceType As String, Address As String) As String
        Address = If(Address, "").Trim()
        Dim Kind As String = If(InterfaceType, "").Trim()

        ' ---- 接口类型 ----
        If Kind.Equals("Loopback", StringComparison.OrdinalIgnoreCase) Then Return "Loopback"
        If Kind.Equals("Tunnel", StringComparison.OrdinalIgnoreCase) Then Return "Tunnel"

        ' ---- 保留 / 专用地址段 ----
        If Address.StartsWith("127.") Then Return "Loopback 地址段"
        If Address.StartsWith("169.254.") Then Return "APIPA（未取得 DHCP 地址）"
        ' 198.18.0.0/15 是 RFC 2544 基准测试保留段，真实校园网不会用它
        If Address.StartsWith("198.18.") OrElse Address.StartsWith("198.19.") Then Return "RFC 2544 保留段（常见于 TUN）"

        ' ---- 虚拟网卡特征 ----
        Dim Text As String = (If(InterfaceName, "") & " " & If(Description, "")).ToLowerInvariant()
        For Each Keyword In VirtualKeywords
            If Text.Contains(Keyword) Then Return "虚拟网卡（" & Keyword & "）"
        Next

        Return ""
    End Function

    ' 只用于识别「不是真实物理链路」的网卡。宁可漏判也不要误判 ——
    ' 误判会把用户真正的校园网卡排除掉，那才是灾难。
    Private ReadOnly VirtualKeywords As String() = {
        "tun", "tap", "wintun", "clash", "meta", "vpn", "wireguard", "openvpn",
        "zerotier", "tailscale", "radmin", "softether", "hyper-v", "vmware",
        "virtualbox", "vethernet", "docker", "wsl", "loopback", "bluetooth",
        "teredo", "isatap", "virtual"
    }

    ''' <summary>
    ''' 选出用于访问认证服务器的本机出口地址；没有合适候选时返回空串。
    ''' 这是 GUI「连接」与自动重连共用的唯一入口 —— 不要在页面里另写一套网卡选择。
    ''' </summary>
    Public Function ResolveBindAddress(Optional Server As String = "") As String
        Return ResolveBindAddressInfo(Server).Address
    End Function

    ''' <summary>与 <see cref="ResolveBindAddress"/> 同源，但额外给出「为什么选它」。</summary>
    Public Class BindResolution
        Public Property Address As String = ""
        ''' <summary>Physical / Fallback / None</summary>
        Public Property Source As String = "None"
        Public Property InterfaceName As String = ""
        Public Property Candidates As Integer = 0
        Public Property UsableCandidates As Integer = 0
        Public Property Note As String = ""
        ''' <summary>全部候选（含被排除的），供诊断逐条展示。</summary>
        Public Property All As New List(Of BindCandidate)
    End Class

    ''' <param name="Server">认证服务器地址（当前解析规则不依赖它，保留以便将来按网段判断）。</param>
    ''' <param name="Candidates">测试注入用；为空时读取本机真实网卡。</param>
    Public Function ResolveBindAddressInfo(Optional Server As String = "",
                                           Optional Candidates As List(Of BindCandidate) = Nothing) As BindResolution
        Dim Result As New BindResolution
        Dim All As List(Of BindCandidate) = If(Candidates, GetCandidateBindAddresses())
        Result.All = All
        Result.Candidates = All.Count

        Dim Usable As List(Of BindCandidate) = All.Where(Function(C) C.Usable).ToList()
        Result.UsableCandidates = Usable.Count

        Dim Best As BindCandidate = PickBestCandidate(All)
        If Best IsNot Nothing Then
            Result.Address = Best.Address
            Result.InterfaceName = Best.InterfaceName
            Result.Source = "Physical"
            Return Result
        End If

        ' 一个可用的物理网卡都没有：退而求其次，挑一个「不是明显保留」的地址，
        ' 总比完全不绑、任由 TUN 接管要好。
        Dim Fallback As BindCandidate = RankCandidates(All).FirstOrDefault(
            Function(C) Not If(C.Address, "").StartsWith("127.") AndAlso
                        Not If(C.Address, "").StartsWith("169.254."))
        If Fallback IsNot Nothing Then
            Result.Address = Fallback.Address
            Result.InterfaceName = Fallback.InterfaceName
            Result.Source = "Fallback"
            Result.Note = "没有识别到可用的物理网卡，退回第一个非保留地址"
            Return Result
        End If

        Result.Source = "None"
        Result.Note = "本机没有任何可用的 IPv4 出口地址"
        Return Result
    End Function

    ''' <summary>
    ''' 该地址现在是否仍然是本机在用的 IPv4。
    ''' 自动重连用它判断构造时记下的出口是否已失效（DHCP 换 IP、换网卡、Wi-Fi/有线切换）。
    ''' </summary>
    Public Function IsLocalAddressPresent(Address As String) As Boolean
        If String.IsNullOrEmpty(Address) Then Return False
        Try
            For Each Nic In Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                If Nic.OperationalStatus <> Net.NetworkInformation.OperationalStatus.Up Then Continue For
                For Each Addr In Nic.GetIPProperties().UnicastAddresses
                    If Addr.Address.AddressFamily = Net.Sockets.AddressFamily.InterNetwork AndAlso
                       Addr.Address.ToString() = Address.Trim() Then
                        Return True
                    End If
                Next
            Next
        Catch
        End Try
        Return False
    End Function

#End Region

End Module
