// ============================================================================
// 文件：SkillOptions.cs
// 用途：技能系统相关的配置。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：DI 装配（AddMonoLLMAgent）、应用启动
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Mono.LLM.Agent.Skills;

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// 一个 <c>*.skill.json</c> 技能目录。
/// </summary>
public sealed class SkillDirectoryOptions
{
    /// <summary>获取或设置目录路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>获取或设置文件搜索模式，默认 <c>*.skill.json</c>。</summary>
    public string FilePattern { get; set; } = "*.skill.json";

    /// <summary>获取或设置是否递归扫描子目录，默认 <c>false</c>。</summary>
    public bool Recursive { get; set; }
}

/// <summary>
/// 一个标准技能目录（内含若干 <c>SKILL.md</c>）。
/// </summary>
public sealed class StandardSkillDirectoryOptions
{
    /// <summary>获取或设置目录路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>获取或设置是否递归扫描子目录，默认 <c>true</c>。</summary>
    public bool Recursive { get; set; } = true;

    /// <summary>获取或设置单个技能的捆绑资源文件数上限，默认 200。</summary>
    public int MaxResourceFiles { get; set; } = 200;
}

/// <summary>
/// 标准技能的元数据校验参数。
/// </summary>
public sealed class SkillValidationSettings
{
    /// <summary>获取或设置是否强制「小写字母 + 数字 + 连字符」的名称规范，默认 <c>false</c>（允许中文名）。</summary>
    public bool StrictNames { get; set; }

    /// <summary>获取或设置名称长度上限，默认 64。</summary>
    public int MaxNameLength { get; set; } = 64;

    /// <summary>获取或设置描述长度上限，默认 1024。</summary>
    public int MaxDescriptionLength { get; set; } = 1024;

    /// <summary>获取或设置正文建议行数上限，默认 500。</summary>
    public int MaxBodyLines { get; set; } = 500;

    /// <summary>获取或设置正文最短字符数，默认 20。</summary>
    public int MinBodyLength { get; set; } = 20;
}

/// <summary>
/// 技能按需加载工具的参数。
/// </summary>
public sealed class SkillToolsetSettings
{
    /// <summary>获取或设置加载技能的工具名，默认 <c>load_skill</c>。</summary>
    public string LoadSkillToolName { get; set; } = SkillToolset.DefaultLoadSkillToolName;

    /// <summary>获取或设置读取资源的工具名，默认 <c>read_skill_resource</c>。</summary>
    public string ReadResourceToolName { get; set; } = SkillToolset.DefaultReadResourceToolName;

    /// <summary>获取或设置单次资源读取的字节上限，默认 256 KB。</summary>
    public int MaxResourceBytes { get; set; } = SkillResourceReader.DefaultMaxBytes;
}

/// <summary>
/// 技能索引（渐进式披露第一层）的渲染参数。
/// </summary>
public sealed class SkillIndexSettings
{
    /// <summary>获取或设置索引标题，默认 <c>## 可用技能</c>。</summary>
    public string Heading { get; set; } = "## 可用技能";

    /// <summary>获取或设置索引前的使用说明。设为空可省略。</summary>
    public string Intro { get; set; } =
        "需要时再加载：先按描述判断相关性，相关时调用 `load_skill` 取完整说明，再按说明操作。";

    /// <summary>获取或设置是否按分类分组，默认 <c>true</c>。</summary>
    public bool GroupByCategory { get; set; } = true;

    /// <summary>获取或设置单条描述的最大字符数，默认 240。</summary>
    public int MaxDescriptionLength { get; set; } = 240;
}

/// <summary>
/// 技能系统相关的配置。
/// </summary>
/// <remarks>
/// 同时覆盖两条技能通道：<c>*.skill.json</c>（整段提示词常驻）与标准 <c>SKILL.md</c>
/// （索引常驻、正文与资源按需加载）。分区里的 <see cref="Validation"/>、<see cref="Toolset"/>、
/// <see cref="Index"/> 只作用于标准技能。
/// </remarks>
public sealed class SkillOptions
{
    /// <summary>获取或设置是否装载 <see cref="SkillCatalog"/> 里的内置技能，默认 <c>true</c>。</summary>
    public bool LoadBuiltInCatalog { get; set; } = true;

    /// <summary>获取或设置 JSON 技能目录列表。</summary>
    public List<SkillDirectoryOptions> Directories { get; set; } = new();

    /// <summary>获取或设置标准技能目录列表。</summary>
    public List<StandardSkillDirectoryOptions> StandardDirectories { get; set; } = new();

    /// <summary>
    /// 获取或设置是否把标准技能的索引拼到系统提示词之后，默认 <c>true</c>。
    /// </summary>
    /// <remarks>
    /// 只有声明了 <see cref="StandardDirectories"/> 时生效。关闭后模型看不到技能清单，
    /// 只能靠应用自己把索引放进提示词，或直接按名称激活技能。
    /// </remarks>
    public bool InjectIndexIntoSystemPrompt { get; set; } = true;

