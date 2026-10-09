# PDB Finder 编译指南

本文档说明如何从源码构建 PDB Finder 的全部产物。中英文项目介绍、解决的问题及使用配置见 [README.md](README.md)，项目规划与历史验证记录见 [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)。

## 解决方案结构

| 项目 | 目标框架 | 说明 |
| --- | --- | --- |
| `PdbFinder.Core` | `netstandard2.0` | 匹配引擎：minidump/PDB/PE 解析、候选查找与索引缓存、file.ptr 符号映射。 |
| `PdbFinder.Verify` | `net9.0` | 命令行验证工具，脱离 VS 直接验证匹配逻辑。 |
| `PdbFinder.Vsix` | `net472` | VS 2022 扩展（VSIX），引用 Core。 |

## 环境要求

1. **支持 `net9.0` 的 .NET SDK**。Verify 项目面向 `net9.0`，Core 为 `netstandard2.0`，VSIX 为 `net472`；SDK 版本与 VSIX 的运行时要求不同。
2. **Visual Studio 2022（x64）**。VSIX 清单支持 Community、Professional、Enterprise 的 17.x 实例；本项目的真实调试验证环境为 Community 17.14。命令行构建 VSIX 使用下面的 NuGet 构建工具，不要求启动 VS IDE。
3. **.NET Framework 4.7.2 引用程序集/目标包**。若构建时报缺少 `net472` 引用程序集，安装对应 Developer Pack。
4. 首次编译需要联网还原 NuGet 包：
   - `Microsoft.VisualStudio.SDK` 17.14.40265
   - `Microsoft.VSSDK.BuildTools` 17.14.2142（含 VSCT 编译、pkgdef 生成、VSIX 打包工具链，无需单独安装 VS SDK）

只安装和使用已编译 VSIX 的用户不需要上述 .NET SDK、目标包或 NuGet 构建工具；扩展运行时要求由 VSIX 清单定义（VS 2022 x64、.NET Framework 4.7.2 或更高）。

## 编译

在解决方案根目录（`PDB Finder`）执行：

```powershell
# 还原 + 编译整个解决方案（Debug）
dotnet build PdbFinder.sln -c Debug

# 发布用 Release 构建（生成 VSIX）
dotnet build PdbFinder.sln -c Release
```

单独构建某个项目：

```powershell
dotnet build PdbFinder.Core\PdbFinder.Core.csproj -c Release
dotnet build PdbFinder.Verify\PdbFinder.Verify.csproj -c Release
dotnet build PdbFinder.Vsix\PdbFinder.Vsix.csproj -c Release   # 同时产出 .vsix
```

## 在 Visual Studio 中直接生成

1. 用 VS 2022 打开 `PdbFinder.sln`。
2. 顶部工具栏把解决方案配置选为 **Release**（日常调试用 Debug），平台保持 **Any CPU**。
3. 菜单 **生成 → 生成解决方案**（快捷键 **Ctrl+Shift+B**）。
4. 在 **输出** 窗口确认三个项目全部成功；VSIX 产物在
   `PdbFinder.Vsix\bin\<配置>\net472\PdbFinder.Vsix.vsix`。

右键单独生成某个项目也可以：解决方案资源管理器里右键项目 → **生成**。VSIX 项目生成时会自动完成 VSCT 编译、pkgdef 生成和打包，无需额外操作。

### 按 F5 调试扩展（可选）

当前 csproj 里 `<DeployExtension>false</DeployExtension>`，直接 F5 **不会**自动部署扩展。想用 F5 在实验实例中调试，把该行改为 `true` 并确认 `PdbFinder.Vsix` 是启动项目，F5 即会以 `devenv /RootSuffix Exp` 启动实验实例并部署本扩展。日常验证也可以不调试：生成后用"安装扩展"一节的命令装入实验实例。

## 版本号

构建时自动打版本戳（`Directory.Build.props`）：格式 `1.0.<当年第N天>.<HHmm>`（如 `1.0.272.1346`），同时写入：

