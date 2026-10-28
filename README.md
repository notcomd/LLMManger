# Mono.LLM.Agent

面向 .NET 的统一 LLM 接入与管理框架。把「换模型」「加重试」「管会话」「接工具」「组多智能体」这些重复劳动收敛到一套抽象里，业务代码只依赖 `ILLMClient` 和几个管理器。

- 目标框架：`net10.0`
- 核心依赖：`Microsoft.Extensions.AI`、`Azure.AI.OpenAI`、`Microsoft.Data.Sqlite`
- 许可证：MIT

## 能力

| 模块 | 能力 |
|---|---|
| 提供商抽象 | OpenAI、DeepSeek、Azure OpenAI、Ollama（含自实现的 `IChatClient`） |
| 弹性 | 线性退避重试（可配次数/延迟/可重试异常）、多提供商故障转移 |
| 对话 | 多轮历史、系统提示替换、上下文裁剪、整轮回退、思考过程提取与剥离 |
| 会话 | CRUD、标签、搜索、活跃会话、事件通知 |
| 持久化 | 内存 / JSON 文件 / SQLite 三种 `ISessionStore` |
| 工具调用 | 结构化参数 JSON Schema、调用前校验、执行超时、批量并行、执行事件、快照缓存与工具树 |
| MCP | 标准客户端：stdio / Streamable HTTP 传输、OAuth 2.1 授权、工具发现与调用、resources/prompts、变更通知 |
| 技能 | 内置目录缓存、异步文件加载、工具白名单、标准 `SKILL.md`（frontmatter + 渐进式披露 + 捆绑资源）、树形索引与指纹缓存、按输入自动路由、`FileSystemWatcher` 热重载、Fluent 加载配置、DI 注册 |
| 多智能体 | 顺序 / 并行 / 辩论 / 团队 / 路由五种编排，提示词策略可替换 |
| 配置 | 统一 `AgentOptions`：分区声明、JSON 落盘、环境变量取密钥、带路径的校验、运行时映射、DI 一键装配 |
| 其他 | 流式输出、向量嵌入、余弦相似度与 TopK 检索 |

## 构建与测试

```bash
dotnet build Mono.LLM.Agent.slnx
dotnet test  Mono.LLM.Agent.Tests/Mono.LLM.Agent.Tests.csproj
```

测试不需要任何 API Key，也不会发起网络请求。模型行为由夹具回放，落盘与 HTTP 也各有沙箱隔离——详见 [Mono.LLM.Agent.Tests/README.md](Mono.LLM.Agent.Tests/README.md)。

## 演示应用（Harness）

`Harness` 是一个能直接对话的 agent 控制台：连续多轮、按需调用工具、随时切换技能。
它把库的几个子系统串成一条看得见的链路——会话历史决定上下文，工具管理器决定模型能做什么，
技能决定「以什么身份做、能用哪些工具」。

```bash
DEEPSEEK_API_KEY=sk-*** dotnet run --project Harness
```

```
════════════════════════════════════════════════════════════════
  Mono.LLM.Agent  ·  Agent Console
════════════════════════════════════════════════════════════════
  模型      deepseek-chat
  技能      13（内置 10 · 文件 3）
  工具      5
  技能目录  Harness\bin\Debug\net10.0\skills
  上下文    32k tokens，超出后自动裁剪最早的对话

你 > 帮我记住，会议是周三下午三点
   ⚙ remember(key=会议, value=周三下午三点) · 成功 · 2ms
     ↳ 已记住「会议」= 周三下午三点（当前共 1 条）
AI > 好，已经记住会议时间了。

你 > 会议是什么时候？
   ⚙ recall(key=会议) · 成功 · 1ms
     ↳ 周三下午三点
AI > 会议在周三下午三点。

你 > /use 理财精算
✓ 已切换到「理财精算」· 工具 2 个（get_current_time、calc） · 历史保留

你(理财精算) > 10 万本金，年化 3.5%，存两年有多少利息？
   ⚙ calc(expression=100000*0.035*2) · 成功 · 2ms
     ↳ 100000*0.035*2 = 7000
AI > 两年利息 7000.00 元，到期本息合计 107000.00 元。
   · 3.1s · 第 4 轮 · 历史 9 条 · 上下文约 1.2k/32k tokens
```

工具调用、实参、返回值、耗时都直接打印，模型决定了什么、工具做了什么一眼可见。
每轮结束会给出耗时、轮次、历史条数与上下文占用（按字符启发式估算）。

### 会话内命令

| 命令 | 作用 |
|---|---|
| `/help` | 命令列表与技能文件格式 |
| `/skills` | 列出全部技能，`*` 标记当前技能 |
| `/use <技能名>` | 切换技能：替换系统提示，并按白名单收窄工具 |
| `/use off` | 清除技能，恢复默认设定与全部工具 |
| `/tools` | 列出当前可用工具及其参数 |
| `/reload` | 重新扫描技能目录；磁盘变更也会被 watcher 自动同步，输出 `⟳ 技能热更新：+1 ~0 -0` |
| `/history [n]` | 查看最近 n 条历史 |
| `/save [文件]` | 导出会话为 Markdown |
| `/reset` | 清空上下文，保留技能 |
| `/reasoning` | 开/关思考过程显示 |
| `/team <目标>` | 跑一次多智能体团队（协调 → 执行 → 评审） |
| `/exit` | 退出 |

### 内置工具

五个本地工具，都不依赖外部服务，因此演示不需要额外准备环境：

