Imports System.IO
Imports Microsoft.VisualBasic

''' <summary>
''' 配置模块。
'''
''' v4 配置（面向西华大学用户）只保存用户真正需要维护的信息：
'''
'''     main:
'''       version: 4
'''     account:
'''       user_id: "1234567890123"          ' 学号，永远按字符串保存
'''       password_protected: "BASE64..."   ' DPAPI 保护后的密码
'''     auth:
'''       operator: telecom                 ' telecom / mobile / unicom / xhu / office
'''     function:
'''       auto_reconnect: false
'''       reconnect_interval: 5
'''
''' 以下属于认证运行时数据，v4 不再保存（由 ModPortalDiscover / ModAuthentication 动态获得）：
'''     url / cookie / login_data / headers / logout_data
'''     service / queryString / passwordEncrypt / operatorPwd / operatorUserId / validcode
'''
''' 兼容层：<see cref="ReadCfg"/> 仍然返回旧版结构的 Dictionary（url/cookie/login_data/headers
''' 由学校固定常量与 v4 用户数据合成），以便 PageStatus、PageConfig 与 NetworkMonitor 在
''' 后续接线阶段平滑过渡。这些合成出来的字段不会写回磁盘。
''' </summary>
Public Module ModConfig

    ''' <summary>当前配置格式版本。</summary>
    Public Const CurrentConfigVersion As Integer = 4

    ''' <summary>西华大学门户固定地址与接口路径（v4 不再让用户配置）。</summary>
    Public Const SchoolServer As String = "http://202.115.144.51"
    Public Const SchoolLoginPath As String = "/eportal/InterFace.do?method=login"
    Public Const SchoolLogoutPath As String = "/eportal/InterFace.do?method=logout"

    ''' <summary>v3 备份文件名后缀（与 config.yml 同目录）。</summary>
    Public Const LegacyBackupSuffix As String = ".v3.bak"

#Region "路径"

    Public Function GetConfigPath() As String
        Return Path.Combine(PathExeFolder, "config.yml")
    End Function

    Public Function GetLogsDir() As String
        Return Path.Combine(PathExeFolder, "logs")
    End Function

#End Region

#Region "配置键"

    Public Class ConfigKeys
        Public Const Main As String = "main"
        Public Const Version As String = "version"
        Public Const FunctionSection As String = "function"
        Public Const AutoReconnect As String = "auto_reconnect"
        Public Const ReconnectInterval As String = "reconnect_interval"

        ' ---- v4 ----
        Public Const Account As String = "account"
        Public Const UserId As String = "user_id"
        Public Const PasswordProtected As String = "password_protected"
        Public Const Auth As String = "auth"
        Public Const OperatorKey As String = "operator"

        ' ---- 以下仅用于旧结构兼容层，v4 不再落盘 ----
        Public Const Url As String = "url"
        Public Const Server As String = "server"
        Public Const Login As String = "login"
        Public Const Logout As String = "logout"
        Public Const Cookie As String = "cookie"
        Public Const LoginData As String = "login_data"
        Public Const LogoutData As String = "logout_data"
        Public Const Headers As String = "headers"
    End Class

    ''' <summary>
    ''' 永远按字符串解析的键。学号一旦经过数字推断就可能被改写（前导零丢失、
    ''' 超过 15 位丢精度），因此这里显式豁免，不依赖通用 YAML 类型推断。
    ''' </summary>
    Private ReadOnly AlwaysStringKeys As String() = {
        ConfigKeys.UserId, "userId", "password", ConfigKeys.PasswordProtected
    }

    Private Function IsAlwaysStringKey(Key As String) As Boolean
        If Key Is Nothing Then Return False
        For Each Item In AlwaysStringKeys
            If String.Equals(Item, Key, StringComparison.OrdinalIgnoreCase) Then Return True
        Next
        Return False
    End Function

#End Region

#Region "v4 数据模型"

    ''' <summary>用户配置层：只包含用户真正需要提供的信息。</summary>
    Public Class PortalUserConfig
        ''' <summary>学号。始终按 String 处理，绝不经过数字类型。</summary>
        Public Property UserId As String = ""

        Private _Password As String = ""
        ''' <summary>运行期明文密码。不直接落盘（落盘走 password_protected）。</summary>
        Public Property Password As String
            Get
                Return _Password
            End Get
            Set(value As String)
                _Password = If(value, "")
                PasswordChanged = True
            End Set
        End Property

        ''' <summary>
        ''' 本次会话中密码是否被显式改过。
        ''' 为 False 且磁盘上已有密文时，保存会原样复用原密文 ——
        ''' 这样「只改学号或运营商」不会无谓地重新加密，密文也没有任何变化。
        ''' </summary>
        Public Property PasswordChanged As Boolean = False

        ''' <summary>从配置读到的 password_protected 原文（未解密）。</summary>
        Public Property PasswordProtectedRaw As String = ""

        ''' <summary>运营商。无法识别时为 Unknown，绝不静默归为某个真实运营商。</summary>
        Public Property [Operator] As PortalOperator = PortalOperator.Unknown

        ''' <summary>由配置解析调用：装载明文与原始密文，但不标记为「已修改」。</summary>
        Public Sub SetLoadedPassword(PlainText As String, ProtectedRaw As String)
            _Password = If(PlainText, "")
            PasswordProtectedRaw = If(ProtectedRaw, "")
            PasswordChanged = False
        End Sub
    End Class

    ''' <summary>运行期功能开关。</summary>
    Public Class RuntimeFunctionConfig
        Public Property AutoReconnect As Boolean = False
        Public Property ReconnectInterval As Integer = 5
    End Class

    ''' <summary>应用配置。这是其它模块读取配置的统一入口。</summary>
    Public Class AppConfig
        Public Property Version As Integer = CurrentConfigVersion
        Public Property User As New PortalUserConfig()
        Public Property [Function] As New RuntimeFunctionConfig()

        ''' <summary>密码尚未保存或无法解密，需要用户重新输入。</summary>
        Public Property PasswordNeedsReentry As Boolean = False
        ''' <summary>迁移时无法识别的旧 service 原文（供 UI 提示用户重新选择）。</summary>
        Public Property UnknownServiceRaw As String = ""
        ''' <summary>迁移备份文件路径（若发生过迁移）。</summary>
        Public Property BackupPath As String = ""
        ''' <summary>读取/迁移过程中的告警，可直接展示给用户。</summary>
        Public Property Warnings As New List(Of String)

        ''' <summary>是否可以直接用于认证。</summary>
        Public ReadOnly Property IsReadyForAuthentication As Boolean
            Get
                Return NotReadyReason.Length = 0
            End Get
        End Property

        ''' <summary>
        ''' 配置不可用于认证时的可展示原因；可用时返回空串。
        ''' 调用方应在发起任何网络请求之前先用它给出提示（不要把无效请求发出去）。
        ''' </summary>
        Public ReadOnly Property NotReadyReason As String
            Get
                If User Is Nothing Then Return "配置为空，请重新填写。"
                If String.IsNullOrEmpty(User.UserId) Then Return "还没有填写学号，请先在配置页填写。"
                If [Function] Is Nothing Then Return "配置缺少功能设置段。"
                If String.IsNullOrEmpty(User.Password) Then
                    If PasswordNeedsReentry AndAlso Warnings.Count > 0 Then
                        Return "配置中的密码无法解密，请重新输入密码。"
                    End If
                    Return "还没有保存密码，请先在配置页填写密码。"
                End If
                If User.[Operator] = PortalOperator.Unknown Then
                    If Not String.IsNullOrEmpty(UnknownServiceRaw) Then
                        Return "无法识别原来的运营商（" & UnknownServiceRaw & "），请重新选择。"
                    End If
                    Return "还没有选择运营商，请先在配置页选择。"
                End If
                Return ""
            End Get
        End Property

        ''' <summary>
        ''' 转换成认证层需要的账户对象。密码使用已解密的运行期明文
        ''' （LoadAppConfig 时经 ModCredential.TryUnprotectPassword 得到）。
        ''' </summary>
        Public Function ToPortalAccount(Optional ValidCode As String = "") As PortalAccount
            If User Is Nothing Then Return Nothing
            Return New PortalAccount With {
                .UserId = User.UserId,
                .Password = User.Password,
                .[Operator] = User.[Operator],
                .ValidCode = If(ValidCode, "")
            }
        End Function
    End Class

    ''' <summary>v3 → v4 迁移结果。</summary>
    Public Class MigrationResult
        Public Property Migrated As Boolean = False
        Public Property FromVersion As Integer = 0
        Public Property ToVersion As Integer = CurrentConfigVersion
        Public Property BackupPath As String = ""
        Public Property Config As AppConfig
        Public Property Message As String = ""
    End Class

