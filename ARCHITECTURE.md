# PDB Finder 工作流程

本文档描述截至 2026-10-09 的代码工作流程：从在 Visual Studio 中打开一个 UE Windows dump，到准备并加载主 EXE 模块符号。中英文项目介绍和配置方法见 [README.md](README.md)，构建方法见 [BUILD.md](BUILD.md)，历史验证记录见 [DEVELOPMENT_PLAN.md](DEVELOPMENT_PLAN.md)。

## 总览

VS 2022 打开 UE Windows dump 后，扩展按 **本地已有 PDB → 本地索引 → 共享目录扫描** 的顺序查找与主 EXE 的 GUID+Age 一致的符号。本地命中不读取索引、不查共享符号目录；索引命中仍需验证候选文件，候选在共享目录时会产生网络访问。需要暂存时，复制到本机并使用 dump 预期的文件名；符号就绪且调试器处于中断模式时，调用 VS 加载符号。

判定匹配只用 dump 里记录的事实，**不以文件名或构建目录名做最终判断**。

## 代码结构

| 项目 | 角色 |
| --- | --- |
| `PdbFinder.Core` | `BinaryReaders.cs` 解析 dump/PDB/PE；`CandidateFinder.cs` 查索引、扫描共享目录；`SymbolMapping.cs` 检查本地符号、暂存 PDB/EXE、创建 file.ptr；`IndexProgress.cs` 定义进度快照。netstandard2.0，无 VS 依赖。 |
| `PdbFinder.Vsix` | `PdbFinderPackage.cs` 编排事件、命令、匹配、符号路径和加载；`MatchSession.cs` 管理每个 dump 的任务及取消生命周期；`FinderOptions.cs` / `FinderOptionsControl.cs` 提供设置；`IndexProgressForm.cs` 显示匹配/索引进度；`CopyProgressForm.cs` 显示 PDB 复制进度；`PdbFinder.vsct` 注册菜单。net472。 |
| `PdbFinder.Verify` | 命令行解析 dump、检查指定 PDB/EXE、按索引/共享目录查找；`--self-test` 用本地二进制夹具验证本地查找、索引安全、回退和取消。普通 CLI 查找不会自动读取 VS 缓存设置。net9.0。 |

## 端到端流程

