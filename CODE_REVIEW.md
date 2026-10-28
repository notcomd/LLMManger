# Mono.LLM.Agent 代码评审报告

> 评审时间：2026-10-25
> 评审范围：`Mono.LLM.Agent`（类库）+ `ChatAppAI`（演示程序），共 51 个源文件 / 7316 行 C#
> 评审方式：全量源码通读 + 编译验证 + 关键缺陷运行复现

---

## 修复记录（2026-10-25）

本节记录评审后当日的修复结果。下方第一至四章的评分与问题清单保留为**修复前基线**，便于对照。

| 编号 | 问题 | 状态 | 处理方式 |
|---|---|---|---|
| P0-1 | 硬编码 API Key | 已修复（源码） | 删除兜底密钥，未设置环境变量时提示并退出；**Git 历史仍含旧密钥，需吊销并改写历史** |
| P0-2 | `AddSkillSystem` 栈溢出 | 已修复 | 注册表工厂内部一次性装载，不再反向解析自身 |
| P0-3 | `AddSkill` 不生效 | 已修复 | 构建期收集到 `SkillRegistrationOptions`，与 `AddSkillSystem` 顺序无关 |
| P1-4 | 工具参数 Schema 丢失 | 已修复 | 自定义 `ToolAIFunction`，输出真实 properties/required/enum |
| P1-5 | 流式工具循环半成品 | 已修复 | 重写为多轮循环，`FunctionCallContent`/`FunctionResultContent` 标准往返 |
| P1-6 | 无自动化测试 | 已修复 | 新增 `Mono.LLM.Agent.Tests`，67 个用例，无需密钥与网络 |
| P1-7 | 惰性序列重试不安全 + options 被污染 | 已修复 | 重试前物化消息、`ChatOptions.Clone()` 后再补默认值 |
| P2-8 | 活跃会话锁外写入 | 已修复 | 统一走 `SetActiveSessionIdInternal` |
| P2-9 | 死代码且与文档矛盾 | 已修复 | `SetActiveSession` 不存在时静默返回，符合接口契约 |
| P2-10 | 编排器异常路径泄漏 | 已修复 | 四处编排器全部改为 `using var` |
| P2-11 | `Task.WhenAll` 丢弃部分成果 | 未修复 | 需引入「部分成功」结果类型，属契约变更，建议单独设计 |
| P2-12 | 每次 new `HttpClient` | 未修复 | 性能优化项，不影响正确性，建议后续接入 `IHttpClientFactory` |
| P2-13 | Ollama 嵌入串行请求 | 未修复 | 已在 README「已知限制」标注 |
| P2-14 | token 估算低估中文 | 已修复 | CJK 按 1:1、拉丁按 4:1 估算 |
| P2-15 | JSON 存储全量读写 | 未修复 | 已在 README「已知限制」标注，建议高并发改用 SQLite |
| P2-16 | 技能文件错误被静默吞掉 | 已修复 | 新增 `LoadErrors` 暴露被跳过的文件与原因 |
| P2-17 | 不可达兜底异常 | 保留 | 已补参数校验，兜底分支语义明确 |
| P2-18 | 重试异常精确类型匹配 | 已修复 | 改为 `IsInstanceOfType`，子类可命中；取消时不再重试 |
| P2-19 | `SkillExecutionContext` 未释放 | 已修复 | 实现 `IDisposable` |
| P2-20 | XML 注释笔误 | 已修复 | 同时清掉全部 12 条 XML 注释警告 |
| P2-21 | 空文件残留 | 已修复 | 已删除 |
| P2-22 | README 空白 | 已修复 | 补齐能力矩阵、五个快速上手示例、架构与限制 |
| P2-23 | LICENSE 未填写 | 已修复 | `Copyright (c) 2026 notcomd` |
| P2-24 | 无打包元数据 / 无 CI | 部分修复 | 已补 `PackageId`/`Version`/`Description`/`RepositoryUrl`、`.editorconfig`、根解决方案；CI 属于仓库级配置，未纳入本次范围 |

验证结果：

```
dotnet build Mono.LLM.Agent.slnx                          → 0 警告, 0 错误
dotnet test  Mono.LLM.Agent.Tests                        → 67 通过, 0 失败
```

---

## 优化记录（2026-10-25 第二轮）

在 P0/P1 问题清零之后，针对「技能加载」与「工具调用」两条主链路做的能力增强。

### 工具调用

