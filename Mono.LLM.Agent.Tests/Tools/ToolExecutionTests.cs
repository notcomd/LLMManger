using System.Diagnostics;
using System.Text.Json;
using Mono.LLM.Agent.Tests.Fakes;
using Mono.LLM.Agent.Tools;
using Microsoft.Extensions.AI;

namespace Mono.LLM.Agent.Tests.Tools;

/// <summary>
/// 覆盖工具执行管线的增强能力：超时、参数校验、默认值、并行、可观测性、工具集裁剪。
/// </summary>
public class ToolExecutionTests
{
    private static ToolResult Ok(string name = "t", string content = "ok") =>
        new() { Name = name, Result = content };

    private static JsonElement DefaultOf(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ── 超时 ──

    [Fact]
    public async Task ExecuteToolAsync_ExceedingTimeout_ReturnsFailureInsteadOfHanging()
    {
        var manager = new ToolManager(new ToolExecutionOptions
        {
            Timeout = TimeSpan.FromMilliseconds(80)
        });

        manager.RegisterTool(
            "slow",
            "慢工具",
            Array.Empty<ToolParameterInfo>(),
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return Ok("slow", "done");
            });

        var result = await manager.ExecuteToolAsync("slow", "{}");

        Assert.False(result.IsSuccess);
        Assert.Contains("超时", result.Error);
    }

