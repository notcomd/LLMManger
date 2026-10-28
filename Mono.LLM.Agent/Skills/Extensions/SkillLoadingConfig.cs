// ============================================================================
// 文件：SkillLoadingConfig.cs
// 用途：技能加载配置器，使用 Fluent API 声明式配置技能的来源和加载行为。
// 实现方：SkillLoadingConfig
// 调用方：应用启动、AgentOptions.Skills
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 技能加载配置器，使用 Fluent API 声明式配置技能的来源和加载行为。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SkillLoadingConfig"/> 是技能加载体系的入口配置类。通过链式调用方法声明
/// 技能从哪里加载，最终调用 <see cref="BuildSource"/> 生成一个 <see cref="ISkillSource"/> 实例，
/// 或直接调用 <see cref="LoadInto"/> 将结果写入 <see cref="ISkillRegistry"/>。
/// </para>
/// <para>
/// 支持的加载策略：
/// <list type="bullet">
///   <item><b>内置技能</b>: 使用 <see cref="UseBuiltIn()"/> 加载 <see cref="SkillCatalog"/> 中的预定义集合</item>
///   <item><b>文件目录</b>: 使用 <see cref="UseDirectory(string, string, bool)"/> 从磁盘 JSON 文件加载</item>
///   <item><b>标准技能目录</b>: 使用 <see cref="UseStandardDirectory"/> 加载 <c>SKILL.md</c> 形态的技能</item>
///   <item><b>内存列表</b>: 使用 <see cref="UseMemory(string, IEnumerable{SkillDefinition})"/> 包装已有对象</item>
///   <item><b>自定义来源</b>: 使用 <see cref="UseSource(ISkillSource)"/> 接入任意 <see cref="ISkillSource"/> 实现</item>
/// </list>
/// </para>
/// <para>
/// 可叠加多个来源，内部自动构建 <see cref="CompositeSkillSource"/> 进行合并，
/// 后添加的来源中的同名技能会覆盖先前的同名技能。
/// </para>
/// <para>
/// 所有加载均为显式调用，不依赖反射或自动发现机制。
/// </para>
/// </remarks>
public class SkillLoadingConfig
{
    private readonly List<ISkillSource> _sources = new();

    /// <summary>获取当前已配置的所有来源数量。</summary>
    public int SourceCount => _sources.Count;

    /// <summary>初始化 <see cref="SkillLoadingConfig"/> 的新实例。</summary>
    public SkillLoadingConfig() { }

    /// <summary>
    /// 添加内置预定义技能来源（<see cref="SkillCatalog"/> 中的全部技能）作为加载源之一。
    /// </summary>
    /// <returns>当前实例，支持链式调用。</returns>
    public SkillLoadingConfig UseBuiltIn()
    {
        _sources.Add(new InMemorySkillSource("built-in", SkillCatalog.GetAll()));
        return this;
    }

    /// <summary>
    /// 添加基于文件目录的技能来源，从指定路径扫描 JSON 文件加载技能定义。
    /// </summary>
    /// <param name="directoryPath">包含 *.skill.json 文件的目录路径。</param>
    /// <param name="filePattern">可选的文件搜索模式，默认为 "*.skill.json"。</param>
    /// <param name="recursive">是否递归搜索子目录，默认为 false。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public SkillLoadingConfig UseDirectory(string directoryPath, string filePattern = "*.skill.json", bool recursive = false)
    {
        _sources.Add(new FileBasedSkillSource(directoryPath, filePattern: filePattern, recursive: recursive));
        return this;
    }

    /// <summary>
    /// 添加标准技能来源，从指定目录扫描 <c>SKILL.md</c> 加载技能。
    /// </summary>
    /// <param name="directoryPath">
    /// 技能根目录。目录下每个子目录各含一个 <c>SKILL.md</c>，或该目录自身就含 <c>SKILL.md</c>。
    /// </param>
    /// <param name="recursive">是否递归扫描子目录，默认为 <c>true</c>。</param>
    /// <param name="validation">可选的校验参数；为 <c>null</c> 时使用规范默认值。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    /// <remarks>
    /// 与 <see cref="UseDirectory"/> 的区别在于技能形态：这里加载的是带 frontmatter 与捆绑资源的标准技能，
    /// 加载结果会在 <see cref="SkillDefinition.Body"/> 与 <see cref="SkillDefinition.Resources"/> 中携带二三层内容。
    /// 两类来源可以叠加，同名时后添加的覆盖先前的。
    /// </remarks>
    public SkillLoadingConfig UseStandardDirectory(
        string directoryPath,
        bool recursive = true,
        SkillValidationOptions? validation = null)
    {
        _sources.Add(new StandardSkillSource(new StandardSkillOptions
        {
            DirectoryPath = directoryPath,
            Recursive = recursive,
            Validation = validation ?? new SkillValidationOptions()
        }));

        return this;
    }

    /// <summary>
    /// 添加基于内存列表的技能来源，将已有的 <see cref="SkillDefinition"/> 集合包装为来源。
    /// </summary>
    /// <param name="sourceName">此来源的标识名称。</param>
    /// <param name="skills">要包含的技能定义集合。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public SkillLoadingConfig UseMemory(string sourceName, IEnumerable<SkillDefinition> skills)
    {
        _sources.Add(new InMemorySkillSource(sourceName, skills));
        return this;
    }

    /// <summary>
    /// 添加自定义的 <see cref="ISkillSource"/> 实现作为技能来源。
    /// </summary>
    /// <param name="source">自定义的技能来源实现。</param>
    /// <returns>当前实例，支持链式调用。</returns>
    public SkillLoadingConfig UseSource(ISkillSource source)
    {
        _sources.Add(source);
        return this;
    }

    /// <summary>
    /// 根据当前配置构建最终的 <see cref="ISkillSource"/> 实例。
    /// </summary>
    /// <returns>
    /// 若只配置了单个来源则直接返回该来源；
    /// 若配置了多个来源则返回聚合后的 <see cref="CompositeSkillSource"/>；
    /// 若未配置任何来源则返回空的 <see cref="InMemorySkillSource"/>。
    /// </returns>
    public ISkillSource BuildSource()
    {
        switch (_sources.Count)
        {
            case 0:
                return new InMemorySkillSource("empty", Array.Empty<SkillDefinition>());
            case 1:
                return _sources[0];
            default:
                var composite = new CompositeSkillSource();
                foreach (var s in _sources)
                    composite.Add(s);
                return composite;
        }
    }

    /// <summary>
    /// 根据当前配置执行加载操作，并将所有技能注册到指定的注册表中。
    /// </summary>
    /// <param name="registry">目标技能注册表，加载结果将被写入此表。</param>
    /// <returns>实际加载并注册的技能数量。</returns>
    public int LoadInto(ISkillRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var source = BuildSource();
        var skills = source.LoadSkills().ToList();

        foreach (var skill in skills)
            registry.Register(skill);

        return skills.Count;
    }

    /// <summary>
    /// 异步执行加载，并将所有技能注册到指定的注册表中。
    /// </summary>
    /// <param name="registry">目标技能注册表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际加载并注册的技能数量。</returns>
    /// <remarks>
    /// 当来源包含文件或网络访问时应使用此方法，避免阻塞调用线程。
    /// </remarks>
    public async Task<int> LoadIntoAsync(ISkillRegistry registry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var source = BuildSource();
        var skills = await source.LoadSkillsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var skill in skills)
            registry.Register(skill);

        return skills.Count;
    }
}
