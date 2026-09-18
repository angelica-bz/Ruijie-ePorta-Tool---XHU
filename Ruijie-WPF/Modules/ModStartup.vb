Imports System.IO
Imports System.Diagnostics
Imports Microsoft.Win32

Public Module ModStartup

    Private Const RunKeyName As String = "Ruijie ePorta Tool"
    Private Const RunKeyPath As String = "Software\Microsoft\Windows\CurrentVersion\Run"

    Public Function IsAutoStartEnabled() As Boolean
        Try
            Using Key = Registry.CurrentUser.OpenSubKey(RunKeyPath, False)
                If Key IsNot Nothing Then Return Key.GetValue(RunKeyName) IsNot Nothing
            End Using
        Catch ex As Exception
            Log(ex, "查询开机启动失败")
        End Try
        Return False
    End Function

    Public Function SetAutoStart(Enable As Boolean) As Boolean
        CleanupLegacy()
        Try
            Using Key = Registry.CurrentUser.OpenSubKey(RunKeyPath, True)
                If Key Is Nothing Then Return False
                If Enable Then
                    Key.SetValue(RunKeyName, """" & PathExe & """ --background")
                Else
                    Key.DeleteValue(RunKeyName, False)
                End If
                Return True
            End Using
        Catch ex As Exception
            Log(ex, "设置开机启动失败")
            Return False
        End Try
    End Function

    Private Sub CleanupLegacy()
        Try
            Using Proc As New Process()
                Proc.StartInfo = New ProcessStartInfo With {
                    .FileName = "schtasks",
                    .Arguments = "/delete /tn """ & RunKeyName & """ /f",
                    .UseShellExecute = False,
                    .CreateNoWindow = True
                }
                Proc.Start()
                Proc.WaitForExit(3000)
            End Using
        Catch
        End Try

        Try
            Dim ShortcutPath As String = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft\Windows\Start Menu\Programs\Startup\Ruijie_ePorta_Tool.lnk")
            If File.Exists(ShortcutPath) Then File.Delete(ShortcutPath)
        Catch
        End Try
    End Sub

End Module
