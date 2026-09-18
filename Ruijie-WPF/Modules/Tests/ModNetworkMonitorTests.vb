Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModNetworkMonitorTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModNetworkMonitorTests

#Region "反射辅助"

    Private Function CreateMonitor() As NetworkMonitor
        Dim cfg = GetDefaultConfig()
        Return New NetworkMonitor(cfg)
    End Function

    Private Sub CallRaiseLog(monitor As NetworkMonitor, msg As String)
        Dim method = GetType(NetworkMonitor).GetMethod("RaiseLog",
            BindingFlags.Instance Or BindingFlags.NonPublic)
        method.Invoke(monitor, New Object() {msg})
    End Sub

    Private Sub CallRaiseSchoolStatus(monitor As NetworkMonitor, serverUrl As String)
        Dim method = GetType(NetworkMonitor).GetMethod("RaiseSchoolStatus",
            BindingFlags.Instance Or BindingFlags.NonPublic)
        method.Invoke(monitor, New Object() {serverUrl})
    End Sub

    Private Sub SetWasConnected(monitor As NetworkMonitor, connected As Boolean)
        Dim field = GetType(NetworkMonitor).GetField("_WasConnected",
            BindingFlags.Instance Or BindingFlags.NonPublic)
        field.SetValue(monitor, New Nullable(Of Boolean)(connected))
    End Sub


#End Region

#Region "测试用例"

    Private Sub Test_InitialSnapshot()
        Dim m = CreateMonitor()
        Dim snap = m.GetSnapshot()

        AssertFalse(snap.Connected, "初始Connected应为False")
        AssertFalse(snap.SchoolReachable.HasValue, "初始SchoolReachable应为Nothing")
        AssertEqual(0, snap.Logs.Length, "初始日志缓冲应为空")
    End Sub

    Private Sub Test_SingleLogBuffered()
        Dim m = CreateMonitor()
        CallRaiseLog(m, "[08:00:01] 监控启动 - 网络已连接")

        Dim snap = m.GetSnapshot()
        AssertEqual(1, snap.Logs.Length, "应返回1条日志")
        AssertTrue(snap.Logs(0).Contains("监控启动"), "日志内容应包含'监控启动'")
    End Sub

    Private Sub Test_BufferCapFifty()
        Dim m = CreateMonitor()
        For i = 1 To 55
            CallRaiseLog(m, $"消息 {i}")
        Next

        Dim snap = m.GetSnapshot()
        AssertEqual(50, snap.Logs.Length, "超过50条应截断为最近50条")
        AssertTrue(snap.Logs(0).Contains("消息 6"), "第一条应为第6条(5条旧的被挤出)")
        AssertTrue(snap.Logs(49).Contains("消息 55"), "最后一条应为第55条")
    End Sub

    Private Sub Test_SchoolReachableTracked()
        Dim m = CreateMonitor()
        ' 初始快照 SchoolReachable 应为 Nothing
        Dim snap1 = m.GetSnapshot()
        AssertFalse(snap1.SchoolReachable.HasValue, "初始SchoolReachable应为Nothing")

        ' 调用 RaiseSchoolStatus 之后应能读到值
        CallRaiseSchoolStatus(m, "http://127.0.0.1")
        Dim snap2 = m.GetSnapshot()
        AssertTrue(snap2.SchoolReachable.HasValue, "调用后SchoolReachable应有值")
    End Sub

    Private Sub Test_ConcurrentGetSnapshotSafe()
        Dim m = CreateMonitor()
        Dim done As New ManualResetEvent(False)
        Dim crashCount As Integer = 0

        Dim writer As New Thread(
            Sub()
                Try
                    For i = 1 To 200
                        CallRaiseLog(m, $"并发消息 {i}")
                        Thread.Sleep(1)
                    Next
                Catch
                    Interlocked.Increment(crashCount)
                End Try
            End Sub) With {.Name = "TestWriter", .IsBackground = True}

        Dim reader As New Thread(
            Sub()
                Try
                    For i = 1 To 200
                        Dim snap = m.GetSnapshot()
                        Thread.Sleep(1)
                    Next
                Catch
                    Interlocked.Increment(crashCount)
                End Try
            End Sub) With {.Name = "TestReader", .IsBackground = True}

        writer.Start()
        reader.Start()
        writer.Join(5000)
        reader.Join(5000)

        AssertEqual(0, crashCount, "并发读写应无异常")
    End Sub

    Private Sub Test_BackgroundStartScenario()
        ' 模拟：开机启动 → monitor 产生日志和状态 → UI挂载 → 快照回放
        Dim m = CreateMonitor()

        ' 模拟 monitor 后台运行产生状态
        SetWasConnected(m, True)  ' 网络已连接
        CallRaiseSchoolStatus(m, "http://127.0.0.1")  ' 校园网可达
        CallRaiseLog(m, "[08:00:01] 监控启动 - 网络已连接")
        CallRaiseLog(m, "[08:00:02] 连接成功")

        ' UI 挂载时取快照
        Dim snap = m.GetSnapshot()

        ' 验证：连接状态应正确
        AssertTrue(snap.Connected, "快照应反映网络已连接")
        ' 验证：校园网状态应存在
        AssertTrue(snap.SchoolReachable.HasValue, "快照应包含校园网状态")
        ' 验证：日志应回放
        AssertEqual(2, snap.Logs.Length, "应有2条日志")
        AssertTrue(snap.Logs(1).Contains("连接成功"), "最后一条日志应包含'连接成功'")
    End Sub

    Private Sub Test_LogsUnderCapReturnAll()
        Dim m = CreateMonitor()
        For i = 1 To 3
            CallRaiseLog(m, $"消息 {i}")
        Next

        Dim snap = m.GetSnapshot()
        AssertEqual(3, snap.Logs.Length, "不足50条时应全部返回")
        AssertTrue(snap.Logs(2).Contains("消息 3"), "最后一条应为消息3")
    End Sub


#End Region

#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("初始快照为空", AddressOf Test_InitialSnapshot)
        RunTest("单条日志入缓冲后可取回", AddressOf Test_SingleLogBuffered)
        RunTest("超过50条仅保留最近50条", AddressOf Test_BufferCapFifty)
        RunTest("RaiseSchoolStatus写入_SchoolReachable", AddressOf Test_SchoolReachableTracked)
        RunTest("并发读写GetSnapshot不抛异常", AddressOf Test_ConcurrentGetSnapshotSafe)
        RunTest("模拟开机启动→UI挂载→快照回放场景", AddressOf Test_BackgroundStartScenario)
        RunTest("日志数量不足50时全部返回", AddressOf Test_LogsUnderCapReturnAll)
    End Sub

#End Region

End Module