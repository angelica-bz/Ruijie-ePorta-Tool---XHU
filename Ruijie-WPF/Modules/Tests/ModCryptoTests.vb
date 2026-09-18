Imports System.Reflection
Imports System.Threading
Imports Microsoft.VisualBasic

''' <summary>
''' ModCryptoTests：离线测试用例。
'''
''' 测试框架（RunTest / 断言 / 统计）在 ModTestFramework.vb，
''' 跨模块共用的夹具在 ModTestFixtures.vb。
''' </summary>
Public Module ModCryptoTests

#Region "ModCrypto 黄金向量"

    ' 以下期望值全部由门户的真实 security.js 在 Node.js 中运行生成，
    ' 生成后即写死为常量 —— 测试运行期间不会再次调用 JavaScript，
    ' 否则两套实现一起出错也会测试通过，失去交叉验证的意义。
    '
    ' 生成脚本（仅设计期使用，不属于仓库）：
    '   %TEMP%\ruijie-eportal-investigation\gen-golden.js
    ' 参考实现：
    '   %TEMP%\ruijie-eportal-investigation\security.decoded.js

    ' ===== 公钥：第二阶段由门户 pageInfo 实际取得（公开信息，可安全写入源码） =====
    Friend Const TestModulus As String =
        "94dd2a8675fb779e6b9f7103698634cd400f27a154afa67af6166a43fc264172" &
        "22a79506d34cacc7641946abda1785b7acf9910ad6a0978c91ec84d40b71d289" &
        "1379af19ffb333e7517e390bd26ac312fe940c340466b4a5d4af1d65c3b59440" &
        "78f96a1a51a5a53e4bc302818b7c9f63c4a1b07bd7d874cef1c3d4b2f5eb7871"

    Friend Const TestExponent As String = "10001"

    ' test1234 / 111111111  (1 块, 256 字符)
    Friend Const GoldenBasic As String =
        "25286dfdf2c55417a4f91d6c6d7e80aabd83833b66a189f39f4adaf16df92743" &
        "4505e0ac1dc3c7adbde4ba5be4fc65e43027df2cb4a42671bd2f0872f7c22103" &
        "c2aa891a2f791485b5b4de16e5f60a3d5cf37605fdc2e2f7e812dbb2fc325a67" &
        "72fcfe70668c853eb88a96b5116a6b7d38351dd1cb0434275a1fe0af1e6a532b"

    ' test1234 / abc123  (1 块, 256 字符)
    Private Const GoldenMacDifferent As String =
        "3b9cd9d8870a2391292ec22a641d6dcc6fbb543ba8b0ed6602b1c70cfb61e617" &
        "4ce240c6a8d29ee4998a2fefe66d98f31047dcfff72a92778cec708bbf6ed2c8" &
        "517e9a070cefe2cd56bf7d5570a00adad0eb4790980976490b24cb3a6c45c320" &
        "6ac1edf97903070cf746e5362226bd50eb970dfffafede9a8c41a036985d4c37"

    ' a / 111111111  (1 块)
    Private Const GoldenMinimal As String =
        "51cc8c729fa3e6d04dd4e5fbb372249a2135fc23a37690baa77000bdbf273919" &
        "3b32c035cf8b74908c8fb79331a990680fcbafbd5504c09189f4aad78cb89640" &
        "78ce5d79af4bf2c7d7e28d5d37e5d168431a23d6d7d0e84f5b989df07e55578e" &
        "f7715dbed6264615a58c22ae06aea8548a24b98bcddbe0872ea40231d6ed12ed"

    ' !@#$%^&*()_+ / 111111111  (1 块)
    Private Const GoldenPunctuation As String =
        "04a0f20023a81a4778b6f8fcd53f62c631f7517b1f9daf4de32f20b3effb11b0" &
        "419a69a5bb73c1d424974547454bfd154b316179f23be0b3774ad7efbc239604" &
        "96649cb721b222f42fa7e75f5c600b40a2d48673c3d227a0f244f5c391457c6e" &
        "610ac092ef4ec0d0aea6ecbe56f22cb808738d4a8f64b7742429e6255c521890"

    ' "a" & ChrW(&HFF) / 111111111  (1 块)
    Private Const GoldenLatin1FF As String =
        "68d869f3e47af25504221c4306ad4497fa4450e468961bf440a8d35c738c20a4" &
        "d2d68434f9836b4b88cb016927ece7ccdb68470ba731e45e156dde8a90c4e772" &
        "fec4fad1838d9cd07873b5868c3cb33015d4cf4ecb7c572707d8d12efe7266c7" &
        "05bc81bb76576afd9e473810edb7c31c0d34cc302324fe05d4c2c18459646c28"

    ' String("b"c, 116) / 111111111  -> raw 长度恰为 126，单块且无需补零
    Private Const GoldenRawExactly126 As String =
        "22a3d4a319c520c79988f9aa8fc5e5b90ad666d6ee94f77c22c385f20ac87540" &
        "7bceaa6e97c74aa26824277c9ac17b2fbdf402b3ae77e81c0b39ed74c3ded602" &
        "3882a4eb3d37c1b1b1dcd9a3e3bc4da47450623d5d473eedd5ba717aa7a96355" &
        "a727466bde7602aa7177fe433a9ca26110200b793fc4f10bcf6c88e1028e0a1a"

    ' String("c"c, 117) / 111111111  -> raw 长度 127，补零后为两块
    Private Const GoldenRawExactly127 As String =
        "3bbb11049db6105ce563fce8df38666aa88f286069fb2d27deb86dc50ec4544f" &
        "b30dee7121593c241164f73054b32691983b516e9e84dc74cbc8f12d67065c33" &
        "3127533c700ed9f84978fdf4fec679e9b29405b7f1314b51e6e1535050225ab7" &
        "a1b15d43153f35e4fff0d8561d20e770397daca082de285749cb03120356a197" &
        " 0d35c06b8b49b5338b532e7c5d1ade2741d9e3ec6715c8eac03902a07d2f807" &
        "2cda854bc9b130e2e40509811daaa52bdd1e386982b533c8f187711bbb151960" &
        "c2bd3d0eb9fdbaf7fa2ebdcccd236bae73557ce71d48b797965ef680372e8c9a" &
        "afcfb3ecd270b51b3fc1064bc4b453a3f8ebca0d10e8b040431d29c500934d54" &
        "3"

    ' String("z"c, 127) / 111111111  (2 块)
    Private Const GoldenMultiBlock As String =
        "4d9664fab96a9240fb3cb18f32624fbd1a483874c6692c6aa20c6348cd996460" &
        "d36b4317a051bf94dc5d1f58e700256a3d42119aafd84601a26093af0867312e" &
        "1fc117b5c338fd0a884e84c333eb62a65f73df6612ed85e31ee1cbe77608f062" &
        "ab5eeac7e98ef455df445e06eede24ffade8ec2a82c5f6895ee14089416dfd47" &
        " 20934ec3cc3efad33fdc1898d57c7efd2e605c77a38f4965fa060c32357ac5b" &
        "d9f4f7c9b7cf51a4efc743a7aae3a8ad7374de061fc1eb56a0eda38dcb19e8a1" &
        "1b01c41f4d20a4e8bd6bcd42c257541fefc3b6145986efd35034286430bc78e1" &
        "4a81449f4f7996308faf66bb57b730a795c57ddadb01bc331b19d9da26752df0" &
        "2"

    ' ChrW(&H100) / 111111111  (1 块) —— U+0100 已超出 Latin-1，但落在偶数下标
    Private Const GoldenU0100EvenIdx As String =
        "8c8c687542f7e6ee4e02d609c2b96ebfbd77d6cfa537054f634ce4a6cfabddf8" &
        "0d091a0bee9172f50054f84c5ce31649d1b973cecffdc27f327d3d332cdaa260" &
        "197b565c33a9cce20898c8d951df78876d0e0905cf3c3d42398ffe86021dcd47" &
        "6eeddb0f62e4f416b8a62b317af12d2e31dce173e9f3ca9d5984cd91f90d0ba9"

    ' ChrW(&H4E2D) / 111111111  (1 块) —— CJK，同样落在偶数下标
    Private Const GoldenCjkEvenIdx As String =
        "754c8629605c77a3785871902b10b49d4bcabc3a6be22baba49f423bbb3b8a23" &
        "f1f8e6a9c131d02c7922c15981683b98699b4c5d45595adac3d168080579672b" &
        "51b7f97d7bb420adaef3db31cec1a6ae9cda4a4630b361b84bd6325717ff82f5" &
        "e1485f720efe765ad104c9e68746771fac6f26383ce6bd6ac35b710b95ba8573"