| 项 | 优化前 | 优化后 |
|---|---|---|
| 执行超时 | 无，handler 挂住即永久挂住 | `ToolExecutionOptions.Timeout`（默认 30s），超时转为失败结果并回传模型 |
| 参数校验 | 无，缺参在 handler 内抛 `KeyNotFoundException` | 调用前校验必填/类型/枚举，失败不进入 handler，错误信息可直接指导模型纠错 |
| 默认值 | `ToolParameterInfo.DefaultValue` 从未被读取 | 写入 JSON Schema 的 `default`，并在调用时填充缺失的可选参数 |
| 批量执行 | 逐个 `foreach` 串行 | `ExecuteToolCallsAsync`，可配并发度，结果与输入同序 |
| 可观测性 | 无 | `ToolExecuted` 事件 + `ToolResult.Duration` |
| 调用标识 | `ToolResult.ToolCallId` 从未赋值 | 由 `ToolCallRequest.CallId` 透传 |
| 重名注册 | 静默覆盖 | `overwrite: false` 时抛异常，便于启动期发现冲突 |
| 无参工具 | 强制要求模型提供无意义的 `input` | `RegisterSimpleTool(requiresInput: false)` 声明零参数 |
| 自动调用路径 | 绕过校验与超时（直接调 handler） | `AIFunction.InvokeAsync` 与手工调用走同一管线 |

### 技能加载

| 项 | 优化前 | 优化后 |
|---|---|---|
| 内置目录 | 每次 `GetAll()` 新建 10 个对象 | 静态缓存，首次访问构建一次 |
| 文件加载 | 同步 `ReadAllText` 串行 | `LoadSkillsAsync` 并发读取（上限 8），同步 API 保留为包装 |
| 加载校验 | 仅校验 `name` | `name` + `systemPrompt` 必填，失败原因进 `LoadErrors` |
| 多源合并 | 串行、同步 | 各源并行加载，合并仍按注册顺序，「后者覆盖前者」语义不变 |
| 死字段 | `ToolSignature` 从未被任何路径读取 | 标记 `[Obsolete]`，由 `AllowedTools` 取代 |
| 工具可见性 | 全部工具暴露给每个技能 | `AllowedTools` 白名单，未列出的工具既不进提示词也无法执行 |
| 历史管理 | 执行器自建 messages，与 `Conversation` 双份历史 | 统一使用 `Conversation`，支持同上下文多轮 |
| 轮次上限 | 直接返回最后一次带工具的响应 | 关闭工具再请求一次，确保拿到最终答复 |

### 验证结果

```
dotnet build Mono.LLM.Agent.slnx -t:Rebuild              → 0 警告, 0 错误
dotnet test  Mono.LLM.Agent.Tests                       → 118 通过, 0 失败
```

新增 51 个用例，覆盖超时、参数校验、默认值、并行峰值、执行事件、工具裁剪、异步技能加载、字段校验、白名单生效与历史复用。

### 本轮未处理

- 工具重试（幂等工具可考虑，但需要显式的幂等声明，否则会放大副作用）
- 技能热重载（文件变更自动失效缓存，需要引入 `FileSystemWatcher` 与生命周期管理）
- 并行编排的部分失败语义（P2-11，仍待单独设计）

---



## 一、项目定位与技术栈

| 项 | 内容 |
|---|---|
| 定位 | 面向 .NET 的统一 LLM 接入与管理框架（多提供商抽象 + 会话 + 技能 + 多智能体） |
| 目标框架 | `net10.0`，`Nullable=enable`，`ImplicitUsings=enable` |
| 核心依赖 | `Microsoft.Extensions.AI` 10.6.0、`Azure.AI.OpenAI` 2.1.0、`Microsoft.Data.Sqlite` 9.0.0 |
| 编译结果 | 0 警告 / 0 错误（首次构建需还原，耗时较长；`--no-restore` 下 2.3s 通过） |
| 版本管理 | Git，remote 为公开仓库 `github.com/notcomd/LLMManger`，仅 2 个提交 |

**能力清单**（实现程度不一）：

1. 多提供商客户端 —— OpenAI / DeepSeek / Azure OpenAI / Ollama（`LLMClientFactory`）
2. 弹性策略 —— 线性退避重试（`DefaultRetryPolicy`）+ 多提供商故障转移（`FallbackStrategy`）
3. 对话管理 —— 多轮历史、系统提示、上下文裁剪、回退整轮（`ConversationManager`）
4. 会话管理 —— CRUD / 标签 / 搜索 / 活跃会话 / 事件（`SessionManager`）
5. 会话持久化 —— 内存 / JSON 文件 / SQLite 三实现（`ISessionStore`）
6. 工具调用 —— 注册、Schema 生成、多轮工具循环（`ToolManager` + `SkillExecutor`）
7. 技能系统 —— 内置目录、文件/内存/组合来源、Fluent 加载配置
8. 多智能体 —— 顺序 / 并行 / 辩论 / 路由四种编排（`IAgentOrchestrator` 四实现）
9. 流式输出与向量嵌入