| 工具 | 参数 | 用途 |
|---|---|---|
| `get_current_time` | 无 | 当前本地时间（含时区） |
| `calc` | `expression` | 四则运算，只接受数字与 `+ - * / ( )` |
| `remember` | `key` / `value` | 记入会话便签 |
| `recall` | `key` | 按标签取回 |
| `search_skills` | `query` | 检索技能，让模型知道有哪些角色可用 |

`remember` / `recall` 是刻意成对的：它们让「跨轮记忆」变成可观察的行为——
第一轮写入、第二轮取回，模型必须真的调用工具才能答对，而不是靠上下文猜。

### 技能加载

启动时依次做三件事：装入 10 个内置技能 → 扫描技能目录（`*.skill.json`，含子目录）→
把文件技能合并进同一个注册表。文件与内置同名时以文件为准；删掉文件后重载，只下线文件技能，不会误删内置的。

随程序带了三个示例（`Harness/skills/`）：

```
skills/
├── finance-calc.skill.json      理财精算 · 白名单 calc + get_current_time
├── weekly-report.skill.json     周报助手 · 白名单 remember + recall
└── academic-polish.skill.json   学术润色 · 不限制工具
```

文件格式与 `SkillDefinition` 字段一一对应：

```json
{
  "name": "理财精算",
  "description": "用工具完成本金、利率、期限的精确计算，不做心算",
  "category": "财务",
  "systemPrompt": "你是一名严谨的理财顾问。凡是涉及数字的计算，必须调用 calc 工具……",
  "tags": ["计算", "理财"],
  "version": "1.0",
  "allowedTools": ["calc", "get_current_time"]
}
```

`allowedTools` 是技能级白名单：声明后只有名单内的工具会交给模型，可选工具越少，模型选错的概率越低。
切换技能不会清空历史——换的是角色设定与工具范围，前文仍然记得。

不需要先 `/use`：每轮会按输入自动匹配技能，命中时打印 `⚡ 自动加载技能：xxx`。
`/use <技能名>` 用于强制指定，`/use off` 回到自动模式。

### 启动参数

```bash
dotnet run --project Harness -- --model deepseek-chat --skills ./my-skills --max-tokens 2000
```

| 选项 | 默认 | 说明 |
|---|---|---|
| `-m, --model <id>` | `deepseek-chat` | 模型标识 |
| `-s, --skills <目录>` | `./skills`，不存在则用随程序发布的 | 技能目录 |
| `--max-tokens <n>` | `1200` | 单次回复上限 |
| `-h, --help` | | 显示帮助 |

## 统一配置模型

可配置项原本散在 `LLMConfiguration`、`ToolExecutionOptions`、`SkillValidationOptions`、`McpServerConfig` 等处，逐个拼装容易漏，也没法整体落盘。`AgentOptions` 把它们收成一棵树，并提供校验、转换与装配。

| 分区 | 对应运行时类型 | 关键项 |
|---|---|---|
| `Llm` | `LLMConfiguration` | 提供商、模型、密钥（可指向环境变量）、端点、重试、温度 |
| `Conversation` | `ConversationManager` | 系统提示词、上下文 token 上限 |
| `Sessions` | `ISessionStore` | 内存 / JSON 文件 / SQLite 三选一 |
| `Tools` | `ToolExecutionOptions` | 超时、并发度、参数校验、默认值填充 |
| `Skills` | `ISkillSource` / `SkillValidationOptions` / `SkillToolsetOptions` | 内置目录、JSON 目录、标准目录、校验阈值、按需加载工具 |
| `Mcp` | `McpServerConfig` | 服务器列表、传输、OAuth、超时 |
| `MultiAgent` | `TeamOrchestrator` / `DebateOrchestrator` | 轮次、并发度、执行方式 |

```csharp
var options = new AgentOptions()
    .UseDeepSeek("sk-***", "deepseek-chat")
    .UseStandardSkills("skills")
    .UseSessionStore(SessionStoreKind.Sqlite, "sessions.db")
    .ConfigureTools(tools =>
    {
        tools.Timeout = TimeSpan.FromSeconds(10);
        tools.MaxDegreeOfParallelism = 4;
    });

options.ValidateOrThrow();          // 一次列出所有问题，带字段路径
services.AddMonoLLMAgent(options);  // 客户端 / 工具 / 技能 / 会话 / MCP 一次装配
```

同一份配置也可以放在文件里：

```json
{
  "llm": {
    "provider": "deepSeek",
    "modelId": "deepseek-chat",
    "apiKeyEnvironmentVariable": "DEEPSEEK_API_KEY",
    "retryDelay": "500ms"
  },
  "conversation": { "systemPrompt": "你是一个简洁的技术助手。", "contextTokenLimit": 64000 },
  "sessions": { "store": "sqlite", "filePath": "sessions.db" },
  "tools": { "timeout": "10s", "maxDegreeOfParallelism": 4 },
  "skills": {
    "loadBuiltInCatalog": true,
    "standardDirectories": [{ "path": "skills", "recursive": true }],
    "validation": { "strictNames": true }
  },
  "mcp": {
    "servers": [{
      "name": "filesystem",
      "transport": "stdio",
      "command": "npx",
      "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "D:/work"]
    }]
  },
  "multiAgent": { "maxRounds": 3, "executionMode": "sequential" }
}
```

```csharp
var options = AgentOptions.Load("agent.json");
services.AddMonoLLMAgent(options);
```

时间间隔可写 `30s` / `5m` / `2h` / `500ms` / `1d`，也接受 `00:05:00` 和纯数字（按秒解释）。JSON 允许注释与尾逗号，枚举按字符串读写（`deepSeek`、`stdio`、`sequential`）。

