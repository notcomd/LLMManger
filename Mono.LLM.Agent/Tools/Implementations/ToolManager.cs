// ============================================================================
// 文件：ToolManager.cs
// 用途：管理工具函数的注册、查询和执行，是 LLM 工具调用（Function Calling）机制的核心组件。
// 实现方：ToolManager
// 调用方：SkillExecutor、StreamingManager、McpToolBridge、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Mono.LLM.Agent.Tools;

/// <summary>
/// 管理工具函数的注册、查询和执行，是 LLM 工具调用（Function Calling）机制的核心组件。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ToolManager"/> 提供完整的工具生命周期管理能力：
/// </para>
/// <list type="number">
///   <item><b>注册</b> — 通过 <see cref="RegisterTool"/> 或 <see cref="RegisterSimpleTool"/> 将自定义逻辑注册为可被 AI 调用的工具；</item>
///   <item><b>转换</b> — 通过 <see cref="GetAITools"/> 将已注册工具转换为 Microsoft.Extensions.AI 框架的 <see cref="AITool"/> 列表；</item>
///   <item><b>执行</b> — 通过 <c>ExecuteToolAsync</c> 按名称调用已注册的工具并返回统一的 <see cref="ToolResult"/>。</item>
/// </list>
/// <para>
/// 执行前会按注册时的参数定义做校验（必填、类型、枚举），校验失败不会调用 handler，
/// 而是把可读的错误描述返回给模型，使其能自行修正。执行受 <see cref="ToolExecutionOptions.Timeout"/>
/// 约束，超时被转换为失败结果而非异常。
/// </para>
/// <para>
/// 线程安全：所有公开方法均使用内置锁保护并发访问。
/// </para>
/// </remarks>
public class ToolManager
{
    /// <summary>内部结构：将工具元数据与执行处理器绑定在一起，避免双集合一致性问题。</summary>
    private sealed class ToolEntry
    {
        public required ToolRegistration Registration { get; init; }
        public required Func<JsonElement, CancellationToken, Task<ToolResult>> Handler { get; init; }
    }

    private readonly List<ToolEntry> _entries = new();
    private readonly object _lock = new();
    private readonly ToolExecutionOptions _options;

    /// <summary>注册表版本号，每次增删工具自增，用于让缓存失效。</summary>
    private int _version;

    private List<AITool>? _aiToolCache;
    private int _aiToolCacheVersion = -1;
    private ToolTree? _toolTreeCache;
    private int _toolTreeCacheVersion = -1;

    /// <summary>
    /// 初始化 <see cref="ToolManager"/> 的新实例。
    /// </summary>
    /// <param name="options">
    /// 执行选项（超时、并发度、是否校验参数）。为 <c>null</c> 时使用默认值。
    /// </param>
    public ToolManager(ToolExecutionOptions? options = null)
    {
        _options = options ?? new ToolExecutionOptions();
    }

    /// <summary>获取当前生效的执行选项。</summary>
    public ToolExecutionOptions Options => _options;

    /// <summary>
    /// 获取注册表版本号；每次注册或注销工具都会自增。
    /// </summary>
    /// <remarks>
    /// <see cref="GetAITools"/> 与 <see cref="GetToolTree"/> 的缓存以此为失效依据，
    /// 也可用于观测运行期工具集是否发生变化。
    /// </remarks>
    public int Version => _version;

    /// <summary>
    /// 每次工具执行结束后触发，可用于日志、指标或审计。
    /// </summary>
    /// <remarks>
    /// 订阅方抛出的异常会被吞掉，不会影响工具本身的执行结果。
    /// </remarks>
    public event EventHandler<ToolExecutedEventArgs>? ToolExecuted;

