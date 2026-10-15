# 安装与配置（0.9.0 Alpha）

## 必需与可选组件

需要 Stardew Valley 1.6 桌面版、SMAPI 4.0+ 和自己的可用模型服务。先确认普通 SMAPI 游戏能进入存档。

AIWorld 自带 F9 聊天不依赖 SocialValley。若需要原版对话与 SocialValley 聊天 UI 互相续聊，请另装 SocialValley；当前反射桥接依据 1.10.1 实现，版本变化可能使桥接失效，原版流程会保留。各第三方模组请按其自身安装说明补全前置。

SVE 可选，加载 `FlashShifter.StardewValleyExpandedCP` 时启用内置人格；没有 SVE 也能玩。The Stardew Squad 不再是正常玩家链路的硬前置。Wizardry/ManaBarAPI 只用于对应观察能力。AIWorld 不要求 Player2。安装包不含第三方 DLL、游戏资源或测试主机。

## 安装和升级

关闭游戏，备份存档及旧 AIWorld 配置/历史，再解压仓库 `releases/AIWorld-0.9.0-alpha.zip` 到 `Mods`。结果应为 `Mods/AIWorld.Demo/manifest.json`，避免多套同 UniqueID 的安装，也不要多套一层文件夹。

升级时替换包内文件，保留自己原来的 `config.json`、历史和报告。不要把源码目录、`AIWorld.RuntimeTestHost`、测试命令或测试信号放进正常 Mods。重新通过 SMAPI 启动并确认日志加载 AIWorld 0.9.0。

## 模型设置

进入存档按 F9 → 模型。使用自己的服务商地址、模型 ID 与 API Key；自定义地址需兼容 Chat Completions JSON 协议。地址允许 HTTPS 或本机 HTTP，不接受带凭据、查询串或片段的 URL。不同服务的协议扩展不保证全部兼容。

默认 DeepSeek 路由使用 `https://api.deepseek.com/chat/completions`，代码回退模型为 `deepseek-flash`；这不是对服务商当前模型可用性的保证，请按账户实际可用 ID 填写。也可通过 `DEEPSEEK_API_KEY` / `DEEPSEEK_MODEL` 环境变量配置。更改环境变量后，必要时重启游戏及启动器。

F9 保存凭据时，Windows 写用户环境变量；自定义服务使用按服务地址派生的 `AIWORLD_KEY_*` 名称。其他系统仅保留当前进程。Key 不写入发布包或配置文件。点击连接测试会真正调用服务，可能计费。

## 对话与费用控制

- F9：选择 NPC 聊天，查看历史和设置。
- F8：在支持的原版对话与 SocialValley 界面间切换；事件、问题、复杂回调及不安全页面不会接管。
- 普通日常台词默认可自动改写。`RewriteDailyDialogue`、`EnableDialogueBridge`、`EnableWorldPerception` 和 `AllowProductionLlm` 可在配置中控制；最后一项关闭 AIWorld 玩家模型服务。
- 村民自动交谈默认最多 4 位参与者、6 回合、8 秒间隔、90 秒冷却、24 次自动调用/游戏日。手动聊天、原版改写、连接测试和重试不属于这项自动预算。
- 时间邀请推荐明确的数字 `HH:mm`。自然语言时间解析不保证六种语言同等覆盖。条件变更需要玩家确认并重新取得 NPC 同意。

## 隐私、语言与故障

配置的模型服务会收到生成所需的角色和游戏上下文。历史报告可含完整提示词与玩家文本，分享前请检查；凭据脱敏不能代替对私人对话的检查。未返回 token 用量的服务显示未知，不当作零消耗。UI 桥接服务默认仅在本机 8617 端口监听，请勿转发到公网。

界面跟随游戏语言，覆盖简中、繁中、英语、日语、韩语、西语；繁中依赖对应语言包和字体。模型语言由提示词要求，无法保证每次遵循；原始日志不会被自动翻译。

遇到问题先查看 SMAPI 日志与 F9 报告，附版本、复现步骤、NPC/地图和脱敏报告。缺少兼容 UI 或模型失败时，不应阻断原版剧情；若其他模组接管 NPC，AIWorld 会放弃或中断相应行动。离线测试不能证明所有实机模组组合均正常。