### 校验：一次看全，带路径

`Validate()` 不抛异常，把所有问题收齐再返回：

```
[Error]   llm.modelId: 必须指定模型标识，例如 gpt-4o 或 deepseek-chat。
[Error]   mcp.servers[0].command: stdio 传输必须配置 command。
[Warning] llm.temperature: 温度 3 超出常见范围 0～2，输出可能不可控。
```

路径就是 JSON 里的字段路径，可以直接定位。`ValidateOrThrow()` 供启动期使用；`AddMonoLLMAgent(options)` 内部已经先校验再装配，配置有问题不会走到「运行到一半才报错」。

### 映射与转换

配置和运行时对象之间只有一处映射，不会出现两套字段各写一遍：

| 方法 | 产物 |
|---|---|
| `ToLLMConfiguration()` | `LLMConfiguration` |
| `ToToolExecutionOptions()` | `ToolExecutionOptions` |
| `CreateSkillSources()` | 内置 → JSON 目录 → 标准目录，后加载的覆盖先前的 |
| `CreateSessionStore()` | `InMemorySessionStore` / `JsonFileSessionStore` / `SQLiteSessionStore` |
| `CreateMcpServerConfigs()` | 已启用服务器的 `McpServerConfig`，关闭的自动跳过 |
| `MultiAgent.CreateTeam(...)` / `CreateDebate(...)` | 按配置参数装好的编排器 |

`AddMonoLLMAgent(AgentOptions)` 注册项全部走 `TryAdd`，因此之后仍可替换任意实现。MCP 只装配不连接，启动时仍需 `await manager.StartAsync()`。

声明了 `skills.standardDirectories` 时，装配会顺带做两件事：把 `load_skill` / `read_skill_resource` 挂进工具管理器，并把技能索引拼到系统提示词之后——只列文件加载的技能，内置的十个不进索引。不想要这段索引就把 `injectIndexIntoSystemPrompt` 设为 `false`。

用 `Microsoft.Extensions.Configuration` 绑定时，节名取 `AgentOptions.DefaultConfigurationSection`（`MonoLlmAgent`）：

```csharp
var options = configuration.GetSection(AgentOptions.DefaultConfigurationSection).Get<AgentOptions>()!;
```

## 快速开始

### 1. 基础对话

```csharp
using Mono.LLM.Agent.Core;
using Microsoft.Extensions.AI;

var apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
             ?? throw new InvalidOperationException("请先设置 DEEPSEEK_API_KEY");

var factory = new LLMClientFactory();
using var client = factory.Create(LLMConfiguration.CreateDeepSeek(apiKey));

var response = await client.ChatAsync(new[]
{
    new ChatMessage(ChatRole.System, "你是一个简洁的技术助手。"),
    new ChatMessage(ChatRole.User, "一句话解释什么是向量数据库。")
});

Console.WriteLine(response.Content);
Console.WriteLine($"tokens: {response.InputTokens} in / {response.OutputTokens} out");
```

### 2. 工具调用

注册时声明的参数会原样变成模型可见的 JSON Schema，模型可以直接按字段传参：

```csharp
using Mono.LLM.Agent.Tools;
using Microsoft.Extensions.AI;

var tools = new ToolManager();

tools.RegisterTool(
    toolName: "get_weather",
    description: "查询指定城市的天气",
    parameters:
    [
        new ToolParameterInfo { Name = "city", Type = "string", Description = "城市名", Required = true },
        new ToolParameterInfo
        {
            Name = "unit",
            Type = "string",
            Description = "温度单位",
            Required = false,
            EnumValues = ["celsius", "fahrenheit"]
        }
    ],
    handler: (args, ct) =>
    {
        var city = args.GetProperty("city").GetString();
        return Task.FromResult(new ToolResult { Name = "get_weather", Result = $"{city} 晴，24°C" });
    });

var options = new ChatOptions { Tools = tools.GetAITools() };
var reply = await client.ChatAsync(
    [new ChatMessage(ChatRole.User, "上海现在多少度？")],
    options);
```

生成的 Schema：

```json
{
  "type": "object",
  "properties": {
    "city": { "type": "string", "description": "城市名" },
    "unit": { "type": "string", "description": "温度单位", "enum": ["celsius", "fahrenheit"] }
  },
  "required": ["city"],
  "additionalProperties": false
}
```

### 工具执行策略

`ToolManager` 在调用 handler 之前会走一条完整的执行管线：

```csharp
var tools = new ToolManager(new ToolExecutionOptions
{
    Timeout = TimeSpan.FromSeconds(10),   // 超时转为失败结果，不会拖死整个请求
    ValidateArguments = true,             // 必填 / 类型 / 枚举校验，失败不调用 handler
    ApplyDefaultValues = true,            // 缺失的可选参数按 DefaultValue 填充
    MaxDegreeOfParallelism = 1            // 1 = 串行（默认），> 1 时批量并行
});
```

校验失败时模型收到的是可操作的信息，而不是堆栈：

```
参数校验未通过：缺少必填参数 'city'，类型 string（城市名）。参数 'unit' 的取值必须是 celsius / fahrenheit 之一，实际为 'kelvin'。
```

需要观测执行情况时订阅事件（订阅方异常不会影响工具结果）：

```csharp
tools.ToolExecuted += (_, e) =>
    logger.LogInformation("tool={Tool} ok={Ok} cost={Cost}ms", e.ToolName, e.IsSuccess, e.Duration.TotalMilliseconds);
```

