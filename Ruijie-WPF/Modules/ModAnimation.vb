Imports System.Windows
Imports System.Windows.Media
Imports System.Threading
Imports System.Runtime.CompilerServices
Imports System.Collections.Concurrent
Imports System.Linq

Public Module ModAnimation

#Region "声明"

    Public AniGroups As New ConcurrentDictionary(Of String, AniGroupEntry)
    Public AniSpeed As Double = 1
    Public Class AniGroupEntry
        Public Data As List(Of AniData)
        Public StartTick As Long
        Public Uuid As Integer = GetUuid()
    End Class

    Private AniLastTick As Long
    Public AniRunning As Boolean = False
    Private _AniControlEnabled As Integer = 0
    Private ReadOnly AniControlEnabledLock As New Object
    Public Property AniControlEnabled() As Integer
        Get
            Return _AniControlEnabled
        End Get
        Set(value As Integer)
            SyncLock AniControlEnabledLock
                _AniControlEnabled = value
            End SyncLock
        End Set
    End Property

#End Region

#Region "类与枚举"

    Public Structure AniData
        Public TypeMain As AniType
        Public TypeSub As AniTypeSub
        Public TimeTotal As Integer
        Public TimeFinished As Integer
        Public TimePercent As Double
        Public IsAfter As Boolean
        Public Ease As AniEase
        Public Obj As Object
        Public Value As Object
        Public ValueLast As Object

        Public Overrides Function ToString() As String
            Return GetStringFromEnum(TypeMain) & " | " & TimeFinished & "/" & TimeTotal & "(" & Math.Round(TimePercent * 100) & "%)" & If(Obj Is Nothing, "", " | " & Obj.ToString)
        End Function
    End Structure

    Public Enum AniType
        Number
        Color
        Scale
        Code
        ScaleTransform
        RotateTransform
    End Enum

    Public Enum AniTypeSub
        X
        Y
        Width
        Height
        Opacity
        Value
        TranslateX
        TranslateY
        [Double]
    End Enum

#End Region