```
打开 .dmp
   │
   ▼
① 触发与编排（PdbFinderPackage）
   DocumentOpened / WindowActivated → ProcessDocument
   - 非 .dmp 或 AutoMatch 关闭 → 自动事件不匹配；手动命令不受 AutoMatch 限制
   - 同一 dump 正在扫描/复制时，重复事件不重启任务
   - 同一 dump 的本地符号已就绪时提示；文件失效则重新匹配
   - 每个 dump 使用 MatchSession；新任务统一取消旧扫描、PDB 复制和 EXE 修补，并清除旧候选，后台启动 MatchAsync
   - 匹配结束后会话仍持有复制任务的取消源；退休会话等待所有 worker 退出后才释放取消源，旧回调不能更新新会话的状态或提示
   - 立即显示可取消的进度窗口；读取 dump、本地符号检查、等待扫描队列、索引查询、目录枚举和匹配均有阶段提示
   │
   ▼
② 解析 dump（MinidumpReader.MainExecutable）
   校验 'MDMP' 签名 → ModuleListStream → 第一个 .exe 模块，得到 DumpModule：
   - 模块名（如 HTGame.exe）
   - PE TimeDateStamp / SizeOfImage / CheckSum
   - CodeView RSDS：期望 PDB 名（HTGame-Win64-Shipping.pdb）+ GUID + Age
   无 RSDS → 报错"无法安全自动匹配"；不是 minidump（如被改写成 XML）→ 明确失败日志
   │
   ▼
③ 查找候选（MatchAsync 编排本地检查，再调用 CandidateFinder.Find）
   先检查本地已有符号（LocalSymbolCache.FindLocal）：
   - 按 GUID+Age 校验 VS 符号缓存、扩展备用缓存和本地 dump 同目录中的预期名称 PDB
   - 每个本地缓存根目录检查 symsrv 键控位置和根目录下同名 PDB，再检查本地 dump 同目录
   - 校验命中的 PDB，同时检查其同目录 EXE；该缓存根目录中有验证过的键控 EXE 时可复用
   - 有效命中直接应用，不读取索引、不访问共享符号路径，也不等待扫描队列
   - UNC 和映射网络盘不属于本地检查范围；本地命中无需配置共享根目录
   - 本地未命中且没有共享根目录时提示配置；否则等待 _scanGate 后读取索引
   本地无匹配时，再在索引中按 GUID+Age 筛选当前共享根目录内的候选：
   - 只读取候选 PDB 的身份信息，校验文件仍有效，并验证同目录 EXE
   - 有效命中立即返回（FromIndex=true、DirectoriesVisited=0），不枚举共享目录
   - 根目录收窄时可继续复用旧索引；路径按目录边界过滤，排除范围外的旧条目
   索引无有效候选时才扫描共享目录：
   对设置的每个共享根目录（分号分隔多个，单个不可访问只记录不阻断）：
   - 完整递归所有根目录，覆盖混合布局和构建子目录；<根>/<频道>/execute/<构建>/ 优先检查，但不排除其他目录
   - 重叠根目录去重；不跟随子目录中的 junction/符号链接，避免循环和跳出归档范围
   - 构建目录按名称倒序安排检查，Parallel.ForEach 最多 8 路并行扫描
   - 每个 .pdb 重新读取当前 GUID+Age，再更新索引；即使文件长度/时间未变，也不会复用旧身份导致漏配
   - 命中后校验同目录 EXE（VerifyPe）：
       时间戳相同，且 PE 必须有与 dump 一致的 PDB GUID+Age →
         映像大小相同，校验和相同或 dump 校验和为 0 → PeVerification.Exact
         大小/校验和不同          → PeVerification.Repackaged（链接后被重打包，采用并标注）
       否则不采用该 EXE
   - 扫描命中经过验证的 EXE 时提前停扫；只有 PDB 命中时仍可作为符号候选
   找不到 → 说明原因（已查目录/PDB 数、错误），直接允许手动选择 PDB（仍校验 GUID+Age），不再重建并重复扫描
   索引读取失败 → 清除未完整读取的缓存，降级扫描；索引保存失败 → 返回匹配结果并记录警告，不阻断符号准备
   │
   ▼
④ 应用匹配（ApplyMatch）
   a) SymbolMapping.CreatePointerStore：
      %LOCALAPPDATA%\PDB Finder\symbol-store\<期望名>\<GUID+Age>\file.ptr
      file.ptr 内容 = 候选 PDB 路径（本地或共享）→ SymSrv 按 dump 期望名即可解析改名 PDB，
      共享目录原文件不做任何改动
   b) 写符号路径（ShellSettingsManager → 私有注册表 Debugger\SymbolPath / SymbolPathState）：
      追加 <构建目录> 与 srv*<symbol-store>；同会话再次匹配时先移除上一次加入的构建目录
      ※ 调试器在 VS 启动时缓存符号路径，该写入对后续新会话生效
   c) 日志记录匹配来源；StartStaging 显示符号就绪或复制中的 InfoBar
   d) Repackaged 候选调用 OfferExePatch：完整内存 dump 跳过提示；已有本地修补 EXE 时复用；否则询问是否复制并修补本地 EXE
   e) StartStaging
   │
   ▼
⑤ 本地符号暂存（LocalSymbolCache.StageAsync）
   原因：实测 Module.LoadSymbols 唯一可靠形态是"本地目录 + 目录内是 dump 期望名的 PDB"；
   UNC 路径被 dbghelp 按 symsrv 存储语义键控查找，改名 PDB 必然 miss。
   - 已有可用 PDB 时不再复制，直接给出"就绪"提示（InfoBar/日志/状态栏）：
     a) PDB 已在本地同名目录 → 直接用该目录
     b) 暂存位置已存在同 GUID+Age 的 PDB（含 VS 引擎自己下载进缓存的）→ 直接就绪
   - 否则复制到 VS 符号缓存目录（Debugger\SymbolCacheDir，本机 C:\vs_symbol_cache；
     未配置或不是本机路径时回退 %LOCALAPPDATA%\PDB Finder\pdb-modules），布局与 symsrv 缓存一致：
       <缓存目录>\<期望名>\<guid+age 小写>\<期望名>
     先写每次操作独立的 .download 临时文件 → 校验 GUID+Age → 原子发布；失败/取消清理临时文件，同构建完成后复用
   - 需要复制时始终显示置顶 CopyProgressForm，不再取决于 InfoBar 是否挂载成功
   - 窗口显示来源/目标、已复制/总大小、字节百分比和耗时；状态栏及 InfoBar 保留辅助提示
   - Worker 只提交最新快照，窗口每 150ms 刷新；小文件也报告初始与最终传输进度，传输到 100% 后显示校验阶段
   - 成功后短暂显示就绪并自动关闭；错误或取消保留窗口供查看。关闭正在复制的窗口等同请求取消
   - 用户取消后，同一 dump 的激活事件不会自动重新复制；手动 Match Current Dump 可重试
   - 新 dump 取消旧复制；旧候选的延迟回调不能覆盖当前候选的暂存目录或结果
   │
   ▼
⑥ 加载符号（TryLoadSymbols）
   暂存完成时、以及每次进入中断模式（DebuggerEvents.OnEnterBreakMode）时：
   - 暂存未完成 → 记录"尚未就绪，完成后自动重试"（并显示一次 InfoBar 说明）
   - 同一会话再次打开同一 dump（被去重跳过匹配）时，若符号已就绪也会提示一次"可以直接调试"
   - 在进程模块中找到主模块（HTGame.exe）→ Module.LoadSymbols(暂存目录)
   - module.SymbolFile 非空才算成功：
     成功 → 日志"VS 已加载符号：<路径>" + InfoBar"符号已加载"
     失败 → 如实记录 VS 返回的错误，绝不把"找到 PDB"误报为"已加载符号"
```