批量执行多个调用，结果与输入同序：

```csharp
var results = await tools.ExecuteToolCallsAsync(calls, maxDegreeOfParallelism: 4);
```

只读工具适合开并行；下单、发消息这类有副作用的工具建议保持串行。

`GetAITools()` 的结果按注册表版本缓存，Schema 生成与包装对象只在工具增删后重建一次；
`GetToolTree()` 按工具名前缀（MCP 的 `server__tool`）给出工具树，没有前缀的归入「通用」。
两者都以 `ToolManager.Version` 为失效依据。

无参工具（如「获取当前时间」）应显式声明 `requiresInput: false`，否则 schema 会凭空要求模型提供一个 `input`：

```csharp
tools.RegisterSimpleTool("get_current_time", "获取当前时间",
    _ => Task.FromResult(DateTime.Now.ToString("u")), requiresInput: false);
```

### 3. 依赖注入

```csharp
using Mono.LLM.Agent.Extensions;
using Mono.LLM.Agent.Skills;

services.AddMonoLLMAgent();
services.AddLLMClient(config);                 // 同时注册为默认客户端
services.AddSessionManager(config);            // 默认内存存储
services.AddToolManager();
services.AddSkillSystem(loadCatalog: true)
        .AddSkill(new SkillDefinition
        {
            Name = "weekly-report",
            Description = "把零散记录整理成周报",
            SystemPrompt = "你是一名严谨的技术负责人…"
        });

// 需要落盘时切换存储实现
services.AddSessionManager(new JsonFileSessionStore("sessions.json"), config);
```

`AddSkillSystem` 与 `AddSkill` 的调用顺序无关，两者都会生效。

### 技能级工具白名单

默认情况下技能能看到全部已注册工具。声明 `AllowedTools` 后只暴露名单内的工具：

```csharp
var skill = new SkillDefinition
{
    Name = "weather-brief",
    SystemPrompt = "你是一名气象播报员…",
    AllowedTools = ["get_weather", "get_forecast"]
};
```

可选工具从几十个降到两三个，模型选错的概率明显下降，提示词也短了。名单里写了但尚未注册的工具会在创建上下文时记录警告，便于发现拼写错误。

文件式技能同样支持：

```json
{ "name": "weather-brief", "systemPrompt": "…", "allowedTools": ["get_weather"] }
```

### 标准技能（SKILL.md）

`*.skill.json` 把说明全文塞进一个字段，技能一多就全量常驻上下文。库另外实现了 Agent Skills 标准：
技能是一个目录，`SKILL.md` 用 YAML frontmatter 声明元数据、正文写操作说明，附属文件按约定放在子目录里。

```
skills/
└── pdf-processing/
    ├── SKILL.md
    ├── scripts/extract.py
    ├── references/REFERENCE.md
    └── assets/template.docx
```

```markdown
---
name: pdf-processing
description: 提取 PDF 文本与表格，填写表单。用于任何 .pdf 文件的处理任务。
license: MIT
allowed-tools: [read_file, run_shell]
metadata:
  category: 文档
  tags: pdf, 抽取
---

# PDF 处理

1. 先用 `scripts/extract.py` 抽取文本，不要手写解析逻辑。
2. 需要字段说明时读 `references/REFERENCE.md`。
```

加载：

```csharp
var source = new StandardSkillSource("skills");
var skills = await source.LoadSkillsAsync();

foreach (var d in source.Diagnostics)
    Console.WriteLine(d);   // 坏文件不中断整批加载，原因保留在这里
```

加载结果中 `Description` 是第一层，`Body` 是第二层，`Resources` 是第三层。**三层不会一起进入上下文**——
这是标准技能与「把所有提示词拼进系统提示」的本质差别。

#### 渐进式披露

`SkillToolset` 把注册表暴露成两个工具，由模型自己决定何时加载：

```csharp
var toolset = new SkillToolset(registry);
toolset.RegisterTools(tools);

var conversation = new ConversationManager(client, toolset.BuildSystemPrompt("你是一个可以调用工具的助手。"));
```

- 系统提示词里只有技能名与一句话描述；
- 模型调用 `load_skill("pdf-processing")` 后，正文作为工具结果进入对话；
- 模型调用 `read_skill_resource("pdf-processing", "references/REFERENCE.md")` 读取单个资源。

十个技能常驻上下文只是十行索引，而不是十段说明。`SkillActivated` 事件可以观测模型何时加载了什么。

也可以不用工具、由应用直接激活，复用 `SkillExecutor` 的多轮工具循环：

```csharp
// 正文作为系统提示词；读取资源仍需按上面的方式挂上 read_skill_resource
var context = executor.CreateContext(registry.Get("pdf-processing")!, config, tools);
```

#### 自动加载（不需要 /use）

`SkillRouter` 在每轮开始前按输入匹配技能，命中就把正文注入系统提示词：

```csharp
var router = new SkillRouter(registry);
var plan = router.PlanTurn(userInput, "你是一个助手。");

conversation.SetSystemPrompt(plan.SystemPrompt);   // 基础提示 + 树形索引 + 命中技能的正文
Console.WriteLine(plan);                          // 已激活：pdf-processing
```

匹配只看名称、描述、分类、标签的词元覆盖度（中文二元切分、英文按词），不调用模型，
因此每轮跑也不影响首字延迟。阈值与上限可调：

```csharp
new SkillRouter(registry, options: new SkillRoutingOptions
{
    MaxSkills = 2,
    MinScore = 0.15,
    MaxInstructionCharacters = 8000   // 正文总预算，超出截断
});
```

