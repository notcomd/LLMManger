using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.MultiAgent;
using Mono.LLM.Agent.Tests.Fakes;

namespace Mono.LLM.Agent.Tests.MultiAgent;

/// <summary>
/// 覆盖团队模式的执行并发行为与共享工作区语义。
/// </summary>
public class TeamExecutionTests
{
    private const string PlanMarker = "拆解成可以分头执行";
    private const string ExecuteMarker = "你负责的子任务";
    private const string ReviewMarker = "判断原始任务是否已经被完整解决";

    private static AgentDefinition Member(string name) => new()
    {
        Name = name,
        Role = "成员",
        Description = "测试成员",
        SystemPrompt = $"你是{name}",
        Configuration = new LLMConfiguration
        {
            ProviderType = LLMProviderType.Ollama,
            ModelId = name,
            Endpoint = "http://localhost:11434"
        }
    };

    private static string TwoTaskPlan(string first, string second) =>
        $"### TASK\nASSIGNEE: {first}\n任务一\n\n### TASK\nASSIGNEE: {second}\n任务二";

    // ── 并行 ──

    [Fact]
    public async Task RunAsync_DefaultMode_RunsSubTasksConcurrently()
    {
        int current = 0, peak = 0;

        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker))
            {
                var running = Interlocked.Increment(ref current);
                InterlockedExtensions.Max(ref peak, running);
                Thread.Sleep(60);
                Interlocked.Decrement(ref current);
                return $"{agent} 的产出";
            }

            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return TwoTaskPlan("架构师", "开发");
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师"), Member("开发") },
            maxRounds: 1);

        await orchestrator.RunAsync("做点事");

        Assert.True(peak >= 2, $"预期子任务并发执行，实际峰值 {peak}");
    }

    [Fact]
    public async Task RunAsync_MaxDegreeOfParallelismOne_SerializesExecution()
    {
        int current = 0, peak = 0;

        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker))
            {
                var running = Interlocked.Increment(ref current);
                InterlockedExtensions.Max(ref peak, running);
                Thread.Sleep(40);
                Interlocked.Decrement(ref current);
                return "产出";
            }

            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return TwoTaskPlan("架构师", "开发");
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师"), Member("开发") },
            maxRounds: 1,
            maxDegreeOfParallelism: 1);

        await orchestrator.RunAsync("做点事");

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task RunAsync_ParallelMode_KeepsEveryMemberOutput()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker))
            {
                Thread.Sleep(20);
                return $"来自{agent}的内容";
            }

            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker))
                return "### TASK\nASSIGNEE: 甲\n任务\n\n### TASK\nASSIGNEE: 乙\n任务\n\n### TASK\nASSIGNEE: 丙\n任务";
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("甲"), Member("乙"), Member("丙") },
            maxRounds: 1);

        var result = await orchestrator.RunAsync("做点事");

        var outputs = result.TeamRounds[0].Outputs;
        Assert.Equal(3, outputs.Count);
        Assert.Equal("来自甲的内容", outputs["甲"]);
        Assert.Equal("来自乙的内容", outputs["乙"]);
        Assert.Equal("来自丙的内容", outputs["丙"]);
    }

    // ── 串行 ──

    [Fact]
    public async Task RunAsync_SequentialMode_LaterMemberSeesEarlierOutput()
    {
        var prompts = new List<string>();

        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker))
            {
                lock (prompts) prompts.Add(user);
                return agent == "架构师" ? "架构方案已定" : "按方案实现";
            }

            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return TwoTaskPlan("架构师", "开发");
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师"), Member("开发") },
            maxRounds: 1,
            executionMode: TeamExecutionMode.Sequential);

        await orchestrator.RunAsync("做点事");

        Assert.Equal(2, prompts.Count);
        Assert.DoesNotContain("架构方案已定", prompts[0]);
        Assert.Contains("架构方案已定", prompts[1]);
    }

    [Fact]
    public async Task RunAsync_ParallelMode_DoesNotInjectSiblingOutputs()
    {
        var prompts = new List<string>();

        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker))
            {
                lock (prompts) prompts.Add(user);
                Thread.Sleep(30);
                return agent == "架构师" ? "架构方案已定" : "按方案实现";
            }

            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return TwoTaskPlan("架构师", "开发");
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师"), Member("开发") },
            maxRounds: 1);

        await orchestrator.RunAsync("做点事");

        // 并行时两个成员同时启动，彼此都拿不到对方的产出
        Assert.Equal(2, prompts.Count);
        Assert.All(prompts, p => Assert.DoesNotContain("架构方案已定", p));
    }

    // ── 跨轮工作区 ──

    [Fact]
    public async Task RunAsync_SecondRoundSeesFirstRoundWorkspace()
    {
        var executePrompts = new List<string>();
        var reviews = 0;

        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker))
            {
                lock (executePrompts) executePrompts.Add(user);
                return agent == "架构师" ? "初版方案" : "修订版方案";
            }

            if (user.Contains(ReviewMarker))
            {
                reviews++;
                return reviews == 1
                    ? "DONE: false\nREASON: 需要补充细节\nNEXT: 补充实现细节"
                    : "DONE: true\nREASON: ok\nNEXT:";
            }

            if (user.Contains(PlanMarker))
                return reviews == 0
                    ? "### TASK\nASSIGNEE: 架构师\n出初稿"
                    : "### TASK\nASSIGNEE: 开发\n补细节";

            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师"), Member("开发") },
            maxRounds: 2);

        await orchestrator.RunAsync("做点事");

        Assert.Equal(2, executePrompts.Count);
        Assert.DoesNotContain("初版方案", executePrompts[0]);
        // 第二轮执行的成员能读到第一轮写进工作区的成果
        Assert.Contains("初版方案", executePrompts[1]);
    }

    [Fact]
    public async Task RunAsync_SecondRoundMemberCanBuildOnPreviousOutput()
    {
        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker))
                return user.Contains("初版方案") ? "在初版基础上补充" : "初版方案";

            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return "### TASK\nASSIGNEE: 架构师\n出初稿";
            return "最终";
        });

        var orchestrator = new TeamOrchestrator(
            factory,
            new[] { Member("架构师") },
            maxRounds: 1);

        var result = await orchestrator.RunAsync("做点事");

        Assert.Equal("初版方案", result.TeamRounds[0].Outputs["架构师"]);
    }

    // ── 组合 ──

    [Fact]
    public async Task AgentTeam_UseTeamWithSequentialMode_IsHonored()
    {
        var prompts = new List<string>();

        var factory = new ScriptedFactory((agent, user) =>
        {
            if (user.Contains(ExecuteMarker))
            {
                lock (prompts) prompts.Add(user);
                return agent == "甲" ? "甲的成果" : "乙的成果";
            }

            if (user.Contains(ReviewMarker)) return "DONE: true\nREASON: ok\nNEXT:";
            if (user.Contains(PlanMarker)) return TwoTaskPlan("甲", "乙");
            return "最终";
        });

        var team = new AgentTeam(factory)
            .AddMembers(new[] { Member("甲"), Member("乙") })
            .UseTeam(maxRounds: 1, executionMode: TeamExecutionMode.Sequential);

        await team.RunAsync("做点事");

        Assert.Equal(2, prompts.Count);
        Assert.Contains("甲的成果", prompts[1]);
    }
}
