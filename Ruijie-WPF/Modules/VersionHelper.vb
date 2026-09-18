Imports System.Reflection

Public Module VersionHelper
    Public Function GetAppVersion() As String
        Dim attr = Assembly.GetExecutingAssembly() _
            .GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()

        If attr IsNot Nothing AndAlso Not String.IsNullOrEmpty(attr.InformationalVersion) Then
            Return attr.InformationalVersion
        End If

        Dim ver = Assembly.GetExecutingAssembly().GetName().Version
        Return $"{ver.Major}.{ver.Minor}.{ver.Build}"
    End Function
End Module
