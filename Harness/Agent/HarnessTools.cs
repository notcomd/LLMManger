using System.Globalization;
using System.Text.Json;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Harness.Agent;

/// <summary>
/// 演示用的工具集：时间、四则运算、会话便签、技能检索。
/// </summary>
/// <remarks>
/// 这些工具都是本地的、确定性的，不依赖外部服务，因此可以直接演示
/// 「模型决定调用工具 → 参数校验 → 执行 → 结果回填」的完整过程。
/// </remarks>
public static class HarnessTools
{
    /// <summary>
    /// 创建工具集。
    /// </summary>
    /// <param name="skills">用于技能检索的注册表，保证重载后工具返回的是最新列表。</param>
    /// <remarks>便签存放在本次创建的闭包中，进程退出即失效。</remarks>
    public static ToolManager Create(ISkillRegistry skills)
    {
        ArgumentNullException.ThrowIfNull(skills);

        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var tools = new ToolManager(new ToolExecutionOptions
        {
            Timeout = TimeSpan.FromSeconds(10),
            MaxDegreeOfParallelism = 1
        });

        tools.RegisterSimpleTool(
            "get_current_time",
            "获取当前本地时间（ISO 8601，含时区偏移）。用户问到「现在」「今天」时使用。",
            _ => Task.FromResult(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")),
            requiresInput: false);

        tools.RegisterTool(
            "calc",
            "计算四则运算表达式。只支持数字与 + - * / ( )，不要传入变量或函数。",
            [
                new ToolParameterInfo
                {
                    Name = "expression",
                    Type = "string",
                    Description = "例如 (12 + 8) * 3 / 2",
                    Required = true
                }
            ],
            (arguments, _) =>
            {
                var expression = ReadString(arguments, "expression") ?? string.Empty;

                if (!ArithmeticExpression.TryEvaluate(expression, out var value, out var error))
                    return Task.FromResult(Failure("calc", error!));

                return Task.FromResult(new ToolResult
                {
                    Name = "calc",
                    Result = $"{expression} = {value.ToString("0.####", CultureInfo.InvariantCulture)}"
                });
            });

        tools.RegisterTool(
            "remember",
            "把一条信息记进会话便签，后续可用 recall 取回。适合记录用户偏好、约定、待办。",
            [
                new ToolParameterInfo { Name = "key", Type = "string", Description = "便于检索的短标签", Required = true },
                new ToolParameterInfo { Name = "value", Type = "string", Description = "要记住的内容", Required = true }
            ],
            (arguments, _) =>
            {
                var key = ReadString(arguments, "key");
                var value = ReadString(arguments, "value");

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    return Task.FromResult(Failure("remember", "key 与 value 都不能为空。"));

                notes[key] = value;

                return Task.FromResult(new ToolResult
                {
                    Name = "remember",
                    Result = $"已记住「{key}」= {value}（当前共 {notes.Count} 条）"
                });
            });

        tools.RegisterTool(
            "recall",
            "按标签取回便签内容。标签不存在时返回失败，可提示用户尚未记录。",
            [
                new ToolParameterInfo { Name = "key", Type = "string", Description = "remember 时使用的标签", Required = true }
            ],
            (arguments, _) =>
            {
                var key = ReadString(arguments, "key");

                if (string.IsNullOrWhiteSpace(key))
                    return Task.FromResult(Failure("recall", "key 不能为空。"));

                return Task.FromResult(notes.TryGetValue(key, out var value)
                    ? new ToolResult { Name = "recall", Result = value }
                    : Failure("recall", $"没有找到「{key}」。已记录的标签：{string.Join("、", notes.Keys)}"));
            });

        tools.RegisterTool(
            "search_skills",
            "按关键词搜索可用技能，返回技能名与用途。用户问「你能做什么」「有没有 xx 相关的技能」时使用。",
            [
                new ToolParameterInfo
                {
                    Name = "query",
                    Type = "string",
                    Description = "关键词；留空则列出全部技能",
                    Required = false
                }
            ],
            (arguments, _) =>
            {
                var query = (ReadString(arguments, "query") ?? string.Empty).Trim();

                var matches = skills.GetAll()
                    .Where(skill => query.Length == 0
                        || skill.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || skill.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || skill.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(skill => skill.Name, StringComparer.Ordinal)
                    .ToList();

                if (matches.Count == 0)
                    return Task.FromResult(Failure("search_skills", $"没有匹配「{query}」的技能。"));

                var lines = matches
                    .Take(8)
                    .Select(skill => $"{skill.Name}（{skill.Category}）：{skill.Description}")
                    .ToList();

                if (matches.Count > 8)
                    lines.Add($"（共 {matches.Count} 个，仅列出前 8 个）");

                return Task.FromResult(new ToolResult
                {
                    Name = "search_skills",
                    Result = string.Join("\n", lines)
                });
            });

        return tools;
    }

    private static ToolResult Failure(string name, string error)
        => new() { Name = name, IsSuccess = false, Error = error };

    private static string? ReadString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString()
        };
    }
}

/// <summary>
/// 只支持四则运算与括号的递归下降求值器。
/// </summary>
/// <remarks>
/// 刻意不做通用表达式求值：不接受标识符、函数调用与任何可执行内容，
/// 因此即便模型传入了奇怪字符串，也只会得到一条语法错误。
/// </remarks>
internal static class ArithmeticExpression
{
    public static bool TryEvaluate(string expression, out double value, out string? error)
    {
        value = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "表达式为空。";
            return false;
        }

        foreach (var ch in expression)
        {
            if (!char.IsDigit(ch) && ch != '.' && "+-*/() ".IndexOf(ch) < 0)
            {
                error = $"不支持的字符「{ch}」，只允许数字与 + - * / ( )。";
                return false;
            }
        }

        var parser = new Parser(expression);

        try
        {
            value = parser.ParseExpression();
            parser.EnsureEnd();
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (DivideByZeroException)
        {
            error = "除数不能为零。";
            return false;
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _position;

        public Parser(string text) => _text = text;

        public double ParseExpression()
        {
            var value = ParseTerm();

            while (true)
            {
                if (Match('+'))
                    value += ParseTerm();
                else if (Match('-'))
                    value -= ParseTerm();
                else
                    return value;
            }
        }

        public void EnsureEnd()
        {
            SkipSpaces();

            if (_position < _text.Length)
                throw new FormatException($"位置 {_position + 1} 处存在多余字符「{_text[_position]}」。");
        }

        private double ParseTerm()
        {
            var value = ParseFactor();

            while (true)
            {
                if (Match('*'))
                {
                    value *= ParseFactor();
                }
                else if (Match('/'))
                {
                    var divisor = ParseFactor();

                    if (divisor == 0)
                        throw new DivideByZeroException();

                    value /= divisor;
                }
                else
                {
                    return value;
                }
            }
        }

        private double ParseFactor()
        {
            if (Match('-'))
                return -ParseFactor();

            if (Match('+'))
                return ParseFactor();

            if (Match('('))
            {
                var value = ParseExpression();

                if (!Match(')'))
                    throw new FormatException("括号不匹配。");

                return value;
            }

            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipSpaces();
            var start = _position;

            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.'))
                _position++;

            if (start == _position)
                throw new FormatException($"位置 {_position + 1} 处缺少数字。");

            var span = _text[start.._position];

            if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"无法解析数字「{span}」。");

            return value;
        }

        private bool Match(char expected)
        {
            SkipSpaces();

            if (_position >= _text.Length || _text[_position] != expected)
                return false;

            _position++;
            return true;
        }

        private void SkipSpaces()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
                _position++;
        }
    }
}