#Region "动画创建方法"

    Public Function AaOpacity(Obj As Object, Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Number, .TypeSub = AniTypeSub.Opacity,
                                   .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Obj, .Value = Value, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaHeight(Obj As Object, Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Number, .TypeSub = AniTypeSub.Height,
                                   .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Obj, .Value = Value, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaWidth(Obj As Object, Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Number, .TypeSub = AniTypeSub.Width,
                                   .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Obj, .Value = Value, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaX(Obj As Object, Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Number, .TypeSub = AniTypeSub.X,
                                   .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Obj, .Value = Value, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaY(Obj As Object, Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Number, .TypeSub = AniTypeSub.Y,
                                   .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Obj, .Value = Value, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaTranslateX(Obj As Object, Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Number, .TypeSub = AniTypeSub.TranslateX,
                                   .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Obj, .Value = Value, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaTranslateY(Obj As Object, Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Number, .TypeSub = AniTypeSub.TranslateY,
                                   .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Obj, .Value = Value, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaColor(Obj As FrameworkElement, Prop As DependencyProperty, Value As MyColor, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Color, .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = {Obj, Prop, ""}, .Value = Value, .IsAfter = After, .TimeFinished = -Delay, .ValueLast = New MyColor(0, 0, 0, 0)}
    End Function

    Public Function AaColor(Obj As FrameworkElement, Prop As DependencyProperty, Res As String, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Color, .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = {Obj, Prop, Res}, .Value = New MyColor(Application.Current.FindResource(Res)) - New MyColor(Obj.GetValue(Prop)), .IsAfter = After, .TimeFinished = -Delay, .ValueLast = New MyColor(0, 0, 0, 0)}
    End Function

    Public Function AaScaleTransform(Obj As Object, Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.ScaleTransform, .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Obj, .Value = Value, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaRotateTransform(Obj As Object, Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.RotateTransform, .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Obj, .Value = Value, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaScale(Obj As FrameworkElement, Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional Unused As Object = Nothing, Optional Absolute As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Number, .TypeSub = AniTypeSub.Width,
                                   .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Obj, .Value = Value, .IsAfter = False, .TimeFinished = -Delay}
    End Function

    Public Function AaDouble(Code As Action(Of Double), Value As Double, Optional Time As Integer = 400, Optional Delay As Integer = 0, Optional Ease As AniEase = Nothing, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Number, .TypeSub = AniTypeSub.Double,
                                   .TimeTotal = Time, .Ease = If(Ease, New AniEaseLinear), .Obj = Code, .Value = Value, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaCode(Code As Action, Optional Delay As Integer = 0, Optional After As Boolean = False) As AniData
        Return New AniData With {.TypeMain = AniType.Code,
                                   .TimeTotal = 1, .Value = Code, .IsAfter = After, .TimeFinished = -Delay}
    End Function

    Public Function AaStack(Stack As StackPanel, Optional Time As Integer = 100, Optional Delay As Integer = 25) As List(Of AniData)
        AaStack = New List(Of AniData)
        Dim AniDelay As Integer = 0
        For Each Item In Stack.Children
            If TypeOf Item Is UIElement Then
                CType(Item, UIElement).Opacity = 0
                AaStack.Add(AaOpacity(Item, 1, Time, AniDelay))
                AniDelay += Delay
            End If
        Next
    End Function

#End Region

#Region "缓动函数"

    Public MustInherit Class AniEase
        Public MustOverride Function GetValue(t As Double) As Double
        Public Overridable Function GetDelta(t1 As Double, t0 As Double) As Double
            Return GetValue(t1) - GetValue(t0)
        End Function
    End Class

    Public Class AniEaseLinear
        Inherits AniEase
        Public Overrides Function GetValue(t As Double) As Double
            Return t.Clamp(0, 1)
        End Function
        Public Overrides Function GetDelta(t1 As Double, t0 As Double) As Double
            Return t1.Clamp(0, 1) - t0.Clamp(0, 1)
        End Function
    End Class

    Public Enum AniEasePower As Integer
        Weak = 2
        Middle = 3
        Strong = 4
        ExtraStrong = 5
    End Enum

    Public Class AniEaseInFluent
        Inherits AniEase
        Private ReadOnly p As AniEasePower
        Public Sub New(Optional Power As AniEasePower = AniEasePower.Middle)
            p = Power
        End Sub
        Public Overrides Function GetValue(t As Double) As Double
            Return t.Clamp(0, 1) ^ p
        End Function
    End Class

    Public Class AniEaseOutFluent
        Inherits AniEase
        Private ReadOnly p As AniEasePower
        Public Sub New(Optional Power As AniEasePower = AniEasePower.Middle)
            p = Power
        End Sub
        Public Overrides Function GetValue(t As Double) As Double
            Return 1 - (1 - t).Clamp(0, 1) ^ p
        End Function
    End Class

    Public Class AniEaseInout
        Inherits AniEase
        Private ReadOnly EaseIn As AniEase, EaseOut As AniEase, EaseInPercent As Double
        Public Sub New(EaseIn As AniEase, EaseOut As AniEase, Optional EaseInPercent As Double = 0.5)
            Me.EaseIn = EaseIn : Me.EaseOut = EaseOut : Me.EaseInPercent = EaseInPercent
        End Sub
        Public Overrides Function GetValue(t As Double) As Double
            If t < EaseInPercent Then
                Return EaseInPercent * EaseIn.GetValue(t / EaseInPercent)
            Else
                Return (1 - EaseInPercent) * EaseOut.GetValue((t - EaseInPercent) / (1 - EaseInPercent)) + EaseInPercent
            End If
        End Function
    End Class

    Public Class AniEaseInoutFluent
        Inherits AniEase
        Private Ease As AniEaseInout
        Public Sub New(Optional Power As AniEasePower = AniEasePower.Middle, Optional Middle As Double = 0.5)
            Ease = New AniEaseInout(New AniEaseInFluent(Power), New AniEaseOutFluent(Power), Middle)
        End Sub
        Public Overrides Function GetValue(t As Double) As Double
            Return Ease.GetValue(t)
        End Function
    End Class

    Public Class AniEaseOutBack
        Inherits AniEase
        Private ReadOnly p As Double
        Public Sub New(Optional Power As AniEasePower = AniEasePower.Middle)
            p = 3 - Power * 0.5
        End Sub
        Public Overrides Function GetValue(t As Double) As Double
            t = t.Clamp(0, 1)
            Return 1 - (1 - t) ^ p * Math.Cos(1.5 * Math.PI * t)
        End Function
    End Class

    Public Class AniEaseOutElastic
        Inherits AniEase
        Private ReadOnly p As Integer
        Public Sub New(Optional Power As AniEasePower = AniEasePower.Middle)
            p = Power + 4
        End Sub
        Public Overrides Function GetValue(t As Double) As Double
            t = 1 - t.Clamp(0, 1)
            Return 1 - t ^ ((p - 1) * 0.25) * Math.Cos((p - 3.5) * Math.PI * (1 - t) ^ 1.5)
        End Function
    End Class

    Public Class AniEaseOutFluentWithInitial
        Inherits AniEase
        Private ReadOnly initialSpeed As Double
        Private ReadOnly totalTime As Double
        Private ReadOnly totalDistance As Double
        Public Sub New(initialSpeed As Double, totalTime As Double, totalDistance As Double)
            Me.initialSpeed = initialSpeed
            Me.totalTime = totalTime
            Me.totalDistance = totalDistance
        End Sub
        Public Overrides Function GetValue(t As Double) As Double
            t = t.Clamp(0, 1)
            Dim deceleration As Double = (initialSpeed * totalTime - 2 * totalDistance) / (totalTime * totalTime)
            Return initialSpeed * t + 0.5 * deceleration * t * t * totalTime * totalTime
        End Function
    End Class

    Public Class AniEaseInBack
        Inherits AniEase
        Public Sub New(Optional Power As AniEasePower = AniEasePower.Middle)
        End Sub
        Public Overrides Function GetValue(t As Double) As Double
            t = t.Clamp(0, 1)
            Return t * t * (2.70158 * t - 1.70158)
        End Function
    End Class

#End Region

#Region "接口（开始、中断、检测）"

    ''' <summary>
    ''' 添加一组动画并按 Name 注册。Name 相同时替换旧组。
    ''' 可从任意线程调用（ConcurrentDictionary 保证容器安全）。
    ''' </summary>
    Public Sub AniStart(AniGroup As IList, Optional Name As String = "", Optional RefreshTime As Boolean = False)
        If RefreshTime Then AniLastTick = GetTimeMs()
        Dim AllAnis = New List(Of AniData)
        For Each Element In AniGroup
            If TypeOf Element Is ICollection Then
                AllAnis.AddRange(Element)
            Else
                AllAnis.Add(Element)
            End If
        Next
        Dim NewEntry As New AniGroupEntry With {.Data = AllAnis, .StartTick = GetTimeMs()}
        If Name = "" Then
            Name = NewEntry.Uuid.ToString()
        Else
            Dim Dummy As AniGroupEntry = Nothing
            AniGroups.TryRemove(Name, Dummy)
        End If
        AniGroups(Name) = NewEntry
    End Sub

    Public Sub AniStart(AniGroup As AniData, Optional Name As String = "", Optional RefreshTime As Boolean = False)
        AniStart(New List(Of AniData) From {AniGroup}, Name, RefreshTime)
    End Sub

    ''' <summary>
    ''' 移除指定动画组。可从任意线程调用。
    ''' </summary>
    Public Sub AniStop(Name As String)
        Dim Dummy As AniGroupEntry = Nothing
        AniGroups.TryRemove(Name, Dummy)
    End Sub

    Public Function AniIsRun(Name As String) As Boolean
        Return AniGroups.ContainsKey(Name)
    End Function

#End Region

#Region "动画执行"

    Private AniCount As Integer = 0

    ''' <summary>
    ''' 启动动画计时器线程。
    ''' 该线程通过 RunInUiWait 将每帧回调 marshalling 到 UI 线程，
    ''' 因此 AniTimer 内部对 Entry.Data 的修改始终在 UI 线程上执行。
    ''' AniStart/AniStop 可从任意线程调用，仅操作 ConcurrentDictionary 层面。
    ''' </summary>
    Public Sub AniStart()
        AniLastTick = GetTimeMs()
        AniRunning = True

        RunInNewThread(
        Sub()
            Try
                Log("[Animation] Animation thread started")
                Do While True
                    Dim DeltaTime As Long = (GetTimeMs() - AniLastTick).Clamp(0, 100000)
                    If DeltaTime < 3 Then
                        Thread.Sleep(If(AniGroups.Count = 0, 16, 1))
                        Continue Do
                    End If
                    AniLastTick = GetTimeMs()
                    If AniGroups.Count > 0 Then
                        RunInUiWait(
                        Sub()
                            AniCount = 0
                            AniTimer(DeltaTime)
                        End Sub)
                    End If
                Loop
            Catch ex As Exception
                Log(ex, "Animation frame failed")
            End Try
        End Sub, "Animation", ThreadPriority.AboveNormal)
    End Sub

    ''' <summary>
    ''' 处理一帧动画。
    ''' 【线程约束】此方法必须且只能在 UI 线程上调用。
    ''' Entry.Data（List(Of AniData)）的修改只允许在此方法内发生，
    ''' 不得在其他线程上直接操作 Entry.Data，否则会导致数据竞争。
    ''' </summary>
    Public Sub AniTimer(DeltaTick As Integer)
        Try
            ' 快照字典 key 列表，避免遍历中字典结构变化
            Dim Snapshot = AniGroups.ToList()
            Dim KeysToRemove As New List(Of String)

            For Each Kvp In Snapshot
                Dim Name As String = Kvp.Key
                Dim Entry As AniGroupEntry = Kvp.Value

                ' Entry.Data 的所有修改（RemoveAt、索引赋值）仅在 UI 线程执行，
                ' ConcurrentDictionary 保证我们拿到的 Entry 引用是完整的，
                ' 后续对 Entry.Data 的操作不需要额外同步。
                If Entry.StartTick > AniLastTick Then Continue For
                Dim CanRemoveAfter = True
                Dim ii = 0

                Do While ii < Entry.Data.Count
                    Dim Anim As AniData = Entry.Data(ii)
                    If Anim.IsAfter = False Then
                        CanRemoveAfter = False
                        Anim.TimeFinished += DeltaTick
                        If Anim.TimeFinished > 0 Then
                            Anim = AniRun(Anim)
                            AniCount += 1
                        End If
                        If Anim.TimeFinished >= Anim.TimeTotal Then
                            If Anim.TypeMain = AniType.Color AndAlso Not Anim.Obj(2) = "" Then
                                Anim.Obj(0).SetResourceReference(Anim.Obj(1), Anim.Obj(2))
                            End If
                            Entry.Data.RemoveAt(ii)
                            GoTo NextAni
                        End If
                        Entry.Data(ii) = Anim
                    Else
                        If CanRemoveAfter Then
                            CanRemoveAfter = False
                            Anim.IsAfter = False
                            Entry.Data(ii) = Anim
                            GoTo NextAni
                        Else
                            Exit Do
                        End If
                    End If
                    ii += 1
NextAni:
                Loop

                If Not Entry.Data.Any() Then
                    KeysToRemove.Add(Name)
                End If
            Next

            ' 统一删除已完成的动画组（在快照遍历结束后）
            For Each Key In KeysToRemove
                Dim Dummy As AniGroupEntry = Nothing
                AniGroups.TryRemove(Key, Dummy)
            Next

        Catch ex As Exception
            Log(ex, "Animation tick failed")
        End Try
    End Sub

    Private Function AniRun(Ani As AniData) As AniData
        Try
            Select Case Ani.TypeMain

                Case AniType.Number
                    Dim Delta As Double = Lerp(0, Ani.Value, Ani.Ease.GetDelta(Ani.TimeFinished / Ani.TimeTotal, Ani.TimePercent))
                    If Delta <> 0 Then
                        Select Case Ani.TypeSub
                            Case AniTypeSub.Opacity
                                If TypeOf Ani.Obj Is UIElement Then
                                    Dim Obj As UIElement = Ani.Obj
                                    Obj.Opacity = (Obj.Opacity + Delta).Clamp(0, 1)
                                End If
                            Case AniTypeSub.Width
                                Dim Obj As FrameworkElement = Ani.Obj
                                Obj.Width = Math.Max(If(Double.IsNaN(Obj.Width), Obj.ActualWidth, Obj.Width) + Delta, 0)
                            Case AniTypeSub.Height
                                Dim Obj As FrameworkElement = Ani.Obj
                                Obj.Height = Math.Max(If(Double.IsNaN(Obj.Height), Obj.ActualHeight, Obj.Height) + Delta, 0)
                            Case AniTypeSub.X
                                If TypeOf Ani.Obj Is Window Then
                                    CType(Ani.Obj, Window).Left += Delta
                                Else
                                    Select Case Ani.Obj.HorizontalAlignment
                                        Case HorizontalAlignment.Left, HorizontalAlignment.Stretch
                                            Ani.Obj.Margin = New Thickness(Ani.Obj.Margin.Left + Delta, Ani.Obj.Margin.Top, Ani.Obj.Margin.Right, Ani.Obj.Margin.Bottom)
                                        Case HorizontalAlignment.Right
                                            Ani.Obj.Margin = New Thickness(Ani.Obj.Margin.Left, Ani.Obj.Margin.Top, Ani.Obj.Margin.Right - Delta, Ani.Obj.Margin.Bottom)
                                    End Select
                                End If
                            Case AniTypeSub.Y
                                If TypeOf Ani.Obj Is Window Then
                                    CType(Ani.Obj, Window).Top += Delta
                                Else
                                    Select Case Ani.Obj.VerticalAlignment
                                        Case VerticalAlignment.Top
                                            Ani.Obj.Margin = New Thickness(Ani.Obj.Margin.Left, Ani.Obj.Margin.Top + Delta, Ani.Obj.Margin.Right, Ani.Obj.Margin.Bottom)
                                        Case VerticalAlignment.Bottom
                                            Ani.Obj.Margin = New Thickness(Ani.Obj.Margin.Left, Ani.Obj.Margin.Top, Ani.Obj.Margin.Right, Ani.Obj.Margin.Bottom - Delta)
                                    End Select
                                End If
                            Case AniTypeSub.Value
                                Ani.Obj.Value += Delta
                            Case AniTypeSub.TranslateX
                                If Ani.Obj.RenderTransform Is Nothing OrElse TypeOf Ani.Obj.RenderTransform IsNot TranslateTransform Then Ani.Obj.RenderTransform = New TranslateTransform(0, 0)
                                CType(Ani.Obj.RenderTransform, TranslateTransform).X += Delta
                            Case AniTypeSub.TranslateY
                                If Ani.Obj.RenderTransform Is Nothing OrElse TypeOf Ani.Obj.RenderTransform IsNot TranslateTransform Then Ani.Obj.RenderTransform = New TranslateTransform(0, 0)
                                CType(Ani.Obj.RenderTransform, TranslateTransform).Y += Delta
                            Case AniTypeSub.Double
                                CType(Ani.Obj, Action(Of Double))(Delta)
                        End Select
                    End If

                Case AniType.Color
                    Dim Delta As MyColor = MyColor.Lerp(New MyColor(0, 0, 0, 0), Ani.Value, Ani.Ease.GetDelta(Ani.TimeFinished / Ani.TimeTotal, Ani.TimePercent)) + Ani.ValueLast
                    Dim Obj As FrameworkElement = Ani.Obj(0)
                    Dim Prop As DependencyProperty = Ani.Obj(1)
                    Dim NewColor As MyColor = New MyColor(Obj.GetValue(Prop)) + Delta
                    Obj.SetValue(Prop, If(Prop.PropertyType.Name = "Color", CType(NewColor, Color), CType(NewColor, SolidColorBrush)))
                    Ani.ValueLast = NewColor - New MyColor(Obj.GetValue(Prop))

                Case AniType.Code
                    CType(Ani.Value, Action)()

                Case AniType.ScaleTransform
                    Dim Obj As FrameworkElement = Ani.Obj
                    If TypeOf Obj.RenderTransform IsNot ScaleTransform Then
                        If Obj.RenderTransformOrigin = New Point(0, 0) Then Obj.RenderTransformOrigin = New Point(0.5, 0.5)
                        Obj.RenderTransform = New ScaleTransform(1, 1)
                    End If
                    Dim Delta As Double = Lerp(0, Ani.Value, Ani.Ease.GetDelta(Ani.TimeFinished / Ani.TimeTotal, Ani.TimePercent))
                    CType(Obj.RenderTransform, ScaleTransform).ScaleX = Math.Max(CType(Obj.RenderTransform, ScaleTransform).ScaleX + Delta, 0)
                    CType(Obj.RenderTransform, ScaleTransform).ScaleY = Math.Max(CType(Obj.RenderTransform, ScaleTransform).ScaleY + Delta, 0)

                Case AniType.RotateTransform
                    Dim Obj As FrameworkElement = Ani.Obj
                    If TypeOf Obj.RenderTransform IsNot RotateTransform Then
                        If Obj.RenderTransformOrigin = New Point(0, 0) Then Obj.RenderTransformOrigin = New Point(0.5, 0.5)
                        Obj.RenderTransform = New RotateTransform(0)
                    End If
                    Dim Delta As Double = Lerp(0, Ani.Value, Ani.Ease.GetDelta(Ani.TimeFinished / Ani.TimeTotal, Ani.TimePercent))
                    CType(Obj.RenderTransform, RotateTransform).Angle = CType(Obj.RenderTransform, RotateTransform).Angle + Delta

            End Select
            Ani.TimePercent = Ani.TimeFinished / Ani.TimeTotal
        Catch ex As Exception
            Log(ex, "Animation execution failed: " & Ani.ToString)
        End Try
        Return Ani
    End Function

#End Region

#Region "通用销毁动画"

    ''' <summary>
    ''' 安全地从父容器中移除指定元素。
    ''' 兼容 Panel（Children）、ContentControl（Content）、Decorator（Child）等父容器类型。
    ''' </summary>
    Public Sub RemoveFromParent(Element As FrameworkElement)
        If Element.Parent Is Nothing Then Return

        Dim Parent = Element.Parent

        If TypeOf Parent Is Panel Then
            CType(Parent, Panel).Children.Remove(Element)
        ElseIf TypeOf Parent Is ContentControl Then
            CType(Parent, ContentControl).Content = Nothing
        ElseIf TypeOf Parent Is Decorator Then
            CType(Parent, Decorator).Child = Nothing
        Else
            Try
                CType(Parent, Object).Children.Remove(Element)
            Catch
                Log("RemoveFromParent: 不支持的父容器类型 " & Parent.GetType().Name)
            End Try
        End If
    End Sub

    ''' <summary>
    ''' 执行"缩放+淡出+缩小高度+回调"的标准销毁动画序列。
    ''' 收尾逻辑由 CallBack 参数决定，不在此方法内处理父容器移除。
    ''' </summary>
    Private Sub AniDisposeCore(Control As FrameworkElement,
                               Optional CallBack As Action = Nothing,
                               Optional NamePrefix As String = "Dispose")
        If Control.IsHitTestVisible Then
            Control.IsHitTestVisible = False
            AniStart({
                AaScaleTransform(Control, -0.08, 200,, New AniEaseInFluent),
                AaOpacity(Control, -1, 200,, New AniEaseOutFluent),
                AaHeight(Control, -Control.ActualHeight, 150, 100, New AniEaseOutFluent),
                AaCode(Sub() If CallBack IsNot Nothing Then CallBack(),, True)
            }, NamePrefix & " " & Control.GetHashCode())
        Else
            If CallBack IsNot Nothing Then CallBack()
        End If
    End Sub

#End Region

End Module
