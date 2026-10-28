// ============================================================================
// 文件：SkillHotReloader.cs
// 用途：技能热重载器：用 FileSystemWatcher 监视技能目录，文件变化后自动同步注册表。
// 实现方：SkillHotReloader
// 调用方：DI 装配（watchForChanges）、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using Microsoft.Extensions.Logging;

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 技能热重载的参数。
/// </summary>
public sealed class SkillHotReloadOptions
{
    /// <summary>
    /// 获取或设置事件防抖窗口，默认 300 毫秒。
    /// </summary>
    /// <remarks>
    /// 保存一个文件往往连续触发多次 <c>Changed</c>（写入、刷新、关闭），
    /// 防抖把这一串事件合并成一次重载。
    /// </remarks>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>获取或设置是否递归监视子目录，默认 <c>true</c>。</summary>
    public bool IncludeSubdirectories { get; set; } = true;

    /// <summary>
    /// 获取或设置是否把资源文件的新增/删除也算作变更，默认 <c>true</c>。
    /// </summary>
    /// <remarks>
    /// 资源内容改动不需要重载（读取时才读盘），但资源清单变了要重算，
    /// 否则模型看到的资源列表会过时。
    /// </remarks>
    public bool WatchResourceFiles { get; set; } = true;
}

/// <summary>
/// 一次热重载的结果。
/// </summary>
public sealed class SkillHotReloadResult
{
    /// <summary>获取重载发生的时刻。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>获取新增的技能数。</summary>
    public int Added { get; init; }

    /// <summary>获取内容有变化、被替换的技能数。</summary>
    public int Updated { get; init; }

    /// <summary>获取被移除（或被还原为原定义）的技能数。</summary>
    public int Removed { get; init; }

    /// <summary>获取本次重载产生的诊断，包含坏文件与来源加载失败的原因。</summary>
    public IReadOnlyList<SkillDiagnostic> Diagnostics { get; init; } = Array.Empty<SkillDiagnostic>();

    /// <summary>获取是否发生了实际变更。</summary>
    public bool HasChanges => Added + Updated + Removed > 0;

    /// <summary>返回 <c>+新增 ~更新 -移除</c> 形式的摘要，便于日志。</summary>
    /// <returns>重载摘要。</returns>
    public override string ToString() => $"+{Added} ~{Updated} -{Removed}";
}

/// <summary>
/// 技能热重载器：用 <see cref="FileSystemWatcher"/> 监视技能目录，文件变化后自动同步注册表。
/// </summary>
/// <remarks>
/// <para>
/// 重载采用「全量重读 + 差异对账」而不是按事件增量修改：
/// 技能数量通常在几十个量级，重读成本远低于增量状态机出错的风险。
/// 每个技能用内容指纹比对，因此保存一个没改内容的文件不会产生「更新」。
/// </para>
/// <para>
/// 对账只在重载器自己加载过的技能上生效（内部记录 <c>owned</c> 集合）。
/// 文件技能覆盖了同名内置技能时，删除文件会把内置定义还原回来，而不是把它一起删掉。
/// </para>
/// <para>
/// 来源整体加载失败（目录被删、权限变化）时不会清空注册表——那通常是一次瞬时故障，
/// 把技能全下线只会让问题更难排查。单个文件损坏则按来源自身的语义处理：该技能下线，原因写进诊断。
/// </para>
/// <para>线程安全：重载串行执行，事件合并到防抖窗口内的一次调用。</para>
/// </remarks>
public sealed class SkillHotReloader : IDisposable
{
    private readonly ISkillRegistry _registry;
    private readonly IReadOnlyList<ISkillSource> _sources;
    private readonly IReadOnlyList<string> _directories;
    private readonly SkillHotReloadOptions _options;
    private readonly ILogger<SkillHotReloader>? _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkillDefinition> _shadowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();

