using Microsoft.Extensions.AI;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tests.Harness;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Tests.Scenarios;

/// <summary>
/// 端到端场景：技能执行链路（白名单 → 工具循环 → 结果回填）。
/// </summary>
public class SkillToolPipelineTests : SandboxedTest
{
    [Fact]
    public async Task Skill_RunsToolLoopAndReturnsModelConclusion()
    {
        var analyst = Sandbox.Agents.Define("分析师", "数据分析", "你是数据分析师。", "擅长 SQL 与统计");

        Sandbox.Agents.Script("分析师")
            .WhenContains("1200万", "三季度销售额 1200 万，环比增长 15%。")
            .WhenContainsToolCall("销售额", "query_sales", new Dictionary<string, object?> { ["quarter"] = "Q3" });

        var invocations = new List<string>();
        var tools = BuildTools(invocations);

        var skill = new SkillDefinition
        {
            Name = "销售分析",
            SystemPrompt = "你是数据分析师。",
            AllowedTools = ["query_sales"]
        };

        var executor = new SkillExecutor(Sandbox.Agents.CreateFactory());
        using var context = executor.CreateContext(skill, analyst.Configuration, tools);

        var response = await Sandbox.RunAsync(() => executor.ExecuteAsync(context, "分析三季度销售额"));

        Assert.Equal(new[] { "Q3" }, invocations);
        Assert.Contains("1200", response.Content);
        Assert.Equal(2, Sandbox.Agents.ScriptOrNull("分析师")!.CallCount);
    }

    [Fact]
    public async Task Skill_BlocksToolOutsideWhitelistEvenIfModelRequestsIt()
    {
        var analyst = Sandbox.Agents.Define("分析师");
        var dropped = false;

        Sandbox.Agents.Script("分析师")
            // 拿到"工具不存在"的结果后给出收尾答复，避免反复重试
            .WhenContains("not found", "无法执行该操作，请联系管理员。")
            .WhenContainsToolCall("删除", "drop_database");

        var tools = new ToolManager();
        tools.RegisterTool("drop_database", "删除数据库", Array.Empty<ToolParameterInfo>(),
            (_, _) =>
            {
                dropped = true;
                return Task.FromResult(new ToolResult { Name = "drop_database", Result = "已删除" });
            });
        tools.RegisterTool("query_sales", "查询销售额", Array.Empty<ToolParameterInfo>(),
            (_, _) => Task.FromResult(new ToolResult { Name = "query_sales", Result = "ok" }));

        var skill = new SkillDefinition
        {
            Name = "只读分析",
            SystemPrompt = "你是只读分析师。",
            AllowedTools = ["query_sales"]
        };

        var executor = new SkillExecutor(Sandbox.Agents.CreateFactory());
        using var context = executor.CreateContext(skill, analyst.Configuration, tools);

        var response = await Sandbox.RunAsync(() => executor.ExecuteAsync(context, "删除数据库"));

        Assert.False(dropped);
        Assert.Contains("无法执行", response.Content);

        var toolMessage = Assert.Single(context.Conversation.History, m => m.Role == ChatRole.Tool);
        var result = Assert.Single(toolMessage.Contents.OfType<FunctionResultContent>());
        Assert.Contains("not found", result.Result?.ToString());
    }

    [Fact]
    public void Skill_WithoutWhitelist_ExposesEveryTool()
    {
        var analyst = Sandbox.Agents.Define("分析师");
        var skill = new SkillDefinition { Name = "不限工具", SystemPrompt = "你是助手。" };

        var executor = new SkillExecutor(Sandbox.Agents.CreateFactory());
        using var context = executor.CreateContext(skill, analyst.Configuration, BuildTools(new List<string>()));

        Assert.Equal(2, context.EffectiveTools.Tools.Count);
    }

    [Fact]
    public void Skill_WithMisspelledTool_YieldsEmptyToolSetWithoutThrowing()
    {
        var analyst = Sandbox.Agents.Define("分析师");
        var skill = new SkillDefinition
        {
            Name = "拼错工具名",
            SystemPrompt = "你是助手。",
            AllowedTools = ["qurey_sales"]
        };

        var executor = new SkillExecutor(Sandbox.Agents.CreateFactory());
        using var context = executor.CreateContext(skill, analyst.Configuration, BuildTools(new List<string>()));

        Assert.Empty(context.EffectiveTools.Tools);
    }

    [Fact]
    public async Task Skill_SecondInvocation_ReusesConversationHistory()
    {
        var analyst = Sandbox.Agents.Define("分析师");
        Sandbox.Agents.Script("分析师").Fallback("基于上文继续。");

        var skill = new SkillDefinition { Name = "多轮", SystemPrompt = "你是助手。" };
        var executor = new SkillExecutor(Sandbox.Agents.CreateFactory());

        using var context = executor.CreateContext(skill, analyst.Configuration, new ToolManager());

        await Sandbox.RunAsync(() => executor.ExecuteAsync(context, "第一个问题"));
        await Sandbox.RunAsync(() => executor.ExecuteAsync(context, "第二个问题"));

        var secondCall = Sandbox.Agents.ScriptOrNull("分析师")!.Calls[1];
        Assert.Equal("第二个问题", secondCall.UserMessage);

        // system + 第一轮问答 + 第二轮问答
        Assert.Equal(5, context.Conversation.MessageCount);
    }

    private static ToolManager BuildTools(List<string> invocations)
    {
        var tools = new ToolManager();

        tools.RegisterTool(
            "query_sales",
            "查询指定季度的销售额",
            [new ToolParameterInfo { Name = "quarter", Type = "string", Description = "季度", Required = true }],
            (args, _) =>
            {
                invocations.Add(args.GetProperty("quarter").GetString()!);
                return Task.FromResult(new ToolResult { Name = "query_sales", Result = "1200万" });
            });

        tools.RegisterTool(
            "drop_database",
            "删除整个数据库",
            Array.Empty<ToolParameterInfo>(),
            (_, _) => Task.FromResult(new ToolResult { Name = "drop_database", Result = "已删除" }));

        return tools;
    }
}
