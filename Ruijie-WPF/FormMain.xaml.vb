Public Class FormMain

    ' 两个页面的可视树都很大（各自一整套 XAML），因此**按需创建**：
    ' 只有真正要显示界面时才建。后台启动（开机自启走的正是这条）从不创建本窗口，
    ' 所以这两个字段在后台模式下永远保持 Nothing。
    Private _PageStatus As PageStatus
    Private _PageConfig As PageConfig

    Private _IsUiLoaded As Boolean = False

    Public Sub New()
        InitializeComponent()
        ModStartupProfile.Mark("MainWindowConstruct")
    End Sub

    Private Sub FormMain_Loaded() Handles Me.Loaded
        LabVersion.Text = $"v{VersionHelper.GetAppVersion()}"

        ' 托盘与网络监控已由 ModHost 在应用启动时就建好，这里只负责界面。
        ' 常规启动的 [Startup] 报表由 App 在 Show 之后发出，这里不重复报。
        LoadFullUi()
    End Sub

    Private Sub LoadFullUi()
        If _IsUiLoaded Then Return
        _IsUiLoaded = True

        ModHost.EnsureConfig()
        LoadColorsFromResources()

        ' 到这里才真正建页面 —— 首帧之前不做
        Dim SwUi = Diagnostics.Stopwatch.StartNew()
        If _PageStatus Is Nothing Then _PageStatus = New PageStatus()
        If _PageConfig Is Nothing Then _PageConfig = New PageConfig()
        ModStartupProfile.MarkWithDuration("BuildPages", SwUi.ElapsedMilliseconds)

        FraStatus.Content = _PageStatus
        FraConfig.Content = _PageConfig

        AniStart()
    End Sub

    ''' <summary>
    ''' 右上角 × / 标题栏关闭按钮 —— **不再是"关闭窗口"，只是收起窗口**。
    '''
    ''' 本项目是托盘常驻程序：关掉窗口不应该结束程序，而应保留托盘与 NetworkMonitor。
    ''' 真退出只能走托盘菜单的「退出」。
    ''' </summary>
    Public Sub HideToTray()
        ' IsShuttingDown 时放行：托盘「退出」需要窗口真正关闭
        If ModHost.IsShuttingDown Then
            Close()
            Return
        End If
        Hide()
    End Sub

    Private Sub FormMain_Closing(sender As Object, e As ComponentModel.CancelEventArgs) Handles Me.Closing
        ' 托盘「退出」流程允许真正关闭；其余情况一律取消关闭改为隐藏，
        ' 否则 Window 进入 Closed 状态后 ModHost 仍持有引用，
        ' 再次 Show() 会抛 InvalidOperationException（VerifyCanShow）。
        If ModHost.IsShuttingDown Then Return
        e.Cancel = True
        Hide()
    End Sub

    Private Sub FormMain_Closed(sender As Object, e As EventArgs) Handles Me.Closed
        ModHost.OnMainWindowClosed(Me)
    End Sub

#Region "兼容属性（供 PageStatus 等复用）"

    ''' <summary>网络监控实例。现在归 ModHost 所有，这里转出去以免调用方到处改。</summary>
    Public ReadOnly Property BgMonitor As NetworkMonitor
        Get
            Return ModHost.Monitor
        End Get
    End Property

    ''' <summary>后台启动时是否已弹过「连接成功」提示。</summary>
    Public Property BgNotified As Boolean
        Get
            Return ModHost.BgNotified
        End Get
        Set(value As Boolean)
            ModHost.BgNotified = value
        End Set
    End Property

    Public Sub ShowTrayNotification(Title As String, Message As String, Optional Timeout As Integer = 3000)
        ModHost.ShowTrayNotification(Title, Message, Timeout)
    End Sub

#End Region

#Region "窗口行为"

    Private Sub TitleBar_MouseLeftButtonDown(sender As Object, e As MouseButtonEventArgs)
        If TypeOf e.OriginalSource Is MyIconButton Then Return
        Try
            DragMove()
        Catch
        End Try
    End Sub

    Private Sub BtnTitleTray_Click(sender As Object, e As EventArgs)
        HideToTray()
    End Sub

    Private Sub BtnTitleClose_Click(sender As Object, e As EventArgs)
        HideToTray()
    End Sub

    Private Sub BtnTitleMin_Click(sender As Object, e As EventArgs)
        WindowState = WindowState.Minimized
    End Sub

#End Region