    /// <summary>
    /// 获取或设置是否按输入自动加载相关技能，默认 <c>true</c>。
    /// </summary>
    /// <remarks>
    /// 开启后由 <see cref="SkillRouter"/> 在每轮开始前挑技能，用户不再需要先执行「切换技能」。
    /// 关闭后仍可通过 <see cref="SkillToolset"/> 的 <c>load_skill</c> 由模型自行加载。
    /// </remarks>
    public bool AutoActivate { get; set; } = true;

    /// <summary>获取或设置单轮最多自动加载的技能数，默认 2。</summary>
    public int MaxAutoSkills { get; set; } = 2;

    /// <summary>获取或设置自动加载的最低匹配得分，默认 0.15。</summary>
    public double AutoActivateThreshold { get; set; } = 0.15;

    /// <summary>获取或设置自动注入正文的总字符预算，默认 8000。</summary>
    public int MaxAutoInstructionCharacters { get; set; } = 8000;

    /// <summary>
    /// 获取或设置是否用 <see cref="FileSystemWatcher"/> 监视技能目录并自动热重载，默认 <c>false</c>。
    /// </summary>
    /// <remarks>
    /// 开启后需要解析一次 <see cref="SkillHotReloader"/> 才会真正开始监视（依赖注入装配会启动它）。
    /// 只在开发期或允许运行期改技能的环境里开启：生产环境频繁改文件通常意味着发布流程有问题。
    /// </remarks>
    public bool WatchForChanges { get; set; }