## 缓存与数据落点

| 数据 | 位置 | 说明 |
| --- | --- | --- |
| PDB 索引缓存 | `%LOCALAPPDATA%\PDB Finder\pdb-index.tsv` | TSV：base64 路径、长度、最后修改时间、GUID、Age。查索引时重新读取 GUID+Age 匹配候选的身份信息，有效命中跳过目录扫描；回退扫描时重新读取文件身份。索引读写故障不阻断匹配。 |
| file.ptr 指针存储 | `%LOCALAPPDATA%\PDB Finder\symbol-store\<期望名>\<GUID+Age>\file.ptr` | 按 dump 期望名索引，指向候选 PDB（本地或共享） |
| 暂存 PDB | `<VS 符号缓存>\<期望名>\<guid+age 小写>\<期望名>` | 未设置 VS 缓存时回退 `%LOCALAPPDATA%\PDB Finder\pdb-modules`；`C:\vs_symbol_cache` 是本机配置示例，所需空间取决于 PDB 大小 |
| 暂存/修补 EXE | `<缓存>\<exe 名>\<时间戳 hex8 + 映像大小 hex>\<exe 名>` | 使用 dump 的二进制键；仅修补本地副本的 SizeOfImage/CheckSum，保留时间戳和 PDB 标识 |
| 事件诊断日志 | `%LOCALAPPDATA%\PDB Finder\package-debug.log` | 包初始化、文档打开/激活和匹配触发；业务结果以 VS 的 PDB Finder 输出窗格为准 |
| 设置 | VS 私有注册表（DialogPage 持久化） | 共享根目录（分号分隔多个，默认空、由用户自行配置）、自动匹配开关 |
| 符号路径写入 | 私有注册表 `Debugger\SymbolPath` / `SymbolPathState` | 追加构建目录与 srv* 指针存储，重启 VS 后由调试器使用 |

