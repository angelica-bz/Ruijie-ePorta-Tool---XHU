Imports System.Security.Cryptography

''' <summary>
''' 开发期认证链路仪表（**只做统计，不参与任何业务判定**）。
'''
''' 存在的理由只有一个：真实验收时要能证明自动重连是**重新**走了一遍
'''     Discover → pageInfo → 加密 → login
''' 而不是复用了上一次的旧 queryString / 旧公钥 / 旧密文。
''' 光看「重连成功」是证明不了这一点的 —— 复用旧参数同样会成功。
'''
''' 记录两类信息：
'''   1. 各动态阶段的执行次数（DiscoveryCount / PageInfoCount / LoginCount）。
'''   2. 动态参数是否发生过变化（WlanUserIpChanged / PublicKeyChanged）。
'''
''' 安全约定：**绝不保存明文参数**。queryString 里的 wlanuserip、公钥等
''' 一律先做 SHA-256 指纹再留存，只用来比「一样 / 不一样」，
''' 因此本模块内存里没有可还原的真实取值，输出端也无从泄露。
''' </summary>
Public Module ModAuthTrace

    Private ReadOnly _Lock As New Object()

    Private _DiscoveryCount As Integer
    Private _PageInfoCount As Integer
    Private _LoginCount As Integer
    Private _PasswordEncryptionCount As Integer
    Private _ReconnectCount As Integer

    ' 指纹（非明文）——只用于判断「是否变化」
    Private _WlanUserIpFirst As String = ""
    Private _WlanUserIpLast As String = ""
    Private _WlanUserIpObservations As Integer
    Private _WlanUserIpChanged As Boolean

    Private _PublicKeyFirst As String = ""
    Private _PublicKeyLast As String = ""
    Private _PublicKeyObservations As Integer
    Private _PublicKeyChanged As Boolean