    [Fact]
    public async Task ExecuteToolAsync_InfiniteTimeout_DoesNotCancelLongRunningTool()
    {
        var manager = new ToolManager(new ToolExecutionOptions
        {
            Timeout = Timeout.InfiniteTimeSpan
        });

        manager.RegisterTool(
            "slightly-slow",
            "d",
            Array.Empty<ToolParameterInfo>(),
            async (_, ct) =>
            {
                await Task.Delay(60, ct);
                return Ok();
            });

        var result = await manager.ExecuteToolAsync("slightly-slow", "{}");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ExecuteToolAsync_CallerCancellation_PropagatesAsCanceled()
    {
        var manager = new ToolManager();
        using var cts = new CancellationTokenSource();

        manager.RegisterTool(
            "cancellable",
            "d",
            Array.Empty<ToolParameterInfo>(),
            async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return Ok();
            });

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.ExecuteToolAsync("cancellable", "{}", cts.Token));
    }

    // ── 参数校验 ──

    [Fact]
    public async Task ExecuteToolAsync_MissingRequiredArgument_ReportsWhichOne()
    {
        var manager = new ToolManager();
        var invoked = false;

        manager.RegisterTool(
            "book",
            "订票",
            new[]
            {
                new ToolParameterInfo { Name = "city", Type = "string", Description = "城市", Required = true }
            },
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(Ok());
            });

        var result = await manager.ExecuteToolAsync("book", "{}");

        Assert.False(invoked);
        Assert.False(result.IsSuccess);
        Assert.Contains("city", result.Error);
        Assert.Contains("缺少必填参数", result.Error);
    }

    [Fact]
    public async Task ExecuteToolAsync_WrongArgumentType_ReportsExpectedType()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "calc",
            "d",
            new[]
            {
                new ToolParameterInfo { Name = "count", Type = "int", Required = true }
            },
            (_, _) => Task.FromResult(Ok()));

        var result = await manager.ExecuteToolAsync("calc", """{"count":"abc"}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("integer", result.Error);
    }

    [Fact]
    public async Task ExecuteToolAsync_NumericStringForInteger_IsAccepted()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "calc",
            "d",
            new[]
            {
                new ToolParameterInfo { Name = "count", Type = "int", Required = true }
            },
            (_, _) => Task.FromResult(Ok()));

        var result = await manager.ExecuteToolAsync("calc", """{"count":"3"}""");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ExecuteToolAsync_EnumViolation_ReportsAllowedValues()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "unit",
            "d",
            new[]
            {
                new ToolParameterInfo
                {
                    Name = "unit",
                    Type = "string",
                    Required = true,
                    EnumValues = new List<string> { "celsius", "fahrenheit" }
                }
            },
            (_, _) => Task.FromResult(Ok()));

        var result = await manager.ExecuteToolAsync("unit", """{"unit":"kelvin"}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("celsius", result.Error);
        Assert.Contains("fahrenheit", result.Error);
    }

    [Fact]
    public async Task ExecuteToolAsync_ValidationDisabled_SkipsChecks()
    {
        var manager = new ToolManager(new ToolExecutionOptions { ValidateArguments = false });
        manager.RegisterTool(
            "book",
            "d",
            new[] { new ToolParameterInfo { Name = "city", Type = "string", Required = true } },
            (args, _) => Task.FromResult(Ok("book", args.GetRawText())));

        var result = await manager.ExecuteToolAsync("book", "{}");

        Assert.True(result.IsSuccess);
    }

    // ── 默认值 ──

    [Fact]
    public async Task ExecuteToolAsync_MissingOptionalArgument_FillsDefaultValue()
    {
        var manager = new ToolManager();
        string? received = null;

        manager.RegisterTool(
            "weather",
            "d",
            new[]
            {
                new ToolParameterInfo { Name = "city", Type = "string", Required = true },
                new ToolParameterInfo
                {
                    Name = "unit",
                    Type = "string",
                    Required = false,
                    DefaultValue = DefaultOf("\"celsius\"")
                }
            },
            (args, _) =>
            {
                received = args.GetProperty("unit").GetString();
                return Task.FromResult(Ok());
            });

        var result = await manager.ExecuteToolAsync("weather", """{"city":"杭州"}""");

        Assert.True(result.IsSuccess);
        Assert.Equal("celsius", received);
    }

    [Fact]
    public void Schema_IncludesDefaultValue()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "weather",
            "d",
            new[]
            {
                new ToolParameterInfo
                {
                    Name = "unit",
                    Type = "string",
                    Required = false,
                    DefaultValue = DefaultOf("\"celsius\"")
                }
            },
            (_, _) => Task.FromResult(Ok()));

        var schema = JsonDocument.Parse(((AIFunction)manager.GetAITools()[0]).JsonSchema.GetRawText()).RootElement;

        Assert.Equal("celsius", schema.GetProperty("properties").GetProperty("unit").GetProperty("default").GetString());
    }

    // ── 并行执行 ──

    [Fact]
    public async Task ExecuteToolCallsAsync_Parallel_RunsConcurrently()
    {
        var manager = new ToolManager(new ToolExecutionOptions { MaxDegreeOfParallelism = 8 });
        int current = 0, peak = 0;

        manager.RegisterTool(
            "work",
            "d",
            Array.Empty<ToolParameterInfo>(),
            async (_, _) =>
            {
                var running = Interlocked.Increment(ref current);
                InterlockedExtensions.Max(ref peak, running);
                await Task.Delay(80);
                Interlocked.Decrement(ref current);
                return Ok();
            });

        var calls = Enumerable.Range(0, 4)
            .Select( _ => new ToolCallRequest("work", "{}"))
            .ToList();

        await manager.ExecuteToolCallsAsync(calls);

        Assert.True(peak > 1, $"预期出现并发，实际峰值 {peak}");
    }

    [Fact]
    public async Task ExecuteToolCallsAsync_SerialByDefault_KeepsPeakAtOne()
    {
        var manager = new ToolManager();
        int current = 0, peak = 0;

        manager.RegisterTool(
            "work",
            "d",
            Array.Empty<ToolParameterInfo>(),
            async (_, _) =>
            {
                var running = Interlocked.Increment(ref current);
                InterlockedExtensions.Max(ref peak, running);
                await Task.Delay(30);
                Interlocked.Decrement(ref current);
                return Ok();
            });

        var calls = Enumerable.Range(0, 3)
            .Select(_ => new ToolCallRequest("work", "{}"))
            .ToList();

        await manager.ExecuteToolCallsAsync(calls);

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task ExecuteToolCallsAsync_PreservesInputOrder()
    {
        var manager = new ToolManager(new ToolExecutionOptions { MaxDegreeOfParallelism = 4 });

        manager.RegisterTool(
            "echo",
            "d",
            new[] { new ToolParameterInfo { Name = "v", Type = "string", Required = true } },
            async (args, _) =>
            {
                var v = args.GetProperty("v").GetString();
                await Task.Delay(v == "1" ? 50 : 5);
                return Ok("echo", v!);
            });

        var results = await manager.ExecuteToolCallsAsync(new[]
        {
            new ToolCallRequest("echo", """{"v":"1"}"""),
            new ToolCallRequest("echo", """{"v":"2"}"""),
            new ToolCallRequest("echo", """{"v":"3"}""")
        });

        Assert.Equal(new[] { "1", "2", "3" }, results.Select(r => r.Result));
    }

    // ── 可观测性 ──

    [Fact]
    public async Task ExecuteToolAsync_RaisesEventWithDuration()
    {
        var manager = new ToolManager();
        var captured = new List<ToolExecutedEventArgs>();
        manager.ToolExecuted += (_, e) => captured.Add(e);

        manager.RegisterTool(
            "quick",
            "d",
            Array.Empty<ToolParameterInfo>(),
            async (_, _) =>
            {
                await Task.Delay(20);
                return Ok("quick");
            });

        await manager.ExecuteToolAsync("quick", "{}");

        var evt = Assert.Single(captured);
        Assert.Equal("quick", evt.ToolName);
        Assert.True(evt.IsSuccess);
        Assert.True(evt.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task ExecuteToolAsync_SubscriberThrowing_DoesNotBreakExecution()
    {
        var manager = new ToolManager();
        manager.ToolExecuted += (_, _) => throw new InvalidOperationException("订阅方炸了");

        manager.RegisterTool(
            "ok",
            "d",
            Array.Empty<ToolParameterInfo>(),
            (_, _) => Task.FromResult(Ok("ok")));

        var result = await manager.ExecuteToolAsync("ok", "{}");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ExecuteToolCallsAsync_CarriesCallIdIntoResult()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "d1",
            "d",
            Array.Empty<ToolParameterInfo>(),
            (_, _) => Task.FromResult(Ok("d1")));

        var results = await manager.ExecuteToolCallsAsync(new[]
        {
            new ToolCallRequest("d1", "{}", "call-42")
        });

        Assert.Equal("call-42", results[0].ToolCallId);
        Assert.True(results[0].Duration > TimeSpan.Zero);
    }

    // ── 工具集裁剪 ──

    [Fact]
    public void CreateScoped_KeepsOnlyAllowedTools()
    {
        var manager = new ToolManager();
        manager.RegisterTool("a", "d", Array.Empty<ToolParameterInfo>(), (_, _) => Task.FromResult(Ok("a")));
        manager.RegisterTool("b", "d", Array.Empty<ToolParameterInfo>(), (_, _) => Task.FromResult(Ok("b")));

        var scoped = manager.CreateScoped(new[] { "a" });

        Assert.Single(scoped.Tools);
        Assert.True(scoped.IsRegistered("a"));
        Assert.False(scoped.IsRegistered("b"));
        Assert.Equal(2, manager.Tools.Count);
    }

    [Fact]
    public async Task CreateScoped_ExecutionStillWorks()
    {
        var manager = new ToolManager();
        manager.RegisterTool("a", "d", Array.Empty<ToolParameterInfo>(), (_, _) => Task.FromResult(Ok("a", "from-a")));

        var scoped = manager.CreateScoped(new[] { "a" });
        var result = await scoped.ExecuteToolAsync("a", "{}");

        Assert.True(result.IsSuccess);
        Assert.Equal("from-a", result.Result);
    }

    // ── 注册控制 ──

    [Fact]
    public void RegisterTool_OverwriteFalse_ThrowsOnDuplicate()
    {
        var manager = new ToolManager();
        manager.RegisterTool("dup", "d", Array.Empty<ToolParameterInfo>(), (_, _) => Task.FromResult(Ok()));

        Assert.Throws<InvalidOperationException>(
            () => manager.RegisterTool("dup", "d", Array.Empty<ToolParameterInfo>(), (_, _) => Task.FromResult(Ok()), overwrite: false));
    }

    [Fact]
    public void RegisterSimpleTool_WithoutInput_DeclaresNoParameters()
    {
        var manager = new ToolManager();
        manager.RegisterSimpleTool("now", "当前时间", _ => Task.FromResult("now"), requiresInput: false);

        Assert.Empty(manager.Tools[0].Parameters);
    }

    [Fact]
    public async Task RegisterSimpleTool_WithoutInput_CanBeInvokedWithEmptyArguments()
    {
        var manager = new ToolManager();
        manager.RegisterSimpleTool("now", "当前时间", _ => Task.FromResult("2026-10-25"), requiresInput: false);

        var result = await manager.ExecuteToolAsync("now", "{}");

        Assert.True(result.IsSuccess);
        Assert.Equal("2026-10-25", result.Result);
    }

    [Fact]
    public async Task RegisterSimpleTool_WithInput_StillRequiresInput()
    {
        var manager = new ToolManager();
        manager.RegisterSimpleTool("echo", "d", input => Task.FromResult(input));

        var missing = await manager.ExecuteToolAsync("echo", "{}");
        var provided = await manager.ExecuteToolAsync("echo", """{"input":"hi"}""");

        Assert.False(missing.IsSuccess);
        Assert.Equal("hi", provided.Result);
    }

    // ── 自动调用路径同样受管线保护 ──

    [Fact]
    public async Task AIFunctionInvoke_EnforcesValidation()
    {
        var manager = new ToolManager();
        manager.RegisterTool(
            "book",
            "d",
            new[] { new ToolParameterInfo { Name = "city", Type = "string", Required = true } },
            (_, _) => Task.FromResult(Ok()));

        var function = (AIFunction)manager.GetAITools()[0];
        var result = await function.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.IsType<string>(result);
        Assert.Contains("city", (string)result!);
    }
}

