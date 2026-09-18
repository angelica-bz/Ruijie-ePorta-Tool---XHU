Imports System.Web.Script.Serialization

Public Module ModJson

    Private ReadOnly Serializer As New JavaScriptSerializer()

    Public Function ParseJsonResponse(Body As String) As Dictionary(Of String, Object)
        If Body Is Nothing OrElse Body.Trim() = "" Then
            Return New Dictionary(Of String, Object) From {
                {"result", "error"},
                {"message", "服务器返回了空响应"}
            }
        End If

        Try
            Dim Result = Serializer.Deserialize(Of Dictionary(Of String, Object))(Body.Trim())
            If Result IsNot Nothing Then Return Result
        Catch
        End Try

        Return New Dictionary(Of String, Object) From {
            {"result", "error"},
            {"message", "服务器返回了非 JSON 响应（可能服务异常或需重新认证）"}
        }
    End Function

    ''' <summary>
    ''' 宽松解析任意 JSON（对象 / 数组 / 标量），失败时给出原因而不是抛异常。
    ''' 供门户发现解析嵌套 JSON 字符串使用（例如 getServices 的 services 字段本身
    ''' 是一个 JSON 数组字符串，无法用 ParseJsonResponse 处理）。
    ''' 不改变 ParseJsonResponse 的既有行为。
    ''' </summary>
    Public Function TryParseJsonValue(Body As String, ByRef Value As Object, ByRef ErrorMessage As String) As Boolean
        Value = Nothing
        ErrorMessage = ""

        If Body Is Nothing OrElse Body.Trim() = "" Then
            ErrorMessage = "内容为空"
            Return False
        End If

        Try
            Value = Serializer.DeserializeObject(Body.Trim())
            If Value Is Nothing Then
                ErrorMessage = "内容是 JSON null"
                Return False
            End If
            Return True
        Catch ex As Exception
            ErrorMessage = ex.Message
            Return False
        End Try
    End Function

End Module