#Region "计数"

    Public ReadOnly Property DiscoveryCount As Integer
        Get
            SyncLock _Lock
                Return _DiscoveryCount
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property PageInfoCount As Integer
        Get
            SyncLock _Lock
                Return _PageInfoCount
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property LoginCount As Integer
        Get
            SyncLock _Lock
                Return _LoginCount
            End SyncLock
        End Get
    End Property

    ''' <summary>门户发现（含探测重定向）执行次数。</summary>
    Public Sub CountDiscovery()
        SyncLock _Lock
            _DiscoveryCount += 1
        End SyncLock
    End Sub

    ''' <summary>pageInfo（取动态公钥）执行次数。</summary>
    Public Sub CountPageInfo()
        SyncLock _Lock
            _PageInfoCount += 1
        End SyncLock
    End Sub

    ''' <summary>login 请求发送次数。</summary>
    Public Sub CountLogin()
        SyncLock _Lock
            _LoginCount += 1
        End SyncLock
    End Sub

    ''' <summary>用门户动态公钥加密密码的次数。插桩点在 ModAuth，以保持 ModCrypto 纯函数。</summary>
    Public ReadOnly Property PasswordEncryptionCount As Integer
        Get
            SyncLock _Lock
                Return _PasswordEncryptionCount
            End SyncLock
        End Get
    End Property

    Public Sub CountPasswordEncryption()
        SyncLock _Lock
            _PasswordEncryptionCount += 1
        End SyncLock
    End Sub

    ''' <summary>NetworkMonitor 尝试自动重连的次数。</summary>
    Public ReadOnly Property ReconnectCount As Integer
        Get
            SyncLock _Lock
                Return _ReconnectCount
            End SyncLock
        End Get
    End Property

    Public Sub CountReconnect()
        SyncLock _Lock
            _ReconnectCount += 1
        End SyncLock
    End Sub

    ''' <summary>
    ''' 自动重连是否真的重新走了动态链路。
    ''' 判据：重连窗口内 Discovery / pageInfo / 密码加密 / login 四项都至少发生过一次。
    ''' 只有四项齐全，才能说重连用的是新 queryString + 新公钥 + 新密文，
    ''' 而不是复用了上一次的旧参数。
    ''' </summary>
    Public ReadOnly Property ReconnectUsedDynamicDiscovery As Boolean
        Get
            SyncLock _Lock
                Return _ReconnectCount >= 1 AndAlso
                       _DiscoveryCount >= 1 AndAlso
                       _PageInfoCount >= 1 AndAlso
                       _PasswordEncryptionCount >= 1 AndAlso
                       _LoginCount >= 1
            End SyncLock
        End Get
    End Property

#End Region

#Region "动态参数变化检测"

    Public ReadOnly Property WlanUserIpObservations As Integer
        Get
            SyncLock _Lock
                Return _WlanUserIpObservations
            End SyncLock
        End Get
    End Property

    ''' <summary>多次观测中 wlanuserip 是否出现过变化。</summary>
    Public ReadOnly Property WlanUserIpChanged As Boolean
        Get
            SyncLock _Lock
                Return _WlanUserIpChanged
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property PublicKeyObservations As Integer
        Get
            SyncLock _Lock
                Return _PublicKeyObservations
            End SyncLock
        End Get
    End Property

    ''' <summary>多次观测中门户公钥是否出现过变化。</summary>
    Public ReadOnly Property PublicKeyChanged As Boolean
        Get
            SyncLock _Lock
                Return _PublicKeyChanged
            End SyncLock
        End Get
    End Property

    ''' <summary>
    ''' 记录一次 queryString 观测。内部只留 wlanuserip 的指纹，不留原文。
    ''' </summary>
    Public Sub ObserveQueryString(QueryString As String)
        If String.IsNullOrEmpty(QueryString) Then Return

        Dim WlanUserIp As String = ""
        Try
            Dim Parameters As Dictionary(Of String, String) =
                ModPortalDiscover.ParseQueryParameters(QueryString)
            If Parameters.ContainsKey("wlanuserip") Then WlanUserIp = Parameters("wlanuserip")
        Catch
            Return
        End Try

        If String.IsNullOrEmpty(WlanUserIp) Then Return
        Dim Print As String = Fingerprint(WlanUserIp)

        SyncLock _Lock
            _WlanUserIpObservations += 1
            If _WlanUserIpObservations = 1 Then
                _WlanUserIpFirst = Print
            ElseIf Print <> _WlanUserIpFirst Then
                _WlanUserIpChanged = True
            End If
            _WlanUserIpLast = Print
        End SyncLock
    End Sub

    ''' <summary>记录一次公钥观测。内部只留指纹，不留原文。</summary>
    Public Sub ObservePublicKey(ModulusHex As String, ExponentHex As String)
        If String.IsNullOrEmpty(ModulusHex) Then Return
        Dim Print As String = Fingerprint(If(ModulusHex, "").Trim().ToLowerInvariant() & "|" &
                                          If(ExponentHex, "").Trim().ToLowerInvariant())

        SyncLock _Lock
            _PublicKeyObservations += 1
            If _PublicKeyObservations = 1 Then
                _PublicKeyFirst = Print
            ElseIf Print <> _PublicKeyFirst Then
                _PublicKeyChanged = True
            End If
            _PublicKeyLast = Print
        End SyncLock
    End Sub

#End Region

#Region "最近一次门户 HTTP 响应"

    ' 只记录状态码与正文长度，不含正文内容（正文里可能有会话信息）
    Private _LastHttpStatus As Integer
    Private _LastBodyLength As Integer
    Private _LastHttpUrl As String = ""

    ''' <summary>最近一次门户 HTTP 响应的状态码；0 表示还没发过请求。</summary>
    Public ReadOnly Property LastHttpStatus As Integer
        Get
            SyncLock _Lock
                Return _LastHttpStatus
            End SyncLock
        End Get
    End Property

    ''' <summary>最近一次门户 HTTP 响应的正文字节长度。</summary>
    Public ReadOnly Property LastBodyLength As Integer
        Get
            SyncLock _Lock
                Return _LastBodyLength
            End SyncLock
        End Get
    End Property

    ''' <summary>最近一次门户请求的 method 名（pageInfo / getServices / login / logout）。</summary>
    Public ReadOnly Property LastMethod As String
        Get
            SyncLock _Lock
                Return _LastHttpUrl
            End SyncLock
        End Get
    End Property

    ''' <summary>记录一次门户 HTTP 响应。只留状态码与长度，不留正文。</summary>
    Public Sub ObserveHttpResponse(StatusCode As Integer, BodyLength As Integer, Optional Method As String = "")
        SyncLock _Lock
            _LastHttpStatus = StatusCode
            _LastBodyLength = BodyLength
            _LastHttpUrl = If(Method, "")
        End SyncLock
    End Sub

#End Region

#Region "复位与工具"

    ''' <summary>
    ''' 只清零三个阶段计数，**保留**动态参数观测记录。
    ''' 验收时用来划出一个统计窗口：窗口之前先观测一次参数作为基线，
    ''' 窗口内（自动重连）再观测一次，就能判断参数到底变没变。
    ''' </summary>
    Public Sub ResetCounters()
        SyncLock _Lock
            _DiscoveryCount = 0
            _PageInfoCount = 0
            _LoginCount = 0
            _PasswordEncryptionCount = 0
            _ReconnectCount = 0
        End SyncLock
    End Sub

    ''' <summary>清零全部计数与观测。验收时在每个阶段窗口开始前调用。</summary>
    Public Sub Reset()
        SyncLock _Lock
            _DiscoveryCount = 0
            _PageInfoCount = 0
            _LoginCount = 0
            _PasswordEncryptionCount = 0
            _ReconnectCount = 0

            _WlanUserIpFirst = ""
            _WlanUserIpLast = ""
            _WlanUserIpObservations = 0
            _WlanUserIpChanged = False

            _PublicKeyFirst = ""
            _PublicKeyLast = ""
            _PublicKeyObservations = 0
            _PublicKeyChanged = False

            _LastHttpStatus = 0
            _LastBodyLength = 0
            _LastHttpUrl = ""
        End SyncLock
    End Sub

    ''' <summary>
    ''' 单向指纹。用途仅限于「比较两次观测是否相同」，
    ''' 因此本模块从不持有可还原的 wlanuserip / 公钥原文。
    ''' </summary>
    Private Function Fingerprint(Value As String) As String
        If String.IsNullOrEmpty(Value) Then Return ""
        Using Sha As SHA256 = SHA256.Create()
            Return Convert.ToBase64String(Sha.ComputeHash(Text.Encoding.UTF8.GetBytes(Value)))
        End Using
    End Function

#End Region

End Module
