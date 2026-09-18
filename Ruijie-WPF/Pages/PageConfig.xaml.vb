Imports System.Windows.Controls
Imports Microsoft.VisualBasic

''' <summary>
''' 校园网账号配置页。
'''
''' 界面只暴露三项：学号 / 密码 / 网络类型（运营商）。
''' 服务器地址、登录路径、Cookie、queryString、请求头等抓包参数
''' 全部由认证核心（ModPortalDiscover / ModAuthentication）自动处理，不在此出现。
'''
''' 表单语义（加载、校验、保存、密码保持规则）全部放在 ModAccountForm 里，
''' 那里与 WPF 无关、可被 --test 直接覆盖；本类只负责控件与表单对象之间的搬运。
''' </summary>
Public Class PageConfig

    Private Cfg As AppConfig
    Private State As ModAccountForm.FormState
    Private ReadOnly OperatorItems As New List(Of OperatorItem)
    Private PasswordVisible As Boolean = False
    ''' <summary>用户是否点击了「清除已保存密码」（保存后生效）。</summary>
    Private ClearRequested As Boolean = False

    ''' <summary>下拉框条目。显示名统一来自 ModAuth，本类不维护第二套映射。</summary>
    Private Class OperatorItem
        Public Property Op As PortalOperator
        Public Property Display As String
        Public Overrides Function ToString() As String
            Return Display
        End Function
    End Class

#Region "加载"

    Private Sub Page_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        ' MyTextButton 的 Text 走动画，在 Loaded 之后再设置更稳妥
        BtnTogglePassword.Text = "显示"
        BtnClearPassword.Text = "清除已保存密码"
        BuildOperatorList()
        ReloadFromConfig()
    End Sub

    Private Sub BuildOperatorList()
        OperatorItems.Clear()
        CmbOperator.Items.Clear()
        For Each Op In ModAccountForm.GetUiOperatorOrder()
            Dim Item As New OperatorItem With {.Op = Op, .Display = ModAuth.GetUiDisplayName(Op)}
            OperatorItems.Add(Item)
            CmbOperator.Items.Add(Item)
        Next
    End Sub

    ''' <summary>从磁盘重新加载配置并刷新整个页面。密码框永远不回填明文。</summary>
    Private Sub ReloadFromConfig()
        Try
            Cfg = ModConfig.LoadAppConfig()
        Catch
            Cfg = ModConfig.GetDefaultAppConfig()
        End Try

        State = ModAccountForm.DescribeState(Cfg)
        ClearRequested = False
        PasswordVisible = False
        TxtPassword.Visibility = Visibility.Visible
        TxtPasswordVisible.Visibility = Visibility.Collapsed

        Dim Form As ModAccountForm.AccountForm = ModAccountForm.BuildForm(Cfg)
        TxtUserId.Text = Form.UserId
        SelectOperator(Form.[Operator])
        SetPasswordText("")
        ApplyStateToUi()
    End Sub

    Private Sub ApplyStateToUi()
        LabPasswordHint.Text = State.PasswordHint
        BtnClearPassword.Visibility = If(State.CanClearPassword, Visibility.Visible, Visibility.Collapsed)

        HintMigration.Text = State.MigrationHint
        HintMigration.Visibility = If(String.IsNullOrEmpty(State.MigrationHint), Visibility.Collapsed, Visibility.Visible)

        HintUnknownService.Text = State.UnknownServiceHint
        HintUnknownService.Visibility = If(String.IsNullOrEmpty(State.UnknownServiceHint), Visibility.Collapsed, Visibility.Visible)

        LabConfigStatus.Text = State.StatusText
        ShapeStatusDot.Fill = New SolidColorBrush(
            If(State.StatusReady,
               Color.FromRgb(&H4C, &HAF, &H50),
               Color.FromRgb(&HF4, &H43, &H36)))
    End Sub

#End Region

#Region "运营商下拉框"

    Private Sub SelectOperator(Op As PortalOperator)
        For Each Item In OperatorItems
            If Item.Op = Op Then
                CmbOperator.SelectedItem = Item
                Return
            End If
        Next
        If OperatorItems.Count > 0 Then CmbOperator.SelectedIndex = 0
    End Sub

    Private Function SelectedOperator() As PortalOperator
        Dim Item = TryCast(CmbOperator.SelectedItem, OperatorItem)
        If Item Is Nothing Then Return PortalOperator.Unknown
        Return Item.Op
    End Function

#End Region

#Region "密码输入"

    ''' <summary>当前密码框里的内容（明文/隐藏两种模式取其一）。</summary>
    Private Function CurrentPasswordInput() As String
        Return If(PasswordVisible, TxtPasswordVisible.Text, TxtPassword.Password)
    End Function

    Private Sub SetPasswordText(Value As String)
        TxtPassword.Password = If(Value, "")
        TxtPasswordVisible.Text = If(Value, "")
    End Sub

    Private Sub BtnTogglePassword_Click(sender As Object, e As EventArgs) Handles BtnTogglePassword.Click
        PasswordVisible = Not PasswordVisible
        If PasswordVisible Then
            ' 只显示用户本次输入的内容；配置里已保存的密码永远不回填
            TxtPasswordVisible.Text = TxtPassword.Password
            TxtPassword.Visibility = Visibility.Collapsed
            TxtPasswordVisible.Visibility = Visibility.Visible
            BtnTogglePassword.Text = "隐藏"
        Else
            TxtPassword.Password = TxtPasswordVisible.Text
            TxtPasswordVisible.Visibility = Visibility.Collapsed
            TxtPassword.Visibility = Visibility.Visible
            BtnTogglePassword.Text = "显示"
        End If
    End Sub

    Private Sub BtnClearPassword_Click(sender As Object, e As EventArgs) Handles BtnClearPassword.Click
        If MessageBox.Show("将清除已保存的密码。" & vbCrLf & "保存配置后，下次启动需要重新输入校园网密码。" & vbCrLf & vbCrLf & "确定继续？",
                           "清除已保存密码", MessageBoxButton.YesNo, MessageBoxImage.Question) <> MessageBoxResult.Yes Then
            Return
        End If

        ClearRequested = True
        SetPasswordText("")
        LabPasswordHint.Text = "未保存密码，请输入校园网密码"
        BtnClearPassword.Visibility = Visibility.Collapsed
    End Sub

#End Region

#Region "保存"

    Private Sub BtnSaveConfig_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnSaveConfig.Click
        Dim Form As New ModAccountForm.AccountForm With {
            .UserId = TxtUserId.Text,
            .[Operator] = SelectedOperator(),
            .NewPassword = CurrentPasswordInput(),
            .ClearStoredPassword = ClearRequested
        }

        Dim Outcome As ModAccountForm.SaveOutcome = ModAccountForm.ApplyAndSave(Form)

        If Not Outcome.Success Then
            MessageBox.Show(Outcome.ValidationError, "无法保存", MessageBoxButton.OK, MessageBoxImage.Warning)
            Return
        End If

        ' 重新加载，确保界面反映磁盘上的真实状态（密码框清空、状态灯更新）
        ReloadFromConfig()
        MessageBox.Show(Outcome.Message, "保存成功", MessageBoxButton.OK, MessageBoxImage.Information)
    End Sub

#End Region

End Class