#Region "Tab 切换"

    ''' <summary>切到「状态」页。Public 供 GUI 自检入口（--ui-test）复用。</summary>
    Public Sub TabStatus_Click(sender As Object, e As MouseButtonEventArgs)
        If FraStatus.Visibility = Visibility.Visible Then Return
        FraStatus.Visibility = Visibility.Visible
        FraConfig.Visibility = Visibility.Collapsed
        ApplyTabVisual(TabStatus, TabConfig)
    End Sub

    ''' <summary>切到「配置」页。Public 供 GUI 自检入口（--ui-test）复用。</summary>
    Public Sub TabConfig_Click(sender As Object, e As MouseButtonEventArgs)
        If FraConfig.Visibility = Visibility.Visible Then Return
        FraConfig.Visibility = Visibility.Visible
        FraStatus.Visibility = Visibility.Collapsed
        ApplyTabVisual(TabConfig, TabStatus)
    End Sub

    ''' <summary>
    ''' 设置 Tab 的选中/未选中外观。
    '''
    ''' 【踩坑记录】原实现写的是
    '''     CType(TabX.Child, TextBlock).Foreground = New SolidColorBrush(FindResource("ColorBrush1"))
    ''' 这会抛 InvalidCastException「指定的转换无效」：
    '''     FindResource 返回 Object，而 SolidColorBrush 有 (Color) 与 (Brush) 两个重载，
    '''     VB 无法把 Object 隐式转成 Color，于是该表达式整体失败。
    ''' 关键是**逐条拆开时每一步都成功**（Object→TextBlock 没问题、FindResource 也没问题），
    ''' 只有把 CType 与 FindResource 嵌在同一个构造函数调用里才炸 —— 所以极难靠读代码看出。
    ''' 现在显式取 Color 再构造，并且直接把资源里的 Brush 拿来复用，不再每次点击都新建画刷。
    ''' </summary>
    Private Sub ApplyTabVisual(Selected As Border, Unselected As Border)
        Selected.Background = FindResource("ColorBrush3")
        CType(Selected.Child, TextBlock).Foreground = New SolidColorBrush(Colors.White)
        Unselected.Background = FindResource("ColorBrushGray5")
        CType(Unselected.Child, TextBlock).Foreground = FindResource("ColorBrush1")
    End Sub

    ''' <summary>诊断输出缓冲（仅 DiagnoseTabSwitch 使用）。</summary>
    Private _DiagSb As Text.StringBuilder
    Private _DiagStep As Integer

    Private Sub DiagRun(Label As String, Act As Action)
        _DiagStep += 1
        Try
            Act()
            _DiagSb.AppendLine("  [OK]   " & _DiagStep.ToString("00") & " " & Label)
        Catch ex As Exception
            _DiagSb.AppendLine("  [FAIL] " & _DiagStep.ToString("00") & " " & Label & "  -> " &
                               ex.GetType().Name & ": " & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' 逐语句体检页面切换逻辑，用于定位 InvalidCastException 到底发生在哪一步。
    ''' 只被 --ui-test 调用；每一步单独 try/catch，避免异常中断后续步骤。
    ''' </summary>
    Public Function DiagnoseTabSwitch() As String
        _DiagSb = New Text.StringBuilder()
        _DiagStep = 0

        _DiagSb.AppendLine("--- TabConfig_Click 逐语句 ---")
        DiagRun("读 FraConfig.Visibility", Sub()
                                               Dim V = FraConfig.Visibility
                                           End Sub)
        DiagRun("比对 Visibility = Visible", Sub()
                                                 Dim B = (FraConfig.Visibility = Visibility.Visible)
                                             End Sub)
        DiagRun("写 FraConfig.Visibility = Visible", Sub()
                                                         FraConfig.Visibility = Visibility.Visible
                                                     End Sub)
        DiagRun("写 FraStatus.Visibility = Collapsed", Sub()
                                                           FraStatus.Visibility = Visibility.Collapsed
                                                       End Sub)
        DiagRun("TabConfig.Background = FindResource(ColorBrush3)", Sub()
                                                                       TabConfig.Background = FindResource("ColorBrush3")
                                                                   End Sub)
        DiagRun("CType(TabConfig.Child, TextBlock)", Sub()
                                                         Dim T = CType(TabConfig.Child, TextBlock)
                                                     End Sub)
        DiagRun("TextBlock.Foreground = White", Sub()
                                                    CType(TabConfig.Child, TextBlock).Foreground = New SolidColorBrush(Colors.White)
                                                End Sub)
        DiagRun("TabStatus.Background = FindResource(ColorBrushGray5)", Sub()
                                                                           TabStatus.Background = FindResource("ColorBrushGray5")
                                                                       End Sub)
        DiagRun("修复后：ApplyTabVisual（生产代码路径）", Sub()
                                                              ApplyTabVisual(TabConfig, TabStatus)
                                                          End Sub)
        DiagRun("旧写法（应失败，作为回归对照）", Sub()
                                                          ' 原生产代码：把 SolidColorBrush 当 Color 用
                                                          CType(TabStatus.Child, TextBlock).Foreground =
                                                              New SolidColorBrush(FindResource("ColorBrush1"))
                                                      End Sub)

        _DiagSb.AppendLine("--- 完整调用 TabStatus_Click ---")
        Try
            TabStatus_Click(TabStatus, Nothing)
            _DiagSb.AppendLine("  [OK]   TabStatus_Click 整体成功")
        Catch ex As Exception
            _DiagSb.AppendLine("  [FAIL] TabStatus_Click -> " & ex.GetType().Name & ": " & ex.Message)
            _DiagSb.AppendLine("         Stack: " & ex.StackTrace)
        End Try

        Return _DiagSb.ToString()
    End Function

    ''' <summary>
    ''' 自检：验证页面「卸载 → 重新加载」不会累积监控事件订阅。
    ''' 通过把 PageStatus 从 Frame 上摘下再挂回来，真实触发 Unloaded / Loaded。
    ''' 只被 --ui-test 调用。
    ''' </summary>
    Public Function DiagnoseMonitorSubscription() As String
        Dim Sb As New Text.StringBuilder()
        Try
            If _PageStatus Is Nothing Then
                Sb.AppendLine("  (PageStatus 尚未创建，跳过)")
                Return Sb.ToString()
            End If

            Sb.AppendLine("  初始订阅数 = " & _PageStatus.MonitorHandlerCount)
            Sb.AppendLine(_PageStatus.SelfTestSubscriptionCycle(3))
            Sb.AppendLine("  期望：每轮解除后 0，重新订阅后恰好 3（不累积）")
        Catch ex As Exception
            Sb.AppendLine("  自检异常: " & ex.GetType().Name & ": " & ex.Message)
        End Try
        Return Sb.ToString()
    End Function

#End Region

End Class