    /// <summary>获取当前已注册的所有工具的只读列表。</summary>
    public IReadOnlyList<ToolRegistration> Tools
    {
        get
        {
            lock (_lock)
                return _entries.Select(e => e.Registration).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// 注册一个具有完整参数定义的自定义工具。
    /// </summary>
    /// <param name="toolName">工具的唯一名称标识符。</param>
    /// <param name="description">工具功能的自然语言描述。</param>
    /// <param name="parameters">工具参数的定义列表。</param>
    /// <param name="handler">工具的实际执行逻辑委托。</param>
    /// <param name="overwrite">
    /// 同名工具已存在时是否覆盖。为 <c>false</c> 时抛出 <see cref="InvalidOperationException"/>，
    /// 便于在启动阶段发现命名冲突而不是被静默覆盖。
    /// </param>
    public void RegisterTool(
        string toolName,
        string description,
        IEnumerable<ToolParameterInfo> parameters,
        Func<JsonElement, CancellationToken, Task<ToolResult>> handler,
        bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(handler);

        var parameterList = parameters.ToList();
        EnsureCanRegister(toolName, overwrite);

        lock (_lock)
        {
            _entries.RemoveAll(e => e.Registration.Name == toolName);
            _version++;

            _entries.Add(new ToolEntry
            {
                Registration = new ToolRegistration
                {
                    Name = toolName,
                    Description = description,
                    Parameters = parameterList
                },
                Handler = handler
            });
        }
    }

    /// <summary>
    /// 注册一个仅接收单个字符串输入的简化工具（便捷方法）。
    /// </summary>
    /// <param name="toolName">工具的唯一名称标识符。</param>
    /// <param name="description">工具功能的自然语言描述。</param>
    /// <param name="handler">简化的执行逻辑委托，接收字符串输入返回字符串结果。</param>
    /// <param name="requiresInput">
    /// 该工具是否需要输入。为 <c>false</c> 时不声明任何参数，handler 收到空字符串。
    /// 对于「获取当前时间」这类无参工具应设为 <c>false</c>，否则模型必须编造一个无意义的输入才能调用。
    /// </param>
    /// <param name="overwrite">同名工具已存在时是否覆盖。</param>
    public void RegisterSimpleTool(
        string toolName,
        string description,
        Func<string, Task<string>> handler,
        bool requiresInput = true,
        bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(handler);

        EnsureCanRegister(toolName, overwrite);

        var parameters = requiresInput
            ? new List<ToolParameterInfo>
            {
                new() { Name = "input", Type = "string", Description = description, Required = true }
            }
            : new List<ToolParameterInfo>();

        lock (_lock)
        {
            _entries.RemoveAll(e => e.Registration.Name == toolName);
            _version++;

            _entries.Add(new ToolEntry
            {
                Registration = new ToolRegistration
                {
                    Name = toolName,
                    Description = description,
                    Parameters = parameters
                },
                Handler = async (json, ct) =>
                {
                    var input = requiresInput ? ExtractInputFromJson(json) : string.Empty;
                    var result = await handler(input).ConfigureAwait(false);
                    return new ToolResult { Name = toolName, Result = result };
                }
            });
        }
    }

    /// <summary>
    /// 注册一个直接携带原始 JSON Schema 的工具。
    /// </summary>
    /// <param name="toolName">工具的唯一名称标识符。</param>
    /// <param name="description">工具功能的自然语言描述。</param>
    /// <param name="inputSchema">完整的参数 JSON Schema，必须是一个 <c>type: object</c> 的对象。</param>
    /// <param name="handler">工具的实际执行逻辑委托。</param>
    /// <param name="overwrite">同名工具已存在时是否覆盖。</param>
    /// <remarks>
    /// <para>
    /// 与 <see cref="RegisterTool"/> 的区别在于参数描述的来源：这里原样透传调用方给的 Schema，
    /// 因此支持嵌套对象、数组元素结构、<c>oneOf</c> 等 <see cref="ToolParameterInfo"/> 无法表达的约束。
    /// MCP 工具的 <c>inputSchema</c> 正是这种形态，桥接时不必做有损降级。
    /// </para>
    /// <para>
    /// 代价是失去本地参数校验：校验器只理解扁平的 <see cref="ToolParameterInfo"/>，
    /// 面对嵌套 Schema 会误判，因此这类工具的参数校验交由 handler 自身完成。
    /// </para>
    /// </remarks>
    public void RegisterRawTool(
        string toolName,
        string description,
        JsonElement inputSchema,
        Func<JsonElement, CancellationToken, Task<ToolResult>> handler,
        bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(handler);

        EnsureCanRegister(toolName, overwrite);

        // JsonElement 可能来自调用方已释放的 JsonDocument，必须 Clone 后才能长期持有。
        var schema = inputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? ToolSchemaBuilder.Build(Array.Empty<ToolParameterInfo>())
            : inputSchema.Clone();

        lock (_lock)
        {
            _entries.RemoveAll(e => e.Registration.Name == toolName);
            _version++;

            _entries.Add(new ToolEntry
            {
                Registration = new ToolRegistration
                {
                    Name = toolName,
                    Description = description,
                    Parameters = new List<ToolParameterInfo>(),
                    InputSchema = schema
                },
                Handler = handler
            });
        }
    }

    /// <summary>
    /// 注销一个已注册的工具。
    /// </summary>
    /// <param name="toolName">要移除的工具名称，不区分大小写。</param>
    /// <returns>确实移除了工具返回 <c>true</c>；工具不存在返回 <c>false</c>。</returns>
    /// <remarks>
    /// 用于动态工具集场景（例如 MCP 服务端宣告工具列表变化后，先下线旧工具再注册新工具）。
    /// 已经拿在手上的 <see cref="AITool"/> 快照不受影响，但后续按名称执行会返回「工具不存在」。
    /// </remarks>
    public bool UnregisterTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        lock (_lock)
        {
            var removed = _entries.RemoveAll(
                e => string.Equals(e.Registration.Name, toolName, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
                _version++;

            return removed;
        }
    }

    /// <summary>判断指定名称的工具是否已注册。</summary>
    public bool IsRegistered(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        lock (_lock)
            return _entries.Any(e => string.Equals(e.Registration.Name, toolName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>获取全部已注册工具的名称。</summary>
    public IReadOnlyList<string> GetRegisteredNames()
    {
        lock (_lock)
            return _entries.Select(e => e.Registration.Name).ToList();
    }

    /// <summary>
    /// 创建一个只包含指定工具的子集管理器。
    /// </summary>
    /// <param name="allowedToolNames">允许保留的工具名，不区分大小写。</param>
    /// <returns>新的 <see cref="ToolManager"/>，与当前实例共享工具处理器但注册表独立。</returns>
    /// <remarks>
    /// 用于把工具集裁剪到某个技能真正需要的范围。暴露的工具越少，模型选择错误工具的概率越低，
    /// 提示词占用的 token 也越少。
    /// </remarks>
    public ToolManager CreateScoped(IEnumerable<string> allowedToolNames)
    {
        ArgumentNullException.ThrowIfNull(allowedToolNames);

        var allowed = new HashSet<string>(
            allowedToolNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        var scoped = new ToolManager(_options.Clone());

        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                if (allowed.Contains(entry.Registration.Name))
                    scoped._entries.Add(entry);
            }
        }

        return scoped;
    }

    /// <summary>
    /// 将所有已注册的工具转换为 Microsoft.Extensions.AI 框架可识别的 <see cref="AITool"/> 列表。
    /// </summary>
    /// <returns>可直接赋值给 <see cref="ChatOptions.Tools"/> 的 <see cref="AITool"/> 集合。</returns>
    /// <remarks>
    /// <para>
    /// 每个工具被包装为 <see cref="ToolAIFunction"/>，其 <c>JsonSchema</c> 直接由
    /// 注册时提供的 <see cref="ToolParameterInfo"/> 生成，因此模型能看到真实的参数名、类型、
    /// 枚举取值、默认值与必填约束，而不是退化后的单一字符串参数。
    /// </para>
    /// <para>
    /// 结果按注册表版本缓存：Schema 生成与包装对象只在工具增删后重建一次。
    /// 每次调用返回新的列表副本，调用方可以自由修改列表，不会影响缓存。
    /// </para>
    /// </remarks>
    public List<AITool> GetAITools()
    {
        lock (_lock)
        {
            if (_aiToolCache is not null && _aiToolCacheVersion == _version)
                return new List<AITool>(_aiToolCache);

            var aiFunctions = new List<AITool>(_entries.Count);

            foreach (var entry in _entries)
            {
                var registration = entry.Registration;
                var schema = registration.InputSchema ?? ToolSchemaBuilder.Build(registration.Parameters);

                // 走统一的执行管线（校验 → 超时 → 事件 → 计时），
                // 这样无论上层是自行解析 FunctionCallContent，还是交给框架自动调用，行为一致。
                var captured = entry;
                aiFunctions.Add(new ToolAIFunction(
                    registration.Name,
                    registration.Description,
                    schema,
                    (arguments, cancellationToken) =>
                        ExecuteEntryAsync(captured, arguments, callId: null, cancellationToken)));
            }

            _aiToolCache = aiFunctions;
            _aiToolCacheVersion = _version;

            return new List<AITool>(aiFunctions);
        }
    }

    /// <summary>
    /// 获取按命名空间分组的工具树，按注册表版本缓存。
    /// </summary>
    /// <returns>与当前注册表内容一致的工具树。</returns>
    /// <remarks>
    /// MCP 工具名形如 <c>server__tool</c>，树里会聚合到 <c>server</c> 命名空间下；
    /// 没有前缀的工具归入「通用」。树只用于展示与裁剪，不参与模型选工具。
    /// </remarks>
    public ToolTree GetToolTree()
    {
        lock (_lock)
        {
            if (_toolTreeCache is not null && _toolTreeCacheVersion == _version)
                return _toolTreeCache;

            _toolTreeCache = ToolTree.Build(_entries.Select(entry => entry.Registration));
            _toolTreeCacheVersion = _version;

            return _toolTreeCache;
        }
    }

    /// <summary>
    /// 按名称异步执行已注册的工具（接受结构化 JSON 参数）。
    /// </summary>
    /// <param name="toolName">要执行的工具名称。</param>
    /// <param name="arguments">工具参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一的执行结果，失败时通过 <see cref="ToolResult.Error"/> 说明原因。</returns>
    public Task<ToolResult> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(toolName, arguments, callId: null, cancellationToken);

    /// <summary>
    /// 按名称异步执行已注册的工具（接受 JSON 字符串形式的参数）。
    /// </summary>
    /// <param name="toolName">要执行的工具名称。</param>
    /// <param name="argumentsJson">JSON 格式的参数对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>统一的执行结果。</returns>
    /// <remarks>
    /// 非法 JSON 会返回失败的 <see cref="ToolResult"/>，而不是向调用方抛出异常，
    /// 与其它执行失败路径保持一致。
    /// </remarks>
    public Task<ToolResult> ExecuteToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
        => ExecuteCallAsync(new ToolCallRequest(toolName, argumentsJson), cancellationToken);

    /// <summary>
    /// 批量执行多个工具调用，并按输入顺序返回结果。
    /// </summary>
    /// <param name="calls">待执行的调用集合。</param>
    /// <param name="maxDegreeOfParallelism">
    /// 覆盖本次调用的并发度。为 <c>null</c> 时使用 <see cref="ToolExecutionOptions.MaxDegreeOfParallelism"/>。
    /// 并发度为 1 时按顺序执行。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>与 <paramref name="calls"/> 顺序一致的执行结果。</returns>
    /// <remarks>
    /// 并行执行适合只读工具（查询、检索）；带有副作用的工具应保持串行，避免竞态。
    /// </remarks>
    public async Task<IReadOnlyList<ToolResult>> ExecuteToolCallsAsync(
        IEnumerable<ToolCallRequest> calls,
        int? maxDegreeOfParallelism = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calls);

        var list = calls as IReadOnlyList<ToolCallRequest> ?? calls.ToList();
        if (list.Count == 0)
            return Array.Empty<ToolResult>();

        var degree = Math.Max(1, maxDegreeOfParallelism ?? _options.MaxDegreeOfParallelism);
        var results = new ToolResult[list.Count];

        if (degree == 1 || list.Count == 1)
        {
            for (int i = 0; i < list.Count; i++)
            {
                results[i] = await ExecuteCallAsync(list[i], cancellationToken).ConfigureAwait(false);
            }

            return results;
        }

        await Parallel.ForEachAsync(
            Enumerable.Range(0, list.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = degree,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                results[index] = await ExecuteCallAsync(list[index], token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return results;
    }

    private async Task<ToolResult> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        string? callId,
        CancellationToken cancellationToken)
    {
        ToolEntry? entry;
        lock (_lock)
        {
            entry = _entries.FirstOrDefault(
                e => string.Equals(e.Registration.Name, toolName, StringComparison.OrdinalIgnoreCase));
        }

        if (entry == null)
        {
            return new ToolResult
            {
                Name = toolName,
                ToolCallId = callId ?? string.Empty,
                IsSuccess = false,
                Error = $"Tool '{toolName}' not found."
            };
        }

        return await ExecuteEntryAsync(entry, arguments, callId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolResult> ExecuteCallAsync(ToolCallRequest call, CancellationToken cancellationToken)
    {
        JsonElement arguments;

        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new ToolResult
            {
                Name = call.Name,
                ToolCallId = call.CallId ?? string.Empty,
                IsSuccess = false,
                Error = $"Invalid JSON arguments: {ex.Message}"
            };
        }

        return await ExecuteToolAsync(call.Name, arguments, call.CallId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolResult> ExecuteEntryAsync(
        ToolEntry entry,
        JsonElement arguments,
        string? callId,
        CancellationToken cancellationToken)
    {
        var name = entry.Registration.Name;
        var parameters = entry.Registration.Parameters;
        var stopwatch = Stopwatch.StartNew();

        var effectiveArguments = _options.ApplyDefaultValues
            ? ToolArgumentValidator.ApplyDefaults(arguments, parameters)
            : arguments;

        // 原始 Schema 工具不声明 ToolParameterInfo，本地校验器无法理解嵌套结构，跳过即可。
        if (_options.ValidateArguments && parameters.Count > 0)
        {
            var errors = ToolArgumentValidator.Validate(effectiveArguments, parameters);
            if (errors.Count > 0)
            {
                stopwatch.Stop();
                return Failure(name, callId, "参数校验未通过：" + string.Join(" ", errors), stopwatch.Elapsed, effectiveArguments);
            }
        }

        using var timeoutSource = _options.HasTimeout
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        timeoutSource?.CancelAfter(_options.Timeout);
        var executionToken = timeoutSource?.Token ?? cancellationToken;

        try
        {
            var result = await entry.Handler(effectiveArguments, executionToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (string.IsNullOrEmpty(result.Name))
                result.Name = name;

            if (!string.IsNullOrEmpty(callId))
                result.ToolCallId = callId;

            result.Duration = stopwatch.Elapsed;
            RaiseExecuted(result, DescribeArguments(effectiveArguments));

            return result;
        }
        catch (OperationCanceledException) when (
            timeoutSource is { IsCancellationRequested: true } &&
            !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failure(
                name,
                callId,
                $"工具执行超时（>{_options.Timeout.TotalSeconds:0.##}s）。",
                stopwatch.Elapsed,
                effectiveArguments);
        }
        catch (OperationCanceledException)
        {
            // 调用方主动取消：向上传播，交由上层决定语义。
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(name, callId, ex.Message, stopwatch.Elapsed, effectiveArguments);
        }
    }

    private ToolResult Failure(string name, string? callId, string error, TimeSpan duration, JsonElement arguments)
    {
        var result = new ToolResult
        {
            Name = name,
            ToolCallId = callId ?? string.Empty,
            IsSuccess = false,
            Error = error,
            Duration = duration
        };

        RaiseExecuted(result, DescribeArguments(arguments));
        return result;
    }

    private static string? DescribeArguments(JsonElement arguments)
        => arguments.ValueKind == JsonValueKind.Undefined ? null : arguments.GetRawText();

    private void RaiseExecuted(ToolResult result, string? arguments = null)
    {
        var handler = ToolExecuted;
        if (handler == null)
            return;

        try
        {
            handler(this, new ToolExecutedEventArgs
            {
                ToolName = result.Name,
                CallId = string.IsNullOrEmpty(result.ToolCallId) ? null : result.ToolCallId,
                IsSuccess = result.IsSuccess,
                Duration = result.Duration,
                Error = result.Error,
                Arguments = arguments,
                Result = result.IsSuccess ? result.Result : null
            });
        }
        catch
        {
            // 订阅方的异常不应影响工具执行结果。
        }
    }

    private void EnsureCanRegister(string toolName, bool overwrite)
    {
        if (overwrite)
            return;

        if (IsRegistered(toolName))
        {
            throw new InvalidOperationException(
                $"Tool '{toolName}' is already registered. Pass overwrite: true to replace it.");
        }
    }

    private static string ExtractInputFromJson(JsonElement json)
    {
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in json.EnumerateObject())
            {
                if (prop.NameEquals("input") && prop.Value.ValueKind != JsonValueKind.Null)
                    return ExtractStringValue(prop.Value);
            }

            foreach (var prop in json.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Null)
                    return ExtractStringValue(prop.Value);
            }
        }

        if (json.ValueKind == JsonValueKind.String)
            return json.GetString() ?? string.Empty;

        return json.GetRawText();
    }

    private static string ExtractStringValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToString()
        };
    }
}
