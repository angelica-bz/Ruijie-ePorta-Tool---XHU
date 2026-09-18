Public Class FormMain

    Private ReadOnly PageStatus As New PageStatus()
    Private ReadOnly PageConfig As New PageConfig()

    Private TrayIcon As System.Windows.Forms.NotifyIcon
    Private IsShuttingDown As Boolean = False
    Private _IsUiLoaded As Boolean = False
    Private _BgMonitor As NetworkMonitor
    Public BgNotified As Boolean = False

    Public Sub New()
        InitializeComponent()
        If Application.IsBackgroundStart Then
            Me.WindowState = WindowState.Minimized
        End If
    End Sub

    Private Sub FormMain_Loaded() Handles Me.Loaded
        LabVersion.Text = $"v{VersionHelper.GetAppVersion()}"

        If Application.IsBackgroundStart Then
            Hide()
            CreateTrayIcon()
            StartBackgroundMonitor()
            Return
        End If

        CreateTrayIcon()
        WindowState = WindowState.Normal
        LoadFullUi()
    End Sub

    Private Sub LoadFullUi()
        If _IsUiLoaded Then Return
        _IsUiLoaded = True
        If SharedCfg Is Nothing Then InitSharedConfig()
        LoadColorsFromResources()
        EnsureMonitor()
        AniStart()
        FraStatus.Content = PageStatus
        FraConfig.Content = PageConfig
    End Sub

    Private Sub EnsureMonitor()
        If _BgMonitor IsNot Nothing Then Return
        _BgMonitor = New NetworkMonitor(SharedCfg)
        _BgMonitor.Start()
    End Sub

    Private Sub StartBackgroundMonitor()
        InitSharedConfig()
        EnsureMonitor()
        AddHandler _BgMonitor.StatusChanged, Sub(connected)
            If connected AndAlso Not BgNotified AndAlso Not _IsUiLoaded Then
                BgNotified = True
                RunInUi(Sub() ShowTrayNotification("连接成功", "网络已连接"))
            End If
        End Sub
    End Sub

    Private Sub StopBackgroundMonitor()
        If _BgMonitor IsNot Nothing Then
            _BgMonitor.Stop()
            _BgMonitor = Nothing
        End If
    End Sub

    Public ReadOnly Property BgMonitor As NetworkMonitor
        Get
            Return _BgMonitor
        End Get
    End Property

    Private Sub TitleBar_MouseLeftButtonDown(sender As Object, e As MouseButtonEventArgs)
        If TypeOf e.OriginalSource Is MyIconButton Then Return
        Try
            DragMove()
        Catch
        End Try
    End Sub

#Region "托盘"

    Private Sub CreateTrayIcon()
        Dim PrevContext = Threading.SynchronizationContext.Current
        Dim IconStream = Application.GetResourceStream(New Uri("Images/icon.ico", UriKind.Relative)).Stream
        TrayIcon = New System.Windows.Forms.NotifyIcon With {
            .Icon = New System.Drawing.Icon(IconStream),
            .Text = "锐捷 ePorta 连接工具",
            .Visible = True
        }

        Dim Menu As New System.Windows.Forms.ContextMenuStrip()
        Menu.Items.Add("显示主窗口", Nothing, Sub() ShowFromTray())
        Menu.Items.Add(New System.Windows.Forms.ToolStripSeparator())
        Menu.Items.Add("退出", Nothing, Sub() ExitFromTray())
        TrayIcon.ContextMenuStrip = Menu

        AddHandler TrayIcon.MouseClick, Sub(s, e)
            If e.Button = System.Windows.Forms.MouseButtons.Left Then ToggleFromTray()
        End Sub

        Dim TrayRetry As New System.Windows.Forms.Timer With {.Interval = 5000}
        AddHandler TrayRetry.Tick, Sub(s, e)
            TrayRetry.Dispose()
            If TrayIcon IsNot Nothing Then
                TrayIcon.Visible = False
                TrayIcon.Visible = True
            End If
        End Sub
        TrayRetry.Start()
        Threading.SynchronizationContext.SetSynchronizationContext(PrevContext)
    End Sub

    Private Sub ToggleFromTray()
        If Visibility = Visibility.Visible Then
            Hide()
        Else
            ShowFromTray()
        End If
    End Sub

    Private Sub ShowFromTray()
        LoadFullUi()
        Show()
        WindowState = WindowState.Normal
        Activate()
    End Sub

    Private Sub ExitFromTray()
        IsShuttingDown = True
        StopBackgroundMonitor()
        If TrayIcon IsNot Nothing Then
            TrayIcon.Visible = False
            TrayIcon.Dispose()
            TrayIcon = Nothing
        End If
        Application.Current.Shutdown()
    End Sub

    Private Sub BtnTitleTray_Click(sender As Object, e As EventArgs)
        Hide()
    End Sub

    Public Sub ShowTrayNotification(Title As String, Message As String, Optional Timeout As Integer = 3000)
        If IsShuttingDown Then Return
        If TrayIcon IsNot Nothing AndAlso TrayIcon.Visible AndAlso Visibility <> Visibility.Visible Then
            TrayIcon.ShowBalloonTip(Timeout, Title, Message, System.Windows.Forms.ToolTipIcon.Info)
        End If
    End Sub

#End Region

    Private Sub BtnTitleClose_Click(sender As Object, e As EventArgs)
        Close()
    End Sub

    Private Sub BtnTitleMin_Click(sender As Object, e As EventArgs)
        WindowState = WindowState.Minimized
    End Sub

    Private Sub FormMain_Closed(sender As Object, e As EventArgs) Handles Me.Closed
        StopBackgroundMonitor()
        If TrayIcon IsNot Nothing Then
            TrayIcon.Visible = False
            TrayIcon.Dispose()
            TrayIcon = Nothing
        End If
    End Sub

    Private Sub TabStatus_Click(sender As Object, e As MouseButtonEventArgs)
        If FraStatus.Visibility = Visibility.Visible Then Return
        FraStatus.Visibility = Visibility.Visible
        FraConfig.Visibility = Visibility.Collapsed
        TabStatus.Background = FindResource("ColorBrush3")
        CType(TabStatus.Child, TextBlock).Foreground = New SolidColorBrush(Colors.White)
        TabConfig.Background = FindResource("ColorBrushGray5")
        CType(TabConfig.Child, TextBlock).Foreground = FindResource("ColorBrush1")
    End Sub

    Private Sub TabConfig_Click(sender As Object, e As MouseButtonEventArgs)
        If FraConfig.Visibility = Visibility.Visible Then Return
        FraConfig.Visibility = Visibility.Visible
        FraStatus.Visibility = Visibility.Collapsed
        TabConfig.Background = FindResource("ColorBrush3")
        CType(TabConfig.Child, TextBlock).Foreground = New SolidColorBrush(Colors.White)
        TabStatus.Background = FindResource("ColorBrushGray5")
        CType(TabConfig.Child, TextBlock).Foreground = FindResource("ColorBrush1")
    End Sub

End Class