    private Timer? _debounce;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="SkillHotReloader"/>。
    /// </summary>
    /// <param name="registry">要同步的技能注册表。</param>
    /// <param name="directories">要监视的目录；不存在的目录会被跳过。</param>
    /// <param name="fileSources">文件型技能来源（JSON 与标准技能），每次重载都会重新读取。</param>
    /// <param name="options">热重载参数；为 <c>null</c> 时使用默认值。</param>
    /// <param name="logger">可选的日志记录器。</param>
    /// <exception cref="ArgumentNullException">
    /// 当 <paramref name="registry"/>、<paramref name="directories"/> 或 <paramref name="fileSources"/> 为 <c>null</c> 时抛出。
    /// </exception>
    public SkillHotReloader(
        ISkillRegistry registry,
        IEnumerable<string> directories,
        IEnumerable<ISkillSource> fileSources,
        SkillHotReloadOptions? options = null,
        ILogger<SkillHotReloader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(fileSources);

        _registry = registry;
        _options = options ?? new SkillHotReloadOptions();

        if (_options.Debounce <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "防抖窗口必须为正数。");

        _directories = directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _sources = fileSources.ToList();
        _logger = logger;
    }

    /// <summary>获取被监视的目录（绝对路径）。</summary>
    public IReadOnlyList<string> Directories => _directories;

