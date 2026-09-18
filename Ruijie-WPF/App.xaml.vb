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

    Private Sub Application_Startup(sender As Object, e As StartupEventArgs) Handles Me.Startup
        If e.Args.Contains("--accept") OrElse e.Args.Contains("--preflight") OrElse
           e.Args.Contains("--e2e") OrElse
           e.Args.Contains("--diagnose") OrElse e.Args.Contains("--test") Then
            UseUtf8Console()
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
        AddHandler Me.DispatcherUnhandledException, AddressOf App_DispatcherUnhandledException
        System.Runtime.ProfileOptimization.SetProfileRoot(PathExeFolder)
        System.Runtime.ProfileOptimization.StartProfile("Startup.profile")
    End Sub

    Private Sub App_DispatcherUnhandledException(sender As Object, e As Windows.Threading.DispatcherUnhandledExceptionEventArgs)
        MessageBox.Show("发生未处理的异常：" & vbCrLf & e.Exception.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error)
        e.Handled = True
    End Sub

End Class
