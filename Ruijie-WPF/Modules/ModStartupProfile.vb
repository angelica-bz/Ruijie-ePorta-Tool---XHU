Imports System.Diagnostics
Imports System.Runtime.InteropServices
Imports Microsoft.VisualBasic

''' <summary>
''' 启动阶段性能探针。
'''
''' 目的：把「Windows 什么时候创建进程」和「进程内部初始化花了多久」分开，
''' 因为这两者的优化手段完全不同 —— 前者要改自启动方式，后者要改代码。
'''
''' ── 时间基准（重要，务必先读）────────────────────────────────────────
''' 本模块的**所有时间点**都取「距 Begin() 的毫秒数」，来自同一个单调
''' Stopwatch，因此彼此同轴、可直接相减、不会出现负的分段。
'''
''' ProcessToApp 是唯一的例外：它描述的是「进程创建 → Begin()」这一段，
''' 属于进程**外部**的时间，无法用进程内的 Stopwatch 表达，因此单独作为
''' 一个「时长」记录（Note 里标注 ms），**不参与分段差值**。
'''
''' 曾经踩过的两个坑：
'''   1. 把局部 Stopwatch 的读数当时间点 -> 分段出现负数（-431 ms）
'''   2. 用 Process.StartTime 取进程创建时刻 -> 在 Windows 登录风暴下
'''      该属性内部要走一次 WMI 查询，可能被 CPU 饥饿阻塞数秒，导致
'''      ProcessToApp 出现大于 Total 的不可能值（实测 4242 ms vs Total 1001 ms）
''' 现在改用 GetProcessTimes：直接读内核里的进程创建时间，
''' 不依赖 WMI、不受系统负载影响。
''' ────────────────────────────────────────────────────────────────
'''
''' 输出：写一行到日志（便于事后回溯），同时写控制台（重定向时可采集）。
''' 未启动探针时全部是空操作，不引入额外开销。
''' </summary>
Public Module ModStartupProfile

    Private ReadOnly _Watch As Stopwatch = New Stopwatch()
    Private ReadOnly _Marks As New List(Of String())
    Private _Started As Boolean = False

