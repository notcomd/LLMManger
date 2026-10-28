namespace Mono.LLM.Agent.Tests.Fakes;

/// <summary>测试用的原子最大值更新辅助。</summary>
internal static class InterlockedExtensions
{
    public static void Max(ref int target, int value)
    {
        int snapshot;
        while (value > (snapshot = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, snapshot) == snapshot)
                return;
        }
    }
}
