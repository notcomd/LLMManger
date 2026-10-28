# 测试夹具（Test Harness）

这层夹具解决三件在测试里反复出现的事：假模型代码到处复制、落盘测试互相污染、并行断言随调度随机失败。

## 目录

```
Harness/
├── AgentSandbox.cs   单个测试的隔离环境 + xUnit 基类 SandboxedTest
├── AgentHarness.cs   Agent 定义、模型脚本、客户端工厂
├── ModelScript.cs    规则回放、工具调用模拟、调用记录
├── FileSandbox.cs    独立临时目录
└── HttpSandbox.cs    出站 HTTP 拦截与记录

Scenarios/            端到端场景
Core/ Conversations/ ...  按模块划分的单元测试
```

## 快速上手

继承 `SandboxedTest`，测试结束时沙箱自动释放：

```csharp
public class MyTests : SandboxedTest
{
    [Fact]
    public async Task 团队两轮收敛()
    {
        var coordinator = Sandbox.Agents.Define("协调", "拆解与整合");
        var member = Sandbox.Agents.Define("执行");

        Sandbox.Agents.Script("协调")
            .WhenContains("整合成一份完整的最终答复", "最终方案")
            .WhenContains("拆解", """
                ### TASK
                ASSIGNEE: 执行
                做事
                """);

        // 按调用次序返回：第一次不通过，第二次通过
        Sandbox.Agents.Script("评审").Sequence(
            ScriptedReply.Say("DONE: false\nREASON: 缺数据\nNEXT: 补数据"),
            ScriptedReply.Say("DONE: true\nREASON: ok\nNEXT:"));

        Sandbox.Agents.Script("执行").Fallback("产出");

        var team = new AgentTeam(Sandbox.Agents.CreateFactory())
            .AddMembers([member])
            .WithLeader(coordinator)
            .UseTeam(maxRounds: 2);

        var result = await Sandbox.RunAsync(() => team.RunAsync("做个方案"));

        Assert.Equal(2, result.TeamRounds.Count);
        Assert.Equal(2, Sandbox.Agents.ScriptOrNull("执行")!.CallCount);
    }
}
```

## 四个沙箱各管什么

| 组件 | 职责 | 关键 API |
|---|---|---|
| `AgentHarness` | 定义 Agent、绑定模型脚本、产出工厂 | `Define` / `Script` / `CreateFactory` |
| `ModelScript` | 按提示词规则回放、模拟工具调用、记录调用 | `WhenContains` / `Sequence` / `Fallback` / `Calls` |
| `FileSandbox` | 每测试独立目录，退出即删，拒绝路径逃逸 | `Write` / `Read` / `GetPath` / `ListFiles` |
| `HttpSandbox` | 拦截出站请求，返回预设响应并留痕 | `RespondWith` / `RespondWithSequence` / `Requests` |
| `AgentSandbox` | 聚合以上资源，统一超时与释放 | `Agents` / `Files` / `Http` / `Services` / `RunAsync` |

## 三条约定

**一、模型只回放，不联网。** `AgentHarness.CreateFactory()` 返回的工厂不会创建真实客户端。任何需要网络的测试都必须显式用 `HttpSandbox.CreateClient()`。

**二、多轮匹配看完整上下文。** 规则匹配用的是「用户消息 + 此前工具结果」拼成的 transcript，所以同一句话在第一轮和拿到工具结果后可以给出不同回复：

```csharp
Sandbox.Agents.Script("分析师")
    .WhenContains("1200万", "三季度销售额 1200 万。")   // 拿到工具结果后
    .WhenContainsToolCall("销售额", "query_sales");     // 第一轮先去查
```

规则按声明顺序匹配，**更具体的放前面**。

**三、并行断言要给延迟。** `Parallel.ForEachAsync` 不保证同时启动。想断言「并行时看不到兄弟产出」，必须让两个任务确定性重叠：

```csharp
var delay = TimeSpan.FromMilliseconds(80);
Sandbox.Agents.Script("甲").Fallback(ScriptedReply.Say("甲的产出", delay));
Sandbox.Agents.Script("乙").Fallback(ScriptedReply.Say("乙的产出", delay));
```

否则线程池在高负载下可能恰好串行调度，测试会随负载随机失败——这属于测试不可靠，不是产品缺陷。

## 失败即暴露

沙箱在退出时若记录了问题（操作超时、临时目录清理失败、容器释放异常），`SandboxedTest` 会直接让该测试失败，而不是打个日志了事。资源泄漏应当被看见。

## 跑测试

```bash
dotnet test Mono.LLM.Agent.Tests/Mono.LLM.Agent.Tests.csproj

# 只跑场景
dotnet test --filter "FullyQualifiedName~Scenarios"

# 只跑夹具自测（夹具本身也必须可靠）
dotnet test --filter "FullyQualifiedName~HarnessSelfTests"
```
