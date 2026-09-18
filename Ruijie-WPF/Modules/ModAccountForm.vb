Imports Microsoft.VisualBasic

''' <summary>
''' 账号配置页（PageConfig）的表单逻辑，与 WPF 完全无关。
'''
''' 这样做的目的：
'''   1. 配置页的「读 / 校验 / 保存」语义可以被 --test 直接覆盖，不需要构造 WPF 可视化树；
'''   2. 将来托盘菜单、命令行等其它入口可以复用同一套逻辑；
'''   3. PageConfig 只负责把控件值搬进 <see cref="AccountForm"/>、把结果搬回控件。
'''
''' 密码语义（本模块的核心）：
'''   密码框为空 + 已有可用密码  → 保持磁盘上的 password_protected 不变
'''   密码框为空 + 没有可用密码  → 校验失败，提示「请输入密码」
'''   密码框有新密码            → 用新密码覆盖（DPAPI 由 ModConfig.SaveAppConfigTo 负责）
'''   用户点了「清除已保存密码」  → 置空密码（保存后生效）
''' </summary>
Public Module ModAccountForm

#Region "数据模型"

    ''' <summary>密码在配置页上的三种状态。</summary>
    Public Enum PasswordUiState
        ''' <summary>从未保存过密码。</summary>
        NotSaved = 0
        ''' <summary>已保存且可以解密。</summary>
        Saved = 1
        ''' <summary>保存过但解不开（换过 Windows 用户 / 数据损坏 / entropy 变更）。</summary>
        Broken = 2
    End Enum

    ''' <summary>配置页表单的运行期数据。</summary>
    Public Class AccountForm
        ''' <summary>学号。始终按 String 处理。</summary>
        Public Property UserId As String = ""
        Public Property [Operator] As PortalOperator = PortalOperator.Unknown
        ''' <summary>用户在密码框里新输入的明文密码；为空表示「不修改密码」。</summary>
        Public Property NewPassword As String = ""
        ''' <summary>用户点击了「清除已保存密码」。</summary>
        Public Property ClearStoredPassword As Boolean = False
    End Class

    ''' <summary>配置页需要展示的全部状态。</summary>
    Public Class FormState
        Public Property PasswordState As PasswordUiState = PasswordUiState.NotSaved
        ''' <summary>密码框下方的说明文字。</summary>
        Public Property PasswordHint As String = ""
        ''' <summary>旧配置迁移提示；为空表示不显示。</summary>
        Public Property MigrationHint As String = ""
        ''' <summary>未知网络类型提示；为空表示不显示。</summary>
        Public Property UnknownServiceHint As String = ""
        ''' <summary>底部状态文字。</summary>
        Public Property StatusText As String = ""
        ''' <summary>底部状态是否为「可以认证」。</summary>
        Public Property StatusReady As Boolean = False
        ''' <summary>是否显示「清除已保存密码」。</summary>
        Public Property CanClearPassword As Boolean = False
    End Class

    Public Class SaveOutcome
        Public Property Success As Boolean = False
        ''' <summary>表单校验失败原因；为空表示校验通过。</summary>
        Public Property ValidationError As String = ""
        ''' <summary>保存成功后的提示文字。</summary>
        Public Property Message As String = ""
        ''' <summary>保存并回读后的配置。</summary>
        Public Property Config As AppConfig
    End Class

#End Region

#Region "加载：配置 → 表单 / 状态"

    ''' <summary>把配置映射成表单初值。密码永远不从配置回填（§六）。</summary>
    Public Function BuildForm(Config As AppConfig) As AccountForm
        Dim Result As New AccountForm()
        If Config Is Nothing OrElse Config.User Is Nothing Then
            ' 未知运营商时下拉框给一个有效项，避免控件处于未选中状态（§十四）
            Result.[Operator] = PortalOperator.Office
            Return Result
        End If

        Result.UserId = Config.User.UserId
        Result.NewPassword = ""
        Result.ClearStoredPassword = False

        ' 无法识别的旧 service：下拉框先落到门户的默认服务（办公网），
        ' 同时由 UnknownServiceHint 明确提示用户重新选择，不做静默归类。
        Result.[Operator] = If(Config.User.[Operator] = PortalOperator.Unknown,
                               PortalOperator.Office,
                               Config.User.[Operator])
        Return Result
    End Function

    ''' <summary>根据配置得出页面需要展示的状态。反映的是**已保存**的配置，而不是表单上的临时编辑。</summary>
    Public Function DescribeState(Config As AppConfig, Optional ConfigPath As String = "") As FormState
        Dim Result As New FormState()
        If Config Is Nothing Then
            Result.PasswordHint = "尚未保存密码，请输入校园网密码"
            Result.StatusText = "配置读取失败，请重新打开配置页。"
            Return Result
        End If

        Dim EffectivePath As String = If(String.IsNullOrEmpty(ConfigPath), ModConfig.GetConfigPath(), ConfigPath)

        ' ---- 密码状态：以磁盘上的 password_protected 原文为准 ----
        Dim RawProtected As String = ModConfig.ReadStoredProtectedPassword(EffectivePath)
        If RawProtected.Length = 0 Then
            Result.PasswordState = PasswordUiState.NotSaved
            Result.PasswordHint = "尚未保存密码，请输入校园网密码"
        ElseIf (Not Config.PasswordNeedsReentry) AndAlso Not String.IsNullOrEmpty(Config.User.Password) Then
            Result.PasswordState = PasswordUiState.Saved
            Result.PasswordHint = "已保存密码，留空保持不变"
        Else
            Result.PasswordState = PasswordUiState.Broken
            Result.PasswordHint = "已保存的密码无法解密，请重新输入密码"
        End If
        Result.CanClearPassword = (Result.PasswordState <> PasswordUiState.NotSaved)

        ' ---- 迁移提示：只在「密码待重新输入」且确实存在 v3 备份时出现 ----
        If Config.PasswordNeedsReentry AndAlso ModConfig.LegacyBackupExists(EffectivePath) Then
            Result.MigrationHint = "检测到旧版本配置。" & vbCrLf &
                                   "账号信息已经迁移成功。" & vbCrLf &
                                   "旧版本密码无法恢复，请重新输入校园网密码。"
        End If

        ' ---- 未知网络类型 ----
        If Not String.IsNullOrEmpty(Config.UnknownServiceRaw) Then
            Result.UnknownServiceHint = "旧版本配置中的网络类型已无法识别，请重新选择。"
        End If

        ' ---- 底部状态 ----
        If Config.IsReadyForAuthentication Then
            Result.StatusReady = True
            Result.StatusText = "配置完整，可以认证"
        Else
            Result.StatusReady = False
            Result.StatusText = If(String.IsNullOrEmpty(Config.NotReadyReason),
                                   "配置不完整。", Config.NotReadyReason)
        End If

        Return Result
    End Function

