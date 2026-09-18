Imports System.Windows.Threading

Public Class CustomEventService
    Public Enum EventType
        None = 0
        Click = 1
    End Enum
    Public Shared Function GetEventType(sender As Object) As EventType
        Return EventType.None
    End Function
End Class

Public Class CustomEvent
    Public Enum EventType
        None = 0
        Click = 1
    End Enum
End Class
