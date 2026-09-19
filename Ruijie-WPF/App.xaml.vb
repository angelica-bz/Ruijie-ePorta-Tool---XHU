Imports Microsoft.VisualBasic

Class Application

    Public Shared IsBackgroundStart As Boolean = False

    ''' <summary>解析 --bind=&lt;本机地址&gt;（用于绕过 VPN/TUN，占住校园网出口）。</summary>
    Private Shared Function GetBindAddress(Args As String()) As String
        For Each Arg In Args
            If Arg.StartsWith("--bind=", StringComparison.OrdinalIgnoreCase) Then
                Return Arg.Substring("--bind=".Length).Trim()
            End If
        Next
        Return ""
    End Function

    ''' <summary>
    ''' 命令行模式的输出全是中文。控制台默认码页不是 UTF-8 时，
    ''' 一旦把输出重定向到文件（CI、日志留存、`--test &gt; log.txt`），
    ''' 中文就会变成乱码。这里在进入任何命令行分支前统一切到 UTF-8。
    ''' </summary>
    Private Shared Sub UseUtf8Console()
        ' 只设 Console.OutputEncoding 是不够的：本程序是 WPF 的 WinExe，
        ' 从命令行启动时没有自己的控制台，重定向到文件后仍然按系统码页（936）写字节。
        ' 直接把标准输出换成 UTF-8 的写入器，才与是否重定向无关。
        Try
            Console.OutputEncoding = Text.Encoding.UTF8
        Catch
            ' 没有真实控制台时可能抛异常，忽略：下面替换写入器才是关键
        End Try

        Try
            Console.SetOut(New IO.StreamWriter(Console.OpenStandardOutput(),
                                               New Text.UTF8Encoding(False)) With {.AutoFlush = True})
        Catch
            ' 极端情况下拿不到标准输出，保持默认行为即可
        End Try
    End Sub

    ''' <summary>
    ''' 开机自启的「一次性应用」入口。
    ''' GUI 里的开关在正常情况下直接由 ModStartup.Enable() 完成；
    ''' 只有非提权注册失败时才会带提权拉起本入口（触发**一次** UAC），
    ''' 因此这是**回退路径**，不是主路径。
    '''
    ''' 退出码：0 成功 / 1 失败
    ''' </summary>
    Private Function RunApplyStartup(Enable As Boolean) As Integer
        Console.WriteLine("=== Apply Startup (" & If(Enable, "enable", "disable") & ") ===")
        Console.WriteLine("任务名   : " & ModStartup.TaskName())
        Console.WriteLine("当前用户 : " & ModStartup.CurrentUserName())
        Console.WriteLine("已提权   : " & If(ModStartup.IsElevated(), "是", "否"))
        Console.WriteLine("目标 EXE : " & PathExe)
        Console.WriteLine()

        Dim Code As Integer = ModStartup.ApplyOnce(Enable)
        Console.WriteLine("任务状态 : " & ModStartup.GetState().ToString())
        Console.WriteLine("任务存在 : " & If(ModStartup.IsTaskRegistered(), "是", "否"))
        Console.WriteLine("Run 键   : " & If(ModStartup.IsRunKeyPresent(), "存在", "不存在"))
        Console.WriteLine()
        Console.WriteLine(If(Code = 0, "结果: 成功", "结果: 失败"))
        Console.Out.Flush()
        Return Code
    End Function

    Private Sub Application_Startup(sender As Object, e As StartupEventArgs) Handles Me.Startup
        ModStartupProfile.Begin()
        ' 最早可执行位置记录启动身份：PID / 命令行 / 父进程 / 是否带 --background。
        ' 出现「后台模式却建了窗口」这类问题时，这是唯一可靠的事后证据。
        ModStartupIdentity.LogOnce()
        ' 开机自启的提权回退入口：在任何 UI 初始化之前处理，纯控制台行为。
        ' 正常路径不会走到这里 —— GUI 开关直接调 ModStartup.Enable()，
        ' 只有非提权注册失败时才会带提权拉起这两个参数。
        If e.Args.Contains("--apply-startup") OrElse e.Args.Contains("--apply-startup-off") Then
            UseUtf8Console()
            Me.ShutdownMode = ShutdownMode.OnExplicitShutdown
            Environment.ExitCode = RunApplyStartup(e.Args.Contains("--apply-startup"))
            Me.Shutdown()
            Return
        End If

        ' 开发期 GUI 回归工具：程序化复现「页面切换 + 窗口生命周期」并打印完整异常。
        ' 用途：AI/脚本无法点击界面，靠它把 DispatcherUnhandledException 的完整堆栈取出来。
        ' 说明见 CONTRIBUTING.md。
        If e.Args.Contains("--ui-test") Then
            UseUtf8Console()
            VerboseSingleInstance = True
            Me.ShutdownMode = ShutdownMode.OnExplicitShutdown
            AddHandler Me.DispatcherUnhandledException, AddressOf App_DispatcherUnhandledException
            ModHost.BackgroundMode = False
            ModHost.EnsureConfig()
            ModHost.EnsureTray()
            ModHost.EnsureMonitor()
            Dim W = ModHost.CreateMainWindowForTest()
            W.Show()
            ' 等窗口真正布局/Loaded 之后再切页，否则测的不是真实时序
            Dispatcher.BeginInvoke(
                New Action(Sub() RunUiSelfTest(W)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle)
            Return
        End If

        If e.Args.Contains("--accept") OrElse e.Args.Contains("--preflight") OrElse
           e.Args.Contains("--e2e") OrElse
           e.Args.Contains("--diagnose") OrElse e.Args.Contains("--test") Then
            UseUtf8Console()
        End If

        ' 开发期退出回归：走与托盘「退出」完全相同的路径，并验证进程真的结束。
        ' 用法：--exit-test[=<秒>]（默认 5 秒后退出）
        ' 需要它是因为「托盘退出」只能点击托盘菜单，AI/脚本无法点击，
        ' 而「进程是否残留」只有退出之后才观察得到。
        If e.Args.Any(Function(A) A.StartsWith("--exit-test", StringComparison.OrdinalIgnoreCase)) Then
            UseUtf8Console()
            VerboseSingleInstance = True
            Me.ShutdownMode = ShutdownMode.OnExplicitShutdown
            AddHandler Me.DispatcherUnhandledException, AddressOf App_DispatcherUnhandledException
            ' 这段在下面统一解析 --background 之前，必须自己先认一次，
            ' 否则模拟出来的永远是「常规启动」，测不到真正的后台模式退出路径。
            If e.Args.Contains("--background") Then IsBackgroundStart = True

            Dim DelaySec As Integer = 5
            Dim Arg As String = e.Args.First(Function(A) A.StartsWith("--exit-test", StringComparison.OrdinalIgnoreCase))
            If Arg.Contains("=") Then Integer.TryParse(Arg.Split("="c)(1), DelaySec)
            If DelaySec < 1 Then DelaySec = 1
            ' --exit-hide：退出前先把窗口隐藏，用于验证「GUI 已隐藏」这条路径
            Dim HideFirst As Boolean = e.Args.Contains("--exit-hide")

            RunNormalStartup()

            Dim Tm As New System.Windows.Threading.DispatcherTimer With {
                .Interval = TimeSpan.FromSeconds(DelaySec)
            }
            AddHandler Tm.Tick,
                Sub()
                    Tm.Stop()
                    If HideFirst Then
                        Console.WriteLine("[ExitTest] 先隐藏主窗口（模拟点 X），再退出")
                        Try
                            If ModHost.Main IsNot Nothing Then ModHost.Main.HideToTray()
                        Catch ex As Exception
                            Console.WriteLine("[ExitTest] 隐藏失败: " & ex.Message)
                        End Try
                    End If
                    Console.WriteLine("[ExitTest] 即将调用 ModHost.ExitApp()（与托盘「退出」同一条路径）")
                    DumpThreads("at-exit")
                    Console.Out.Flush()
                    ModHost.ExitApp()
                    ' 正常情况下进程随即结束，下面这行不会有机会输出；
                    ' 若前台线程挂住了进程，它就会出现 —— 这本身就是证据。
                    Console.WriteLine("[ExitTest] ⚠ ExitApp 已返回但进程仍活着（存在前台线程）")
                    Console.Out.Flush()
                End Sub
            Tm.Start()
            Return
        End If

        If e.Args.Contains("--preflight") Then
            ' 真实验收前的只读飞行前检查：不登出、不登录、不启监控。
            ' 退出码：0 可以开始验收 / 1 存在问题。
            '
            ' 必须关掉日志写入：--preflight 会在进程内跑完整离线测试套件，
            ' 而 NetworkMonitor.RaiseLog 是无条件写每日日志的（测试用例会通过
            ' 它注入「消息 1」这类假日志）。不关就会把测试噪音写进用户的
            ' bin\logs\ 生产日志里 —— 与 --test 的处理保持一致。
            DailyWriteEnabled = False
            Me.ShutdownMode = ShutdownMode.OnExplicitShutdown
            Environment.ExitCode = ModPreflight.Run(
                BindAddress:=GetBindAddress(e.Args),
                RunOfflineTests:=Not e.Args.Contains("--preflight-quick"))
            Me.Shutdown()
            Return
        End If

        If e.Args.Contains("--accept") Then
            ' 真实验收：会登出、会登录、会制造一次认证丢失，按阶段给出 PASS/FAIL。
            ' 退出码：0 全通过 / 1 有阶段失败 / 2 配置未就绪 / 3 需要验证码。
            Me.ShutdownMode = ShutdownMode.OnExplicitShutdown
            DailyWriteEnabled = True
            Environment.ExitCode = ModAcceptTests.Run(
                BindAddress:=GetBindAddress(e.Args),
                WithReconnect:=Not e.Args.Contains("--accept-no-reconnect"))
            Me.Shutdown()
            Return
        End If
        If e.Args.Contains("--e2e") Then
            ' 真实网络端到端认证测试：会真的登出、真的登录，改变校园网认证状态。
            ' 退出码：0 成功 / 1 测试失败 / 2 配置未就绪。
            Me.ShutdownMode = ShutdownMode.OnExplicitShutdown
            DailyWriteEnabled = True
            Environment.ExitCode = ModE2ETests.Run(
                BindAddress:=GetBindAddress(e.Args),
                WithReconnect:=Not e.Args.Contains("--e2e-no-reconnect"))
            Me.Shutdown()
            Return
        End If
        If e.Args.Contains("--diagnose") Then
            ' 只读诊断：不会 Logout / Login / 启动监控，不改变网络状态。
            ' 同样不写生产日志 —— 开发期命令不该污染用户的 bin\logs\。
            DailyWriteEnabled = False
            Me.ShutdownMode = ShutdownMode.OnExplicitShutdown
            Environment.ExitCode = ModE2ETests.RunDiagnose(BindAddress:=GetBindAddress(e.Args))
            Me.Shutdown()
            Return
        End If
        If e.Args.Contains("--test") Then
            DailyWriteEnabled = False
            Me.ShutdownMode = ShutdownMode.OnExplicitShutdown
            ModTestSuite.RunAllTests()
            ' 全部通过 → 0；存在任何失败 → 1。
            ' 这样 CI / 脚本才能据退出码判断测试结果，而不是永远看到 0。
            Environment.ExitCode = If(ModTestSuite.HasFailures, 1, 0)
            Me.Shutdown()
            Return
        End If
        If e.Args.Contains("--background") Then
            IsBackgroundStart = True
        End If

        RunNormalStartup()
    End Sub

    ''' <summary>本进程的单实例守卫。主实例退出时释放。</summary>
    Private _Guard As ModSingleInstance

    ''' <summary>由 --ui-test / --exit-test 置位：把单实例判定过程写到控制台，便于脚本化验证。</summary>
    Private VerboseSingleInstance As Boolean = False

    ''' <summary>
    ''' 正常启动路径（所有命令行模式都已在上面 Return 掉）。
    '''
    ''' 职责划分：
    '''     RunNormalStartup —— 只决定「这次要不要 GUI」
    '''     ModSingleInstance —— 单实例判定 + 激活已有实例
    '''     ModHost           —— 托盘、网络监控、主窗口的创建
    '''     FormMain          —— 纯 GUI 外壳
    '''
    ''' 与旧实现的关键区别：App.xaml 不再有 StartupUri，因此后台启动
    ''' 不会创建 FormMain、不会创建 HWND、不会初始化 WPF 渲染树。
    '''
    ''' 注意：方法名不能叫 Main —— WPF 会为 ApplicationDefinition 生成入口点 Main，
    ''' 重名会报 BC30269。
    ''' </summary>
    Private Sub RunNormalStartup()
        ' 托盘常驻程序：进程存活由托盘决定，而不是由窗口决定
        Me.ShutdownMode = ShutdownMode.OnExplicitShutdown

        ' 单实例必须最先做：不是主实例就直接退出，不做任何 UI/托盘/监控初始化
        _Guard = New ModSingleInstance()
        _Guard.Verbose = VerboseSingleInstance
        _Guard.OnActivateRequested = Sub() ModHost.OnActivate()
        If Not _Guard.TryAcquire() Then
            Environment.ExitCode = 0
            Me.Shutdown()
            Return
        End If

        AddHandler Me.DispatcherUnhandledException, AddressOf App_DispatcherUnhandledException
        System.Runtime.ProfileOptimization.SetProfileRoot(PathExeFolder)
        System.Runtime.ProfileOptimization.StartProfile("Startup.profile")

        ModHost.BackgroundMode = IsBackgroundStart

        ' 托盘与监控是应用级的 —— 两种模式都要，且只建一次。
        ' 放在这里而不是 FormMain 里，后台模式才能「不建窗口也有托盘和自动重连」。
        ' 对应的探针标记也留在这里，否则启动报表会缺 ConfigLoad / TrayInit 两段。
        Dim SwCfg = Diagnostics.Stopwatch.StartNew()
        ModHost.EnsureConfig()
        ModStartupProfile.MarkWithDuration("ConfigLoad", SwCfg.ElapsedMilliseconds)

        ModHost.EnsureTray()
        ModStartupProfile.Mark("TrayInit")

        ModHost.EnsureMonitor()
        AddHandler ModHost.Monitor.StatusChanged, AddressOf OnMonitorStatusChanged
        ModStartupProfile.Mark("PostInit")

        If IsBackgroundStart Then
            ' 首帧之后报告：此时托盘与监控都已就绪
            Dispatcher.BeginInvoke(New Action(Sub() ModStartupProfile.Report("后台启动")),
                                   System.Windows.Threading.DispatcherPriority.Loaded)
            ' 启动早期落一次形态快照：确认后台模式确实没有创建 GUI。
            ' 事后无论进程是否还在，都能从日志对账。
            ModHost.ScheduleBackgroundFormSnapshot(45)
            ' 刻意不创建 FormMain —— 用户第一次打开界面时才建（ModHost.EnsureMainWindow）
        Else
            ModHost.MarkCreateReason("正常 GUI 启动")
            Dim W = ModHost.EnsureMainWindow()
            W.Show()
            ModStartupProfile.Mark("Show")
            W.Activate()
            Dispatcher.BeginInvoke(New Action(Sub() ModStartupProfile.Report("常规启动")),
                                   System.Windows.Threading.DispatcherPriority.Loaded)
        End If
    End Sub

    ''' <summary>后台模式下网络恢复时弹一次托盘提示。</summary>
    Private Sub OnMonitorStatusChanged(Connected As Boolean)
        If Connected AndAlso Not ModHost.BgNotified AndAlso ModHost.Main Is Nothing Then
            ModHost.BgNotified = True
            RunInUi(Sub() ModHost.ShowTrayNotification("连接成功", "网络已连接"))
        End If
    End Sub

    Private Sub Application_Exit(sender As Object, e As ExitEventArgs) Handles Me.Exit
        ' 释放命名 Mutex，让下一次启动能正常成为主实例。
        ' 异常结束时不走这里也没关系：命名内核对象由操作系统回收。
        If _Guard IsNot Nothing Then
            _Guard.Release()
            _Guard = Nothing
        End If
    End Sub

    ''' <summary>
    ''' GUI 自检：连续切换「状态/配置」并报告每一步状态与异常。
    ''' 全部在 UI 线程上执行，异常就地捕获并打印完整堆栈。
    ''' </summary>
    Private Sub RunUiSelfTest(W As FormMain)
        Const ROUNDS As Integer = 10
        Dim OkCount As Integer = 0
        Dim FailCount As Integer = 0
        Console.WriteLine("=== UI SELF TEST ===")
        Console.WriteLine("窗口      : IsLoaded=" & W.IsLoaded.ToString())
        Console.WriteLine("FraStatus : Content=" & DescribeContent(W.FraStatus) & "  Type=" & If(W.FraStatus.Content IsNot Nothing, W.FraStatus.Content.GetType().FullName, "(null)"))
        Console.WriteLine("FraConfig : Content=" & DescribeContent(W.FraConfig) & "  Type=" & If(W.FraConfig.Content IsNot Nothing, W.FraConfig.Content.GetType().FullName, "(null)"))
        Console.WriteLine()
        ' 逐项体检：把每个可能抛 InvalidCastException 的点单独跑一遍，定位到具体是哪一步
        Console.WriteLine("--- 逐项诊断 ---")
        Probe("TabStatus.Child 实际类型", Function() W.TabStatus.Child.GetType().FullName)
        Probe("TabConfig.Child 实际类型", Function() W.TabConfig.Child.GetType().FullName)
        Probe("CType(TabStatus.Child, TextBlock)", Function() CType(W.TabStatus.Child, TextBlock).GetType().Name)
        Probe("CType(TabConfig.Child, TextBlock)", Function() CType(W.TabConfig.Child, TextBlock).GetType().Name)
        Probe("FindResource(ColorBrush3)", Function() W.FindResource("ColorBrush3").GetType().FullName)
        Probe("FindResource(ColorBrushGray5)", Function() W.FindResource("ColorBrushGray5").GetType().FullName)
        Probe("FindResource(ColorBrush1)", Function() W.FindResource("ColorBrush1").GetType().FullName)
        Probe("TabStatus.Background = brush", Function()
                                                     W.TabStatus.Background = CType(W.FindResource("ColorBrush3"), Brush)
                                                     Return W.TabStatus.Background.GetType().Name
                                                 End Function)
        Probe("设置 TextBlock.Foreground", Function()
                                                  CType(W.TabStatus.Child, TextBlock).Foreground = New SolidColorBrush(Colors.White)
                                                  Return "ok"
                                              End Function)
        ' 上面 9 项全 OK，但处理器仍抛异常 -> 嫌疑集中在 Visibility 的读取/写入
        Probe("读 FraStatus.Visibility", Function() W.FraStatus.Visibility.ToString())
        Probe("读 FraConfig.Visibility", Function() W.FraConfig.Visibility.ToString())
        Probe("写 FraStatus.Visibility=Visible", Function()
                                                          W.FraStatus.Visibility = Visibility.Visible
                                                          Return W.FraStatus.Visibility.ToString()
                                                      End Function)
        Probe("写 FraConfig.Visibility=Collapsed", Function()
                                                             W.FraConfig.Visibility = Visibility.Collapsed
                                                             Return W.FraConfig.Visibility.ToString()
                                                         End Function)
        Probe("读 PaneStatus / 页面树", Function()
                                                  Return "FraStatus.Content=" & W.FraStatus.Content.GetType().Name &
                                                         " Parent=" & If(W.FraStatus.Content?.GetType() IsNot Nothing, "?", "?")
                                              End Function)
        Console.WriteLine()
        ' 逐语句体检：直接在 FormMain 内部跑，定位到具体语句
        Console.WriteLine(W.DiagnoseTabSwitch())
        Console.WriteLine()

        For R = 1 To ROUNDS
            For Each Target In {"Config", "Status"}
                Try
                    If Target = "Config" Then
                        W.TabConfig_Click(W.TabConfig, Nothing)
                    Else
                        W.TabStatus_Click(W.TabStatus, Nothing)
                    End If
                    OkCount += 1
                    Console.WriteLine(String.Format("  [{0,2}] {1,-7} OK    Status.Vis={2,-9} Config.Vis={3,-9} PanPage.Children={4}",
                        R, Target, W.FraStatus.Visibility.ToString(), W.FraConfig.Visibility.ToString(), W.PanPage.Children.Count))
                Catch ex As Exception
                    FailCount += 1
                    Console.WriteLine(String.Format("  [{0,2}] {1,-7} FAIL", R, Target))
                    Console.WriteLine("        Type    : " & ex.GetType().FullName)
                    Console.WriteLine("        Message : " & ex.Message)
                    If ex.InnerException IsNot Nothing Then
                        Console.WriteLine("        Inner   : " & ex.InnerException.GetType().FullName & " / " & ex.InnerException.Message)
                    End If
                    Console.WriteLine("        Stack   :")
                    Console.WriteLine(ex.StackTrace)
                    Console.WriteLine()
                End Try
            Next
        Next

        Console.WriteLine()
        Console.WriteLine("=== 汇总: OK=" & OkCount & "  FAIL=" & FailCount & " (共 " & (ROUNDS * 2) & " 次切换) ===")
        Console.Out.Flush()

        ' ---- 窗口生命周期：打开 → 收起 → 再打开 ×5，最后真关闭 ----
        Console.WriteLine()
        Console.WriteLine("=== WINDOW LIFECYCLE (X/Hide/Show x5) ===")
        Dim LifeFail As Integer = 0
        For R = 1 To 5
            Try
                W.HideToTray()                                  ' 模拟点右上角 X
                Dim HiddenOk = Not W.IsVisible
                ModHost.ShowMain()                              ' 模拟点托盘
                Dim ShownOk = W.IsVisible AndAlso ModHost.Main Is W
                Console.WriteLine(String.Format("  [{0}] X->hidden={1}  托盘->show={2}  sameInstance={3}",
                    R, HiddenOk, ShownOk, (ModHost.Main Is W)))
                If Not (HiddenOk AndAlso ShownOk) Then LifeFail += 1
            Catch ex As Exception
                LifeFail += 1
                Console.WriteLine("  [" & R & "] FAIL -> " & ex.GetType().Name & ": " & ex.Message)
                Console.WriteLine("        " & ex.StackTrace)
            End Try
        Next
        Console.WriteLine("  生命周期失败次数 = " & LifeFail)

        ' ---- 监控订阅不累积：验证 Unloaded 真的 RemoveHandler ----
        Console.WriteLine()
        Console.WriteLine("=== MONITOR SUBSCRIPTION (卸载/重载 x3) ===")
        Dim SubDiag As String = W.DiagnoseMonitorSubscription()
        Console.WriteLine(SubDiag)
        Dim SubFail As Integer = If(SubDiag.Contains("异常"), 1, 0)

        Console.WriteLine()
        Console.WriteLine("=== 结果: 切换FAIL=" & FailCount & "  生命周期FAIL=" & LifeFail &
                          "  订阅FAIL=" & SubFail & " ===")
        Console.Out.Flush()
        Environment.ExitCode = If(FailCount = 0 AndAlso LifeFail = 0 AndAlso SubFail = 0, 0, 1)
        ModHost.ExitApp()
    End Sub

    Private Function DescribeContent(F As Object) As String
        Try
            If F Is Nothing Then Return "(null frame)"
            Return If(F.Content Is Nothing, "(null)", "已设置")
        Catch ex As Exception
            Return "读取异常: " & ex.Message
        End Try
    End Function

    ''' <summary>逐项执行并报告结果，用于精确定位 InvalidCastException 的具体位置。</summary>
    Private Sub Probe(Label As String, F As Func(Of String))
        Try
            Console.WriteLine("  [OK]   " & Label.PadRight(38) & " -> " & F())
        Catch ex As Exception
            Console.WriteLine("  [FAIL] " & Label.PadRight(38) & " -> " & ex.GetType().Name & ": " & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' 打印当前进程的托管线程清单，重点标出**前台线程**。
    ''' 用途：诊断「Application.Shutdown 之后进程仍不退出」——
    ''' 只要存在任何一个 IsBackground=False 的存活线程，.NET 就不会结束进程。
    ''' </summary>
    Private Sub DumpThreads(Stage As String)
        Try
            Console.WriteLine("--- 托管线程 @ " & Stage & " ---")
            Dim ThreadType = GetType(Threading.Thread)
            Dim MI As Reflection.MethodInfo = ThreadType.GetMethod("GetAllThreads",
                Reflection.BindingFlags.NonPublic Or Reflection.BindingFlags.Static Or Reflection.BindingFlags.Public)
            If MI Is Nothing Then
                Console.WriteLine("  (拿不到线程列表)")
                Return
            End If
            Dim Arr = CType(MI.Invoke(Nothing, Nothing), IEnumerable)
            Dim Fg As Integer = 0
            For Each Th As Threading.Thread In Arr
                If Th Is Nothing Then Continue For
                Dim Alive As Boolean = False
                Try
                    Alive = (Th.ThreadState And Threading.ThreadState.Stopped) = 0
                Catch
                End Try
                If Not Alive Then Continue For
                Dim IsBg As Boolean = Th.IsBackground
                If Not IsBg Then Fg += 1
                Console.WriteLine(String.Format("  id={0,-4} bg={1,-5} state={2,-12} name={3}",
                    Th.ManagedThreadId, IsBg.ToString(), Th.ThreadState.ToString(), If(Th.Name, "(unnamed)")))
            Next
            Console.WriteLine("  存活线程中的前台线程数 = " & Fg & If(Fg > 0, "   <== 会导致进程无法退出", "   (无前台线程，可正常退出)"))
        Catch ex As Exception
            Console.WriteLine("  线程枚举失败: " & ex.Message)
        End Try
    End Sub

    Private Sub App_DispatcherUnhandledException(sender As Object, e As Windows.Threading.DispatcherUnhandledExceptionEventArgs)
        ' 先把**完整**异常信息落盘再弹窗。
        ' 只显示 ex.Message 会把 StackTrace / InnerException 全部丢掉，
        ' 现场无法定位（本轮「指定的转换无效」就是这么被掩盖的）。
        Try
            Dim Sb As New Text.StringBuilder()
            Sb.AppendLine("[" & GetTimeNow() & "] ===== DispatcherUnhandledException =====")
            Sb.AppendLine("Type           : " & e.Exception.GetType().FullName)
            Sb.AppendLine("Message        : " & e.Exception.Message)
            Sb.AppendLine("Source         : " & If(e.Exception.Source, ""))
            Sb.AppendLine("TargetSite     : " & If(e.Exception.TargetSite?.ToString(), ""))
            If e.Exception.InnerException IsNot Nothing Then
                Sb.AppendLine("InnerException : " & e.Exception.InnerException.GetType().FullName &
                              " / " & e.Exception.InnerException.Message)
            End If
            Sb.AppendLine("StackTrace     :")
            Sb.AppendLine(e.Exception.StackTrace)
            Sb.AppendLine("=================================================")
            DailyWrite(Sb.ToString())
            Log(Sb.ToString())
        Catch
        End Try

        MessageBox.Show("发生未处理的异常：" & vbCrLf & e.Exception.Message & vbCrLf & vbCrLf &
                        "详细信息已写入 bin\logs\ 下的当日日志。", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error)
        e.Handled = True
    End Sub

End Class