树与索引走缓存，注册表内容不变就不会重建：

```csharp
var cache = new SkillTreeCache();
var tree = cache.Get(registry);      // 名称/描述/正文/分类/标签任一变化才失效
Console.WriteLine(tree.Render());    // 分类一层、技能一层
```

`AgentOptions` 里对应的开关是 `skills.autoActivate` / `maxAutoSkills` / `autoActivateThreshold` /
`maxAutoInstructionCharacters`；装配后 `SkillRouter` 与 `SkillTreeCache` 都是单例。
两种方式可以并存：自动路由没命中时，模型仍能按索引调用 `load_skill`。

#### 热重载

`SkillHotReloader` 用 `FileSystemWatcher` 监视技能目录，文件增删改后自动同步注册表：

```csharp
using var reloader = new SkillHotReloader(
    registry,
    directories: ["skills"],
    fileSources: [new StandardSkillSource("skills")],
    options: new SkillHotReloadOptions { Debounce = TimeSpan.FromMilliseconds(300) });

reloader.Reloaded += (_, result) => Console.WriteLine($"技能已更新：{result}");
reloader.Start();
```

配置里开启后由依赖注入装配，解析即启动：

```csharp
var options = new AgentOptions().UseOllama("llama3");
options.Skills.WatchForChanges = true;
options.UseStandardSkills("skills");
services.AddMonoLLMAgent(options);

provider.GetRequiredService<SkillHotReloader>();   // 解析后开始监视，退出时随容器释放
```

几个实现取舍：

- **全量重读 + 指纹对账**，不做增量状态机。技能规模小，正确性优先；内容没变的文件不会算作「更新」。
- 文件技能覆盖了同名内置技能时，删除文件会**还原**内置定义，而不是把它一起删掉。
- 来源整体加载失败（目录被删、权限变化）时**不清空注册表**——那通常是瞬时故障，只记诊断等下一次事件重试；单个坏文件才会让该技能下线，原因写进诊断。
- 默认防抖 300ms；`SKILL.md` / `*.skill.json` 的内容改动才触发，资源文件的**新增/删除**也算（资源清单要重算），资源内容改动不算。
- 树缓存不用手动失效：指纹覆盖了正文、描述、资源清单，重载后下一次读取自动重建。

#### 校验与诊断

`SkillValidator` 按规范检查 `name`（≤64 字符）、`description`（≤1024 字符，且要说明「何时使用」）、
正文行数（建议 ≤500 行，超出应把细节移到 `references/`）。默认允许中文名；
`new SkillValidationOptions { StrictNames = true }` 时强制 `^[a-z0-9]+(-[a-z0-9]+)*$`，适合对外分发技能包。

诊断分三级：`Error` 拒绝加载，`Warning` 照常加载但值得修，`Info` 仅提示。
每条都带 `SkillDiagnosticCode`，调用方按代码判断，不必匹配错误文本。

#### 资源读取

资源路径来自模型给出的工具参数，按不可信输入处理：绝对路径、`..` 逃逸、盘符跳转都会被拒绝；
单次读取默认上限 256 KB，超出截断并告知模型——一个 10 MB 的日志塞进上下文会直接挤掉真正的对话内容。

#### 与 JSON 技能共存

两种格式可以叠加，同名时后添加的覆盖先前的：

```csharp
services.AddSkillSystem(loadCatalog: false);
services.AddStandardSkills("skills");          // 标准目录

var config = new SkillLoadingConfig()
    .UseBuiltIn()
    .UseDirectory("legacy-skills")             // *.skill.json
    .UseStandardDirectory("skills");           // SKILL.md
```

`SkillToolset` 对两种来源一视同仁：JSON 技能没有 `Body` 时，加载结果里回退到 `SystemPrompt`。

### 4. 多智能体

```csharp
using Mono.LLM.Agent.MultiAgent;

var team = new AgentTeam(factory)
    .AddMembers([architect, developer, reviewer])
    .UseSequential();

var result = await team.RunAsync("设计一个带限流的短链服务");

Console.WriteLine(result.FinalOutput);
Console.WriteLine($"tokens={result.TotalTokens}, 耗时={result.Duration.TotalSeconds:F1}s");
```

五种模式：

| 模式 | 方法 | 适用场景 |
|---|---|---|
| 顺序 | `UseSequential()` | 流水线固定、后一步依赖前一步 |
| 并行 | `UseParallel()` + `WithLeader()` | 多视角独立评估同一对象 |
| 辩论 | `UseDebate(rounds)` + `WithJudge()` | 立场对立、需要论证与裁判 |
| 团队 | `UseTeam(maxRounds)` + `WithLeader()` / `WithJudge()` | 任务可拆分且需要多轮收敛 |
| 路由 | `RouterOrchestrator` | 先判定任务类型再派给对应专家 |

编排过程中的提示词由 `IOrchestrationPromptBuilder` 提供，可用 `WithPromptBuilder` 完全替换。

#### 团队模式

适合「做一个完整方案」这类无法一次成型的目标。每一轮走四个阶段：

```
规划 ── 协调者结合历史与评审意见，拆解出带指派对象的子任务
  ↓
执行 ── 子任务并行下发，成员各自完成后写入共享工作区
  ↓
汇总 ── 工作区中的产出拼接为阶段性成果
  ↓
评审 ── 判定是否完成；未完成则给出下一轮聚焦点，回到规划
```

