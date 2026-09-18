Imports System.Diagnostics
Imports System.Security.Cryptography
Imports System.Text
Imports Microsoft.VisualBasic

''' <summary>
''' 真实验收前的**只读**飞行前检查（--preflight）。
'''
''' 它只回答一个问题：**现在这台机器、这份配置、这个构建，够不够格跑一次真实验收。**
''' 因此它绝对不做 Logout / Login，也不启动 NetworkMonitor —— 跑完校园网状态不变。
'''
''' 输出 §十七 要求的固定块：
'''     === PRE-FLIGHT ===
'''     Repository / Network / Config / Build / Tests
'''
''' 退出码：0 可以开始真实验收 / 1 存在会挡住验收的问题。
''' </summary>
Public Module ModPreflight

    Public Const ExitReady As Integer = 0
    Public Const ExitNotReady As Integer = 1

    Private Sub Item(Name As String, Value As String)
        Console.WriteLine("  " & Name.PadRight(16) & ": " & Value)
    End Sub

    ''' <summary>跑一次飞行前检查。RunOfflineTests=False 时跳过离线测试（快速检查）。</summary>
    Public Function Run(Optional BindAddress As String = "",
                        Optional RunOfflineTests As Boolean = True) As Integer
        Dim Problems As New List(Of String)()

        Console.WriteLine("=== PRE-FLIGHT ===" & vbCrLf)

        ' ---------------- Repository ----------------
        Console.WriteLine("Repository:")
        Item("Root", DescribeRepoRoot())
        Item("Branch", DescribeGit("rev-parse --abbrev-ref HEAD"))
        Item("WorkingTree", DescribeWorkingTree())

        ' ---------------- Network ----------------
        Dim CampusIp As String = DetectCampusAddress()
        Dim Effective As String = If(String.IsNullOrEmpty(BindAddress), CampusIp, BindAddress)

        Console.WriteLine()
        Console.WriteLine("Network:")
        Item("Campus NIC", DescribeNicFor(Effective))
        Item("Campus IP", If(String.IsNullOrEmpty(Effective), "(未识别)", Effective))
        Item("BindAddress", If(String.IsNullOrEmpty(Effective),
                               "(为空 → 走默认路由，VPN/TUN 会抢走请求)",
                               Effective & " —— 验收建议带上 --bind=" & Effective))

        Dim PortalOk As Boolean = False
        Try
            PortalOk = ModNetwork.TcpProbe(ModConfig.SchoolServer, 3)
        Catch
        End Try
        Item("Portal", ModConfig.SchoolServer & " -> " & If(PortalOk, "Reachable", "UNREACHABLE"))
        If Not PortalOk Then Problems.Add("门户 TCP 不可达")

        Dim Internet As Boolean = False
        Try
            Internet = ModNetwork.TestInternet(Timeout:=4)
        Catch
        End Try
        Item("Internet", If(Internet, "connected", "unauthenticated"))

        ' 探测走的是外网站点，未认证时被门户劫持成 302 才算正常
        Try
            Dim Probe = ModPortalDiscover.DiscoverRedirect(ModPortalDiscover.DefaultProbeUrl, 6, Effective)
            Item("Probe", Probe.Status.ToString())
            Item("Probe means", If(Probe.Status = ModPortalDiscover.PortalDiscoveryStatus.AlreadyOnline,
                                   "当前已联网，验收将先登出再重跑全链路",
                                   If(Probe.Status = ModPortalDiscover.PortalDiscoveryStatus.NeedAuthentication,
                                      "当前未认证，验收将直接进入真实登录",
                                      "探测异常：" & Probe.Message)))
        Catch ex As Exception
            Item("Probe", "FAIL —— " & ex.Message)
            Problems.Add("门户探测失败：" & ex.Message)
        End Try

        ' ---------------- Config ----------------
        Console.WriteLine()
        Console.WriteLine("Config:")
        Dim Cfg As AppConfig = Nothing
        Try
            Cfg = ModConfig.LoadAppConfig()
        Catch ex As Exception
            Item("Load", "FAIL —— " & ex.Message)
            Problems.Add("配置读取失败")
        End Try

        If Cfg Is Nothing Then
            Item("Version", "(n/a)")
        Else
            Item("Version", "v" & Cfg.Version)
            Item("User", If(String.IsNullOrEmpty(Cfg.User.UserId), "missing", "configured"))
            Item("Operator", ModConfig.GetOperatorToken(Cfg.User.[Operator]))

            Dim Raw As String = ""
            Try
                Raw = ModConfig.ReadStoredProtectedPassword()
            Catch
            End Try
            Dim Decryptable As Boolean = False
            Try
                Dim Plain As String = ""
                Decryptable = Raw.Length > 0 AndAlso ModCredential.TryUnprotectPassword(Raw, Plain)
            Catch
            End Try
            Item("Password", If(Raw.Length = 0, "missing",
                                If(Decryptable, "configured (DPAPI 可解密)", "configured (无法解密！)")))

            If Cfg.Version <> ModConfig.CurrentConfigVersion Then
                Problems.Add("配置版本 v" & Cfg.Version & "，需要 v" & ModConfig.CurrentConfigVersion)
            End If
            If String.IsNullOrEmpty(Cfg.User.UserId) Then Problems.Add("没有保存学号")
            If Raw.Length = 0 Then Problems.Add("没有保存密码")
            If Raw.Length > 0 AndAlso Not Decryptable Then Problems.Add("已保存的密码无法解密（DPAPI）")
            If Cfg.User.[Operator] = PortalOperator.Unknown Then Problems.Add("没有有效的网络类型")

            Item("Ready", If(Problems.Count = 0, "YES", "NO —— " & String.Join("；", Problems.ToArray())))

            ' 自动重连开关：验收会在内存里临时打开，不需要改配置
            Item("AutoReconnect", Cfg.[Function].AutoReconnect.ToString().ToLower() &
                                  "（验收时在内存中临时置 true，不改配置文件）")
        End If

        ' ---------------- Build ----------------
        Console.WriteLine()
        Console.WriteLine("Build:")
        Dim ExePath As String = ""
        Try
            ExePath = Reflection.Assembly.GetEntryAssembly().Location
        Catch
        End Try
        If ExePath.Length > 0 AndAlso IO.File.Exists(ExePath) Then
            Dim Fi As New IO.FileInfo(ExePath)
            Item("Exe", Fi.Name)
            Item("LastWriteTime", Fi.LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss"))
            Item("Size", Fi.Length.ToString() & " bytes")
            Item("SHA256", TrySha256(ExePath))
        Else
            Item("Exe", "(无法定位)")
        End If

        ' ---------------- Tests ----------------
        Console.WriteLine()
        Console.WriteLine("Tests:")
        If RunOfflineTests Then
            Console.WriteLine("  （下面运行完整离线测试套件）")
            Console.WriteLine()
            ModTestSuite.RunAllTests()
            Console.WriteLine()
            Item("Passed", ModTestSuite.PassedTests.ToString())
            Item("Failed", ModTestSuite.FailedTests.ToString())
            Item("ExitCode", If(ModTestSuite.HasFailures, 1, 0).ToString())
            If ModTestSuite.HasFailures Then Problems.Add("离线测试未全通过")
        Else
            Item("Passed", "(已跳过)")
            Item("Failed", "(已跳过)")
            Item("ExitCode", "(已跳过)")
        End If

        ' ---------------- 结论 ----------------
        Console.WriteLine()
        Console.WriteLine("E2E Readiness:")
        If Problems.Count = 0 Then
            Item("Result", "READY")
            If String.IsNullOrEmpty(BindAddress) AndAlso Not String.IsNullOrEmpty(Effective) Then
                Item("Suggested", "--accept --bind=" & Effective &
                                  "（本机默认路由被 VPN/TUN 接管，不绑定会在未认证时探测失败）")
            Else
                Item("Suggested", "--accept" & If(String.IsNullOrEmpty(Effective), "", " --bind=" & Effective))
            End If
            Console.WriteLine()
            Console.WriteLine("=== PRE-FLIGHT READY (exit 0) ===")
            Return ExitReady
        End If

        Item("Result", "NOT READY")
        For Each Problem In Problems
            Item("Problem", Problem)
        Next
        Console.WriteLine()
        Console.WriteLine("=== PRE-FLIGHT NOT READY (exit 1) ===")
        Return ExitNotReady
    End Function

#Region "小工具"

    Private Function TrySha256(Path As String) As String
        Try
            Using Sha As SHA256 = SHA256.Create()
                Using Fs As IO.FileStream = IO.File.OpenRead(Path)
                    Return BitConverter.ToString(Sha.ComputeHash(Fs)).Replace("-", "")
                End Using
            End Using
        Catch ex As Exception
            Return "(计算失败：" & ex.Message & ")"
        End Try
    End Function

    ''' <summary>沿可执行文件目录向上找 .git，定位仓库根。找不到就返回可执行文件所在目录。</summary>
    Private Function DescribeRepoRoot() As String
        Try
            Dim Dir As String = IO.Path.GetDirectoryName(Reflection.Assembly.GetEntryAssembly().Location)
            For I As Integer = 1 To 8
                If Dir Is Nothing Then Exit For
                If IO.Directory.Exists(IO.Path.Combine(Dir, ".git")) Then Return Dir
                Dir = IO.Path.GetDirectoryName(Dir)
            Next
        Catch
        End Try
        Return "(未找到 .git)"
    End Function

    ''' <summary>直接读 .git/HEAD，避免为了一行信息去起一个 git 进程。</summary>
    Private Function DescribeGit(Args As String) As String
        Try
            Dim Root As String = DescribeRepoRoot()
            If Root = "(未找到 .git)" Then Return "(未知)"

            Dim Head As String = IO.Path.Combine(Root, ".git", "HEAD")
            If Not IO.File.Exists(Head) Then Return "(未知)"
            Dim Text As String = IO.File.ReadAllText(Head).Trim()

            ' "ref: refs/heads/main" → main
            If Text.StartsWith("ref:", StringComparison.OrdinalIgnoreCase) Then
                Return Text.Substring(4).Trim().Split("/"c).Last()
            End If
            Return "detached (" & Text.Substring(0, Math.Min(8, Text.Length)) & ")"
        Catch
            Return "(未知)"
        End Try
    End Function

    ''' <summary>
    ''' 工作区状态。调用 git 只是为了给开发者一个数字，
    ''' 失败（没装 git / 被拦）不影响飞行前结论。
    ''' </summary>
    Private Function DescribeWorkingTree() As String
        Try
            Dim Root As String = DescribeRepoRoot()
            If Root = "(未找到 .git)" Then Return "(未知)"

            Dim Psi As New ProcessStartInfo("git", "status --porcelain") With {
                .WorkingDirectory = Root,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .UseShellExecute = False,
                .CreateNoWindow = True
            }
            Using P As Process = Process.Start(Psi)
                If P Is Nothing Then Return "(git 未启动)"
                Dim Out As String = P.StandardOutput.ReadToEnd()
                If Not P.WaitForExit(5000) Then
                    Try
                        P.Kill()
                    Catch
                    End Try
                    Return "(git 超时)"
                End If
                If P.ExitCode <> 0 Then Return "(git 返回 " & P.ExitCode & ")"

                Dim Modified As Integer = 0
                Dim Untracked As Integer = 0
                For Each Line In Out.Split(New String() {vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                    If Line.StartsWith("??") Then
                        Untracked += 1
                    Else
                        Modified += 1
                    End If
                Next
                If Modified = 0 AndAlso Untracked = 0 Then Return "clean"
                Return Modified & " modified, " & Untracked & " untracked"
            End Using
        Catch ex As Exception
            Return "(未知：" & ex.Message & ")"
        End Try
    End Function

    ''' <summary>
    ''' 猜校园网地址：排除 APIPA(169.254)、Clash 的 198.18/15、Radmin 的 26/8。
    ''' 猜错也不致命 —— 验收时用 --bind 显式指定即可。
    ''' </summary>
    Private Function DetectCampusAddress() As String
        Try
            For Each Nic In Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                If Nic.OperationalStatus <> Net.NetworkInformation.OperationalStatus.Up Then Continue For
                If Nic.NetworkInterfaceType = Net.NetworkInformation.NetworkInterfaceType.Loopback Then Continue For

                For Each Addr In Nic.GetIPProperties().UnicastAddresses
                    If Addr.Address.AddressFamily <> Net.Sockets.AddressFamily.InterNetwork Then Continue For
                    Dim Ip As String = Addr.Address.ToString()
                    If Ip.StartsWith("169.254.") Then Continue For
                    If Ip.StartsWith("198.18.") OrElse Ip.StartsWith("198.19.") Then Continue For
                    If Ip.StartsWith("26.") Then Continue For
                    Return Ip
                Next
            Next
        Catch
        End Try
        Return ""
    End Function

    Private Function DescribeNicFor(Ip As String) As String
        If String.IsNullOrEmpty(Ip) Then Return "(未识别)"
        Try
            For Each Nic In Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                For Each Addr In Nic.GetIPProperties().UnicastAddresses
                    If Addr.Address.AddressFamily = Net.Sockets.AddressFamily.InterNetwork AndAlso
                       Addr.Address.ToString() = Ip Then
                        Return Nic.Name
                    End If
                Next
            Next
        Catch
        End Try
        Return "(未匹配到网卡)"
    End Function

#End Region

End Module
