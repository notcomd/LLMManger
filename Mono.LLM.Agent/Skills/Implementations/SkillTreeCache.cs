// ============================================================================
// 文件：SkillTreeCache.cs
// 用途：技能树缓存：按注册表当前内容构建一次树，内容不变就复用。
// 实现方：SkillTreeCache
// 调用方：SkillRouter、SkillToolset、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 技能树缓存：按注册表当前内容构建一次树，内容不变就复用。
/// </summary>
/// <remarks>
/// <para>
/// 索引提示词原本每次调用都重新分组、排序、拼接字符串；技能一多，这部分开销会出现在每一轮对话里。
/// 这里把「树」缓存下来，用指纹判断是否需要重建：名称、描述、正文、分类、标签、工具白名单任一变化都会失效。
/// </para>
/// <para>
/// 指纹而不是引用比较，是因为注册表本身没有变更事件，且技能定义是可变的普通对象——
/// 引用相同的对象也可能被就地改过。
/// </para>
/// <para>实例是线程安全的：构建与命中都在同一把锁内完成。</para>
/// </remarks>
public sealed class SkillTreeCache
{
    private readonly object _lock = new();

    private SkillTree? _tree;
    private long _fingerprint = long.MinValue;
    private int _buildCount;

    /// <summary>获取当前缓存版本（内容指纹）；尚未构建时为 <see cref="long.MinValue"/>。</summary>
    public long Version
    {
        get
        {
            lock (_lock)
                return _fingerprint;
        }
    }

    /// <summary>获取累计重建次数，用于观测缓存是否频繁失效。</summary>
    public int BuildCount
    {
        get
        {
            lock (_lock)
                return _buildCount;
        }
    }

    /// <summary>
    /// 获取技能树；内容发生变化时自动重建。
    /// </summary>
    /// <param name="registry">技能注册表。</param>
    /// <returns>与当前注册表内容一致的技能树。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="registry"/> 为 <c>null</c> 时抛出。</exception>
    public SkillTree Get(ISkillRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var skills = registry.GetAll();
        var fingerprint = ComputeFingerprint(skills);

        lock (_lock)
        {
            if (_tree is not null && _fingerprint == fingerprint)
                return _tree;

            _tree = SkillTree.Build(skills);
            _fingerprint = fingerprint;
            _buildCount++;

            return _tree;
        }
    }

    /// <summary>
    /// 强制重建技能树。
    /// </summary>
    /// <param name="registry">技能注册表。</param>
    /// <returns>重建后的技能树。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="registry"/> 为 <c>null</c> 时抛出。</exception>
    public SkillTree Rebuild(ISkillRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var skills = registry.GetAll();

        lock (_lock)
        {
            _tree = SkillTree.Build(skills);
            _fingerprint = ComputeFingerprint(skills);
            _buildCount++;

            return _tree;
        }
    }

    /// <summary>清空缓存，下次 <see cref="Get"/> 会重建。</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _tree = null;
            _fingerprint = long.MinValue;
        }
    }

    /// <summary>
    /// 计算技能集合的内容指纹。
    /// </summary>
    /// <param name="skills">技能集合。</param>
    /// <returns>64 位指纹；同一内容多次调用结果相同。</returns>
    public static long ComputeFingerprint(IEnumerable<SkillDefinition> skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offset;

        foreach (var skill in skills.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase))
        {
            hash = Mix(hash, skill.Name);
            hash = Mix(hash, skill.Description);
            hash = Mix(hash, skill.Category);
            hash = Mix(hash, skill.Body);
            hash = Mix(hash, skill.SystemPrompt);
            hash = Mix(hash, skill.Version);
            hash = Mix(hash, skill.License);
            hash = Mix(hash, skill.DirectoryPath);
            hash = Mix(hash, string.Join('\u0001', skill.Tags));
            hash = Mix(hash, string.Join('\u0001', skill.AllowedTools));
            hash = Mix(hash, string.Join('\u0001', skill.Resources.Select(resource => resource.RelativePath)));
        }

        return (long)hash;

        static ulong Mix(ulong current, string? value)
        {
            // 长度参与混合，避免 "ab"+"c" 与 "a"+"bc" 撞成同一个值。
            var text = value ?? string.Empty;
            current ^= (ulong)text.Length;
            current *= prime;

            foreach (var ch in text)
            {
                current ^= ch;
                current *= prime;
            }

            return current;
        }
    }
}