#End Region

#Region "YAML 手动解析"

    Private Function ParseYamlLines(Lines As String()) As Dictionary(Of String, Object)
        Dim Result As New Dictionary(Of String, Object)
        Dim CurrentSection As String = ""
        Dim CurrentDict As Dictionary(Of String, Object) = Nothing

        For Each RawLine As String In Lines
            Dim Line As String = RawLine.TrimEnd(vbCr, vbLf)
            Dim Trimmed As String = Line.TrimStart()

            If Trimmed = "" OrElse Trimmed.StartsWith("#") Then Continue For

            Dim Indent As Integer = Line.Length - Line.TrimStart().Length

            If Indent = 0 AndAlso Trimmed.EndsWith(":") Then
                CurrentSection = Trimmed.TrimEnd(":"c)
                CurrentDict = New Dictionary(Of String, Object)
                Result(CurrentSection) = CurrentDict
                Continue For
            End If

            If Indent = 0 AndAlso Trimmed.Contains(":") Then
                Dim ColonIdx As Integer = Trimmed.IndexOf(":"c)
                Dim Key As String = Trimmed.Substring(0, ColonIdx).Trim()
                Dim Value As String = Trimmed.Substring(ColonIdx + 1).Trim()
                Value = StripInlineComment(Value)
                Value = TrimQuoted(Value)
                Result(Key) = ParseValue(Key, Value)
                Continue For
            End If

            If Indent > 0 AndAlso CurrentDict IsNot Nothing AndAlso Trimmed.Contains(":") Then
                Dim ColonIdx As Integer = Trimmed.IndexOf(":"c)
                Dim Key As String = Trimmed.Substring(0, ColonIdx).Trim()
                Dim Value As String = Trimmed.Substring(ColonIdx + 1).Trim()
                Value = StripInlineComment(Value)
                Value = TrimQuoted(Value)
                CurrentDict(Key) = ParseValue(Key, Value)
                Continue For
            End If
        Next

        Return Result
    End Function

    Private Function StripInlineComment(Value As String) As String
        If Value = "" Then Return Value
        Dim InQuote As Boolean = False
        Dim QuoteChar As Char = Nothing
        For i As Integer = 0 To Value.Length - 1
            Dim c As Char = Value(i)
            If InQuote Then
                If c = QuoteChar Then InQuote = False
            Else
                If c = "'"c OrElse c = """"c Then
                    InQuote = True
                    QuoteChar = c
                ElseIf c = "#"c Then
                    Return Value.Substring(0, i).Trim()
                End If
            End If
        Next
        Return Value
    End Function

    Private Function TrimQuoted(Value As String) As String
        If Value.Length >= 2 Then
            If (Value.StartsWith("'") AndAlso Value.EndsWith("'")) OrElse
               (Value.StartsWith("""") AndAlso Value.EndsWith("""")) Then
                Return Value.Substring(1, Value.Length - 2)
            End If
        End If
        Return Value
    End Function

    ''' <summary>
    ''' 标量解析。Key 参与判断：学号一类键永远返回字符串。
    ''' 数字只有在能精确往返时才转换，避免大整数被改写（学号历史上曾因此丢精度）。
    ''' </summary>
    Private Function ParseValue(Key As String, Value As String) As Object
        If Value = "" Then Return Nothing

        Dim Lower As String = Value.ToLower()
        If Lower = "true" Then Return True
        If Lower = "false" Then Return False
        If Lower = "null" OrElse Lower = "~" Then Return Nothing

        ' 学号 / 密码一类的键：不做任何数字推断
        If IsAlwaysStringKey(Key) Then Return Value

        ' 前导零（例如 001234567890）必须保持字符串形态
        If Value.Length > 1 AndAlso Value.StartsWith("0") AndAlso Not Value.Contains(".") Then Return Value

        Dim Invariant = Globalization.CultureInfo.InvariantCulture
        Dim IntVal As Integer
        If Integer.TryParse(Value, Globalization.NumberStyles.Integer, Invariant, IntVal) AndAlso
           IntVal.ToString(Invariant) = Value Then
            Return IntVal
        End If
        Dim LongVal As Long
        If Long.TryParse(Value, Globalization.NumberStyles.Integer, Invariant, LongVal) AndAlso
           LongVal.ToString(Invariant) = Value Then
            Return LongVal
        End If
        Dim DblVal As Double
        If Double.TryParse(Value, Globalization.NumberStyles.Any, Invariant, DblVal) AndAlso
           DblVal.ToString("R", Invariant) = Value Then
            Return DblVal
        End If
        Return Value
    End Function

#End Region

#Region "YAML 输出"

    Private Function QuoteYaml(Value As String) As String
        Dim Text As String = If(Value, "")
        If Text.Contains("'") Then
            Return """" & Text.Replace("""", """""") & """"
        End If
        Return "'" & Text & "'"
    End Function

    Private Function DumpYaml(Cfg As Dictionary(Of String, Object), Optional Indent As Integer = 0) As String
        Dim Sb As New Text.StringBuilder()
        Dim Prefix As String = New String(" "c, Indent)
        For Each Kvp In Cfg
            If TypeOf Kvp.Value Is Dictionary(Of String, Object) Then
                Sb.AppendLine(Prefix & Kvp.Key & ":")
                Sb.Append(DumpYaml(CType(Kvp.Value, Dictionary(Of String, Object)), Indent + 2))
            ElseIf Kvp.Value Is Nothing Then
                Sb.AppendLine(Prefix & Kvp.Key & ":")
            ElseIf TypeOf Kvp.Value Is Boolean Then
                Sb.AppendLine(Prefix & Kvp.Key & ": " & Kvp.Value.ToString().ToLower())
            Else
                Dim Val As String = Kvp.Value.ToString()
                ' 学号一类必须带引号，否则会被 YAML 当数字读回
                If IsAlwaysStringKey(Kvp.Key) OrElse Val.Contains(":") OrElse Val.Contains("#") OrElse Val = "" Then
                    Sb.AppendLine(Prefix & Kvp.Key & ": " & QuoteYaml(Val))
                Else
                    Sb.AppendLine(Prefix & Kvp.Key & ": " & Val)
                End If
            End If
        Next
        Return Sb.ToString()
    End Function

