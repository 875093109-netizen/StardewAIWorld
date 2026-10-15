# 开发指南（0.9.0）

## 结构与原则

游戏真实状态是唯一事实源。LLM 生成台词、意愿与有限意图，不能设置位置、日程、工作状态或任意执行代码。新增能力必须同时具备权限定义、前提验证、真实执行器、结果观察和取消/失败恢复。

- `src/AIWorld.Core`：纯状态与契约、上下文预算、结构/语义验证、同意门、行为生命周期、记忆、人格、剧情与本地化资源。
- `src/AIWorld.Adapters`：模型请求与响应适配，凭据/服务配置和错误处理。
- `src/AIWorld.Demo`：SMAPI 入口、游戏主线程快照、原生移动、日程/环境观察、原版 UI 桥接和 F9 控制台。Demo 是现有模组程序集名称，不代表只含测试。
- `tests`：六个离线测试项目；游戏相关生产文件通过测试替身验证。测试替身不是游戏完整模拟器。

玩家聊天进入 `AiWorldChatServer`；`ChatContextAssembler` 组装本人事实与有界环境。模型结果经过结构与语义校验，再由同意/行动链处理。跟随与出行使用 `NativeFollowAdapter`、`NativeTravelAdapter`；恢复由 `ReturnToBaselinePlanner` 处理。`GuardedNpcPath` 与移动规则负责下一格和地图连接校验。

`NpcKnowledgeSource` 读取实际选中日程和本人知识，`NpcJobState` 从可验证执行状态派生当前活动。它是快照而非第二套世界数据库。读取活动不得调用移动 Tick、寻路或传送。`DialogueUiBridge` 管理安全日常台词接管，`VillageChatDirector` 管理有界的村民轮流对话；三种入口都带当前活动。生成期间状态变化时拒收旧结果。

## 构建与离线验证

安装 .NET 8 SDK 和自己的 Stardew Valley 1.6 / SMAPI。游戏程序集从 `GamePath` 引用，不随仓库发布。游戏模组目标 net6.0，纯核心/适配器另提供 net8.0 供测试。

```powershell
dotnet build src/AIWorld.Demo/AIWorld.Demo.csproj -c Release -p:GamePath="D:/Games/Stardew Valley" -p:EnableModDeploy=false
foreach ($suite in 'Core','Knowledge','Movement','Talk','DialogueUi','Perception') {
    dotnet test "tests/AIWorld.$suite.Tests/AIWorld.$suite.Tests.csproj" -c Release --filter 'Category!=Smoke'
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $suite" }
}
```

本版本发布前从独立源码快照构建和执行上述六组测试。常规离线验证不需要 Key、真实存档或付费模型。Smoke/真实服务测试另需明确配置；不要在自动验证时无意触发付费请求。

## 扩展数据与兼容

- `AIWorld/ScheduleActivities`：为游戏现有动作 ID 提供描述。内置值可引用 `i18n:job.*`，未知 ID 不推断工作。
- `AIWorld/JobRules`：精确匹配 NPC、地图、目标格、动作和在场参与者，再细化活动。最多 128 条、每条最多 4 位参与者、半径最多 5 格。数据见 Demo 的 `assets/job-rules.json`。
- `Chat/Personas`、`Chat/Stories`：人格与封存剧情资源。剧情定义不等于已发生事实，必须由观察/完成证据解锁，并限定知情者。
- 环境描述扩展使用现有 `RegisterSource` / `RegisterNativeDescriptor` 接口；只提供有界观察，不获得行动权限。未知模组实体要保守降级。
- `Localization`：六种嵌入式语言资源。新增面向玩家的文案应补齐所有目录，并测试缺失键/语言回退。

不要用巨量时钟规则猜测 NPC 行为。优先读取已选日程、实际动画、控制器和地图事实；仅在缺少游戏语义时增加小范围、证据明确的注释规则。

## 发布与验证边界

用 `scripts/package.ps1` 从 Release 构建目录提取八个运行文件，生成 `AIWorld.Demo` 目录结构的 ZIP。包内不含配置、Key、聊天记录、日志、第三方 DLL、游戏程序集或 RuntimeTestHost。发布时记录 SHA256，并从远端下载校验。

验证层次分开记录：编译通过、离线测试通过、安装文件一致、真实游戏加载、实际模型交互和完整玩法验收各自只能证明对应结论。当前版本不把前两项当作全量实机成功。

后续重点：单机真实场景回归（首日对话、潘妮日程/剧情、约会与恢复、动态地图阻塞、SVE）、UI 分辨率、多语言模型表现、多人及其他平台。完整 pawn 工作系统不属于本版本实现范围。
