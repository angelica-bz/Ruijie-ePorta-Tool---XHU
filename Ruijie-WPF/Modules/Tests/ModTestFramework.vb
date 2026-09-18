Imports Microsoft.VisualBasic

''' <summary>
''' 离线测试框架：跑用例、记统计、给断言。
'''
''' 这里**只放框架**，不放任何被测模块的用例：
'''   用例分散在 Modules/Tests/ModXxxTests.vb，每个模块自己一个文件；
'''   跨模块共用的夹具集中在 Modules/Tests/ModTestFixtures.vb；
'''   总入口在 Modules/Tests/ModTestSuite.vb。
'''
''' 输出契约（--test 靠它给退出码，改动需谨慎）：
'''   每个用例一行       "  PASS  名称" / "  FAIL  名称: 原因"
'''   每个分节           "=== 「分节名」 测试开始 ==="
'''   结尾统计           "=== 分模块统计 ===" 逐节列出，再 "=== 完成: N 通过, M 失败 ==="
''' </summary>
Public Module ModTestFramework

#Region "统计"

    Private _errors As Integer = 0
    Private _passes As Integer = 0

    ''' <summary>本轮测试总数。</summary>
    Public ReadOnly Property TotalTests As Integer
        Get
            Return _passes + _errors
        End Get
    End Property

    ''' <summary>本轮通过的测试数。</summary>
    Public ReadOnly Property PassedTests As Integer
        Get
            Return _passes
        End Get
    End Property

    ''' <summary>本轮失败的测试数。</summary>
    Public ReadOnly Property FailedTests As Integer
        Get
            Return _errors
        End Get
    End Property

    ''' <summary>是否存在失败。--test 据此返回非 0 退出码。</summary>
    Public ReadOnly Property HasFailures As Boolean
        Get
            Return _errors > 0
        End Get
    End Property

    Private Class SectionResult
        Public Property Name As String
        Public Property Passes As Integer
        Public Property Errors As Integer
    End Class

    Private ReadOnly _sections As New List(Of SectionResult)
    Private _currentSection As SectionResult

    ''' <summary>清零统计。每次 RunAllTests 开头调用。</summary>
    Public Sub ResetStatistics()
        _errors = 0
        _passes = 0
        _sections.Clear()
        _currentSection = Nothing
    End Sub

#End Region

#Region "运行"

    ''' <summary>
    ''' 跑一个用例。异常即判失败，消息原样带出 —— 断言失败信息要足够定位问题。
    ''' test 模块通过它登记用例，因此是 Friend 而不是 Private。
    ''' </summary>
    Friend Sub RunTest(name As String, test As Action)
        Try
            test()
            _passes += 1
            Console.WriteLine($"  PASS  {name}")
        Catch ex As Exception
            _errors += 1
            Console.WriteLine($"  FAIL  {name}: {ex.Message}")
        End Try
    End Sub

    ''' <summary>开始一个分节。分节统计是相对量，因此记的是当时的累计值。</summary>
    Public Sub BeginSection(name As String)
        _currentSection = New SectionResult With {.Name = name, .Passes = _passes, .Errors = _errors}
    End Sub

    Public Sub EndSection()
        If _currentSection Is Nothing Then Return
        _currentSection.Passes = _passes - _currentSection.Passes
        _currentSection.Errors = _errors - _currentSection.Errors
        _sections.Add(_currentSection)
        _currentSection = Nothing
    End Sub

    ''' <summary>
    ''' 一个分节：打标题 → BeginSection → 让该模块登记并执行用例 → EndSection。
    ''' 除第一节外都先补一个空行，保持与历史输出一致。
    ''' </summary>
    Friend Sub Section(name As String, register As Action, Optional First As Boolean = False)
        If First Then
            Console.WriteLine("=== " & name & " 测试开始 ===" & vbCrLf)
        Else
            Console.WriteLine(vbCrLf & "=== " & name & " 测试开始 ===" & vbCrLf)
        End If
        BeginSection(name)
        register()
        EndSection()
    End Sub

    ''' <summary>打印分模块统计与总计。</summary>
    Public Sub WriteStatistics()
        Console.WriteLine(vbCrLf & "=== 分模块统计 ===" & vbCrLf)
        For Each Item In _sections
            Console.WriteLine($"  {Item.Name,-20} {Item.Passes} 通过, {Item.Errors} 失败")
        Next

        Console.WriteLine(vbCrLf & $"=== 完成: {_passes} 通过, {_errors} 失败 ===")
    End Sub

#End Region

#Region "断言"

    Friend Sub AssertTrue(condition As Boolean, msg As String)
        If Not condition Then Throw New Exception($"断言失败: {msg}")
    End Sub

    Friend Sub AssertFalse(condition As Boolean, msg As String)
        If condition Then Throw New Exception($"断言失败: {msg}")
    End Sub

    Friend Sub AssertEqual(expected As Object, actual As Object, msg As String)
        If Not expected.Equals(actual) Then
            Throw New Exception($"断言失败: {msg} (期望={expected}, 实际={actual})")
        End If
    End Sub

    ''' <summary>断言 action 抛出指定类型的异常。只校验类型，不校验消息文本。</summary>
    Friend Sub AssertThrows(action As Action, expectedType As Type, msg As String)
        Try
            action()
        Catch ex As Exception
            If Not expectedType.IsInstanceOfType(ex) Then
                Throw New Exception($"断言失败: {msg} (期望 {expectedType.Name}, 实际 {ex.GetType().Name}: {ex.Message})")
            End If
            Return
        End Try
        Throw New Exception($"断言失败: {msg} (期望 {expectedType.Name}, 但未抛出任何异常)")
    End Sub

    ''' <summary>断言文本包含某片段，用于检查可读错误信息。</summary>
    Friend Sub AssertContains(haystack As String, needle As String, msg As String)
        If haystack Is Nothing OrElse Not haystack.Contains(needle) Then
            Throw New Exception($"断言失败: {msg}（应包含「{needle}」，实际「{haystack}」）")
        End If
    End Sub

    ''' <summary>断言文本不包含某片段，主要用于「不得泄露敏感值」这类检查。</summary>
    Friend Sub AssertNotContains(haystack As String, needle As String, msg As String)
        If haystack IsNot Nothing AndAlso haystack.Contains(needle) Then
            Throw New Exception($"断言失败: {msg}（不应包含「{needle}」）")
        End If
    End Sub

#End Region

#Region "通用小工具"

    ''' <summary>测试用：由 key/value 交替的参数构造字典。</summary>
    Friend Function Dict(ParamArray Pairs As Object()) As Dictionary(Of String, Object)
        Dim Result As New Dictionary(Of String, Object)
        Dim Index As Integer = 0
        While Index + 1 < Pairs.Length
            Result(CStr(Pairs(Index))) = Pairs(Index + 1)
            Index += 2
        End While
        Return Result
    End Function

#End Region

End Module
