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

## GUI 回归测试（`--ui-test`）

页面切换与窗口生命周期**无法靠离线测试覆盖**（它们需要真实的 WPF 窗口），
而这类问题的异常往往没有行号 —— 例如 `InvalidCastException: 指定的转换无效`
的堆栈只到方法名。因此提供一个开发期 GUI 自检入口：

```powershell
.\Ruijie-WPF\bin\Ruijie ePorta Tool.exe --ui-test
```

它会自动完成并逐项打印结果：

```text
状态/配置 连续切换 10 轮（共 20 次）   每轮检查两个 Frame 的 Visibility 与 Children 数
窗口生命周期 ×5                       点 X -> 隐藏；点托盘 -> 复用同一实例重新显示
```

退出码 `0` 表示全部通过，`1` 表示存在失败；失败时会打印**完整**异常类型、
消息与堆栈（无行号时，配合逐语句诊断定位）。

新增或修改 GUI 生命周期、页面切换、`ModHost`、`FormMain` 相关代码后，
请至少跑一次 `--ui-test` 再提交。

### 未处理异常会写入日志

`App_DispatcherUnhandledException` 会把异常的
`Type / Message / Source / TargetSite / InnerException / StackTrace`
完整写入 `bin\logs\<日期>.txt` 后再弹窗。
排查「弹窗只显示一句中文、无处可查」的问题时，先去看当日日志。

## 退出生命周期测试（`--exit-test`）

「托盘退出后进程是否真的结束」只能通过点击托盘菜单触发，脚本无法完成；
而进程残留只有退出之后才观察得到。因此提供：

```powershell
# 走与托盘「退出」完全相同的 ModHost.ExitApp() 路径，N 秒后自动退出（默认 5）
.\Ruijie-WPF\bin\Ruijie ePorta Tool.exe --exit-test=4

# 配合 --background / --exit-hide 覆盖三种退出场景
.\Ruijie-WPF\bin\Ruijie ePorta Tool.exe --background --exit-test=4
.\Ruijie-WPF\bin\Ruijie ePorta Tool.exe --exit-test=4 --exit-hide
```

判定方式是**轮询进程数是否归零**：

```powershell
$exe = '.\Ruijie-WPF\bin\Ruijie ePorta Tool.exe'
Start-Process $exe -ArgumentList '--background','--exit-test=4' -WorkingDirectory (Split-Path $exe)
Start-Sleep 3; (Get-Process 'Ruijie ePorta Tool').Count            # 期望 1
Start-Sleep 6; (Get-Process 'Ruijie ePorta Tool' -EA 0).Count      # 期望 0
```

> **不要**用 `Start-Process -PassThru` + `WaitForExit` 判定，实测会产生假失败；
> 也**不要**用 `cmd /c` 包装启动。以「进程数归零」为唯一判据。

退出各阶段会写入当日日志（`bin\logs\`，见 `TraceLifecycle`），可直接核对停在哪一步：

```text
[Exit] 开始退出 / 动画线程已请求停止 / 主窗口已关闭 / 网络监控已停止 / 托盘已释放 / 调用 Application.Shutdown
```

### 线程必须是后台线程

`RunInNewThread`（`ModBase.vb`）的 `IsBackground` **默认为 True**，请勿改回。
CLR 只要发现任何一个**前台**线程存活就不会结束进程 —— 曾因此出现
「托盘已消失、内存从约 30 MB 降到约 20 MB，但 Task Manager 里进程还在」的缺陷。
长时间运行的循环（如动画线程）除设为后台线程外，还必须提供明确的停止路径。

### 日志分两级

- `Log()` / `Log(ex, desc)` —— 走 `Debug.Write`，只有调试器或控制台重定向时可见
- `TraceLifecycle()` —— **同时写每日日志文件**，用于低频、诊断价值高的事件
  （线程启停、退出各阶段）。不要滥用，否则会刷满用户日志。

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
