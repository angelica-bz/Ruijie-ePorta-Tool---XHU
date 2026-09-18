Imports System.Runtime.CompilerServices
Imports Microsoft.VisualBasic

Public Module ExtensionMethods

    <Extension>
    Public Function StartsWithF(s As String, value As String, Optional ignoreCase As Boolean = False) As Boolean
        If s Is Nothing Then Return False
        Dim comparison = If(ignoreCase, StringComparison.OrdinalIgnoreCase, StringComparison.Ordinal)
        Return s.StartsWith(value, comparison)
    End Function

    <Extension>
    Public Function EndsWithF(s As String, value As String, Optional ignoreCase As Boolean = False) As Boolean
        If s Is Nothing Then Return False
        Dim comparison = If(ignoreCase, StringComparison.OrdinalIgnoreCase, StringComparison.Ordinal)
        Return s.EndsWith(value, comparison)
    End Function

    <Extension>
    Public Function ReplaceLineEndings(s As String) As String
        If s Is Nothing Then Return Nothing
        Return s.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Replace(vbLf, Environment.NewLine)
    End Function

    <Extension>
    Public Function Clamp(value As Double, min As Double, max As Double) As Double
        If value < min Then Return min
        If value > max Then Return max
        Return value
    End Function

    <Extension>
    Public Function Clamp(value As Long, min As Long, max As Long) As Long
        If value < min Then Return min
        If value > max Then Return max
        Return value
    End Function

    <Extension>
    Public Function Clamp(value As Integer, min As Integer, max As Integer) As Integer
        If value < min Then Return min
        If value > max Then Return max
        Return value
    End Function

End Module