- 三个程序集的 AssemblyVersion/FileVersion（`Version` 属性）
- VSIX 清单版本——`source.extension.vsixmanifest` 中的占位符 `@PdbFinderVersion@` 由 csproj 里的 `StampVsixManifestVersion` 目标在打包前替换（VSIX 清单不支持 MSBuild 属性展开，且清单内容会再次展开 `@...@`，故不能用 `$(...)` 形式）

扩展管理器和 dll 属性里看到的版本号即构建时间，便于确认安装的是否为最新构建。

## 产物位置

| 产物 | 路径 |
| --- | --- |
| VSIX 安装包 | `PdbFinder.Vsix\bin\<配置>\net472\PdbFinder.Vsix.vsix` |
| Core 库 | `PdbFinder.Core\bin\<配置>\netstandard2.0\PdbFinder.Core.dll` |
| 验证工具 | `PdbFinder.Verify\bin\<配置>\net9.0\PdbFinder.Verify.dll` |

VSIX 包内容：`PdbFinder.Vsix.dll`、`PdbFinder.Core.dll`、`extension.vsixmanifest`、`PdbFinder.Vsix.pkgdef` 及目录文件。两个程序集均带强名称（见下节），pkgdef 内含 `RuntimeConfiguration` 的 codeBase 绑定项。

## 强名称签名

`PdbFinder.Core` 与 `PdbFinder.Vsix` 都使用仓库根目录的 **`PdbFinder.snk`** 签名（公钥标记 `a8854587a4c0a24f`），在各自 csproj 中通过 `<SignAssembly>` 配置。

- 原因：VS 对位于 appbase 之外的程序集要求强名称后才能按 codeBase 加载（未签名程序集会报 `0x80131041`）。
- 若丢失 `PdbFinder.snk`，可用 Windows SDK 的 `sn.exe -k PdbFinder.snk` 重新生成，但公钥标记会变化，已注册位置的绑定需同步更新。
- 扩展包类上的 `[assembly: ProvideCodeBase(...)]` 会让 pkgdef 携带程序集绑定信息，属于正常部署所需，请勿删除。

## 验证工具用法

```powershell
# 仅解析 dump：打印主模块、预期 PDB 名、GUID+Age、PE 信息
dotnet run --project PdbFinder.Verify -- "C:\path\crash.dmp"

# 解析 dump 并在共享根目录中查找匹配（带索引缓存）
dotnet run --project PdbFinder.Verify -- "C:\path\crash.dmp" "\\server\PackageBackup\wai\windows"

# 校验单个 PDB / EXE 与 dump 的匹配关系
dotnet run --project PdbFinder.Verify -- "C:\path\crash.dmp" "D:\build\HTGame.pdb" "D:\build\HTGame.exe"
```

输出中 EXE 一行会标注验证级别：`Exact`、`Repackaged` 或 `None`，判定细节见 [ARCHITECTURE.md](ARCHITECTURE.md)。目录查找先查询 CLI 索引，未命中再完整扫描一次；索引缓存位于 `%TEMP%\pdb-finder-verify-index.tsv`，删除后下一次查找会重新扫描。普通 CLI 查找不读取 VS 符号缓存设置；CLI 与 VSIX 均不会因未命中而自动重建并重复全量扫描。

多个根目录可用分号放在同一个参数中：

```powershell
dotnet run --project PdbFinder.Verify -- "C:\path\crash.dmp" "\\server\PackageBackup\nei\windows;\\server\PackageBackup\wai\windows"
```

本地回归验证（使用临时二进制夹具，不访问共享目录）：

```powershell
dotnet run --project PdbFinder.Verify -c Release -- --self-test
```

覆盖本地 PDB/EXE 缓存命中、备用缓存和本地 dump 同目录查找、本地 GUID/Age 校验与取消，以及索引直接命中、根目录收窄/范围过滤、PDB 改名、索引缺失、候选删除或身份变化、扫描进度和各扫描阶段取消时保留已有索引。