#End Region

#Region "ModCrypto 测试用例"

    Private Sub Test_CryptoGoldenBasic()
        Dim actual = EncryptPassword("test1234", "111111111", TestModulus, TestExponent)
        AssertEqual(GoldenBasic, actual, "test1234/111111111 必须与真实 security.js 输出逐字符一致")
    End Sub

    Private Sub Test_CryptoDefaultMac()
        ' login_bch.js: macString 缺失/为空 -> isNull -> "111111111"
        AssertEqual("111111111", DefaultMac, "默认 mac 常量应为 111111111")
        AssertEqual(GoldenBasic, EncryptPassword("test1234", DefaultMac, TestModulus, TestExponent),
                    "显式传入默认 mac 应与黄金向量一致")
        AssertEqual(GoldenBasic, EncryptPassword("test1234", "", TestModulus, TestExponent),
                    "mac 为空串时应回退到默认值")
        AssertEqual(GoldenBasic, EncryptPassword("test1234", Nothing, TestModulus, TestExponent),
                    "mac 为 Nothing 时应回退到默认值")
    End Sub

    Private Sub Test_CryptoDifferentMac()
        Dim actual = EncryptPassword("test1234", "abc123", TestModulus, TestExponent)
        AssertEqual(GoldenMacDifferent, actual, "mac=abc123 必须与真实 security.js 输出一致")
        AssertFalse(GoldenBasic = actual, "更换 mac 后密文必须改变，证明 mac 确实参与加密")
    End Sub

    Private Sub Test_CryptoMinimal()
        AssertEqual(GoldenMinimal, EncryptPassword("a", DefaultMac, TestModulus, TestExponent),
                    "单字符密码必须与真实 security.js 输出一致")
    End Sub

    Private Sub Test_CryptoPunctuation()
        AssertEqual(GoldenPunctuation, EncryptPassword("!@#$%^&*()_+", DefaultMac, TestModulus, TestExponent),
                    "含标点密码必须与真实 security.js 输出一致")
    End Sub

    Private Sub Test_CryptoUtf16Latin1()
        ' U+00FF 是 16-bit digit 能容纳的上界（0xFF + 0xFF00 = 0xFFFF）
        AssertEqual(GoldenLatin1FF, EncryptPassword("a" & ChrW(&HFF), DefaultMac, TestModulus, TestExponent),
                    "Latin-1 上界 U+00FF 必须与真实 security.js 输出一致")
    End Sub

    Private Sub Test_CryptoUtf16BeyondLatin1()
        ' U+0100 已超出 Latin-1：若实现被误改成 Encoding.UTF8.GetBytes，
        ' U+0100 会变成两个字节 0xC4 0x80，本向量必然失败。这正是要锁死的语义。
        AssertEqual(GoldenU0100EvenIdx, EncryptPassword(ChrW(&H100), DefaultMac, TestModulus, TestExponent),
                    "U+0100 必须与真实 security.js 输出一致（证明使用 UTF-16 code unit，而非 UTF-8）")
    End Sub

    Private Sub Test_CryptoUtf16Cjk()
        ' 单个 CJK 字符反转后位于偶数下标，与之配对的奇数为补零，digit 仍在 16 bit 内
        AssertEqual(GoldenCjkEvenIdx, EncryptPassword(ChrW(&H4E2D), DefaultMac, TestModulus, TestExponent),
                    "CJK 偶数下标必须与真实 security.js 输出一致（证明 charCodeAt 语义）")
    End Sub

    Private Sub Test_CryptoExactBlockBoundary()
        ' chunkSize = 126；raw = password + ">" + "111111111"，故 116 字符密码恰好得到 raw = 126
        Dim pw = New String("b"c, 116)
        AssertEqual(126, (pw & ">" & DefaultMac).Length, "前置条件：raw 长度应为 126")
        Dim actual = EncryptPassword(pw, DefaultMac, TestModulus, TestExponent)
        AssertEqual(GoldenRawExactly126, actual, "raw=126 必须与真实 security.js 输出一致")
        AssertEqual(256, actual.Length, "raw=126 应为单块 256 个十六进制字符")
    End Sub

    Private Sub Test_CryptoMultiBlock()
        ' raw = 127 -> 补零到 252 -> 两块
        Dim pw127 = New String("c"c, 117)
        AssertEqual(127, (pw127 & ">" & DefaultMac).Length, "前置条件：raw 长度应为 127")
        Dim actual127 = EncryptPassword(pw127, DefaultMac, TestModulus, TestExponent)
        AssertEqual(GoldenRawExactly127, actual127, "raw=127 必须与真实 security.js 输出一致")
        AssertEqual(513, actual127.Length, "两块输出长度应为 256 + 1 + 256 = 513")

        AssertEqual(GoldenMultiBlock,
                    EncryptPassword(New String("z"c, 127), DefaultMac, TestModulus, TestExponent),
                    "127 字符密码必须与真实 security.js 输出一致")
    End Sub

    Private Sub Test_CryptoRejectsOutOfRangeDigit()
        ' "A中B" 反转后为 "B中A>" + "111111111"，'中' 落在奇数下标：
        '   digit = 0x42 + (0x4E2D << 8) = 5123394 > 65535
        ' 此时门户 security.js 会因 JavaScript 32 位整数截断算出错误密文，故必须拒绝。
        Dim cjkOddIndex = "A" & ChrW(&H4E2D) & "B"
        AssertThrows(Sub() EncryptPassword(cjkOddIndex, DefaultMac, TestModulus, TestExponent),
                     GetType(NotSupportedException), "'中' 落在奇数下标时应拒绝而非产出错误密文")

        ' 代理对（surrogate pair）同样会把 digit 顶出 16 bit
        Dim surrogate = "A" & ChrW(&HD83D) & ChrW(&HDE00) & "B"
        AssertThrows(Sub() EncryptPassword(surrogate, DefaultMac, TestModulus, TestExponent),
                     GetType(NotSupportedException), "surrogate pair 越界时应拒绝")
    End Sub

    Private Sub Test_CryptoOutputFormat()
        Dim oneBlock = EncryptPassword("test1234", DefaultMac, TestModulus, TestExponent)
        AssertEqual(256, oneBlock.Length, "单块输出应为 256 个十六进制字符")
        AssertTrue(IsLowerHexOnly(oneBlock), "输出应只含小写十六进制字符（不得出现大写或非 hex 字符）")
        AssertFalse(oneBlock.Contains(" "), "单块输出不应包含空格")

        Dim twoBlocks = EncryptPassword(New String("z"c, 127), DefaultMac, TestModulus, TestExponent)
        Dim parts = twoBlocks.Split(" "c)
        AssertEqual(2, parts.Length, "跨块输出应以单个空格分隔为两块")
        AssertEqual(256, parts(0).Length, "第一块应为 256 个十六进制字符")
        AssertEqual(256, parts(1).Length, "第二块应为 256 个十六进制字符")
        AssertTrue(IsLowerHexOnly(parts(0)) AndAlso IsLowerHexOnly(parts(1)), "各块应只含小写十六进制字符")
    End Sub

    Private Sub Test_CryptoDeterministic()
        Dim a = EncryptPassword("test1234", DefaultMac, TestModulus, TestExponent)
        Dim b = EncryptPassword("test1234", DefaultMac, TestModulus, TestExponent)
        AssertEqual(a, b, "纯函数：相同输入必须恒产生相同输出")
    End Sub

    Private Sub Test_CryptoNullPassword()
        ' JavaScript 中密码框的 value 永远是字符串，不会是 null，故 Nothing 按空串处理
        AssertEqual(EncryptPassword("", DefaultMac, TestModulus, TestExponent),
                    EncryptPassword(Nothing, DefaultMac, TestModulus, TestExponent),
                    "Password = Nothing 应与空串结果一致")
    End Sub

    Private Sub Test_CryptoInvalidArguments()
        AssertThrows(Sub() EncryptPassword("a", DefaultMac, "", TestExponent),
                     GetType(ArgumentException), "modulus 为空串应被拒绝")
        AssertThrows(Sub() EncryptPassword("a", DefaultMac, Nothing, TestExponent),
                     GetType(ArgumentException), "modulus 为 Nothing 应被拒绝")
        AssertThrows(Sub() EncryptPassword("a", DefaultMac, "   ", TestExponent),
                     GetType(ArgumentException), "modulus 为纯空白应被拒绝")
        AssertThrows(Sub() EncryptPassword("a", DefaultMac, TestModulus, ""),
                     GetType(ArgumentException), "exponent 为空串应被拒绝")
        AssertThrows(Sub() EncryptPassword("a", DefaultMac, TestModulus, Nothing),
                     GetType(ArgumentException), "exponent 为 Nothing 应被拒绝")
        AssertThrows(Sub() EncryptPassword("a", DefaultMac, "xyz", TestExponent),
                     GetType(ArgumentException), "modulus 含非 hex 字符应被拒绝")
        AssertThrows(Sub() EncryptPassword("a", DefaultMac, TestModulus, "1000g"),
                     GetType(ArgumentException), "exponent 含非 hex 字符应被拒绝")
        AssertThrows(Sub() EncryptPassword("a", DefaultMac, TestModulus, "0"),
                     GetType(ArgumentException), "exponent = 0 应被拒绝")
        AssertThrows(Sub() EncryptPassword("a", DefaultMac, "0", TestExponent),
                     GetType(ArgumentException), "modulus = 0 应被拒绝")
        AssertThrows(Sub() EncryptPassword("a", DefaultMac, "1", TestExponent),
                     GetType(ArgumentException), "modulus = 1 应被拒绝")
    End Sub


