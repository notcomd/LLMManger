using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Mono.LLM.Agent.Core;
using Mono.LLM.Agent.Harness.Agent;
using Mono.LLM.Agent.Harness.Cli;
using Mono.LLM.Agent.MultiAgent;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Harness.Interactive;

/// <summary>
/// agent 控制台：连续对话、按需调用工具、随时切换或重载技能。
/// </summary>
/// <remarks>
/// 这是给人看的入口，不是断言工具。它把库的几个子系统串成一条可见的链路：
/// 会话历史决定上下文，工具管理器决定模型能做什么，技能决定「以什么身份做、能用哪些工具」。
/// </remarks>
internal sealed class AgentConsole
{
    private const int Width = 64;
    private const int MaxResultPreview = 120;

    private readonly AgentSession _session;
    private readonly SkillLibrary _library;
    private readonly LLMClientFactory _factory;
    private readonly LLMConfiguration _configuration;
    private readonly TextWriter _out;
    private readonly TextReader _in;

    /// <summary>后台线程产生的提示（例如热重载），在下一次输入前统一打印，避免与流式输出抢行。</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _notices = new();

    private bool _showReasoning;
    private int _turns;

    private AgentConsole(
        AgentSession session,
        SkillLibrary library,
        LLMClientFactory factory,
        LLMConfiguration configuration,
        TextWriter output,
        TextReader input)
    {
        _session = session;
        _library = library;
        _factory = factory;
        _configuration = configuration;
        _out = output;
        _in = input;
    }

    /// <summary>投递一条后台提示，在下一次输入前打印。</summary>
    /// <param name="message">提示文本。</param>
    public void Notify(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            _notices.Enqueue(message);
    }