---

## 二、架构解析

```
Mono.LLM.Agent/
├── Core/           LLMConfiguration, ILLMClient, LLMClient, LLMClientFactory, ChatResponse
├── Providers/      OllamaChatClient（自实现 IChatClient + IEmbeddingGenerator）
├── Resilience/     IRetryPolicy/DefaultRetryPolicy, FallbackStrategy
├── Conversation/   ConversationManager（历史 / 裁剪 / 回退）
├── Sessions/       ISessionStore × 3 实现, SessionInfo, SessionManager
├── Skills/         SkillDefinition, Registry, Executor, Source × 3, Catalog
├── Tools/          ToolManager, ToolRegistration, ToolResult
├── MultiAgent/     AgentDefinition, AgentTeam, 4 × Orchestrator, PromptBuilder
├── Streaming/      StreamingManager
├── Embedding/      EmbeddingManager（含余弦相似度、TopK 检索）
└── Extensions/     ServiceCollectionExtensions（DI 注册）
```

**设计上做得对的地方：**

- **抽象分层干净**：每个子系统都有接口（`ILLMClient` / `ISessionStore` / `IRetryPolicy` / `ISkillSource` / `IAgentOrchestrator` / `IOrchestrationPromptBuilder`），可替换点明确。
- **提示词外置**：`IOrchestrationPromptBuilder` 把所有编排提示词抽出来，库本身不含领域硬编码文本 —— 这是有经验的设计取舍。
- **组合优于继承**：`CompositeSkillSource` 合并多来源、`SkillLoadingConfig` Fluent 组装，扩展成本低。
- **底层用法正确**：`IChatClient` 适配、`[EnumeratorCancellation]` 流式取消、`FunctionCallContent`/`FunctionResultContent` 的多轮工具循环（`SkillExecutor` 中是写对的）。
- **XML 文档投入巨大**：几乎每个公开成员都有 `summary/param/returns/remarks/exception`，这在同类练手项目中罕见。

**架构上的结构性问题：**

- `SessionManager.cs` 单文件 588 行，其中接口定义占 240 行，职责偏肥（会话 CRUD + LLM 通信 + 事件通知 + 统计）。
- `Skill` 与 `MultiAgent` 边界模糊：`SkillExecutor` 自己维护消息列表却持有 `SkillExecutionContext.Conversation`，后者全程未被使用。
- `StreamingManager` 与 `TToolManager` / `ConversationManager` 的能力重叠，且实现残缺（见 P1-6）。

---

## 三、问题清单

### P0 — 阻断级

#### P0-1 硬编码真实 API 密钥，且已推送至公开仓库

```csharp
// ChatAppAI/Program.cs:17
var deepseekKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
                  ?? "sk-***（已吊销，勿复原）";
```

- 已确认存在于提交 `92cc89c`，remote 为公开库 `https://github.com/notcomd/LLMManger.git`。
- 环境变量兜底写法让问题更隐蔽：本地开发时看着像"正常读环境变量"，实际上密钥已写死在源码里。
- **处置**：立即到 DeepSeek 控制台吊销该 Key → 重新签发 → 从源码删除 → 用 `git filter-repo` 清理历史（仅删除当前文件不够，历史里仍有）。注意：公开仓库已被抓取的可能性很高，吊销是唯一有效动作。

#### P0-2 `AddSkillSystem()` 默认调用必然栈溢出，进程静默死亡

```csharp
// Extensions/ServiceCollectionExtensions.cs:339-350
if (loadCatalog)
{
    services.AddSingleton(sp =>
    {
        var registry = sp.GetRequiredService<ISkillRegistry>();  // ← 解析的就是自己
        ...
        return registry;                                          // 返回类型推断为 ISkillRegistry
    });
}
```

`AddSingleton(sp => ...)` 的 `TService` 被推断为 `ISkillRegistry`，覆盖了上方的 `Add<ISkillRegistry, SkillRegistry>()`。而该工厂内部又 `GetRequiredService<ISkillRegistry>()` → 解析到自己 → 无限递归 → StackOverflow。

**实测结果**（`dotnet run`，非 Debugger 环境）：

```
=== 场景1: AddSkillSystem() 默认参数 ===
进程退出，REAL_EXIT=1，无异常、无堆栈、无日志
```

栈溢出无法被 `try/catch` 捕获，使用者只会看到程序"莫名其妙退出"。这是本文档中危害最高的一条：**DI 注册的标准用法 100% 不可用**。