## 用户入口

- **Tools 菜单**（vsct）：Match Current Dump / Settings / Test Connection / Rebuild Index
- **设置页**（工具 → 选项 → PDB Finder，自定义列表控件）：共享根目录一行一个（添加/浏览/移除、去重）、自动匹配开关、测试连接、重建索引按钮
- **输出窗格**：`输出 → 显示输出来源: PDB Finder`，全流程日志带时间戳
- **InfoBar**：本地符号就绪、复制中、符号已加载或配置缺失等提示，相关结果标明来源路径
- **状态栏**：扫描进度（目录/PDB 计数）、复制进度（MB + 进度条）、结果摘要
- **匹配/索引窗口**：读取 dump、本地检查、索引查询、目录枚举、扫描、保存索引等阶段；目录完成百分比、当前路径、计数、耗时和取消
- **复制窗口**：每次需要复制 PDB 均显示；来源与目标路径、已复制/总大小、百分比、耗时、取消和复制/校验结果

自动匹配不再调用 `NeedsRebuild` 来决定是否先全量重建；它先检查本地文件，再让 `Find` 复用当前根目录内的索引候选。索引缺失会直接回退扫描，根目录收窄也不强制全量重建。`NeedsRebuild` 方法仍保留，用于检测索引元数据是否与给定根目录集合一致。

## 失败与降级路径

| 情形 | 行为 |
| --- | --- |
| dump 格式无效或主 EXE 没有 RSDS PDB 标识 | 记录明确异常，进度窗口显示失败；无法自动精确匹配 |
| 未配置共享根目录 | 仍先检查本地符号；本地未命中才提示配置，手动匹配可打开设置页 |
| 本地缓存 PDB 无效或 GUID/Age 不同 | 不采用该文件，继续查询索引/共享目录 |
| 索引条目失效或范围外 | 范围外条目不访问；匹配候选会重新读取 GUID+Age，失效时回退扫描 |
| 索引被锁定、损坏或不能保存 | 读取失败降级扫描，无效记录跳过；保存失败保留匹配结果并记录警告，手动重建显示保存失败 |
| 共享根目录全部不可达 | FindResult.Error 说明；自动模式记录原因，可手动选 PDB |
| 部分根目录不可达 | 其余根目录照常扫描，结果附"部分共享目录不可访问" |
| 只有 PDB 无匹配 EXE | 作为符号候选继续流程，日志注明"目录中无匹配 EXE" |
| EXE 缺少 RSDS 或 GUID+Age 不同 | 不作为已验证 EXE，不提示修补，不复用此类旧缓存 EXE；PDB 仍可单独使用 |
| EXE 被重打包（大小/校验和不同） | `Repackaged` 标注差异；完整内存 dump 跳过修补提示，否则可经用户确认创建本地修补副本；拒绝修补时二进制仍可能被调试器拒绝 |
| 手动选择 PDB | 仍强制校验 GUID+Age，不一致给出需要/实际对照 |
| 用户取消或再开新 dump | 通过 CancellationToken 请求停止；同步文件/网络请求返回后才能结束，不继续重建/重试，不发布未完成的索引 |

## 关键设计依据

1. **GUID+Age 是唯一可靠判据**：三份真实 NTE dump 均按此命中正确构建；文件名、目录名都不可作为判据。
2. **重打包真实存在**：共享目录 EXE 的时间戳/PDB ID 与 dump 相同而映像大小/校验和不同 → 引入 `Repackaged` 级别而不是简单判否。
3. **LoadSymbols 的本地目录语义**：已验证的调用形态是本地目录下有 dump 预期名称的 PDB。已有同名本地 PDB 可以直接使用；来自共享目录或改名候选才需要本地暂存。
4. **符号路径写入只对未来会话生效**：当前会话的兜底就是暂存 + LoadSymbols，两条路径互补。