```csharp
var team = new AgentTeam(factory)
    .AddMembers([architect, developer, writer])
    .WithLeader(coordinator)   // 负责拆解与最终整合，未设置则用第一个成员
    .WithJudge(reviewer)       // 负责判定是否完成，未设置则由协调者兼任
    .UseTeam(maxRounds: 3);    // 默认并行；评审确认完成会提前结束

var result = await team.RunAsync("为我们的 API 写一份对外开放文档");

Console.WriteLine(result.FinalOutput);

// 想复盘团队是怎么收敛的：
foreach (var round in result.TeamRounds)
{
    Console.WriteLine($"第 {round.Round} 轮：{round.Plan.Count} 个子任务，完成={round.Completed}");
    Console.WriteLine($"  理由：{round.Reason}");
    if (!string.IsNullOrEmpty(round.NextFocus))
        Console.WriteLine($"  下轮重点：{round.NextFocus}");
}
```

**共享工作区**：每个成员执行完立刻把自己的产出写进 `TeamWorkspace`，工作区跨轮累积。

```csharp
// 执行模式二选一
.UseTeam(maxRounds: 3)                                     // 并行（默认）
.UseTeam(maxRounds: 3, TeamExecutionMode.Sequential)       // 串行
```

- **并行**（默认）：适合彼此独立的子任务。并发度默认 4，可通过 `TeamOrchestrator` 的 `maxDegreeOfParallelism` 调整。成员启动时拿到的是当时的工作区快照，同轮其他成员尚未写入的内容不在其中——但因为每个人都会写回工作区，评审与下一轮能读到全量，信息不会丢。
- **串行**：后执行者能看到此前所有成员的产出，适合同一个子任务必须基于前一个的具体结果。代价是耗时随子任务数线性增长。

写入是**追加**语义：同一成员在一轮里承担多个子任务时，多次产出会拼接而不是相互覆盖。

几个实现上的取舍：

- **协议用标记行而非 JSON**：模型输出长段落时 JSON 的引号与换行极易转义出错，`### TASK` / `ASSIGNEE:` / `DONE:` 这类标记即使被包在代码块或夹带解释文字也能稳定解析。
- **解析失败不空转**：规划不可解析时退化为「每个成员各做一次原任务」；指派对象不存在时按成员轮转分配，子任务不会被丢弃。
- **协调者与评审者不挂工具**：规划、评审、整合属于元认知环节，给它们工具只会诱导模型去做执行成员该做的事。
- **阶段汇总由程序拼接**，不额外调用模型；只有最终答复由协调者整合一次。

### 5. 流式 + 工具循环

```csharp
using Mono.LLM.Agent.Conversation;
using Mono.LLM.Agent.Streaming;

var conversation = new ConversationManager(client, "你是一个助手。");
var streaming = new StreamingManager(client);

var full = await streaming.StreamWithToolCallHandlingAsync(
    conversation,
    tools,
    onToken: Console.Write,
    maxToolRoundtrips: 5);
```

方法内部会完成「模型请求工具 → 执行 → 结果回填 → 继续生成」的完整循环，直到模型给出最终文本。

### 6. 思考过程（推理型模型）

DeepSeek-R1、o 系列、QwQ 这类模型会先产出推理过程再给答案。库把它和正文分开存放，不会混在一起：

```csharp
var response = await client.ChatAsync([new ChatMessage(ChatRole.User, "9.11 和 9.9 哪个大")]);

Console.WriteLine($"【思考】{response.Reasoning}");
Console.WriteLine($"【答复】{response.Content}");
```

流式场景用 `onReasoning` 收思考、`onToken` 收正文：

```csharp
var streaming = new StreamingManager(client);

await streaming.StreamAndCollectAsync(
    messages,
    onToken: t => Console.Write(t),
    onReasoning: r => Console.Write(r));   // 两者不会重复
```

两个约定值得注意：

- **思考过程不会被写进会话历史**。DeepSeek 等厂商要求下一轮请求不得回传 reasoning，`ConversationManager.AddMessage` 会自动剥离；只含思考、没有正文的消息整条丢弃。想留档请从 `ChatResponse.Reasoning` 或 `RawMessage` 取。
- **Ollama 通道已适配** `message.thinking`。非流式与流式都会映射为 `TextReasoningContent`，不会在 JSON 解析阶段丢掉；请求侧也不会把这个字段发出去。

非推理模型不返回该内容，此时 `Reasoning` 恒为 `null`。

## MCP 客户端

内置符合 MCP 规范的客户端，可以把任意标准 MCP 服务器的工具挂到 `ToolManager` 上。
模型看到的就是普通函数——它不知道也不关心这个工具跑在另一个进程或另一台机器上。

| 能力 | 覆盖情况 |
|---|---|
| 传输 | stdio（子进程）、Streamable HTTP（POST + SSE、`Mcp-Session-Id`、可选服务端消息流） |
| 握手 | `initialize` 能力协商、协议版本校验（2025-06-18 / 2025-03-26 / 2024-11-05） |
| 工具 | `tools/list`（自动翻页）、`tools/call`、内容块渲染、`isError` 映射 |
| 资源 | `resources/list`、`resources/templates/list`、`resources/read`、订阅与变更通知 |
| 提示词 | `prompts/list`、`prompts/get` |
| 其他 | `ping`、`logging/setLevel`、日志与进度通知、`notifications/cancelled` |
| 授权 | OAuth 2.1：资源元数据发现、动态客户端注册、授权码 + PKCE（S256）、令牌缓存与自动刷新 |
| 服务端请求 | `sampling/createMessage`、`roots/list`、`elicitation/create`（需显式提供回调） |

