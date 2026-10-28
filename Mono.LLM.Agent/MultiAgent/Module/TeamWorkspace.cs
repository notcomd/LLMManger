// ============================================================================
// 文件：TeamWorkspace.cs
// 用途：团队共享工作区：成员把自己的产出写进来，其他成员与评审者从这里读取。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：AgentTeam、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.MultiAgent;

/// <summary>
/// 团队共享工作区：成员把自己的产出写进来，其他成员与评审者从这里读取。
/// </summary>
/// <remarks>
/// <para>
/// 这是并行执行能够成立的前提。并行时各成员同时开始，无法互相等待，
/// 于是约定「各自写、统一读」：每个成员完成后立即写入工作区，
/// 评审与下一轮从工作区取全量产出，信息不会因为并行而丢失。
/// </para>
/// <para>
/// 写入采用<b>追加</b>语义：同一成员在一轮中承担多个子任务时，
/// 多次产出会拼接在一起，而不是相互覆盖。
/// </para>
/// <para>
/// 所有操作线程安全，可在 <see cref="TeamExecutionMode.Parallel"/> 下被并发调用。
/// </para>
/// </remarks>
public sealed class TeamWorkspace
{
    private readonly Dictionary<string, string> _byAgent = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();
    private readonly object _gate = new();

    /// <summary>获取当前已写入的成员数量。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _byAgent.Count;
        }
    }

    /// <summary>
    /// 写入某个成员的产出。同名成员再次写入时追加到已有内容之后。
    /// </summary>
    /// <param name="agentName">成员名称，不区分大小写。</param>
    /// <param name="content">产出内容。</param>
    /// <exception cref="ArgumentException">当 <paramref name="agentName"/> 为空时抛出。</exception>
    public void Write(string agentName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        content ??= string.Empty;

        lock (_gate)
        {
            if (_byAgent.TryGetValue(agentName, out var existing))
            {
                _byAgent[agentName] = existing.Length == 0
                    ? content
                    : existing + "\n\n" + content;
            }
            else
            {
                _byAgent[agentName] = content;
                _order.Add(agentName);
            }
        }
    }

    /// <summary>
    /// 读取某个成员当前的累计产出。
    /// </summary>
    /// <param name="agentName">成员名称。</param>
    /// <param name="content">读取到的内容；不存在时为 <c>null</c>。</param>
    /// <returns>若该成员已有产出则返回 <c>true</c>。</returns>
    public bool TryGet(string agentName, out string? content)
    {
        lock (_gate)
            return _byAgent.TryGetValue(agentName, out content);
    }

    /// <summary>
    /// 获取当前全部产出的快照，按成员首次写入的顺序排列。
    /// </summary>
    /// <returns>与工作区解耦的新字典，可安全地在并行任务中传递。</returns>
    /// <remarks>
    /// 返回的是副本。并行执行时每个成员拿到的是<b>启动那一刻</b>的快照，
    /// 因此同一轮内并行运行的其他成员的产出不会出现在其中。
    /// </remarks>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new Dictionary<string, string>(_order.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var agent in _order)
                snapshot[agent] = _byAgent[agent];

            return snapshot;
        }
    }
}
