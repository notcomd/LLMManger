using Mono.LLM.Agent.MultiAgent;

namespace Mono.LLM.Agent.Tests.MultiAgent;

/// <summary>
/// 覆盖团队模式的提示词协议解析。
/// </summary>
/// <remarks>
/// 解析是这条链路里最容易出错的部分：模型会夹带开场白、用代码块包裹、把名称写成
/// "架构师（张三）"，甚至漏掉 ASSIGNEE 行。这些情况都必须被稳定处理。
/// </remarks>
public class TeamProtocolTests
{
    private readonly DefaultOrchestrationPromptBuilder _builder = new();

    // ── 规划解析 ──

    [Fact]
    public void ParseTeamPlan_StandardOutput_ReturnsAllTasks()
    {
        const string output = """
            ### TASK
            ASSIGNEE: 架构师
            设计数据模型与接口契约

            ### TASK
            ASSIGNEE: 开发
            实现核心逻辑
            """;

        var tasks = _builder.ParseTeamPlan(output);

        Assert.Equal(2, tasks.Count);
        Assert.Equal("架构师", tasks[0].Assignee);
        Assert.Contains("数据模型", tasks[0].Description);
        Assert.Equal("开发", tasks[1].Assignee);
    }

    [Fact]
    public void ParseTeamPlan_WithPreamble_SkipsPreamble()
    {
        const string output = """
            好的，我来拆解一下这个任务：

            ### TASK
            ASSIGNEE: 架构师
            设计数据模型
            """;

        var tasks = _builder.ParseTeamPlan(output);

        var task = Assert.Single(tasks);
        Assert.Equal("架构师", task.Assignee);
    }

    [Fact]
    public void ParseTeamPlan_WrappedInCodeFence_StillParses()
    {
        const string output = """
            ```
            ### TASK
            ASSIGNEE: 开发
            写代码
            ```
            """;

        var tasks = _builder.ParseTeamPlan(output);

        Assert.Single(tasks);
    }

    [Fact]
    public void ParseTeamPlan_MultiLineDescription_IsPreserved()
    {
        const string output = """
            ### TASK
            ASSIGNEE: 开发
            第一行要求
            第二行要求
            第三行要求
            """;

        var task = Assert.Single(_builder.ParseTeamPlan(output));

        Assert.Contains("第一行", task.Description);
        Assert.Contains("第二行", task.Description);
        Assert.Contains("第三行", task.Description);
    }

    [Fact]
    public void ParseTeamPlan_BlockWithoutAssignee_IsSkipped()
    {
        const string output = """
            ### TASK
            这段没有指派对象

            ### TASK
            ASSIGNEE: 开发
            有效任务
            """;

        var task = Assert.Single(_builder.ParseTeamPlan(output));

        Assert.Equal("开发", task.Assignee);
        Assert.Equal("有效任务", task.Description.Trim());
    }

    [Fact]
    public void ParseTeamPlan_EmptyOrGarbage_ReturnsEmpty()
    {
        Assert.Empty(_builder.ParseTeamPlan(string.Empty));
        Assert.Empty(_builder.ParseTeamPlan("我无法拆解这个任务。"));
    }

    [Fact]
    public void ParseTeamPlan_AssigneeWithDecoration_IsTrimmed()
    {
        const string output = """
            ### TASK
            ASSIGNEE: **架构师**
            设计模型
            """;

        var task = Assert.Single(_builder.ParseTeamPlan(output));

        Assert.Equal("架构师", task.Assignee);
    }

    [Fact]
    public void ParseTeamPlan_IsCaseInsensitiveOnMarker()
    {
        const string output = """
            ### task
            assignee: 开发
            写代码
            """;

        Assert.Single(_builder.ParseTeamPlan(output));
    }

    // ── 评审解析 ──

    [Theory]
    [InlineData("DONE: true", true)]
    [InlineData("DONE: TRUE", true)]
    [InlineData("DONE: yes", true)]
    [InlineData("DONE: 是", true)]
    [InlineData("DONE: 已完成", true)]
    [InlineData("DONE: false", false)]
    [InlineData("DONE: no", false)]
    [InlineData("DONE: 否", false)]
    [InlineData("done: true", true)]
    public void ParseTeamReview_RecognizesVerdicts(string line, bool expected)
    {
        var verdict = _builder.ParseTeamReview($"{line}\nREASON: 因为\nNEXT:");

        Assert.Equal(expected, verdict.Completed);
    }