### 接入一个服务器

```csharp
using Mono.LLM.Agent.Mcp;
using Mono.LLM.Agent.Tools;

var server = new McpServerConfig
{
    Name = "filesystem",
    Transport = McpTransportKind.Stdio,
    Command = "npx",
    Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "D:/work"]
};

var tools = new ToolManager();
var manager = new McpClientManager([server], tools);

await manager.StartAsync();

// 模型现在可以调用 filesystem__read_file 等工具
var options = new ChatOptions { Tools = tools.GetAITools() };
```

模型看到的工具名是 `filesystem__read_file`：前缀取自服务器名，避免多个服务器的同名工具互相覆盖。

HTTP 服务器：

```csharp
var remote = new McpServerConfig
{
    Name = "search",
    Transport = McpTransportKind.StreamableHttp,
    Endpoint = new Uri("https://mcp.example.com/rpc"),
    Headers = { ["Authorization"] = "Bearer ..." },   // 静态令牌，或改用下面的 OAuth
    EnableServerStream = true                          // 需要接收 sampling / roots 请求时开启
};
```

### OAuth 授权

远程服务器普遍要求 OAuth 2.1。填一个 `OAuth` 配置即可，其余交给客户端：

```csharp
var remote = new McpServerConfig
{
    Name = "search",
    Transport = McpTransportKind.StreamableHttp,
    Endpoint = new Uri("https://mcp.example.com/rpc"),
    OAuth = new McpOAuthOptions
    {
        Scopes = ["mcp.read", "mcp.write"]
    }
};

await using var manager = new McpClientManager([remote], tools);
await manager.StartAsync();   // 首次会打开浏览器让用户授权
```

首次连接会自动完成这条链路：

1. **发现** —— 读 `401` 的 `WWW-Authenticate`（`resource_metadata`），或按 RFC 9728 探测
   `/.well-known/oauth-protected-resource`，拿到授权服务器地址；再按 RFC 8414 取授权服务器元数据，
   取不到则回退 OIDC Discovery；
2. **动态注册** —— 按 RFC 7591 提交回调地址与授权类型，拿 `client_id`（也可在配置里给预注册的 `ClientId`）；
3. **授权码 + PKCE** —— 起一个 `127.0.0.1:{随机端口}` 的回调服务、打开浏览器，
   用 S256 挑战换回 `code`，并校验 `state` 防 CSRF；
4. **换令牌** —— 令牌请求带 `resource`（RFC 8707），使令牌只对当前 MCP 服务器有效；
5. **缓存与刷新** —— 令牌与 `client_id` 一起落盘（默认
   `%LOCALAPPDATA%\Mono.LLM.Agent\mcp-oauth`，类 Unix 上权限收紧到 `600`），
   之后的请求直接复用；收到 `401` 时刷新一次令牌并重试，刷新令牌失效才重新走浏览器授权。

需要预注册客户端（企业内部常见）时：

```csharp
OAuth = new McpOAuthOptions
{
    ClientId = "my-client",
    ClientSecret = "…",                       // 公开客户端留空
    AuthorizationServer = new Uri("https://auth.example.com"),  // 跳过资源元数据探测
    Scopes = ["mcp.read"]
}
```

无图形环境（CI、服务器）可以换成自己的交互实现，或把授权地址交给用户处理：

```csharp
OAuth = new McpOAuthOptions
{
    OpenBrowser = false,
    Interaction = new DelegateOAuthInteraction(
        new Uri("http://127.0.0.1:4200/callback"),
        async (redirectUri, authorizationUrl, ct) =>
        {
            Console.WriteLine($"请访问：{authorizationUrl}");
            var callback = await WaitForUserInputAsync(ct);   // 用户回填完整回调地址
            return new Uri(callback);
        })
}
```

令牌存储可以替换：实现 `IMcpOAuthStore` 即可对接系统钥匙串或密钥管理服务；
测试里用 `InMemoryMcpOAuthStore` 避免写盘。

依赖注入：

```csharp
services.AddToolManager();
services.AddMcpServers(server, remote);

// 启动阶段：连接是异步的，不要在注册阶段阻塞
var manager = provider.GetRequiredService<McpClientManager>();
await manager.StartAsync();
```

### 协议保真

MCP 的 `inputSchema` 是任意 JSON Schema，而 `ToolParameterInfo` 只能描述扁平参数。
桥接层因此走 `ToolManager.RegisterRawTool` 原样透传 Schema：嵌套对象、数组元素、`oneOf`
都不会被压平。代价是这类工具跳过本地参数校验——本地校验器理解不了嵌套结构，
交给服务端校验并返回结构化错误更准确。

工具返回的内容块按以下规则转成文本：文本块原样保留；图片/音频只给
`[image: image/png, 12 KB]` 这样的摘要而不塞 base64（否则一次调用就能吃光上下文）；
内嵌文本资源带 URI 与正文；完全没有文本块时退回 `structuredContent` 的 JSON。

### 服务端通知

```csharp
manager.Connections[0].Client!.ListChanged += (_, e) =>
    Console.WriteLine($"{e.ServerName} 的 {e.Kind} 列表变了");
```

`McpServerConfig.AutoRefreshTools`（默认开启）会在收到 `tools/list_changed` 后自动重新同步工具注册，
模型不会调用到已经下线的工具。

### 直接使用客户端

不需要桥接时，可以单独使用 `McpClient`：

