Imports System.Numerics
Imports System.Text
Imports Microsoft.VisualBasic

''' <summary>
''' 西华大学 ePortal 密码加密模块。
'''
''' 行为参考（唯一准则，均为门户实际下发的 JavaScript）：
'''   security.js     —— RSAUtils.encryptedString() / biFromHex / biHighIndex / biToHex / digitToHex
'''   AuthInterFace.js —— encryptedPassword()：明文反转后调用 RSAUtils.encryptedString()
'''   login_bch.js     —— doauthen()：明文 = password + ">" + mac（mac 缺失时为 "111111111"）
'''
''' 算法概要：
'''   password + ">" + mac
'''     → 按 UTF-16 code unit 整体反转
'''     → 每 2 个 code unit 组成一个 16-bit digit（低位在前）
'''     → chunkSize = 2 * biHighIndex(modulus)
'''     → Raw RSA（NoPadding） c = digit^e mod n
'''     → 小写十六进制，每个 16-bit digit 固定 4 位，多块以单个空格连接
'''
''' 本模块为纯函数模块：不访问 UI、配置文件、网络或 SharedCfg，无副作用，
''' 相同输入恒产生相同输出。modulus / exponent 一律由参数传入，不在此硬编码。
''' </summary>
Public Module ModCrypto

    ''' <summary>
    ''' 门户在 queryString 缺少 mac 参数时使用的固定占位值。
    ''' 对应 login_bch.js：
    '''     var macString = getQueryStringByName("mac");
    '''     if (isNull(macString)) { macString = "111111111"; }
    ''' getQueryStringByName 在参数不存在时返回 ""，而 isNull("") 为 True，
    ''' 因此 mac 为 Nothing 或 "" 时都取该默认值。该默认值只在本模块定义。
    ''' </summary>
    Public Const DefaultMac As String = "111111111"

    ''' <summary>security.js 中 maxDigitVal = biRadix - 1 = 65535，即单个 16-bit digit 的上限。</summary>
    Private ReadOnly MaxDigitVal As BigInteger = New BigInteger(65535)

    ''' <summary>security.js 的 digit 基数：biRadix = 1 &lt;&lt; 16 = 65536。</summary>
    Private Const DigitRadix As Integer = 65536

    ''' <summary>
    ''' 复刻门户的密码加密：明文密码 → 请求体中的 password 字段密文。
    ''' </summary>
    ''' <param name="Password">
    ''' 明文密码。Nothing 按空字符串处理（JavaScript 中密码框的 value 永远是字符串，不会是 null）。
    ''' </param>
    ''' <param name="Mac">
    ''' queryString 中的 mac 参数值。Nothing 或 "" 时使用 <see cref="DefaultMac"/>。
    ''' </param>
    ''' <param name="PublicKeyModulusHex">门户 pageInfo 返回的 publicKeyModulus（十六进制，大小写均可）。</param>
    ''' <param name="PublicKeyExponentHex">门户 pageInfo 返回的 publicKeyExponent（如 "10001"）。</param>
    ''' <returns>小写十六进制密文；多块之间以单个空格分隔。</returns>
    Public Function EncryptPassword(Password As String,
                                    Mac As String,
                                    PublicKeyModulusHex As String,
                                    PublicKeyExponentHex As String) As String

        ' ---- 参数校验：公钥必须可用 ----
        Dim ModulusHex As String = NormalizeHex(PublicKeyModulusHex, "PublicKeyModulusHex")
        Dim ExponentHex As String = NormalizeHex(PublicKeyExponentHex, "PublicKeyExponentHex")

        Dim Modulus As BigInteger = ParseHex(ModulusHex)
        If Modulus < New BigInteger(2) Then
            Throw New ArgumentException("公钥 modulus 必须是不小于 2 的整数。", "PublicKeyModulusHex")
        End If

        Dim Exponent As BigInteger = ParseHex(ExponentHex)
        If Exponent < BigInteger.One Then
            Throw New ArgumentException("公钥 exponent 必须是不小于 1 的整数。", "PublicKeyExponentHex")
        End If

        ' biHighIndex(m)：16-bit digit 的最高非零下标；chunkSize = 2 * biHighIndex(m)
        Dim ModulusDigitCount As Integer = DigitCountOf(ModulusHex)
        Dim ChunkSize As Integer = 2 * GetBiHighIndex(ModulusHex)
        If ChunkSize < 2 Then
            Throw New ArgumentException("公钥 modulus 过小，无法确定 chunkSize。", "PublicKeyModulusHex")
        End If

        ' ---- 1. 明文拼接：password + ">" + mac ----（login_bch.js: passwordMac = password+">"+macString）
        Dim MacValue As String = If(String.IsNullOrEmpty(Mac), DefaultMac, Mac)
        Dim PasswordValue As String = If(Password, String.Empty)
        Dim Raw As String = PasswordValue & ">" & MacValue

        ' ---- 2. 整体反转 ----（AuthInterFace.js: password.split("").reverse().join("")）
        ' .NET Char 即 UTF-16 code unit，与 JavaScript split("") 的切分单位一致；
        ' 这里绝不能改用 UTF-8 字节数组。
        Dim Chars As Char() = Raw.ToCharArray()
        Array.Reverse(Chars)

        ' ---- 3. 取 charCodeAt() 语义，并按 0 补齐到 chunkSize 的整数倍 ----
        Dim Codes As New List(Of Integer)(Chars.Length + ChunkSize)
        For Each Ch As Char In Chars
            ' 必须用 Convert.ToInt32：VB 的 AscW 对大于 &H7FFF 的字符会返回负数。
            Codes.Add(Convert.ToInt32(Ch))
        Next
        While Codes.Count Mod ChunkSize <> 0
            Codes.Add(0)
        End While

        ' ---- 4. 构造每个 block 的 16-bit digit（低位 code unit 在前） ----
        ' security.js: block.digits[j] = a[k++]; block.digits[j] += a[k++] << 8;
        Dim DigitsPerBlock As Integer = ChunkSize \ 2
        Dim BlockCount As Integer = Codes.Count \ ChunkSize
        Dim Blocks As New List(Of BigInteger)(BlockCount)

        For BlockIndex As Integer = 0 To BlockCount - 1
            Dim BlockBase As Integer = BlockIndex * ChunkSize
            Dim Block As BigInteger = BigInteger.Zero
            For J As Integer = DigitsPerBlock - 1 To 0 Step -1
                Dim Digit As Integer = Codes(BlockBase + 2 * J) + (Codes(BlockBase + 2 * J + 1) << 8)

                ' 门户 security.js 的 digit 必须落在 16 bit 内。一旦超出（例如密码含
                ' Latin-1 以外的字符且落在奇数下标），security.js 内部会因 JavaScript
                ' 的 32 位整数截断而算出错误密文，服务端无法解密。
                ' 与其复刻一个错误结果，不如在此明确拒绝。
                If Digit > MaxDigitVal Then
                    Throw New NotSupportedException(
                        "密码无法被门户加密：构造出的 16-bit digit = " & Digit & " 超出 65535。" &
                        "门户 security.js 在此时会产生服务端无法解密的错误密文。" &
                        "请改用仅含 Latin-1（U+0000–U+00FF）字符的密码。")
                End If

                Block = Block * DigitRadix + Digit
            Next
            Blocks.Add(Block)
        Next

        ' ---- 5. Raw RSA（NoPadding）：c = block^e mod n ----（security.js: key.barrett.powMod(block, key.e)）
        Dim Sb As New StringBuilder(BlockCount * ModulusDigitCount * 4 + BlockCount)
        For Each Block As BigInteger In Blocks
            If Sb.Length > 0 Then Sb.Append(" "c)
            Dim Crypt As BigInteger = BigInteger.ModPow(Block, Exponent, Modulus)
            Sb.Append(FormatBiToHex(Crypt, ModulusDigitCount))
        Next

        Return Sb.ToString()
    End Function

#Region "security.js 内部表示的低层复刻"

    ''' <summary>
    ''' 对应 security.js 的 biFromHex + biHighIndex。
    ''' biFromHex 从字符串末尾开始每 4 个十六进制字符组成一个 16-bit digit，
    ''' biHighIndex 返回其中最高的非零下标（全零时返回 0）。
    ''' </summary>
    Private Function GetBiHighIndex(HexStr As String) As Integer
        Dim TotalDigits As Integer = DigitCountOf(HexStr)
        For Index As Integer = TotalDigits - 1 To 0 Step -1
            Dim StartIdx As Integer = Math.Max(HexStr.Length - 4 * (Index + 1), 0)
            Dim Length As Integer = Math.Min(HexStr.Length - StartIdx, 4)
            If Length <= 0 Then Continue For
            If Convert.ToInt32(HexStr.Substring(StartIdx, Length), 16) <> 0 Then Return Index
        Next
        Return 0
    End Function

    ''' <summary>biFromHex 会为该长度的十六进制串建立多少个 16-bit digit。</summary>
    Private Function DigitCountOf(HexStr As String) As Integer
        Return CInt(Math.Ceiling(HexStr.Length / 4.0))
    End Function

    ''' <summary>
    ''' 对应 security.js 的 biToHex + digitToHex。
    ''' 从最高非零 digit 递减到 0，每个 digit 输出固定 4 位小写十六进制；
    ''' 因此不能直接用 BigInteger.ToString("x")（会丢高位零、也无法保证 4 位对齐）。
    ''' </summary>
    Private Function FormatBiToHex(Value As BigInteger, ModulusDigitCount As Integer) As String
        Dim HighIndex As Integer = 0
        For Index As Integer = ModulusDigitCount - 1 To 0 Step -1
            If ((Value >> (16 * Index)) And MaxDigitVal) <> BigInteger.Zero Then
                HighIndex = Index
                Exit For
            End If
        Next

        Dim Sb As New StringBuilder((HighIndex + 1) * 4)
        For Index As Integer = HighIndex To 0 Step -1
            Dim Digit As Integer = CInt((Value >> (16 * Index)) And MaxDigitVal)
            Sb.Append(Digit.ToString("x4", Globalization.CultureInfo.InvariantCulture))
        Next
        Return Sb.ToString()
    End Function

    ''' <summary>去掉首尾空白并校验为纯十六进制串。</summary>
    Private Function NormalizeHex(HexStr As String, ParamName As String) As String
        If HexStr Is Nothing Then
            Throw New ArgumentException(ParamName & " 不能为空。", ParamName)
        End If
        Dim Clean As String = HexStr.Trim()
        If Clean.Length = 0 Then
            Throw New ArgumentException(ParamName & " 不能为空。", ParamName)
        End If
        For Each Ch As Char In Clean
            If Not Uri.IsHexDigit(Ch) Then
                Throw New ArgumentException(ParamName & " 含非十六进制字符：'" & Ch & "'。", ParamName)
            End If
        Next
        Return Clean
    End Function

    ''' <summary>按无符号正数解析十六进制串（前置 "0" 以避免最高位被当作符号位）。</summary>
    Private Function ParseHex(HexStr As String) As BigInteger
        Return BigInteger.Parse("0" & HexStr,
                                Globalization.NumberStyles.AllowHexSpecifier,
                                Globalization.CultureInfo.InvariantCulture)
    End Function

#End Region

End Module