#### P0-3 `AddSkill(skill)` 注册的技能永不生效

```csharp
// Extensions/ServiceCollectionExtensions.cs:365-375
services.AddSingleton(sp =>
{
    var registry = sp.GetRequiredService<ISkillRegistry>();
    registry.Register(skill);
    return skill;              // 返回 SkillDefinition，注册为 SkillDefinition
});
```

`IServiceCollection` 是懒解析的，没有任何代码会去解析 `SkillDefinition`，所以工厂永不执行，`Register` 永不调用。

**实测结果**：

```
=== 场景3: AddSkill(自定义) 叠加 ===
OK: skills=0        ← 注册了 1 个自定义技能，registry 里仍是 0
```

### P1 — 严重

#### P1-4 工具的多参数能力形同虚设

`ToolManager.GetAITools()`（第 132-162 行）把所有工具统一包装成 `async (string input) => ...`：

```csharp
var aiFunc = AIFunctionFactory.Create(
    async (string input) => { ... },   // 恒定单参数
    tool.Name, enhancedDescription);
```

用户注册的 `ToolParameterInfo`（名称/类型/必填/枚举）全部被丢弃，只是序列化成文本拼进描述里。

**实测结果**：注册 `city` / `date` / `passengers` 三个参数后，暴露给模型的 Schema 为：

```json
{"type":"object","properties":{"input":{"type":"string"}},"required":["input"]}
```

也就是说，`RegisterTool` 与 `RegisterSimpleTool` 最终效果完全相同，前者的核心价值不存在。代码注释称"当前版本 AIFunctionFactory.Create 不支持独立参数定义"，这个判断是错的 —— 该 API 支持 `AIFunctionFactoryOptions`，也可用 `JsonSchema` 直接构造。

#### P1-5 `StreamingManager.StreamWithToolCallHandlingAsync` 是半成品

- 执行完工具后直接 `break`，**没有把工具结果回传模型再取最终答案**，用户永远拿不到工具调用的结论。
- 把工具调用伪造成助手文本 `conversation.AddAssistantMessage($"[Tool Call: {funcName}]")` 写入历史，丢弃了 `FunctionCallContent` 结构 —— 后续请求的消息序列是非法的。

#### P1-6 所谓 `Tests` 不是测试

`ChatAppAI/Tests/*.cs` 六个文件全部是 `partial class Program` 的控制台演示方法：

- 无 xUnit / NUnit / MSTest（全仓库 grep 无任何测试框架引用）
- 无任何断言，靠人眼看输出
- 需要真实 API Key + 联网才能运行
- 无法 `dotnet test`，无 CI 接入可能

**自动化测试覆盖率：0**。文件命名为 `Tests` 具有误导性。

#### P1-7 重试路径对 `IEnumerable` 不安全 + 入参被污染

```csharp
// Core/LLMClient.cs:93-100
return await _retryPolicy.ExecuteAsync(async (ct, attempt) =>
{
    var response = await _chatClient.GetResponseAsync(messages, effectiveOptions, ct);  // messages 被多次枚举
    ...
});
```

公开签名接受 `IEnumerable<ChatMessage>`。`ConversationManager` 内部传的是 `List` 所以幸免，但任何调用方传 `Select(...)` 或 `yield return` 的惰性序列，重试第二次就会拿到空集合。

同时 `ApplyDefaultOptions`（第 154-173 行）直接修改调用方传入的 `ChatOptions` 实例，调用方复用同一 options 对象时会被静默改值。

### P2 — 一般