#End Region

    ''' <summary>security.js 的 biToHex 只输出小写十六进制字符。</summary>
    Friend Function IsLowerHexOnly(value As String) As Boolean
        If String.IsNullOrEmpty(value) Then Return False
        For Each c As Char In value
            If Not ((c >= "0"c AndAlso c <= "9"c) OrElse (c >= "a"c AndAlso c <= "f"c)) Then Return False
        Next
        Return True
    End Function
#Region "注册"

    ''' <summary>把本模块的用例登记到测试框架。由 ModTestSuite 调用。</summary>
    Friend Sub RegisterAll()
        RunTest("黄金向量: test1234 / mac 111111111", AddressOf Test_CryptoGoldenBasic)
        RunTest("黄金向量: mac 缺失与空串等价于默认值", AddressOf Test_CryptoDefaultMac)
        RunTest("黄金向量: mac 参与加密 (abc123)", AddressOf Test_CryptoDifferentMac)
        RunTest("黄金向量: 极短密码 a", AddressOf Test_CryptoMinimal)
        RunTest("黄金向量: 含标点密码", AddressOf Test_CryptoPunctuation)
        RunTest("黄金向量: UTF-16 语义 (Latin-1 U+00FF)", AddressOf Test_CryptoUtf16Latin1)
        RunTest("黄金向量: UTF-16 语义 (U+0100 非 Latin-1)", AddressOf Test_CryptoUtf16BeyondLatin1)
        RunTest("黄金向量: UTF-16 语义 (CJK 偶数下标)", AddressOf Test_CryptoUtf16Cjk)
        RunTest("黄金向量: 单块边界 raw=126", AddressOf Test_CryptoExactBlockBoundary)
        RunTest("黄金向量: 跨块 raw=127 与多块", AddressOf Test_CryptoMultiBlock)
        RunTest("UTF-16 越界输入被明确拒绝", AddressOf Test_CryptoRejectsOutOfRangeDigit)
        RunTest("输出格式为定宽小写十六进制", AddressOf Test_CryptoOutputFormat)
        RunTest("纯函数: 相同输入恒相同输出", AddressOf Test_CryptoDeterministic)
        RunTest("Password = Nothing 等价于空串", AddressOf Test_CryptoNullPassword)
        RunTest("非法参数被拒绝", AddressOf Test_CryptoInvalidArguments)
    End Sub

#End Region

End Module