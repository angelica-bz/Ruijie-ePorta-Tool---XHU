Imports System.Security.Cryptography
Imports System.Text
Imports Microsoft.VisualBasic

''' <summary>
''' 凭据保护模块：把明文密码变成可安全写入 config.yml 的字符串。
'''
''' 存储链路：
'''     明文密码
'''        ↓ Encoding.UTF8.GetBytes
'''     DPAPI Protect（DataProtectionScope.CurrentUser）
'''        ↓ Convert.ToBase64String
'''     password_protected（写进 config.yml）
'''
''' 行为特性：
'''   - 密文绑定 Windows 当前用户。其它 Windows 用户即使拿到同一份 config.yml 也解不开，
'''     这是预期行为，不是缺陷。
'''   - 使用固定的应用级 entropy，把密文绑定到本程序，避免与其它程序互相误用。
'''     entropy 只存在于程序内部，不进入配置文件。
'''   - 密码明文只作为运行期数据存在，绝不进入日志。
''' </summary>
Public Module ModCredential

    ''' <summary>
    ''' 应用级固定 entropy。修改它会导致既有配置里的密码全部无法解密，
    ''' 因此除非确有必要（例如更换存储方案），不要改动。
    ''' </summary>
    Private ReadOnly Entropy As Byte() = Encoding.UTF8.GetBytes("RuijieEPortaTool.Config.v1")

    ''' <summary>
    ''' 保护明文密码。空密码返回空字符串（表示“没有保存密码”），
    ''' 这样配置文件里不会出现无意义的 DPAPI 块。
    ''' </summary>
    Public Function ProtectPassword(PlainText As String) As String
        If String.IsNullOrEmpty(PlainText) Then Return ""
        Dim PlainBytes As Byte() = Encoding.UTF8.GetBytes(PlainText)
        Dim ProtectedBytes As Byte() = ProtectedData.Protect(PlainBytes, Entropy, DataProtectionScope.CurrentUser)
        Return Convert.ToBase64String(ProtectedBytes)
    End Function

    ''' <summary>
    ''' 解出明文密码。
    ''' 返回 False 表示保护数据不可用（Base64 非法、被其它用户加密、entropy 不匹配、
    ''' 或数据被损坏），此时应要求用户重新输入密码。
    ''' 保护串为空时返回 True 且明文为空——这是“未保存密码”的正常状态。
    ''' </summary>
    Public Function TryUnprotectPassword(ProtectedText As String, ByRef PlainText As String) As Boolean
        PlainText = ""
        If String.IsNullOrEmpty(ProtectedText) Then Return True

        Try
            Dim ProtectedBytes As Byte() = Convert.FromBase64String(ProtectedText)
            Dim PlainBytes As Byte() = ProtectedData.Unprotect(ProtectedBytes, Entropy, DataProtectionScope.CurrentUser)
            PlainText = Encoding.UTF8.GetString(PlainBytes)
            Return True
        Catch
            ' 不区分具体原因：对外都是“解不开，请重新输入密码”。
            ' 这里刻意不记录任何异常细节到日志，避免把密文片段带出去。
            PlainText = ""
            Return False
        End Try
    End Function

    ''' <summary>
    ''' DPAPI blob 的固定文件头：版本 01 00 00 00 + provider GUID
    ''' {df9d8cd0-1501-11d1-8c7a-00c04fc297eb}。
    ''' </summary>
    Private ReadOnly DpapiBlobHeader As Byte() = {
        &H1, &H0, &H0, &H0,
        &HD0, &H8C, &H9D, &HDF, &H1, &H15, &HD1, &H11, &H8C, &H7A, &H0, &HC0, &H4F, &HC2, &H97, &HEB
    }

    ''' <summary>
    ''' 判断一个字符串是否是本模块产出的保护数据。
    ''' 只检查“是不是合法 Base64”是不够的——很多普通文本（例如 "test1234"）本身就是合法 Base64，
    ''' 因此这里进一步校验 DPAPI blob 文件头。
    ''' </summary>
    Public Function LooksProtected(Text As String) As Boolean
        If String.IsNullOrEmpty(Text) Then Return False
        Try
            Dim Raw As Byte() = Convert.FromBase64String(Text)
            If Raw.Length < DpapiBlobHeader.Length Then Return False
            For i As Integer = 0 To DpapiBlobHeader.Length - 1
                If Raw(i) <> DpapiBlobHeader(i) Then Return False
            Next
            Return True
        Catch
            Return False
        End Try
    End Function

End Module