    /// <summary>启动控制台，返回进程退出码。</summary>
    public static async Task<int> RunAsync(AppOptions options, TextWriter output, TextReader input)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            WriteSetupGuide(output);
            return 1;
        }

        var factory = new LLMClientFactory();
        var configuration = LLMConfiguration.CreateDeepSeek(options.ApiKey, options.ModelId);
        configuration.MaxOutputTokens = options.MaxOutputTokens;

        var registry = new SkillRegistry();

        foreach (var skill in SkillCatalog.GetAll())
            registry.Register(skill);

        var library = new SkillLibrary(options.ResolveSkillsDirectory());
        var loaded = await library.ReloadAsync(registry).ConfigureAwait(false);

        // 工具在会话之前创建，并共享同一个注册表，因此 search_skills 永远看到最新技能列表。
        var tools = HarnessTools.Create(registry);

        using var session = new AgentSession(factory, configuration, tools, registry);
        var console = new AgentConsole(session, library, factory, configuration, output, input);

        console.WriteHeader(loaded);

        // 磁盘上改技能文件会自动同步，不必再敲 /reload。
        using var hotReload = new SkillHotReloader(
            registry,
            [library.Directory],
            [new FileBasedSkillSource(library.Directory, recursive: true)],
            new SkillHotReloadOptions { Debounce = TimeSpan.FromMilliseconds(400) });

        hotReload.Reloaded += (_, result) => console.Notify($"   ⟳ 技能热更新：{result}");

        if (library.Exists)
            hotReload.Start();

        await console.LoopAsync().ConfigureAwait(false);

        return 0;
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            while (_notices.TryDequeue(out var notice))
                _out.WriteLine(notice);

            _out.Write(_session.ActiveSkill is null ? "你 > " : $"你({_session.ActiveSkill.Name}) > ");

            var line = _in.ReadLine();

            if (line == null)
            {
                _out.WriteLine();
                return;
            }

            line = line.Trim();

            if (line.Length == 0)
                continue;

            if (line.StartsWith('/'))
            {
                if (await HandleCommandAsync(line).ConfigureAwait(false))
                    return;

                continue;
            }

            await AskAsync(line).ConfigureAwait(false);
        }
    }

    private async Task AskAsync(string input)
    {
        var wroteText = false;
        var reasoning = new StringBuilder();
        var messageCountBefore = _session.Conversation.MessageCount;
        var stopwatch = Stopwatch.StartNew();

        void OnTool(object? sender, ToolExecutedEventArgs args)
        {
            // 工具通常在正文之前被调用；若正文已经开始，先换行再打轨迹，避免糊在一行里。
            if (wroteText)
            {
                _out.WriteLine();
                wroteText = false;
            }

            _out.WriteLine(FormatToolCall(args));
        }

        void OnReasoning(string chunk) => reasoning.Append(chunk);

        void OnSkillsPlanned(SkillTurnPlan plan) =>
            _out.WriteLine($"   ⚡ 自动加载技能：{string.Join("、", plan.ActivatedSkills.Select(skill => skill.Name))}");

        _session.ToolExecuted += OnTool;
        _session.ReasoningReceived += OnReasoning;
        _session.SkillsPlanned += OnSkillsPlanned;

        try
        {
            await _session.SendAsync(input, chunk =>
            {
                if (!wroteText)
                {
                    _out.Write("AI > ");
                    wroteText = true;
                }

                _out.Write(chunk);
            }).ConfigureAwait(false);

            if (wroteText)
                _out.WriteLine();
            else
                _out.WriteLine("AI >（模型没有返回正文）");
        }
        catch (OperationCanceledException)
        {
            if (wroteText)
                _out.WriteLine();

            _out.WriteLine("   （已取消）");
            Rollback(messageCountBefore);
        }
        catch (Exception ex)
        {
            if (wroteText)
                _out.WriteLine();

            _out.WriteLine($"   ✗ 请求失败：{ex.GetType().Name}: {Indent(ex.Message)}");

            // 失败的一轮不留在上下文里，否则下一轮会看到一条没有回答的用户消息。
            Rollback(messageCountBefore);
            _out.WriteLine("   （本轮已撤回）");
        }
        finally
        {
            _session.ToolExecuted -= OnTool;
            _session.ReasoningReceived -= OnReasoning;
            _session.SkillsPlanned -= OnSkillsPlanned;
            stopwatch.Stop();
        }

        if (_showReasoning && reasoning.Length > 0)
            _out.WriteLine($"   💭 {Truncate(OneLine(reasoning.ToString()), 400)}");

        _turns++;
        _out.WriteLine($"   · {stopwatch.Elapsed.TotalSeconds:0.0}s · 第 {_turns} 轮 · 历史 {_session.Conversation.MessageCount} 条 · {ContextUsage()}");
        _out.WriteLine();
    }

    private async Task<bool> HandleCommandAsync(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (command)
        {
            case "/exit" or "/quit" or "/q":
                _out.WriteLine("再见。");
                return true;

            case "/help" or "/?":
                WriteHelp();
                return false;

            case "/skills":
                WriteSkills();
                return false;

            case "/use" or "/skill":
                UseSkill(argument);
                return false;

            case "/tools":
                WriteTools();
                return false;

            case "/reload":
                await ReloadSkillsAsync().ConfigureAwait(false);
                return false;

            case "/history":
                WriteHistory(argument);
                return false;

            case "/save":
                SaveTranscript(argument);
                return false;

            case "/reset" or "/clear":
                _session.Reset();
                _out.WriteLine("✓ 上下文已清空，技能保持不变");
                _out.WriteLine();
                return false;

            case "/reasoning":
                _showReasoning = !_showReasoning;
                _out.WriteLine(_showReasoning ? "✓ 已开启思考过程显示" : "✓ 已关闭思考过程显示");
                _out.WriteLine();
                return false;

            case "/team":
                await RunTeamAsync(argument).ConfigureAwait(false);
                return false;

            default:
                _out.WriteLine($"未知命令：{command}。输入 /help 查看可用命令。");
                _out.WriteLine();
                return false;
        }
    }

    private void UseSkill(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            _out.WriteLine("用法：/use <技能名>，或 /use off 清除当前技能。");
            _out.WriteLine();
            return;
        }

        try
        {
            if (argument.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                _session.ClearSkill();
                _out.WriteLine($"✓ 已清除技能，恢复默认设定与全部 {_session.ActiveTools.Tools.Count} 个工具");
                _out.WriteLine();
                return;
            }

            var skill = _session.UseSkill(argument);
            var scope = skill.AllowedTools.Count > 0
                ? $"工具 {_session.ActiveTools.Tools.Count} 个（{string.Join("、", _session.ActiveTools.GetRegisteredNames())}）"
                : $"工具不受限制（{_session.ActiveTools.Tools.Count} 个）";

            _out.WriteLine($"✓ 已切换到「{skill.Name}」· {scope} · 历史保留");
            _out.WriteLine();
        }
        catch (InvalidOperationException ex)
        {
            _out.WriteLine(ex.Message);
            _out.WriteLine();
        }
    }

    private async Task ReloadSkillsAsync()
    {
        var result = await _library.ReloadAsync(_session.Skills).ConfigureAwait(false);

        _out.WriteLine(
            $"✓ 技能已重载：文件 {result.Loaded} 个，下线 {result.Removed} 个，" +
            $"共 {_session.Skills.Count} 个（内置 {SkillCatalog.Count} · 文件 {_library.FileSkillCount}）");

        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors)
                _out.WriteLine($"   ! {Path.GetFileName(error.FilePath)}：{error.Message}");
        }

        _out.WriteLine();
    }

    private void WriteSkills()
    {
        var active = _session.ActiveSkill;

        _out.WriteLine($"技能 {_session.Skills.Count} 个（内置 {SkillCatalog.Count} · 文件 {_library.FileSkillCount}）" +
                       $"{(active is null ? string.Empty : $" · 当前：{active.Name}")}");

        foreach (var group in _session.Skills.GetAll().GroupBy(s => s.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            _out.WriteLine($"  [{group.Key}]");

            foreach (var skill in group.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                var marker = ReferenceEquals(skill, active) ? "*" : " ";
                var scope = skill.AllowedTools.Count > 0 ? $"（工具 {skill.AllowedTools.Count}）" : string.Empty;

                _out.WriteLine($"   {marker} {skill.Name}{scope} — {Truncate(OneLine(skill.Description), 48)}");
            }
        }

        _out.WriteLine();
    }

    private void WriteTools()
    {
        var tools = _session.ActiveTools.Tools;
        var scope = _session.ActiveSkill switch
        {
            null => string.Empty,
            { AllowedTools.Count: > 0 } skill => $" · 受「{skill.Name}」白名单约束",
            var skill => $" · 「{skill.Name}」未限制工具"
        };

        _out.WriteLine($"当前工具 {tools.Count} 个{scope}");

        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var parameters = tool.Parameters.Count == 0
                ? "无参数"
                : string.Join(", ", tool.Parameters.Select(p => $"{p.Name}: {p.Type}{(p.Required ? "（必填）" : string.Empty)}"));

            _out.WriteLine($"   {tool.Name}（{parameters}）");
            _out.WriteLine($"      {Truncate(OneLine(tool.Description), 64)}");
        }

        _out.WriteLine();
    }

    private void WriteHistory(string argument)
    {
        var count = int.TryParse(argument, out var parsed) && parsed > 0 ? parsed : 8;
        var history = _session.Conversation.History;

        _out.WriteLine($"历史 {history.Count} 条 · 显示最近 {Math.Min(count, history.Count)} 条");

        foreach (var message in history.TakeLast(count))
            _out.WriteLine($"   {RoleLabel(message.Role)} · {Truncate(OneLine(Describe(message)), 68)}");

        _out.WriteLine();
    }

    private void SaveTranscript(string argument)
    {
        var path = string.IsNullOrWhiteSpace(argument)
            ? Path.Combine(Directory.GetCurrentDirectory(), $"transcript-{DateTime.Now:yyyyMMdd-HHmmss}.md")
            : Path.GetFullPath(argument);

        var builder = new StringBuilder();
        builder.AppendLine("# Mono.LLM.Agent 会话记录");
        builder.AppendLine();
        builder.AppendLine($"- 模型：`{_configuration.ModelId}`");
        builder.AppendLine($"- 时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 技能：{_session.ActiveSkill?.Name ?? "（无）"}");
        builder.AppendLine();

        foreach (var message in _session.Conversation.History)
        {
            switch (message.Role)
            {
                case var role when role == ChatRole.System:
                    continue;

                case var role when role == ChatRole.User:
                    builder.AppendLine("## 你");
                    builder.AppendLine();
                    builder.AppendLine(message.Text);
                    break;

                case var role when role == ChatRole.Tool:
                    builder.AppendLine("## 工具结果");
                    builder.AppendLine();
                    builder.AppendLine("```");
                    builder.AppendLine(message.Text);
                    builder.AppendLine("```");
                    break;

                default:
                    builder.AppendLine("## AI");
                    builder.AppendLine();

                    var text = message.Text;

                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    builder.AppendLine(text);
                    break;
            }

            builder.AppendLine();
        }

        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, builder.ToString());
            _out.WriteLine($"✓ 已保存到 {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _out.WriteLine($"保存失败：{ex.Message}");
        }

        _out.WriteLine();
    }

    private async Task RunTeamAsync(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            _out.WriteLine("用法：/team <目标>，例如 /team 给一个待办应用设计数据模型");
            _out.WriteLine();
            return;
        }

        var coordinator = Agent("协调者", "拆解与整合", "你把目标拆成互不重叠的子任务，并整合成员结论。");
        var builder = Agent("实现者", "给出可执行方案", "你负责把方案落到具体步骤与关键取舍。");
        var reviewer = Agent("评审者", "判断是否完成", "你严格判断目标是否达成，指出缺口。");

        var team = new AgentTeam(_factory)
            .AddMembers([builder])
            .WithLeader(coordinator)
            .WithJudge(reviewer)
            .UseTeam(maxRounds: 2);

        _out.WriteLine("团队协作中…");

        try
        {
            var result = await team.RunAsync(task).ConfigureAwait(false);

            foreach (var round in result.TeamRounds)
                _out.WriteLine($"   第 {round.Round} 轮 · 子任务 {round.Plan.Count} 个 · 完成={round.Completed} · {round.Reason}");

            _out.WriteLine();
            _out.WriteLine(result.FinalOutput);
            _out.WriteLine();
            _out.WriteLine($"   · {result.TotalTokens} tokens · {result.Duration.TotalSeconds:0.0}s");
        }
        catch (Exception ex)
        {
            _out.WriteLine($"团队执行失败：{ex.GetType().Name}: {ex.Message}");
        }

        _out.WriteLine();
    }

    private AgentDefinition Agent(string name, string role, string prompt) => new()
    {
        Name = name,
        Role = role,
        SystemPrompt = prompt,
        Configuration = _configuration
    };

    private void WriteHeader(SkillReloadResult loaded)
    {
        _out.WriteLine(new string('═', Width));
        _out.WriteLine("  Mono.LLM.Agent  ·  Agent Console");
        _out.WriteLine(new string('═', Width));
        _out.WriteLine($"  模型      {_configuration.ModelId}");
        _out.WriteLine($"  技能      {_session.Skills.Count}（内置 {SkillCatalog.Count} · 文件 {_library.FileSkillCount}）");
        _out.WriteLine($"  工具      {_session.ActiveTools.Tools.Count}");
        _out.WriteLine($"  技能目录  {ShortenPath(_library.Directory)}");
        _out.WriteLine("  上下文    32k tokens，超出后自动裁剪最早的对话");
        _out.WriteLine();

        if (loaded.Errors.Count > 0)
        {
            foreach (var error in loaded.Errors)
                _out.WriteLine($"  ! {Path.GetFileName(error.FilePath)}：{error.Message}");

            _out.WriteLine();
        }

        _out.WriteLine("  连续对话即可；模型会自行决定是否调用工具，工具轨迹以 ⚙ 显示在回答之前。");
        _out.WriteLine("  /help 查看命令，/exit 退出");
        _out.WriteLine();
        _out.WriteLine("  试试：帮我记住，会议是周三下午三点");
        _out.WriteLine("        (12 + 8) * 3 等于多少");
        _out.WriteLine("        /use 理财精算");
        _out.WriteLine();
    }

    private void WriteHelp()
    {
        _out.WriteLine("命令");
        _out.WriteLine("  /skills            列出全部技能（* 为当前技能）");
        _out.WriteLine("  /use <技能名>      切换技能：替换角色设定，并按白名单收窄工具");
        _out.WriteLine("  /use off           清除技能，恢复默认设定与全部工具");
        _out.WriteLine("  /tools             列出当前可用工具及其参数");
        _out.WriteLine("  /reload            重新扫描技能目录（新增/修改/删除都会生效）");
        _out.WriteLine("  /history [n]       查看最近 n 条历史，默认 8");
        _out.WriteLine("  /save [文件]       导出会话为 Markdown，默认写入当前目录");
        _out.WriteLine("  /reset             清空上下文，保留技能");
        _out.WriteLine("  /reasoning         开/关思考过程显示");
        _out.WriteLine("  /team <目标>       跑一次多智能体团队（协调 → 执行 → 评审）");
        _out.WriteLine("  /exit              退出");
        _out.WriteLine();
        _out.WriteLine("技能文件放在 skills/ 下，格式为 *.skill.json：");
        _out.WriteLine("""  { "name": "…", "description": "…", "systemPrompt": "…", "allowedTools": ["calc"] }""");
        _out.WriteLine();
    }

    private string ContextUsage()
    {
        var tokens = _session.Conversation.History.Sum(message => EstimateTokens(message.Text));
        var limit = _session.Conversation.ContextTokenLimit;

        return $"上下文约 {tokens:N0}/{limit / 1000.0:0.#}k tokens";
    }

    private void Rollback(int messageCount)
    {
        while (_session.Conversation.MessageCount > messageCount)
            _session.Conversation.RemoveLastMessage();
    }

    private static string FormatToolCall(ToolExecutedEventArgs args)
    {
        var arguments = FormatArguments(args.Arguments);
        var status = args.IsSuccess ? "成功" : "失败";

        var header = $"   ⚙ {args.ToolName}{arguments} · {status} · {args.Duration.TotalMilliseconds:0}ms";

        var detail = args.IsSuccess ? args.Result : args.Error;

        return string.IsNullOrWhiteSpace(detail)
            ? header
            : header + Environment.NewLine + $"     ↳ {Truncate(OneLine(detail), MaxResultPreview)}";
    }

    private static string FormatArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return $"({Truncate(OneLine(json), 48)})";

            var parts = document.RootElement
                .EnumerateObject()
                .Select(property => $"{property.Name}={Shorten(property.Value)}");

            return $"({Truncate(OneLine(string.Join(", ", parts)), 56)})";
        }
        catch (JsonException)
        {
            return $"({Truncate(OneLine(json), 48)})";
        }
    }

    private static string Shorten(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Array => $"[{value.GetArrayLength()} 项]",
        JsonValueKind.Object => "{…}",
        _ => value.ToString()
    };

    private static string Describe(ChatMessage message)
    {
        if (message.Role == ChatRole.Tool)
            return message.Text ?? string.Empty;

        var parts = message.Contents.Select(content => content switch
        {
            FunctionCallContent call => $"[调用 {call.Name}]",
            TextReasoningContent => "[思考]",
            TextContent text => text.Text,
            _ => string.Empty
        });

        var text = string.Join(" ", parts).Trim();

        return string.IsNullOrEmpty(text) ? "（空）" : text;
    }

    private static string RoleLabel(ChatRole role) =>
        role == ChatRole.System ? "系统"
        : role == ChatRole.User ? "你"
        : role == ChatRole.Tool ? "工具"
        : "AI";

    private static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var cjk = 0;

        foreach (var ch in text)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF)
                cjk++;
        }

        return Math.Max(1, cjk + (Math.Max(0, text.Length - cjk) / 4));
    }

    private static string ShortenPath(string path)
    {
        var current = Directory.GetCurrentDirectory();

        if (path.StartsWith(current, StringComparison.OrdinalIgnoreCase))
        {
            var relative = path[current.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (relative.Length > 0)
                return relative;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..]
            : path;
    }

    private static string Indent(string text) => text.ReplaceLineEndings(Environment.NewLine + "     ");

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private static void WriteSetupGuide(TextWriter output)
    {
        output.WriteLine(new string('═', Width));
        output.WriteLine("  未检测到 DEEPSEEK_API_KEY");
        output.WriteLine(new string('═', Width));
        output.WriteLine();
        output.WriteLine("  这个演示需要连接真实模型。请先设置环境变量：");
        output.WriteLine();
        output.WriteLine("""    Windows        setx DEEPSEEK_API_KEY "sk-xxxx"     （新开终端生效）""");
        output.WriteLine("""    macOS / Linux  export DEEPSEEK_API_KEY='sk-xxxx'""");
        output.WriteLine();
        output.WriteLine("  密钥只从环境变量读取，不会写入任何文件。");
        output.WriteLine();
    }
}