    /// <summary>获取是否正在监视。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _watchers.Count > 0;
        }
    }

    /// <summary>获取当前由文件提供、可被热重载移除的技能名。</summary>
    public IReadOnlyList<string> OwnedSkills
    {
        get
        {
            lock (_sync)
                return _owned.ToList();
        }
    }

    /// <summary>在文件变化导致注册表被更新后触发。订阅方异常不会影响重载。</summary>
    public event EventHandler<SkillHotReloadResult>? Reloaded;

    /// <summary>
    /// 开始监视目录。重复调用无副作用。
    /// </summary>
    /// <exception cref="ObjectDisposedException">实例已释放时抛出。</exception>
    /// <remarks>
    /// 启动前已存在的技能由调用方先加载（例如依赖注入装配时）。
    /// 本方法只负责「变化之后」的同步，不主动做首次加载。
    /// </remarks>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_watchers.Count > 0)
                return;

            _debounce ??= new Timer(_ => _ = ReloadSafelyAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            foreach (var directory in _directories)
            {
                if (!Directory.Exists(directory))
                {
                    _logger?.LogWarning("技能目录不存在，跳过监视：{Directory}", directory);
                    continue;
                }

                var watcher = new FileSystemWatcher(directory)
                {
                    IncludeSubdirectories = _options.IncludeSubdirectories,
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };

                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;

                _watchers.Add(watcher);
            }

            if (_watchers.Count == 0 && _directories.Count > 0)
                _logger?.LogWarning("技能热重载未监视任何目录，请检查目录是否存在：{Directories}", string.Join(", ", _directories));

            _logger?.LogInformation("技能热重载已启动，监视 {Count} 个目录", _watchers.Count);
        }
    }

    /// <summary>停止监视。重复调用无副作用，可再次 <see cref="Start"/>。</summary>
    public void Stop()
    {
        lock (_sync)
        {
            DisposeWatchers();
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    /// <summary>
    /// 立即重载一次，不依赖文件事件。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次重载的结果。</returns>
    /// <remarks>
    /// 供测试、手动刷新命令或「应用启动后补一次加载」使用。
    /// </remarks>
    public async Task<SkillHotReloadResult> ReloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        SkillHotReloadResult result;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            result = await ReloadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        // 事件在锁外触发，避免订阅方回调重载器造成死锁。
        if (result.HasChanges)
            RaiseReloaded(result);
        else if (result.Diagnostics.Count > 0)
            _logger?.LogWarning("技能重载无变更，但存在诊断：{Diagnostics}",
                string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.ToString())));

        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            DisposeWatchers();
            _debounce?.Dispose();
            _debounce = null;
        }

        _gate.Dispose();
    }

    private async Task<SkillHotReloadResult> ReloadCoreAsync(CancellationToken cancellationToken)
    {
        var desired = new Dictionary<string, SkillDefinition>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<SkillDiagnostic>();
        var failed = false;

        foreach (var source in _sources)
        {
            IReadOnlyList<SkillDefinition> loaded;

            try
            {
                loaded = await source.LoadSkillsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed = true;
                diagnostics.Add(new SkillDiagnostic(
                    SkillDiagnosticCode.FileReadFailed,
                    SkillDiagnosticSeverity.Error,
                    $"来源 {source.SourceName} 加载失败：{ex.Message}",
                    source.SourceName));
                continue;
            }

            // 同名技能后加载的胜出，与 CompositeSkillSource 的语义一致。
            foreach (var skill in loaded)
                desired[skill.Name] = skill;

            diagnostics.AddRange(SkillSourceDiagnostics.Collect(source));
        }

        if (failed)
        {
            // 瞬时故障不清空注册表：保留上一份可用定义，等下一次事件再试。
            _logger?.LogWarning("技能来源加载失败，本次跳过对账以保留现有技能");

            return new SkillHotReloadResult { Diagnostics = diagnostics };
        }

        var added = 0;
        var updated = 0;
        var removed = 0;

        foreach (var (name, skill) in desired)
        {
            var existing = _registry.Get(name);

            if (existing is null)
            {
                _registry.Register(skill);
                MarkOwned(name);
                added++;
                continue;
            }

            if (SameContent(existing, skill))
            {
                MarkOwned(name);
                continue;
            }

            if (!IsOwned(name))
            {
                // 首次覆盖外部定义（通常是同名内置技能），记下来以便文件删除后还原。
                _shadowed[name] = existing;
                _logger?.LogInformation("文件技能「{Skill}」覆盖了已有的同名定义", name);
            }

            _registry.Register(skill);
            MarkOwned(name);
            updated++;
        }

        foreach (var name in OwnedSnapshot().Where(name => !desired.ContainsKey(name)))
        {
            UnmarkOwned(name);

            if (_shadowed.Remove(name, out var original))
            {
                _registry.Register(original);
                _logger?.LogInformation("文件技能「{Skill}」已删除，还原为原定义", name);
            }
            else
            {
                _registry.Unregister(name);
            }

            removed++;
        }

        var result = new SkillHotReloadResult
        {
            Added = added,
            Updated = updated,
            Removed = removed,
            Diagnostics = diagnostics
        };

        if (result.HasChanges)
            _logger?.LogInformation("技能热重载完成：{Summary}", result.ToString());

        return result;
    }

    private static bool SameContent(SkillDefinition left, SkillDefinition right) =>
        ReferenceEquals(left, right)
        || SkillTreeCache.ComputeFingerprint([left]) == SkillTreeCache.ComputeFingerprint([right]);

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldReact(e.FullPath, e.ChangeType))
            ScheduleReload();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (ShouldReact(e.OldFullPath, WatcherChangeTypes.Renamed)
            || ShouldReact(e.FullPath, WatcherChangeTypes.Renamed))
        {
            ScheduleReload();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger?.LogWarning(e.GetException(), "技能目录监视缓冲溢出或出错，触发一次全量重载");
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        lock (_sync)
        {
            if (_disposed || _debounce is null)
                return;

            _debounce.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ReloadSafelyAsync()
    {
        try
        {
            await ReloadAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 关闭过程中触发的最后一次事件，忽略即可。
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "技能热重载失败");
        }
    }

    private bool ShouldReact(string path, WatcherChangeTypes changeType)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var name = Path.GetFileName(path);

        if (name.Length == 0 || name[0] == '.')
            return false;

        if (name.EndsWith('~')
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsIgnoredDirectory(path))
            return false;

        var isSkillFile = name.Equals(SkillFallback.FileName, StringComparison.OrdinalIgnoreCase)
                          || name.EndsWith(".skill.json", StringComparison.OrdinalIgnoreCase);

        if (changeType == WatcherChangeTypes.Changed)
            return isSkillFile;

        return isSkillFile || _options.WatchResourceFiles;
    }

    private static bool IsIgnoredDirectory(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is ".git" or ".vs" or "bin" or "obj" or "node_modules" or "TestResults")
                return true;
        }

        return false;
    }

    private void MarkOwned(string name)
    {
        lock (_sync)
            _owned.Add(name);
    }

    private void UnmarkOwned(string name)
    {
        lock (_sync)
            _owned.Remove(name);
    }

    private bool IsOwned(string name)
    {
        lock (_sync)
            return _owned.Contains(name);
    }

    private List<string> OwnedSnapshot()
    {
        lock (_sync)
            return _owned.ToList();
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnChanged;
            watcher.Created -= OnChanged;
            watcher.Deleted -= OnChanged;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void RaiseReloaded(SkillHotReloadResult result)
    {
        try
        {
            Reloaded?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "技能热重载事件的订阅方抛出异常");
        }
    }
}