| # | 位置 | 问题 |
|---|---|---|
| 8 | `SessionManager.cs:420` | `_activeSessionId = sessionId` 在锁外写，与其它路径的加锁写不一致 |
| 9 | `SessionManager.cs:520` | `if (GetContext(sessionId) == null) return;` 是死代码，`GetContext` 只会抛异常；与接口文档"不存在时静默返回"矛盾（实际会抛 `InvalidOperationException`） |
| 10 | 四个 Orchestrator | `client.Dispose()` 不在 `finally` 中，异常路径泄漏 client |
| 11 | `ParallelOrchestrator.cs:91` | `Task.WhenAll` 任一 agent 失败即整体失败，其余 agent 的成果全部丢弃 |
| 12 | `OllamaChatClient.cs:47` | 自建 `new HttpClient()`，未用 `IHttpClientFactory`，无超时配置 |
| 13 | `OllamaChatClient.cs:205-226` | `GenerateAsync` 串行逐条 POST，N 条文本 = N 次 RTT，无并发、无批量 |
| 14 | `ConversationManager.cs:293` | token 估算 `Length / 4`，中文场景严重低估（中文约 1 字 ≈ 1~1.5 token），裁剪阈值失真 |
| 15 | `JsonFileSessionStore.cs:65-83` | 每次 Save 全量读写整文件（同步 IO 在 lock 内），消息累积后接近 O(n²) |
| 16 | `FileBasedSkillSource.cs:100-105` | 捕获 `JsonException`/`IOException` 后静默跳过，坏文件无任何提示 |
| 17 | `FallbackStrategy.cs:94` | `throw new InvalidOperationException` 为不可达代码 |
| 18 | `DefaultRetryPolicy.cs:144` | `_retryableExceptions.Contains(ex.GetType())` 精确匹配，自定义异常子类不生效，应用 `is` |
| 19 | `SkillExecutionContext` | 未实现 `IDisposable`，`CreateContext` 创建的 client 永不释放 |
| 20 | `Skills/SkillRegistry.cs:11` | XML 注释引用 `ConDictionary{TKey,TValue}`，拼写错误（应为 `ConcurrentDictionary`） |
| 21 | `ChatAppAI/src/module/AangetSourl.cs` | 0 字节空文件，文件名拼写错误（AangetSourl） |
| 22 | `README.md` | 全文 11 字节，仅一行 `# LLMManger`，无安装、无示例、无 API 说明 |
| 23 | `LICENSE.txt` | MIT 模板未填写，仍是 `Copyright (c) [year] [fullname]` |
| 24 | 工程化 | 无 CI、无 `.editorconfig`、无包元数据（`PackageId`/`Description`/`Version`），无法 `dotnet pack` 发布 |

---

## 四、评分

### 分维度得分

下表「修复前」对应评审时的状态，「修复后」为 2026-10-25 完成整改后的复评。

| 维度 | 权重 | 修复前 | 修复后 | 依据 |
|---|---:|---:|---:|---|
| 架构设计 | 20% | 80 | **82** | 分层与抽象保持，DI 装配方式修正；SessionManager 偏肥、Skill/MultiAgent 边界未动 |
| 功能完整度 | 18% | 70 | **88** | 工具 Schema、流式工具循环、技能注册三处半成品全部补齐 |
| 代码质量 | 15% | 72 | **85** | 0 警告（含 XML 注释）、死代码清理、消除入参副作用 |
| 正确性 / 健壮性 | 20% | 45 | **88** | 必崩路径修复，67 个用例兜底；缺少端到端集成测试 |
| 安全性 | 10% | 20 | **60** | 源码已清理；**历史提交与公开仓库中的旧密钥仍在，吊销后才算真正闭环** |
| 测试 | 8% | 15 | **85** | 67 个自动化用例覆盖 DI/工具/会话/重试/降级；无覆盖率门禁 |
| 文档 | 5% | 55 | **85** | README 完整、包元数据齐全；缺架构决策记录 |
| 工程化 | 4% | 40 | **60** | 有 `.editorconfig`、根解决方案、可打包；仍无 CI |

**加权综合：56 → 82 / 100**

### 分视角结论

| 视角 | 修复前 | 修复后 |
|---|---:|---:|
| 作为「LLM 接入抽象层的架构草图 / 学习项目」 | 78（B） | **86（A−）** |
| 作为「可被他人依赖的 NuGet 类库」 | 45（D） | **78（B）** |

评分未给到 90 以上的原因：密钥仍需吊销与历史改写、并行编排的部分失败语义未处理、没有 CI 与覆盖率门禁。

---

## 五、行动建议（按优先级）

1. **今天内**：吊销并轮换 DeepSeek API Key（`Program.cs:17`），清理 Git 历史。
2. **本周内**：修 `AddSkillSystem` 的 DI 递归（改为在 `BuildServiceProvider` 前同步注册，或注册 `IHostedService` 初始化器）；修 `AddSkill` 使其立即生效。
3. **重构工具层**：用 `AIFunctionFactoryOptions` 或手写 `AIFunction` + `JsonSchema` 保留真实参数签名 —— 这是整个库最有价值的能力，不该降级。
4. **补测试**：新建 `Mono.LLM.Agent.Tests`（xUnit），至少覆盖 `ToolManager` schema 生成、`ConversationManager` 裁剪/回退、`DefaultRetryPolicy` 次数与异常分类、`SkillRegistry` CRUD、DI 注册可解析性。
5. **补交付物**：README（安装 / 三种快速上手示例 / 能力矩阵）、LICENSE 填名、`.editorconfig`、`dotnet pack` 元数据。
6. **清理**：删空文件、修 XML 注释笔误、把 `client.Dispose()` 移入 `finally`。