```csharp
var client = new McpClient(new StdioMcpTransport(new McpStdioTransportOptions
{
    Command = "npx",
    Arguments = ["-y", "@modelcontextprotocol/server-everything"]
}));

await client.ConnectAsync();

foreach (var tool in await client.ListToolsAsync())
    Console.WriteLine($"{tool.Name}: {tool.Description}");

var result = await client.CallToolAsync("echo", JsonNode.Parse("""{"message":"hi"}"""));
Console.WriteLine(McpContentFormatter.ToText(result));
```

尚未实现的部分：JSON-RPC 批量（2025-06-18 已从规范移除）、`completion/complete`、
Client ID Metadata Document、设备码授权与 DPoP/mTLS。
这些都不影响工具调用主链路。

## 代码组织

类库按功能分目录，每个功能目录内部再按职责分五类。**命名空间不随子目录变化**（仍是 `Mono.LLM.Agent.Skills` 这类），因此这套分层是纯物理整理，不影响任何调用方。

| 子目录 | 放什么 |
|---|---|
| `Module/` | 数据模型、选项、结果对象、枚举 |
| `Interfaces/` | 接口定义 |
| `Implementations/` | 服务与具体实现 |
| `Extensions/` | 依赖注入注册、Fluent 配置 |
| `Utils/` | 解析、校验、格式化等无状态工具 |

只创建有内容的分层，不建空目录。每个 `.cs` 文件头部有统一注释，写明用途、实现方、调用方、开发时间与版本：

```csharp
// ============================================================================
// 文件：ILLMClient.cs
// 用途：定义与大语言模型交互的统一客户端接口。
// 实现方：LLMClient（OpenAI / DeepSeek / Azure / Ollama 共用）；测试夹具中的脚本化客户端
// 调用方：LLMClientFactory、ConversationManager、SkillExecutor、StreamingManager、EmbeddingManager、SessionManager
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================
```

```
Mono.LLM.Agent/
├── Core/
│   ├── Module/            LLMConfiguration、ChatResponse、LLMProviderType
│   ├── Interfaces/        ILLMClient
│   └── Implementations/   LLMClient、LLMClientFactory
├── Providers/              OllamaChatClient（Implementations）、DTO（Module）
├── Resilience/             DefaultRetryPolicy、FallbackStrategy
├── Conversation/           ConversationManager
├── Sessions/
│   ├── Module/             SessionInfo
│   ├── Interfaces/         ISessionStore
│   └── Implementations/    三种存储 + SessionManager
├── Tools/                  Module / Implementations / Utils
├── Mcp/                    Module / Interfaces / Implementations / Utils / Extensions
├── Skills/                 Module / Interfaces / Implementations / Utils / Extensions
├── MultiAgent/             Module / Interfaces / Implementations / Utils
├── Streaming/              StreamingManager
├── Embedding/              EmbeddingManager
├── Configuration/          Module（AgentOptions 等）/ Utils / Extensions
└── Extensions/             ServiceCollectionExtensions（库级 DI 扩展）

Mono.LLM.Agent.Tests/   xUnit 用例与夹具沙箱
Harness/                agent 演示应用（对话 / 工具 / 技能 / 团队）
```

可替换点：`ILLMClient`、`ISessionStore`、`IRetryPolicy`、`ISkillRegistry`、`ISkillSource`、`IAgentOrchestrator`、`IOrchestrationPromptBuilder`。

## 配置与安全

API Key 一律通过环境变量或密钥管理服务注入，源码中不得出现明文密钥：

```bash
# Windows（新开终端生效）
setx DEEPSEEK_API_KEY "sk-xxxx"
# macOS / Linux
export DEEPSEEK_API_KEY='sk-xxxx'
```

`Harness` 只从该变量读取密钥；未设置时会打印设置指引并退出。

## 已知限制

- `ToolManager.RegisterTool` 的参数只支持一层对象；需要嵌套结构时用 `RegisterRawTool` 直接给完整 JSON Schema（MCP 工具走的就是这条路）。
- `JsonFileSessionStore` 每次保存都会全量读写文件，适合单机与小规模场景；高并发请改用 `SQLiteSessionStore` 或自实现 `ISessionStore`。
- `OllamaChatClient` 的嵌入接口按条串行请求，批量向量化建议在应用层并发编排。
- 上下文裁剪使用字符启发式估算 token（CJK 按 1:1，拉丁按 4:1），并非分词器精确计数。
- 工具超时通过取消令牌传递，handler 若不响应令牌则无法被真正中断。
- 技能白名单只控制「暴露哪些工具」，不做参数级或数据级权限控制。
- 标准技能的 frontmatter 解析只覆盖实际用到的 YAML 子集（标量、引号、块标量、行内/行列表、一层映射），
  复杂 YAML 结构不会生效；资源按 UTF-8 文本读取，图片、字体等二进制文件只能拿到乱码。
- 技能内容加载后驻留内存，修改 `SKILL.md` 需要重新加载；开启 `watchForChanges` 后由 `FileSystemWatcher` 自动完成。
  `FileSystemWatcher` 在网络盘、容器 bind mount、WSL 跨文件系统时可能收不到事件，此时退回 `/reload`
  或手动调用 `SkillHotReloader.ReloadAsync()`；监视启动时已不存在的目录不会被补挂。
- MCP 客户端不实现 JSON-RPC 批量、`completion/complete`、Client ID Metadata Document 与设备码授权；
  OAuth 令牌默认落盘保存，对安全要求更高时应替换 `IMcpOAuthStore`（例如对接系统钥匙串）。

## License

MIT，见 [LICENSE.txt](LICENSE.txt)。
