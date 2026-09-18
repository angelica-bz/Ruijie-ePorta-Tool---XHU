Public Class MyScrollBar
    Inherits Primitives.ScrollBar

    '基础

    Public Uuid As Integer = GetUuid()

    '指向动画

    Private Sub RefreshColor() Handles Me.IsEnabledChanged, Me.GotMouseCapture, Me.LostMouseCapture, Me.MouseEnter, Me.MouseLeave, Me.IsVisibleChanged
        Try

            Dim NewOpacity As Double, NewColor As String, Time As Integer
            If Not IsVisible Then
                NewOpacity = 0
                Time = 20
                NewColor = "ColorBrush4"
            ElseIf IsMouseCaptureWithin Then
                NewOpacity = 1
                NewColor = "ColorBrush4"
                Time = 50
            ElseIf IsMouseOver Then
                NewOpacity = 0.9
                NewColor = "ColorBrush3"
                Time = 130
            Else
                NewOpacity = 0.5
                NewColor = "ColorBrush4"
                Time = 180
            End If
            If IsLoaded AndAlso AniControlEnabled = 0 Then
                AniStart({
                         AaColor(Me, ForegroundProperty, NewColor, Time),
                         AaOpacity(Me, NewOpacity - Opacity, Time)
                 }, "MyScrollBar Color " & Uuid)
            Else
                AniStop("MyScrollBar Color " & Uuid)
                SetResourceReference(ForegroundProperty, NewColor)
                Opacity = NewOpacity
            End If

        Catch ex As Exception
            Log(ex, "滚动条颜色改变出错")
        End Try
    End Sub

End Class