#End Region

#Region "校验与保存"

    ''' <summary>表单校验。返回空串表示通过，否则返回可直接展示给用户的提示。</summary>
    Public Function Validate(Form As AccountForm, State As FormState) As String
        If Form Is Nothing Then Return "表单为空。"

        If If(Form.UserId, "").Trim().Length = 0 Then Return "请输入学号"
        If Form.[Operator] = PortalOperator.Unknown Then Return "请选择网络类型"

        ' 保存后是否会有可用密码
        Dim WillHavePassword As Boolean
        If Form.ClearStoredPassword Then
            WillHavePassword = Not String.IsNullOrEmpty(Form.NewPassword)
        ElseIf Not String.IsNullOrEmpty(Form.NewPassword) Then
            WillHavePassword = True
        Else
            WillHavePassword = (State IsNot Nothing AndAlso State.PasswordState = PasswordUiState.Saved)
        End If
        If Not WillHavePassword Then Return "请输入密码"

        Return ""
    End Function

    ''' <summary>
    ''' 校验 → 只修改 v4 用户模型 → 落盘 → 回读验证。
    ''' 不构造、不触碰旧的 login_data / url / cookie / headers。
    ''' </summary>
    Public Function ApplyAndSave(Form As AccountForm, Optional ConfigPath As String = "") As SaveOutcome
        Dim Result As New SaveOutcome()
        If Form Is Nothing Then
            Result.ValidationError = "表单为空。"
            Return Result
        End If

        Dim EffectivePath As String = If(String.IsNullOrEmpty(ConfigPath), ModConfig.GetConfigPath(), ConfigPath)
        Dim Current As AppConfig = ModConfig.LoadAppConfigFrom(EffectivePath)
        Dim State As FormState = DescribeState(Current, EffectivePath)

        Dim Problem As String = Validate(Form, State)
        If Problem.Length > 0 Then
            Result.ValidationError = Problem
            Return Result
        End If

        ' ---- 只改这几个字段 ----
        Current.User.UserId = If(Form.UserId, "").Trim()
        Current.User.[Operator] = Form.[Operator]
        ' 用户重新选过网络类型，旧的未知标记可以清掉
        Current.UnknownServiceRaw = ""

        ' 优先级：新输入的密码 > 明确清除 > 保持原密码
        If Not String.IsNullOrEmpty(Form.NewPassword) Then
            ' 新密码（DPAPI 在 SaveAppConfigTo 内完成）
            Current.User.Password = Form.NewPassword
        ElseIf Form.ClearStoredPassword Then
            ' 只清除、没有输入新密码
            Current.User.Password = ""
        Else
            ' 密码框为空且已有可用密码：Current.User.Password 就是解密出来的原密码，
            ' 落盘时会原样复用磁盘上的密文，语义与密文都不变。
        End If

        ModConfig.SaveAppConfigTo(EffectivePath, Current)

        ' ---- 回读验证 ----
        Dim Verify As AppConfig = ModConfig.LoadAppConfigFrom(EffectivePath)
        Result.Success = True
        Result.Config = Verify
        Result.Message = DescribeSaveResult(Verify)
        Return Result
    End Function

    ''' <summary>保存成功后给用户的提示（§十）。</summary>
    Public Function DescribeSaveResult(Config As AppConfig) As String
        If Config Is Nothing Then Return "配置已保存。"
        If Config.PasswordNeedsReentry Then Return "配置已保存，但尚未设置密码。"
        If Config.IsReadyForAuthentication Then Return "配置已保存，可以进行校园网认证。"
        Return "配置已保存。" & If(String.IsNullOrEmpty(Config.NotReadyReason), "",
                                    "（" & Config.NotReadyReason & "）")
    End Function

#End Region

#Region "UI 辅助"

    ''' <summary>
    ''' 配置页下拉框的展示顺序：三大运营商在前，校内网络在后。
    ''' 显示名仍由 ModAuth.GetUiDisplayName 提供，这里只决定顺序。
    ''' </summary>
    Public Function GetUiOperatorOrder() As PortalOperator()
        Return New PortalOperator() {
            PortalOperator.Telecom,
            PortalOperator.Mobile,
            PortalOperator.Unicom,
            PortalOperator.Office,
            PortalOperator.Xhu
        }
    End Function

#End Region

End Module