#Region "进程创建时间"

    ''' <summary>
    ''' FILETIME(1601-01-01) 与 DateTime(0001-01-01) 之间的 ticks 差。
    '''
    ''' **不要手写这个 19 位数字面量** —— 实测在 VB 里会被当作 Double 参与运算，
    ''' 导致减法结果完全错误（trace 里 correctedTicks 出现 -3.7e17 这种值）。
    ''' 这里直接让 .NET 用 DateTime 相减算出来，既准确又不会写错零的个数。
    ''' </summary>
    Private ReadOnly FileTimeEpochTicks As Long =
        (New DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc) -
         New DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks

    ''' <summary>
    ''' FILETIME 的两个 32 位字段必须是**有符号** Integer。
    ''' 用 UInteger 会在转成 Long 时被零扩展：当低位字 > &amp;H7FFFFFFF 时
    ''' （实测 lo = &amp;H8B84BB76）会算出一个比真实值大约 2^32 的 ticks，
    ''' 减去 1601 纪元偏移后仍是负数，于是被判为「取值异常」而丢弃。
    ''' 这里用 Integer 让它按 64 位整数组合时正常符号扩展。
    ''' </summary>
    <StructLayout(LayoutKind.Sequential)>
    Private Structure RawFileTime
        Public lo As Integer
        Public hi As Integer
    End Structure

    Private Declare Function GetProcessTimesRaw Lib "kernel32" Alias "GetProcessTimes" (
        h As IntPtr, ByRef c As RawFileTime, ByRef e As RawFileTime,
        ByRef k As RawFileTime, ByRef u As RawFileTime) As Boolean

    ''' <summary>本进程的创建时刻（UTC）。取不到时返回 Nothing。</summary>
    Private Function GetProcessCreationUtc() As Nullable(Of DateTime)
        Try
            Dim C, X, K, U As RawFileTime
            Dim H As IntPtr = Process.GetCurrentProcess().Handle
            If Not GetProcessTimesRaw(H, C, X, K, U) Then
                Return Nothing
            End If
            Dim T As Long = (CLng(C.hi) << 32) Or CLng(C.lo)
            ' FILETIME 以 1601 为 0 点，DateTime 以 0001 为 0 点；
            ' 1601 更晚，所以 FILETIME 的计数值**更小**，要**加上**偏移才是 DateTime 的 ticks。
            ' 之前写成减法，得到的是约公元 426 年的值。
            Dim Corr As Long = T + FileTimeEpochTicks
            If Corr <= 0 OrElse Corr > DateTime.MaxValue.Ticks Then
                Return Nothing
            End If
            Return New DateTime(Corr, DateTimeKind.Utc)
        Catch ex As Exception
            Return Nothing
        End Try
    End Function

#End Region

    ''' <summary>启动探针。在 Application_Startup 最开头调用一次。</summary>
    Public Sub Begin()
        If _Started Then Return
        _Started = True
        _Marks.Clear()
        _Watch.Restart()

        ' 先取创建时刻（此刻系统调用最少），再取当前时刻，两者都在同一次 Begin 内完成
        Dim Created = GetProcessCreationUtc()
        If Created.HasValue Then
            Dim DeltaMs As Long = CLng((DateTime.UtcNow - Created.Value).TotalMilliseconds)
            ' 防御：负数或明显不合理的值不记录，避免污染报表
            If DeltaMs >= 0 AndAlso DeltaMs < 600000 Then
                Record("ProcessToApp", DeltaMs, "时长（进程外）")
            Else
                Record("ProcessToApp", -1, "取值异常，已忽略")
            End If
        Else
            Record("ProcessToApp", -1, "无法获取进程创建时间")
        End If
    End Sub

    ''' <summary>
    ''' 记录一个时间点。时间取「距 Begin 的毫秒数」，因此天然与其它 Mark 同轴、可比大小。
    ''' </summary>
    Public Sub Mark(Name As String)
        If Not _Started Then Return
        Record(Name, _Watch.ElapsedMilliseconds)
    End Sub

    ''' <summary>
    ''' 记录一个时间点，并附上「这一步自己花了多久」。
    ''' 注意：时间轴上的位置仍取全局 elapsed，DurationMs 只作为附注 ——
    ''' 早先直接把局部 Stopwatch 的读数当时间点，会让分段差值出现负数。
    ''' </summary>
    Public Sub MarkWithDuration(Name As String, DurationMs As Long)
        If Not _Started Then Return
        Record(Name, _Watch.ElapsedMilliseconds, DurationMs.ToString() & " ms")
    End Sub

    Private Sub Record(Name As String, Ms As Long, Optional Note As String = "")
        _Marks.Add(New String() {Name, Ms.ToString(), If(Note, "")})
    End Sub

    ''' <summary>
    ''' 输出 [Startup] 段。
    ''' Total 取同轴（Stopwatch）上的最后一个 Mark，因此跨线程记录也不会算错。
    ''' </summary>
    Public Sub Report(Optional Title As String = "启动基线")
        If Not _Started OrElse _Marks.Count = 0 Then Return

        Dim Lines As New List(Of String)
        Lines.Add("[Startup] " & Title)

        For Each M In _Marks
            Dim Note As String = If(M.Length > 2 AndAlso M(2).Length > 0, "   (" & M(2) & ")", "")
            Lines.Add("  " & M(0).PadRight(20) & M(1) & " ms" & Note)
        Next

        ' 分段差值：相邻两个 Mark 之间花了多久，一眼看出谁最贵。
        '
        ' ProcessToApp 是「进程外」的时长，与 Process 内的 Stopwatch 不同轴，
        ' 直接相减会得到负数；因此把它排除在分段表之外，只出现在上面的明细里。
        Dim FirstSeg As Integer = If(_Marks.Count > 1 AndAlso _Marks(0)(0) = "ProcessToApp", 2, 1)
        If FirstSeg <= _Marks.Count - 1 Then
            Lines.Add("  " & "--- 分段 ---")
            For I As Integer = FirstSeg To _Marks.Count - 1
                Dim Prev As Long = CLng(_Marks(I - 1)(1))
                Dim Cur As Long = CLng(_Marks(I)(1))
                Lines.Add("  " & (_Marks(I - 1)(0) & " → " & _Marks(I)(0)).PadRight(20) &
                          (Cur - Prev) & " ms")
            Next
        End If

        ' Total 只累计同轴的 Mark（跳过 ProcessToApp）
        Dim Total As Long = CLng(_Marks(_Marks.Count - 1)(1))
        Lines.Add("  " & "Total".PadRight(20) & Total & " ms")
        Lines.Add("  " & "（ProcessToApp 为进程外时长，不计入 Total 与分段）")

        For Each Line In Lines
            Console.WriteLine(Line)
            Try
                DailyWrite(Line & vbCrLf)
            Catch
            End Try
        Next

        Console.Out.Flush()
    End Sub

    ''' <summary>是否已经开始记录。</summary>
    Public ReadOnly Property IsActive As Boolean
        Get
            Return _Started
        End Get
    End Property

End Module