#End Region

#Region "v4 读取与写入"

    ''' <summary>v4 默认配置。</summary>
    Public Function GetDefaultAppConfig() As AppConfig
        Return New AppConfig With {
            .Version = CurrentConfigVersion,
            .User = New PortalUserConfig With {
                .UserId = "",
                .Password = "",
                .[Operator] = PortalOperator.Unknown
            },
            .[Function] = New RuntimeFunctionConfig With {
                .AutoReconnect = False,
                .ReconnectInterval = 5
            },
            .PasswordNeedsReentry = True
        }
    End Function

    ''' <summary>
    ''' 读取 v4 配置。遇到 v3 会自动迁移（含备份）。
    ''' 这是其它模块读取配置的统一入口。
    ''' </summary>
    Public Function LoadAppConfig(Optional GuiMode As Boolean = False) As AppConfig
        Return LoadAppConfigFrom(GetConfigPath())
    End Function

    ''' <summary>
    ''' 从指定路径读取 v4 配置（测试用；生产代码请用 LoadAppConfig）。
    ''' </summary>
    Public Function LoadAppConfigFrom(ConfigPath As String) As AppConfig
        If Not File.Exists(ConfigPath) Then
            Dim Fresh As AppConfig = GetDefaultAppConfig()
            WriteConfigFile(ConfigPath, Fresh)
            Return Fresh
        End If

        Dim Raw As Dictionary(Of String, Object) = ReadRawConfig(ConfigPath)
        If Raw Is Nothing OrElse Raw.Count = 0 Then
            Log("配置文件无法解析，已回退到默认配置")
            Dim Fallback As AppConfig = GetDefaultAppConfig()
            Fallback.Warnings.Add("配置文件无法解析，已回退到默认配置。")
            Return Fallback
        End If

        Dim Version As Integer = GetConfigVersion(Raw)

        If Version > CurrentConfigVersion Then
            Dim TooNew As AppConfig = GetDefaultAppConfig()
            TooNew.Warnings.Add("配置文件格式版本为 v" & Version & "，高于本程序支持的 v" &
                                CurrentConfigVersion & "，请更新本程序。")
            Log("警告：配置文件版本过高（v" & Version & "）")
            Return TooNew
        End If

        If Version < CurrentConfigVersion Then
            Dim Migration As MigrationResult = MigrateToV4(ConfigPath, Raw)
            If Migration.Config IsNot Nothing Then Return Migration.Config
            Dim Failed As AppConfig = GetDefaultAppConfig()
            Failed.Warnings.Add(Migration.Message)
            Return Failed
        End If

        Return ParseAppConfig(Raw)
    End Function

    ''' <summary>保存 v4 配置。密码在此处经 DPAPI 保护后落盘。</summary>
    Public Sub SaveAppConfig(Cfg As AppConfig)
        SaveAppConfigTo(GetConfigPath(), Cfg)
    End Sub

    ''' <summary>
    ''' 写入指定路径（测试用；生产代码请用 SaveAppConfig）。
    ''' </summary>
    Public Sub SaveAppConfigTo(ConfigPath As String, Cfg As AppConfig)
        If Cfg Is Nothing Then Return
        Cfg.Version = CurrentConfigVersion
        WriteConfigFile(ConfigPath, Cfg)
    End Sub

    ''' <summary>当前的运行期功能配置。</summary>
    Public Function GetCurrentFunctionConfig() As RuntimeFunctionConfig
        Return LoadAppConfig().[Function]
    End Function

    Private Function ReadRawConfig(ConfigPath As String) As Dictionary(Of String, Object)
        Try
            Dim Lines As String() = File.ReadAllLines(ConfigPath, Text.Encoding.UTF8)
            Return ParseYamlLines(Lines)
        Catch ex As Exception
            Log(ex, "读取配置文件失败")
            Return Nothing
        End Try
    End Function

    Private Function GetConfigVersion(Raw As Dictionary(Of String, Object)) As Integer
        Dim Main = GetSubDict(Raw, ConfigKeys.Main)
        If Main Is Nothing Then Return 0
        Return GetDictInt(Main, ConfigKeys.Version, 0)
    End Function

    ''' <summary>把已解析的 v4 字段装进强类型模型。</summary>
    Public Function ParseAppConfig(Raw As Dictionary(Of String, Object)) As AppConfig
        Dim Result As AppConfig = GetDefaultAppConfig()
        Result.Version = GetConfigVersion(Raw)

        ' ---- account ----
        Dim Account = GetSubDict(Raw, ConfigKeys.Account)
        If Account IsNot Nothing Then
            Result.User.UserId = GetRawString(Account, ConfigKeys.UserId)
            If Result.User.UserId.Length = 0 Then
                Result.Warnings.Add("配置里没有填写学号。")
            End If

            Dim ProtectedRaw As String = GetRawString(Account, ConfigKeys.PasswordProtected)
            If ProtectedRaw.Length = 0 Then
                Result.User.SetLoadedPassword("", "")
                Result.PasswordNeedsReentry = True
            Else
                Dim Plain As String = ""
                If ModCredential.TryUnprotectPassword(ProtectedRaw, Plain) Then
                    Result.User.SetLoadedPassword(Plain, ProtectedRaw)
                    Result.PasswordNeedsReentry = (Plain.Length = 0)
                Else
                    Result.User.SetLoadedPassword("", ProtectedRaw)
                    Result.PasswordNeedsReentry = True
                    ' 刻意不输出密文片段
                    Result.Warnings.Add("已保存的密码无法解密（可能来自其它 Windows 用户或已损坏），请重新输入密码。")
                End If
            End If
        Else
            Result.PasswordNeedsReentry = True
        End If

        ' ---- auth ----
        Dim AuthSection = GetSubDict(Raw, ConfigKeys.Auth)
        If AuthSection IsNot Nothing Then
            Dim Token As String = GetRawString(AuthSection, ConfigKeys.OperatorKey)
            Result.User.[Operator] = ParseOperatorToken(Token, Result)
        End If

        ' ---- function ----
        Dim FunctionSection = GetSubDict(Raw, ConfigKeys.FunctionSection)
        If FunctionSection IsNot Nothing Then
            Result.[Function].AutoReconnect = GetDictBool(FunctionSection, ConfigKeys.AutoReconnect, False)
            Result.[Function].ReconnectInterval = GetDictInt(FunctionSection, ConfigKeys.ReconnectInterval, 5)
        End If
        If Result.[Function].ReconnectInterval < 1 Then Result.[Function].ReconnectInterval = 1
        If Result.[Function].ReconnectInterval > 99 Then Result.[Function].ReconnectInterval = 99

        Return Result
    End Function

    ''' <summary>读原始字符串，绝不走数字推断。</summary>
    Private Function GetRawString(Dict As Dictionary(Of String, Object), Key As String) As String
        If Dict Is Nothing OrElse Not Dict.ContainsKey(Key) Then Return ""
        Dim Value = Dict(Key)
        If Value Is Nothing Then Return ""
        Return Value.ToString().Trim()
    End Function

    ''' <summary>把 auth.operator 文本解析成运营商枚举；无法识别时返回 Unknown 并记录原文。</summary>
    Private Function ParseOperatorToken(Token As String, ByRef Config As AppConfig) As PortalOperator
        If String.IsNullOrEmpty(Token) Then Return PortalOperator.Unknown
        If String.Equals(Token, "unknown", StringComparison.OrdinalIgnoreCase) Then Return PortalOperator.Unknown

        Dim Parsed As PortalOperator = PortalOperator.Unknown
        If ModAuth.TryParseOperator(Token, Parsed) Then Return Parsed

        Config.UnknownServiceRaw = Token
        Config.Warnings.Add("无法识别的运营商：" & Token & "，请重新选择。")
        Return PortalOperator.Unknown
    End Function

    ''' <summary>运营商 → 配置里的操作符文本。</summary>
    Public Function GetOperatorToken(Op As PortalOperator) As String
        Select Case Op
            Case PortalOperator.Office : Return "office"
            Case PortalOperator.Telecom : Return "telecom"
            Case PortalOperator.Mobile : Return "mobile"
            Case PortalOperator.Unicom : Return "unicom"
            Case PortalOperator.Xhu : Return "xhu"
            Case Else : Return "unknown"
        End Select
    End Function

    ''' <summary>把 v4 模型序列化成配置文件文本。</summary>
    Public Function BuildConfigText(Cfg As AppConfig) As String
        If Cfg Is Nothing Then Cfg = GetDefaultAppConfig()

        Dim Header As String =
            "# 本配置文件内容需要根据学校服务器设置动态调整" & vbCrLf &
            "# 通过 GUI 配置面板生成／修改" & vbCrLf &
            "# 密码使用 Windows DPAPI（当前用户）加密后保存，文件中看不到明文" & vbCrLf &
            "# 认证所需的服务器地址、queryString、Cookie 等均为运行期数据，不再写入本文件" & vbCrLf & vbCrLf

        Dim Sb As New Text.StringBuilder()
        Sb.AppendLine("main:")
        Sb.AppendLine("  version: " & CurrentConfigVersion)
        Sb.AppendLine()
        Sb.AppendLine("account:")
        Sb.AppendLine("  " & ConfigKeys.UserId & ": " & QuoteYaml(If(Cfg.User.UserId, "")))
        ' 密码没被改过且磁盘上已有密文 → 原样复用，密文保持逐字符不变；
        ' 只有确实改动过（或从未保存过）才重新做 DPAPI 保护。
        Dim ProtectedValue As String
        If (Not Cfg.User.PasswordChanged) AndAlso Cfg.User.PasswordProtectedRaw.Length > 0 Then
            ProtectedValue = Cfg.User.PasswordProtectedRaw
        Else
            ProtectedValue = ModCredential.ProtectPassword(Cfg.User.Password)
        End If
        Sb.AppendLine("  " & ConfigKeys.PasswordProtected & ": " & QuoteYaml(ProtectedValue))
        Sb.AppendLine()
        ' 无法识别的旧 service 原样写回：这样下次加载仍会得到 Unknown + 原始值，
        ' 配置页才能继续提示用户重新选择，而不是把线索丢掉。
        Dim OperatorToken As String = GetOperatorToken(Cfg.User.[Operator])
        If Cfg.User.[Operator] = PortalOperator.Unknown AndAlso
           Not String.IsNullOrEmpty(Cfg.UnknownServiceRaw) Then
            OperatorToken = Cfg.UnknownServiceRaw
        End If
        Sb.AppendLine("auth:")
        Sb.AppendLine("  " & ConfigKeys.OperatorKey & ": " & QuoteYaml(OperatorToken))
        Sb.AppendLine()
        Sb.AppendLine("function:")
        Sb.AppendLine("  " & ConfigKeys.AutoReconnect & ": " & Cfg.[Function].AutoReconnect.ToString().ToLower())
        Sb.AppendLine("  " & ConfigKeys.ReconnectInterval & ": " & Cfg.[Function].ReconnectInterval)

        Return Header & Sb.ToString()
    End Function

    Private Sub WriteConfigFile(ConfigPath As String, Cfg As AppConfig)
        Try
            Dim Dir = Path.GetDirectoryName(ConfigPath)
            If Not String.IsNullOrEmpty(Dir) AndAlso Not Directory.Exists(Dir) Then Directory.CreateDirectory(Dir)
            File.WriteAllText(ConfigPath, BuildConfigText(Cfg), Text.Encoding.UTF8)
        Catch ex As Exception
            Log(ex, "保存配置文件失败")
        End Try
    End Sub