    /// <summary>获取或设置热重载的事件防抖窗口，默认 300 毫秒。</summary>
    public TimeSpan WatchDebounce { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>获取或设置标准技能校验参数。</summary>
    public SkillValidationSettings Validation { get; set; } = new();

    /// <summary>获取或设置按需加载工具的参数。</summary>
    public SkillToolsetSettings Toolset { get; set; } = new();

    /// <summary>获取或设置索引渲染参数。</summary>
    public SkillIndexSettings Index { get; set; } = new();

    /// <summary>
    /// 按配置创建技能来源集合，顺序为：内置 → JSON 目录 → 标准目录。
    /// </summary>
    /// <returns>可直接交给 <see cref="SkillLoadingConfig.UseSource"/> 或手动注册的来源列表。</returns>
    /// <remarks>
    /// 顺序即覆盖顺序：后加载的同名技能胜出，因此磁盘上的技能可以覆盖内置定义。
    /// </remarks>
    public IReadOnlyList<ISkillSource> CreateSources()
    {
        var sources = new List<ISkillSource>();

        if (LoadBuiltInCatalog)
            sources.Add(new InMemorySkillSource("built-in", SkillCatalog.GetAll()));

        sources.AddRange(CreateFileSources());

        return sources;
    }

    /// <summary>
    /// 按配置创建文件型技能来源（JSON 目录 + 标准目录），不含内置技能。
    /// </summary>
    /// <returns>可交给 <see cref="SkillHotReloader"/> 的来源列表。</returns>
    /// <remarks>
    /// 热重载只面向磁盘：内置技能来自代码，改不了也不需要监视。
    /// </remarks>
    public IReadOnlyList<ISkillSource> CreateFileSources()
    {
        var sources = new List<ISkillSource>();

        foreach (var directory in Directories)
        {
            sources.Add(new FileBasedSkillSource(
                directory.Path,
                filePattern: directory.FilePattern,
                recursive: directory.Recursive));
        }

        foreach (var directory in StandardDirectories)
        {
            sources.Add(new StandardSkillSource(new StandardSkillOptions
            {
                DirectoryPath = directory.Path,
                Recursive = directory.Recursive,
                MaxResourceFiles = directory.MaxResourceFiles,
                Validation = ToValidationOptions()
            }));
        }

        return sources;
    }

    /// <summary>
    /// 获取需要监视的目录：JSON 技能目录与标准技能目录。
    /// </summary>
    /// <returns>去重后的目录路径。</returns>
    public IReadOnlyList<string> GetWatchDirectories() =>
        Directories.Select(directory => directory.Path)
            .Concat(StandardDirectories.Select(directory => directory.Path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>转换为运行时的标准技能校验参数。</summary>
    /// <returns><see cref="SkillValidationOptions"/> 实例。</returns>
    public SkillValidationOptions ToValidationOptions() => new()
    {
        StrictNames = Validation.StrictNames,
        MaxNameLength = Validation.MaxNameLength,
        MaxDescriptionLength = Validation.MaxDescriptionLength,
        MaxBodyLines = Validation.MaxBodyLines,
        MinBodyLength = Validation.MinBodyLength
    };

    /// <summary>转换为运行时的按需加载工具参数。</summary>
    /// <returns><see cref="SkillToolsetOptions"/> 实例。</returns>
    public SkillToolsetOptions ToToolsetOptions() => new()
    {
        LoadSkillToolName = Toolset.LoadSkillToolName,
        ReadResourceToolName = Toolset.ReadResourceToolName,
        MaxResourceBytes = Toolset.MaxResourceBytes
    };

    /// <summary>转换为运行时的索引渲染参数。</summary>
    /// <returns><see cref="SkillIndexOptions"/> 实例。</returns>
    public SkillIndexOptions ToIndexOptions() => new()
    {
        Heading = Index.Heading,
        Intro = Index.Intro,
        GroupByCategory = Index.GroupByCategory,
        MaxDescriptionLength = Index.MaxDescriptionLength
    };

    /// <summary>转换为运行时的自动路由参数。</summary>
    /// <returns><see cref="SkillRoutingOptions"/> 实例。</returns>
    public SkillRoutingOptions ToRoutingOptions() => new()
    {
        Enabled = AutoActivate,
        MaxSkills = MaxAutoSkills,
        MinScore = AutoActivateThreshold,
        MaxInstructionCharacters = MaxAutoInstructionCharacters,
        IncludeIndex = InjectIndexIntoSystemPrompt
    };

    /// <summary>
    /// 构建一个与本分区等价的 <see cref="SkillLoadingConfig"/>。
    /// </summary>
    /// <returns>可直接 <c>LoadInto</c> 注册表的加载配置。</returns>
    public SkillLoadingConfig ToLoadingConfig()
    {
        var config = new SkillLoadingConfig();

        if (LoadBuiltInCatalog)
            config.UseBuiltIn();

        foreach (var directory in Directories)
            config.UseDirectory(directory.Path, directory.FilePattern, directory.Recursive);

        foreach (var directory in StandardDirectories)
            config.UseStandardDirectory(directory.Path, directory.Recursive, ToValidationOptions());

        return config;
    }

    /// <summary>校验本分区。</summary>
    /// <returns>问题列表；完全合规时为空。</returns>
    public IReadOnlyList<AgentOptionsIssue> Validate() => Validate("skills");

    internal IReadOnlyList<AgentOptionsIssue> Validate(string prefix)
    {
        var issues = new List<AgentOptionsIssue>();

        for (var i = 0; i < Directories.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(Directories[i].Path))
                issues.Add(AgentOptionsIssue.Error($"{prefix}.directories[{i}].path", "技能目录路径不能为空。"));
        }

        for (var i = 0; i < StandardDirectories.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(StandardDirectories[i].Path))
                issues.Add(AgentOptionsIssue.Error($"{prefix}.standardDirectories[{i}].path", "技能目录路径不能为空。"));

            if (StandardDirectories[i].MaxResourceFiles < 1)
                issues.Add(AgentOptionsIssue.Error($"{prefix}.standardDirectories[{i}].maxResourceFiles", "资源文件上限至少为 1。"));
        }

        if (Validation.MaxNameLength < 1
            || Validation.MaxDescriptionLength < 1
            || Validation.MaxBodyLines < 1
            || Validation.MinBodyLength < 0)
        {
            issues.Add(AgentOptionsIssue.Error($"{prefix}.validation", "校验阈值必须为正数（最小正文长度可为 0）。"));
        }

        if (Toolset.MaxResourceBytes < 1)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.toolset.maxResourceBytes", "资源读取上限必须为正数。"));

        if (string.IsNullOrWhiteSpace(Toolset.LoadSkillToolName)
            || string.IsNullOrWhiteSpace(Toolset.ReadResourceToolName))
        {
            issues.Add(AgentOptionsIssue.Error($"{prefix}.toolset", "两个工具名都不能为空。"));
        }
        else if (string.Equals(
                     Toolset.LoadSkillToolName,
                     Toolset.ReadResourceToolName,
                     StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(AgentOptionsIssue.Error($"{prefix}.toolset", "加载工具与资源工具不能同名。"));
        }

        if (Index.MaxDescriptionLength < 1)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.index.maxDescriptionLength", "描述截断长度必须为正数。"));

        if (MaxAutoSkills < 0)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.maxAutoSkills", "自动加载技能数不能为负。"));

        if (AutoActivateThreshold is < 0 or > 1)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.autoActivateThreshold", "匹配阈值必须落在 0～1 之间。"));

        if (AutoActivate && MaxAutoInstructionCharacters < 1)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.maxAutoInstructionCharacters", "正文预算必须为正数。"));

        if (WatchForChanges && WatchDebounce <= TimeSpan.Zero)
            issues.Add(AgentOptionsIssue.Error($"{prefix}.watchDebounce", "防抖窗口必须为正数。"));

        return issues;
    }
}