    [Fact]
    public void ParseTeamReview_ExtractsReasonAndNextFocus()
    {
        const string output = """
            DONE: false
            REASON: 缺少测试覆盖
            NEXT: 补充单元测试
            """;

        var verdict = _builder.ParseTeamReview(output);

        Assert.False(verdict.Completed);
        Assert.Equal("缺少测试覆盖", verdict.Reason);
        Assert.Equal("补充单元测试", verdict.NextFocus);
    }

    [Fact]
    public void ParseTeamReview_MissingDoneMarker_TreatedAsNotCompleted()
    {
        var verdict = _builder.ParseTeamReview("看起来还不错，但我不确定。");

        Assert.False(verdict.Completed);
    }

    [Fact]
    public void ParseTeamReview_Empty_ReturnsNotCompleted()
    {
        Assert.False(_builder.ParseTeamReview(string.Empty).Completed);
    }

    [Fact]
    public void ParseTeamReview_SurroundedByNoise_StillWorks()
    {
        const string output = """
            我评估了一下当前产出：

            DONE: true
            REASON: 所有要求都已覆盖
            NEXT:

            希望这有帮助。
            """;

        var verdict = _builder.ParseTeamReview(output);

        Assert.True(verdict.Completed);
        Assert.Equal("所有要求都已覆盖", verdict.Reason);
    }

    // ── 上下文构建 ──

    [Fact]
    public void BuildTeamPlanningContext_IncludesMembersAndFormatHint()
    {
        var context = _builder.BuildTeamPlanningContext(
            "做一个登录页",
            new[] { "架构师: 设计 (负责结构)", "开发: 编码 (负责实现)" },
            Array.Empty<TeamRoundRecord>());

        Assert.Contains("做一个登录页", context);
        Assert.Contains("架构师", context);
        Assert.Contains("### TASK", context);
        Assert.Contains("ASSIGNEE:", context);
    }

    [Fact]
    public void BuildTeamPlanningContext_WithHistory_IncludesPreviousRounds()
    {
        var rounds = new List<TeamRoundRecord>
        {
            new()
            {
                Round = 1,
                Plan = new[] { new TeamTask("架构师", "设计模型") },
                Outputs = new Dictionary<string, string> { ["架构师"] = "模型已设计" },
                Reason = "缺少测试",
                NextFocus = "补测试"
            }
        };

        var context = _builder.BuildTeamPlanningContext("做登录页", new[] { "架构师" }, rounds);

        Assert.Contains("【第 1 轮】", context);
        Assert.Contains("缺少测试", context);
        Assert.Contains("补测试", context);
    }

    [Fact]
    public void BuildTeamExecutionContext_ListsCompletedOutputs()
    {
        var context = _builder.BuildTeamExecutionContext(
            "做登录页",
            new TeamTask("开发", "实现表单"),
            new Dictionary<string, string> { ["架构师"] = "用组件库" });

        Assert.Contains("实现表单", context);
        Assert.Contains("架构师", context);
        Assert.Contains("用组件库", context);
    }

    [Fact]
    public void BuildTeamReviewContext_ContainsReviewInstructions()
    {
        var rounds = new List<TeamRoundRecord>
        {
            new()
            {
                Round = 1,
                Plan = new[] { new TeamTask("开发", "写代码") },
                Outputs = new Dictionary<string, string> { ["开发"] = "代码已完成" }
            }
        };

        var context = _builder.BuildTeamReviewContext("做登录页", rounds);

        Assert.Contains("判断原始任务", context);
        Assert.Contains("DONE:", context);
        Assert.Contains("代码已完成", context);
    }

    [Fact]
    public void BuildTeamFinalContext_IncompleteCase_MentionsGap()
    {
        var rounds = new List<TeamRoundRecord>
        {
            new()
            {
                Round = 1,
                Reason = "缺少错误处理",
                NextFocus = "补异常分支"
            }
        };

        var context = _builder.BuildTeamFinalContext("做登录页", rounds, completed: false);

        Assert.Contains("尚未完全达成", context);
        Assert.Contains("缺少错误处理", context);
        Assert.Contains("补异常分支", context);
    }

    [Fact]
    public void BuildTeamFinalContext_CompletedCase_AsksForIntegration()
    {
        var rounds = new List<TeamRoundRecord> { new() { Round = 1 } };

        var context = _builder.BuildTeamFinalContext("做登录页", rounds, completed: true);

        Assert.Contains("评审已确认任务完成", context);
        Assert.Contains("整合", context);
    }
}
