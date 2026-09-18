# 贡献指南

## 构建项目

请参考 [README - 构建方法](README.md#构建方法)。

## 运行测试

提交前请确保离线测试全部通过：

```powershell
.\Ruijie-WPF\bin\Ruijie ePorta Tool.exe --test
```

输出结尾应显示 `0 失败`，退出码为 `0`。

测试代码位于 `Ruijie-WPF\Modules\Tests\`：

- 测试框架（`RunTest`、断言、分节统计）在 `ModTestFramework.vb`
- 跨模块共用的夹具在 `ModTestFixtures.vb`
- 各模块的用例按被测模块分文件，例如 `ModCryptoTests.vb`

新增用例时，在对应文件里写 `Test_` 开头的方法，并在该文件的 `RegisterAll()` 中登记：

```vb
RunTest("用例名称", AddressOf Test_YourCase)
```

如果新增了一个测试模块，还需要在 `ModTestSuite.RunAllTests()` 中加一行
`ModTestFramework.Section(...)`。

## 代码风格

- 语言：VB.NET
- 缩进：4 空格
- 命名：遵循 .NET 常规命名约定
- 注释：核心逻辑请添加中文注释，重点说明**为什么**这么写
- 行尾：CRLF；编码：UTF-8

项目根目录包含 `.editorconfig` 文件，支持该规范的编辑器会自动应用。

### 请勿写入的内容

提交前请确认源码与文档中**不含**以下真实数据：

- 真实学号、密码（明文或密文）
- 真实的 `queryString`、Cookie / JSESSIONID
- 本机特有的 IP 地址

测试用例请使用合成数据（例如 `192.0.2.0/24` 这类文档专用网段）。

## 提交 PR 流程

1. Fork 本仓库
2. 创建功能分支 (`git checkout -b feature/your-feature`)
3. 提交更改 (`git commit -m 'feat: 添加某功能'`)
4. 推送到 Fork 仓库 (`git push origin feature/your-feature`)
5. 提交 Pull Request

## Issue 提交

请使用提供的 Issue 模板，填写完整信息。