结构修复回归还覆盖混合归档/构建子目录、重叠根去重、过期负匹配身份与一次扫描、索引锁定/损坏/保存失败降级、EXE 缺失或错误 GUID/Age、远程暂存目录拒绝、四路复制强制重叠、取消 EXE 发布，以及匹配会话在查找结束后统一取消 PDB/EXE worker。会话测试链接实际的 `MatchSession.cs`，不依赖 VS 进程；UI 回调和真实调试加载仍需 VS 内验证。

复制检查覆盖初始/最终进度、按预期名称发布已验证的 PDB、复用已有副本、传输完成后取消时不发布，以及复制后的 GUID/Age 校验失败。

## 安装扩展

```powershell
# 安装到主实例（当前使用的 VS）
& "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" `
  "PdbFinder.Vsix\bin\Release\net472\PdbFinder.Vsix.vsix"

# 安装到实验实例（调试沙箱）
& "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" `
  /rootSuffix:Exp "PdbFinder.Vsix\bin\Release\net472\PdbFinder.Vsix.vsix"
```

安装后重启 VS 生效。实验实例用 `devenv /RootSuffix Exp` 启动。

## 卸载

- 界面：VS 菜单 **扩展 → 管理扩展**，找到 PDB Finder → **卸载**，重启 VS。
- 命令行（扩展 ID 见 `source.extension.vsixmanifest`）：

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\VSIXInstaller.exe" `
  /u:PdbFinder.9a04a1ea-6386-4608-8565-194d405721fb          # 主实例
# 加 /rootSuffix:Exp 卸载实验实例
```

- 双击仓库根目录的 `install.cmd` / `uninstall.cmd`（自动用 vswhere 定位 VS）。
- 卸载不会清理运行数据：`%LOCALAPPDATA%\PDB Finder` 包含索引、file.ptr、事件日志和备用符号缓存；VS 配置的符号缓存目录还可能包含暂存的 PDB/EXE。清理时按具体构建条目处理，空间取决于文件大小，避免误删需要保留的 VS 缓存条目。

## 常见问题

- **报缺少 `net472` 引用程序集**：安装 .NET Framework 4.7.2 Developer Pack。
- **NuGet 还原失败（VSSDK 包）**：确认能访问 nuget.org；公司代理下先 `dotnet nuget` 配置好代理。
- **`PdbFinder.snk` 缺失**：用 `sn.exe -k` 重新生成（注意公钥标记会变）。
- **VSIX 安装后扩展不加载**：先看 `%APPDATA%\Microsoft\VisualStudio\<版本>\ActivityLog.xml`（需以 `devenv /Log` 启动）中 PdbFinder 相关条目。本机曾遇到 ServiceHub 扩展管理服务损坏（`PkgDefMgmt.dll` 0x8007007E）导致实验实例注册合并失败，修复 VS 可解决。
- **调试 dump 时"未找到匹配的二进制"**：先看日志是否标记 `Repackaged`。若 EXE 被重打包，普通 minidump 可按扩展提示确认创建本地修补副本；完整内存 dump 会跳过该提示。没有匹配 EXE、路径不可用或二进制不属于该构建也可能导致此状态，不应仅凭此提示断定重打包。
- **本地已经有 PDB，索引或共享目录不可用**：当前 VSIX 会先检查本地符号；本地命中不读取索引，也不要求配置共享根目录。确认 PDB 的 GUID+Age、预期文件名和缓存布局，而不是只比较文件名。
- **有索引但仍发生网络访问**：索引保存候选路径和身份信息，不包含 PDB 文件本体；远程候选仍需重新读取标识并检查 EXE。只有本地 PDB 命中才跳过这一步。
- **匹配等待时间长或取消未立即结束**：查看进度窗口的当前阶段与路径。归档顶层会触发宽范围递归扫描，优先配置直接包含频道目录的根；同步网络请求必须返回后才能结束取消。