#End Region

#Region "v3 → v4 迁移"

    ''' <summary>
    ''' 读取配置里保存的 password_protected 原文（不解密）。
    ''' 配置界面据此区分「从未保存过密码」与「保存了但解不开」，两种情况提示不同。
    ''' </summary>
    Public Function ReadStoredProtectedPassword(Optional ConfigPath As String = "") As String
        Dim ConfigFile As String = If(String.IsNullOrEmpty(ConfigPath), GetConfigPath(), ConfigPath)
        If Not File.Exists(ConfigFile) Then Return ""
        Dim Raw As Dictionary(Of String, Object) = ReadRawConfig(ConfigFile)
        If Raw Is Nothing Then Return ""
        Dim Account = GetSubDict(Raw, ConfigKeys.Account)
        If Account Is Nothing Then Return ""
        Return GetRawString(Account, ConfigKeys.PasswordProtected)
    End Function

    ''' <summary>是否还存在 v3 迁移备份（用于「旧配置已迁移」提示）。</summary>
    Public Function LegacyBackupExists(Optional ConfigPath As String = "") As Boolean
        Return File.Exists(GetLegacyBackupPath(ConfigPath))
    End Function

    ''' <summary>v3 备份文件路径。</summary>
    Public Function GetLegacyBackupPath(Optional ConfigPath As String = "") As String
        Dim ConfigFile As String = If(String.IsNullOrEmpty(ConfigPath), GetConfigPath(), ConfigPath)
        Return ConfigFile & LegacyBackupSuffix
    End Function

    ''' <summary>
    ''' 把 v3 配置迁移到 v4。会先备份原文件（备份已存在则不覆盖，保留最初的旧配置）。
    ''' </summary>
    Public Function MigrateToV4(Optional ConfigPath As String = "",
                                Optional Raw As Dictionary(Of String, Object) = Nothing) As MigrationResult
        Dim Result As New MigrationResult()
        Dim ConfigFile As String = If(String.IsNullOrEmpty(ConfigPath), GetConfigPath(), ConfigPath)

        If Not File.Exists(ConfigFile) Then
            Result.Message = "配置文件不存在，无需迁移。"
            Return Result
        End If

        Dim Source As Dictionary(Of String, Object) = Raw
        If Source Is Nothing Then
            Try
                Source = ParseYamlLines(File.ReadAllLines(ConfigFile, Text.Encoding.UTF8))
            Catch ex As Exception
                Log(ex, "迁移：读取旧配置失败")
                Result.Message = "迁移失败：无法读取旧配置文件。"
                Return Result
            End Try
        End If

        Result.FromVersion = GetConfigVersion(Source)
        If Result.FromVersion >= CurrentConfigVersion Then
            Result.Message = "配置已是 v" & Result.FromVersion & "，无需迁移。"
            Return Result
        End If

        ' ---- 1. 先备份 ----
        Dim BackupPath As String = GetLegacyBackupPath(ConfigFile)
        Try
            If Not File.Exists(BackupPath) Then
                File.Copy(ConfigFile, BackupPath, False)
            End If
            Result.BackupPath = BackupPath
        Catch ex As Exception
            Log(ex, "迁移：备份旧配置失败")
            Result.Message = "迁移失败：无法备份旧配置文件，已保持原配置不变。"
            Return Result
        End Try

        ' ---- 2. 组装 v4 ----
        Dim Cfg As AppConfig = GetDefaultAppConfig()
        Cfg.Version = CurrentConfigVersion
        Cfg.BackupPath = BackupPath

        Dim LoginData = GetSubDict(Source, ConfigKeys.LoginData)
        If LoginData IsNot Nothing Then
            ' 学号始终按字符串读取
            Cfg.User.UserId = GetRawString(LoginData, "userId")

            ' 旧 password 是门户 RSA 密文，无法还原成用户明文密码
            Cfg.User.Password = ""
            Cfg.PasswordNeedsReentry = True

            ' service → operator
            Dim ServiceRaw As String = GetRawString(LoginData, "service")
            Cfg.User.[Operator] = ParseOperatorToken(ServiceRaw, Cfg)
            If Cfg.User.[Operator] = PortalOperator.Unknown AndAlso ServiceRaw.Length > 0 AndAlso
               Not String.Equals(ServiceRaw, "unknown", StringComparison.OrdinalIgnoreCase) Then
                ' 保留原文供 UI 提示，绝不静默归为 telecom
                Cfg.UnknownServiceRaw = ServiceRaw
            End If
        Else
            Cfg.PasswordNeedsReentry = True
        End If

        Dim FunctionSection = GetSubDict(Source, ConfigKeys.FunctionSection)
        If FunctionSection IsNot Nothing Then
            Cfg.[Function].AutoReconnect = GetDictBool(FunctionSection, ConfigKeys.AutoReconnect, False)
            Cfg.[Function].ReconnectInterval = GetDictInt(FunctionSection, ConfigKeys.ReconnectInterval, 5)
        End If

        ' ---- 3. 写入 v4 ----
        WriteConfigFile(ConfigFile, Cfg)

        ' ---- 4. 确认写入成功后才算迁移完成 ----
        Dim Verify = ReadRawConfig(ConfigFile)
        If Verify Is Nothing OrElse GetConfigVersion(Verify) <> CurrentConfigVersion Then
            Result.Message = "迁移失败：新配置写入后校验不通过，旧配置备份仍保留在 " & BackupPath
            Cfg.Warnings.Add(Result.Message)
            Result.Config = Cfg
            Return Result
        End If

        Result.Migrated = True
        Result.ToVersion = CurrentConfigVersion
        Result.Config = Cfg
        Result.Message = "配置已从 v3 迁移至 v4。"

        ' 迁移日志只记录非敏感信息
        Log("配置已从 v3 迁移至 v4")
        Log("  学号：" & If(Cfg.User.UserId.Length > 0, "已保存", "未填写"))
        Log("  运营商：" & GetOperatorToken(Cfg.User.[Operator]))
        Log("  密码：需要重新输入")
        Log("  旧配置备份：" & BackupPath)

        Return Result
    End Function

#End Region

#Region "旧结构兼容层"

    ''' <summary>
    ''' 兼容入口：仍然返回旧版结构的 Dictionary。
    ''' url / cookie / login_data / headers 由学校固定常量与 v4 用户数据合成，
    ''' 只存在于运行期，不会写回磁盘。供 PageStatus / PageConfig / NetworkMonitor 过渡使用。
    ''' </summary>
    Public Function ReadCfg(Optional GuiMode As Boolean = False) As Dictionary(Of String, Object)
        Return ToLegacyDictionary(LoadAppConfig(GuiMode))
    End Function

    ''' <summary>把 v4 模型投影成旧结构字典。</summary>
    Public Function ToLegacyDictionary(Cfg As AppConfig) As Dictionary(Of String, Object)
        If Cfg Is Nothing Then Cfg = GetDefaultAppConfig()

        Dim Result As New Dictionary(Of String, Object)
        Result(ConfigKeys.Main) = New Dictionary(Of String, Object) From {
            {ConfigKeys.Version, CurrentConfigVersion}
        }
        Result(ConfigKeys.FunctionSection) = New Dictionary(Of String, Object) From {
            {ConfigKeys.AutoReconnect, Cfg.[Function].AutoReconnect},
            {ConfigKeys.ReconnectInterval, Cfg.[Function].ReconnectInterval}
        }
        Result(ConfigKeys.Url) = New Dictionary(Of String, Object) From {
            {ConfigKeys.Server, SchoolServer},
            {ConfigKeys.Login, SchoolLoginPath},
            {ConfigKeys.Logout, SchoolLogoutPath}
        }
        Result(ConfigKeys.Cookie) = ""
        Result(ConfigKeys.LoginData) = New Dictionary(Of String, Object) From {
            {"userId", Cfg.User.UserId},
            {"password", ""},
            {"service", ServiceCodeOrEmpty(Cfg.User.[Operator])},
            {"queryString", ""},
            {"operatorPwd", ""},
            {"operatorUserId", ""},
            {"validcode", ""},
            {"passwordEncrypt", True}
        }
        Result(ConfigKeys.LogoutData) = New Dictionary(Of String, Object)
        Result(ConfigKeys.Headers) = New Dictionary(Of String, Object)

        ' v4 原生字段也一并暴露，便于过渡期代码直接读取
        Result(ConfigKeys.Account) = New Dictionary(Of String, Object) From {
            {ConfigKeys.UserId, Cfg.User.UserId},
            {ConfigKeys.PasswordProtected, ModCredential.ProtectPassword(Cfg.User.Password)}
        }
        Result(ConfigKeys.Auth) = New Dictionary(Of String, Object) From {
            {ConfigKeys.OperatorKey, GetOperatorToken(Cfg.User.[Operator])}
        }
        Return Result
    End Function

    Private Function ServiceCodeOrEmpty(Op As PortalOperator) As String
        If Op = PortalOperator.Unknown Then Return ""
        Try
            Return ModAuth.GetServiceCode(Op)
        Catch
            Return ""
        End Try
    End Function

    ''' <summary>旧结构默认配置（仅用于兼容旧代码路径，不会落盘）。</summary>
    Public Function GetDefaultConfig() As Dictionary(Of String, Object)
        Return ToLegacyDictionary(GetDefaultAppConfig())
    End Function


    ''' <summary>旧 password 字段是否是门户 RSA 密文（256 位十六进制）。</summary>
    Private Function LooksLikePortalCipher(Value As String) As Boolean
        If Value.Length <> 256 Then Return False
        For Each Ch As Char In Value
            If Not Uri.IsHexDigit(Ch) Then Return False
        Next
        Return True
    End Function

    ''' <summary>
    ''' 旧入口：就地改写 function 段（PageStatus 用它保存自动重连设置）。
    ''' 只改文件里对应的行、不重建文件，因此对 v4 结构同样安全。
    ''' </summary>
    Public Sub WriteCfg(Cfg As Dictionary(Of String, Object))
        Dim ConfigPath As String = GetConfigPath()
        Dim Lines As String()
        Try
            Lines = File.ReadAllLines(ConfigPath, Text.Encoding.UTF8)
        Catch ex As Exception
            Log(ex, "读取配置文件失败，无法写入")
            Return
        End Try

        Dim AutoReconnectVal As String = "false"
        Dim ReconnectIntervalVal As String = "5"

        Dim FunctionCfg = GetFunctionDict(Cfg)
        If FunctionCfg IsNot Nothing Then
            If FunctionCfg.ContainsKey(ConfigKeys.AutoReconnect) Then AutoReconnectVal = FunctionCfg(ConfigKeys.AutoReconnect).ToString().ToLower()
            If FunctionCfg.ContainsKey(ConfigKeys.ReconnectInterval) Then ReconnectIntervalVal = FunctionCfg(ConfigKeys.ReconnectInterval).ToString()
        End If

        Dim HasAuto As Boolean = False
        Dim HasInterval As Boolean = False
        Dim NewLines As New List(Of String)

        For Each Line As String In Lines
            Dim Trimmed As String = Line.TrimStart()
            If Trimmed.StartsWith("auto_reconnect:") Then
                Dim Indent As String = Line.Substring(0, Line.Length - Trimmed.Length)
                NewLines.Add(Indent & "auto_reconnect: " & AutoReconnectVal)
                HasAuto = True
            ElseIf Trimmed.StartsWith("reconnect_interval:") Then
                Dim Indent As String = Line.Substring(0, Line.Length - Trimmed.Length)
                NewLines.Add(Indent & "reconnect_interval: " & ReconnectIntervalVal)
                HasInterval = True
            Else
                NewLines.Add(Line)
            End If
        Next

        If Not HasAuto OrElse Not HasInterval Then
            Dim InsertLines As New List(Of String)
            For i As Integer = 0 To NewLines.Count - 1
                InsertLines.Add(NewLines(i))
                ' 兼容更早的配置模板：在旧的 disconnect_network 行后补写
                If NewLines(i).TrimStart().StartsWith("disconnect_network:") Then
                    If Not HasAuto Then
                        Dim Indent As String = NewLines(i).Substring(0, NewLines(i).Length - NewLines(i).TrimStart().Length)
                        InsertLines.Add(Indent & "auto_reconnect: " & AutoReconnectVal)
                        InsertLines.Add(Indent & "reconnect_interval: " & ReconnectIntervalVal)
                        HasAuto = True : HasInterval = True
                    End If
                End If
            Next
            NewLines = InsertLines
        End If

        Try
            File.WriteAllText(ConfigPath, String.Join(vbCrLf, NewLines) & vbCrLf, Text.Encoding.UTF8)
        Catch ex As Exception
            Log(ex, "写入配置文件失败")
        End Try
    End Sub


#End Region

#Region "配置访问 Helpers"

    Public Function GetSubDict(Dict As Dictionary(Of String, Object), Key As String) As Dictionary(Of String, Object)
        If Dict Is Nothing Then Return Nothing
        If Dict.ContainsKey(Key) AndAlso TypeOf Dict(Key) Is Dictionary(Of String, Object) Then
            Return CType(Dict(Key), Dictionary(Of String, Object))
        End If
        Return Nothing
    End Function

    Public Function GetDictStr(Dict As Dictionary(Of String, Object), Key As String, Optional DefaultValue As String = "") As String
        If Dict Is Nothing Then Return DefaultValue
        If Dict.ContainsKey(Key) AndAlso Dict(Key) IsNot Nothing Then
            Return Dict(Key).ToString()
        End If
        Return DefaultValue
    End Function

    Public Function GetDictBool(Dict As Dictionary(Of String, Object), Key As String, Optional DefaultValue As Boolean = False) As Boolean
        If Dict Is Nothing Then Return DefaultValue
        If Dict.ContainsKey(Key) Then
            Dim Val = Dict(Key)
            If Val Is Nothing Then Return DefaultValue
            If TypeOf Val Is Boolean Then Return CBool(Val)
            Dim s = Val.ToString().ToLower()
            Return s = "true"
        End If
        Return DefaultValue
    End Function

    Public Function GetDictInt(Dict As Dictionary(Of String, Object), Key As String, Optional DefaultValue As Integer = 0) As Integer
        If Dict Is Nothing Then Return DefaultValue
        If Dict.ContainsKey(Key) AndAlso Dict(Key) IsNot Nothing Then
            Dim IntVal As Integer
            If Integer.TryParse(Dict(Key).ToString(), IntVal) Then Return IntVal
        End If
        Return DefaultValue
    End Function

    Public Function GetUrlDict(Cfg As Dictionary(Of String, Object)) As Dictionary(Of String, Object)
        Return GetSubDict(Cfg, ConfigKeys.Url)
    End Function

    Public Function GetFunctionDict(Cfg As Dictionary(Of String, Object)) As Dictionary(Of String, Object)
        Return GetSubDict(Cfg, ConfigKeys.FunctionSection)
    End Function

#End Region

End Module
