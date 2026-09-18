Imports System.Reflection
Imports System.Runtime.InteropServices

<Assembly: AssemblyTitle("锐捷 ePorta 连接工具")>
<Assembly: AssemblyDescription("西华大学校园网 ePortal 认证客户端")>
<Assembly: AssemblyCompany("Red_lnn")>
<Assembly: AssemblyProduct("Ruijie ePorta Tool")>
<Assembly: AssemblyCopyright("Copyright © 2022-2026 Red_lnn. All Rights Reserved")>
<Assembly: AssemblyTrademark("")>
<Assembly: ComVisible(False)>
<Assembly: Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")>

' ============================================================================
' 版本号策略
' ----------------------------------------------------------------------------
' 三个版本号同源同进，一次发布一起改：
'
'   AssemblyVersion               2.0.0.0
'       程序集绑定标识。只有 Major.Minor 参与绑定，因此同一大版本内
'       修 bug 也不改它，避免给使用者带来无谓的重新绑定。
'
'   AssemblyFileVersion           2.0.0.0
'       文件版本，Windows 文件属性里看到的就是它。热修复可以只动第 4 位
'       （例如 2.0.0.1），不必动 AssemblyVersion。
'
'   AssemblyInformationalVersion  2.0.0
'       给人看的版本，界面「关于」显示的就是它（见 VersionHelper.GetAppVersion）。
'       不写第 4 位，避免把内部构建号暴露成产品版本。
'
' ----------------------------------------------------------------------------
' 递增规则（语义化版本）
' ----------------------------------------------------------------------------
'   修 bug、界面微调                 → 2.0.1
'   新增功能、向后兼容               → 2.1.0
'   配置结构或认证模型发生破坏性变更 → 3.0.0
'       （例如配置字段改名、认证流程改动，导致旧配置无法继续使用）
'
' 开发过程中**不**为每次提交递增版本号 —— 构建产物靠 commit 与 SHA-256 区分。
' 只有准备对外分发时才递增，并同步在 CHANGELOG.md 里补一条。
' ============================================================================

<Assembly: AssemblyVersion("2.0.0.0")>
<Assembly: AssemblyFileVersion("2.0.0.0")>
<Assembly: AssemblyInformationalVersion("2.0.0")>
