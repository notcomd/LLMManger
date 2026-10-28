using Mono.LLM.Agent.MultiAgent;

namespace Mono.LLM.Agent.Tests.MultiAgent;

/// <summary>
/// 覆盖团队共享工作区的并发语义。
/// </summary>
public class TeamWorkspaceTests
{
    [Fact]
    public void Write_SingleAgent_StoresContent()
    {
        var workspace = new TeamWorkspace();

        workspace.Write("架构师", "第一版方案");

        Assert.True(workspace.TryGet("架构师", out var content));
        Assert.Equal("第一版方案", content);
        Assert.Equal(1, workspace.Count);
    }

    [Fact]
    public void Write_SameAgentTwice_AppendsInsteadOfOverwriting()
    {
        var workspace = new TeamWorkspace();

        workspace.Write("架构师", "第一段");
        workspace.Write("架构师", "第二段");

        var content = workspace.Snapshot()["架构师"];
        Assert.Contains("第一段", content);
        Assert.Contains("第二段", content);
        Assert.True(content.IndexOf("第一段", StringComparison.Ordinal) <
                    content.IndexOf("第二段", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_IsCaseInsensitive()
    {
        var workspace = new TeamWorkspace();

        workspace.Write("Architect", "a");
        workspace.Write("architect", "b");

        Assert.Equal(1, workspace.Count);
        Assert.Contains("b", workspace.Snapshot()["ARCHITECT"]);
    }

    [Fact]
    public void Snapshot_PreservesInsertionOrder()
    {
        var workspace = new TeamWorkspace();
        workspace.Write("c", "3");
        workspace.Write("a", "1");
        workspace.Write("b", "2");

        Assert.Equal(new[] { "c", "a", "b" }, workspace.Snapshot().Keys.ToArray());
    }

    [Fact]
    public void Snapshot_ReturnsDetachedCopy()
    {
        var workspace = new TeamWorkspace();
        workspace.Write("a", "原始");

        var snapshot = workspace.Snapshot();
        workspace.Write("a", "后续");

        Assert.Equal("原始", snapshot["a"]);
        Assert.Contains("后续", workspace.Snapshot()["a"]);
    }

    [Fact]
    public void TryGet_UnknownAgent_ReturnsFalse()
    {
        var workspace = new TeamWorkspace();

        Assert.False(workspace.TryGet("查无此人", out var content));
        Assert.Null(content);
    }

    [Fact]
    public void Write_EmptyName_Throws()
    {
        var workspace = new TeamWorkspace();

        Assert.Throws<ArgumentException>(() => workspace.Write("  ", "x"));
    }

    [Fact]
    public async Task ConcurrentWrites_ToDifferentAgents_AllPreserved()
    {
        var workspace = new TeamWorkspace();
        const int agents = 32;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, agents),
            async (i, _) =>
            {
                workspace.Write($"agent-{i}", $"输出-{i}");
                await Task.Yield();
            });

        var snapshot = workspace.Snapshot();
        Assert.Equal(agents, snapshot.Count);

        for (int i = 0; i < agents; i++)
            Assert.Equal($"输出-{i}", snapshot[$"agent-{i}"]);
    }

    [Fact]
    public async Task ConcurrentWrites_ToSameAgent_DoNotLoseContent()
    {
        var workspace = new TeamWorkspace();
        const int writes = 200;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, writes),
            async (i, _) =>
            {
                workspace.Write("shared", $"[{i}]");
                await Task.Yield();
            });

        var content = workspace.Snapshot()["shared"];

        for (int i = 0; i < writes; i++)
            Assert.Contains($"[{i}]", content);
    }

    [Fact]
    public async Task Snapshot_DuringConcurrentWrites_IsAlwaysConsistent()
    {
        var workspace = new TeamWorkspace();
        var failures = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 100),
            async (i, ct) =>
            {
                workspace.Write($"agent-{i % 5}", $"片段-{i}");

                // 快照期间不应抛集合修改异常
                try
                {
                    _ = workspace.Snapshot();
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref failures);
                }

                await Task.Yield();
            });

        Assert.Equal(0, failures);
        Assert.Equal(5, workspace.Count);
    }
}
