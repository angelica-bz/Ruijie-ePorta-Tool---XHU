Public Class MyScrollViewer
    Inherits ScrollViewer

    Public Sub New()
        Focusable = False
    End Sub

    Public Property DeltaMult As Double = 1

    Private RealOffset As Double
    Private Sub MyScrollViewer_PreviewMouseWheel(sender As Object, e As MouseWheelEventArgs) Handles Me.PreviewMouseWheel
        If e.Delta = 0 OrElse ActualHeight = 0 OrElse ScrollableHeight = 0 Then Return
        Dim SourceType = e.Source.GetType
        If Content.TemplatedParent Is Nothing AndAlso (
                (GetType(ComboBox).IsAssignableFrom(SourceType) AndAlso CType(e.Source, ComboBox).IsDropDownOpen) OrElse
                (GetType(TextBox).IsAssignableFrom(SourceType) AndAlso CType(e.Source, TextBox).AcceptsReturn) OrElse
                GetType(ComboBoxItem).IsAssignableFrom(SourceType) OrElse
                TypeOf e.Source Is CheckBox) Then
            Return
        End If
        e.Handled = True
        PerformVerticalOffsetDelta(-e.Delta)
    End Sub
    Public Sub PerformVerticalOffsetDelta(Delta As Double)
        AniStart(
            AaDouble(
            Sub(AnimDelta As Double)
                RealOffset = (RealOffset + AnimDelta).Clamp(0, ExtentHeight - ActualHeight)
                ScrollToVerticalOffset(RealOffset)
            End Sub, Delta * DeltaMult, 300,, New AniEaseOutFluent(6)))
    End Sub
    Private Sub MyScrollViewer_ScrollChanged(sender As Object, e As ScrollChangedEventArgs) Handles Me.ScrollChanged
        RealOffset = VerticalOffset
    End Sub

    Public ScrollBar As MyScrollBar
    Private Sub Load() Handles Me.Loaded
        ScrollBar = GetTemplateChild("PART_VerticalScrollBar")
    End Sub
End Class
