// ============================================================================
// 文件：SkillMarkdownParser.cs
// 用途：解析标准技能文件（SKILL.md）：YAML frontmatter + Markdown 正文。
// 实现方：SkillMarkdownParser
// 调用方：SkillRouter、SkillToolset、DI 装配、Harness
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Text;

namespace Mono.LLM.Agent.Skills;

/// <summary>
/// 解析标准技能文件（<c>SKILL.md</c>）：YAML frontmatter + Markdown 正文。
/// </summary>
/// <remarks>
/// <para>
/// 标准技能文件的形态是「一段 <c>---</c> 包裹的 YAML 元数据 + 一段 Markdown 指令」。
/// 本解析器只实现 frontmatter 实际用得到的 YAML 子集：标量、引号字符串、块标量
/// （<c>|</c> / <c>&gt;</c>）、行内列表（<c>[a, b]</c>）、行列表（<c>- a</c>）与一层嵌套映射。
/// 引入完整 YAML 库会把一个轻量技能模块绑上额外依赖，而 frontmatter 的结构远比通用 YAML 简单。
/// </para>
/// <para>
/// 解析过程不会抛异常：结构问题以 <see cref="SkillDiagnostic"/> 记录，正文与能解析出的字段照常返回，
/// 由调用方决定是拒绝加载还是降级使用。
/// </para>
/// </remarks>
public static class SkillMarkdownParser
{
    /// <summary>
    /// 解析技能 Markdown 文本。
    /// </summary>
    /// <param name="markdown">文件全文。</param>
    /// <param name="filePath">可选的文件路径，仅用于诊断信息定位。</param>
    /// <returns>
    /// 解析结果。<see cref="SkillParseResult.Metadata"/> 为 <c>null</c> 表示 frontmatter 缺失或不可解析。
    /// </returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="markdown"/> 为 <c>null</c> 时抛出。</exception>
    public static SkillParseResult Parse(string markdown, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var diagnostics = new List<SkillDiagnostic>();
        var text = markdown.Length > 0 && markdown[0] == '\uFEFF' ? markdown[1..] : markdown;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var start = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
                continue;

            if (lines[i].Trim() == "---")
                start = i;

            break;
        }

        if (start < 0)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.MissingFrontmatter,
                SkillDiagnosticSeverity.Error,
                "缺少 YAML frontmatter：SKILL.md 必须以 --- 开头，元数据结束后再用 --- 收尾。",
                filePath,
                1));

            return new SkillParseResult(null, text.Trim(), filePath, diagnostics);
        }

        var end = -1;

        for (var i = start + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed is "---" or "...")
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.UnclosedFrontmatter,
                SkillDiagnosticSeverity.Error,
                "frontmatter 没有结束标记：补一行 --- 与开头配对。",
                filePath,
                start + 1));

            return new SkillParseResult(null, string.Empty, filePath, diagnostics);
        }

        var yamlLines = new string[end - start - 1];
        Array.Copy(lines, start + 1, yamlLines, 0, yamlLines.Length);

        var body = string.Join("\n", lines.Skip(end + 1)).Trim();
        var firstYamlLine = start + 2;

        var root = SkillYaml.Parse(yamlLines, diagnostics, filePath, firstYamlLine);
        var metadata = BuildMetadata(root, diagnostics, filePath, firstYamlLine);

        return new SkillParseResult(metadata, body, filePath, diagnostics);
    }

    private static SkillMetadata BuildMetadata(
        Dictionary<string, object?> root,
        ICollection<SkillDiagnostic> diagnostics,
        string? filePath,
        int firstLine)
    {
        var metadataMap = ReadMap(root, "metadata", diagnostics, filePath, firstLine);
        var allowedTools = ReadStringList(root, "allowed-tools", "allowedTools", "allowed_tools");

        if (!metadataMap.ContainsKey("tags"))
        {
            var tags = ReadStringList(root, "tags");

            if (tags.Count > 0)
                metadataMap["tags"] = string.Join(", ", tags);
        }

        var version = ReadString(root, "version");

        return new SkillMetadata
        {
            Name = ReadString(root, "name").Trim(),
            Description = ReadString(root, "description").Trim(),
            License = ReadString(root, "license").Trim(),
            Version = string.IsNullOrWhiteSpace(version) ? "1.0" : version.Trim(),
            AllowedTools = allowedTools,
            Metadata = metadataMap
        };
    }

    private static string ReadString(Dictionary<string, object?> root, string key)
    {
        if (!root.TryGetValue(key, out var value) || value is null)
            return string.Empty;

        return value switch
        {
            string text => text,
            List<string> list => string.Join(", ", list),
            _ => string.Empty
        };
    }

    private static List<string> ReadStringList(Dictionary<string, object?> root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetValue(key, out var value) || value is null)
                continue;

            if (value is List<string> list)
            {
                return list
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .ToList();
            }

            if (value is string text)
            {
                return text
                    .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
        }

        return [];
    }

    private static Dictionary<string, string> ReadMap(
        Dictionary<string, object?> root,
        string key,
        ICollection<SkillDiagnostic> diagnostics,
        string? filePath,
        int firstLine)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetValue(key, out var value) || value is null)
            return result;

        if (value is string { Length: 0 })
            return result;

        if (value is not Dictionary<string, object?> map)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.InvalidFrontmatter,
                SkillDiagnosticSeverity.Warning,
                $"字段 '{key}' 应该是键值映射，实际内容被忽略。",
                filePath,
                firstLine));

            return result;
        }

        foreach (var (mapKey, mapValue) in map)
        {
            switch (mapValue)
            {
                case string text:
                    result[mapKey] = text.Trim();
                    break;
                case List<string> list:
                    result[mapKey] = string.Join(", ", list);
                    break;
                case null:
                    result[mapKey] = string.Empty;
                    break;
            }
        }

        return result;
    }
}

/// <summary>
/// <see cref="SkillMarkdownParser"/> 的解析结果。
/// </summary>
public sealed class SkillParseResult
{
    internal SkillParseResult(
        SkillMetadata? metadata,
        string body,
        string? filePath,
        IReadOnlyList<SkillDiagnostic> diagnostics)
    {
        Metadata = metadata;
        Body = body;
        FilePath = filePath;
        Diagnostics = diagnostics;
    }

    /// <summary>获取 frontmatter 元数据；无法解析时为 <c>null</c>。</summary>
    public SkillMetadata? Metadata { get; }

    /// <summary>获取 frontmatter 之后的 Markdown 正文（渐进式披露的第二层）。</summary>
    public string Body { get; }

    /// <summary>获取被解析的文件路径。</summary>
    public string? FilePath { get; }

    /// <summary>获取解析过程中产生的全部诊断。</summary>
    public IReadOnlyList<SkillDiagnostic> Diagnostics { get; }

    /// <summary>获取解析是否成功（元数据可用且没有错误级诊断）。</summary>
    public bool Success => Metadata != null && !Diagnostics.HasErrors();
}

/// <summary>
/// frontmatter 的 YAML 子集解析器。
/// </summary>
/// <remarks>
/// 只服务于 <see cref="SkillMarkdownParser"/>，不对外暴露。取舍是「结构简单 + 出错不抛异常」：
/// 无法理解的行降级为诊断并跳过，而不是让整次加载失败。
/// </remarks>
internal static class SkillYaml
{
    internal static Dictionary<string, object?> Parse(
        IReadOnlyList<string> lines,
        ICollection<SkillDiagnostic> diagnostics,
        string? filePath,
        int firstLineNumber)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var baseIndent = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            if (IsIgnorable(lines[i]))
                continue;

            baseIndent = CountIndent(lines[i]);
            break;
        }

        if (baseIndent < 0)
            return map;

        var index = 0;

        while (index < lines.Count)
        {
            var line = lines[index];

            if (IsIgnorable(line))
            {
                index++;
                continue;
            }

            var indent = CountIndent(line);

            if (indent < baseIndent)
                break;

            if (indent > baseIndent)
            {
                diagnostics.Add(new SkillDiagnostic(
                    SkillDiagnosticCode.InvalidFrontmatter,
                    SkillDiagnosticSeverity.Warning,
                    $"第 {firstLineNumber + index} 行缩进异常，已忽略。",
                    filePath,
                    firstLineNumber + index));

                index++;
                continue;
            }

            ReadEntry(
                lines, ref index, baseIndent,
                diagnostics, filePath, firstLineNumber,
                map);
        }

        return map;
    }

    private static void ReadEntry(
        IReadOnlyList<string> lines,
        ref int index,
        int indent,
        ICollection<SkillDiagnostic> diagnostics,
        string? filePath,
        int firstLineNumber,
        Dictionary<string, object?> target)
    {
        var content = lines[index].Trim();
        var separator = FindKeySeparator(content);

        if (separator <= 0)
        {
            diagnostics.Add(new SkillDiagnostic(
                SkillDiagnosticCode.InvalidFrontmatter,
                SkillDiagnosticSeverity.Warning,
                $"无法解析的行，已忽略：{Shorten(content)}",
                filePath,
                firstLineNumber + index));

            index++;
            return;
        }

        var key = Unquote(content[..separator].Trim());
        var remainder = content[(separator + 1)..].Trim();
        index++;

        if (key.Length == 0)
            return;

        if (remainder.Length == 0)
        {
            var next = NextSignificant(lines, index);

            if (next >= 0 && CountIndent(lines[next]) > indent)
            {
                var childIndent = CountIndent(lines[next]);
                index = next;

                target[key] = IsSequenceItem(lines[next].Trim())
                    ? ParseSequence(lines, ref index, childIndent)
                    : ParseMapping(lines, ref index, childIndent, diagnostics, filePath, firstLineNumber);
            }
            else
            {
                target[key] = string.Empty;
            }

            return;
        }

        if (IsBlockScalar(remainder))
        {
            target[key] = ParseBlockScalar(lines, ref index, indent, remainder);
            return;
        }

        target[key] = ParseValue(remainder);
    }

    private static Dictionary<string, object?> ParseMapping(
        IReadOnlyList<string> lines,
        ref int index,
        int indent,
        ICollection<SkillDiagnostic> diagnostics,
        string? filePath,
        int firstLineNumber)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        while (index < lines.Count)
        {
            if (IsIgnorable(lines[index]))
            {
                index++;
                continue;
            }

            var lineIndent = CountIndent(lines[index]);

            if (lineIndent < indent)
                break;

            if (lineIndent > indent)
            {
                diagnostics.Add(new SkillDiagnostic(
                    SkillDiagnosticCode.InvalidFrontmatter,
                    SkillDiagnosticSeverity.Warning,
                    $"第 {firstLineNumber + index} 行缩进异常，已忽略。",
                    filePath,
                    firstLineNumber + index));

                index++;
                continue;
            }

            if (IsSequenceItem(lines[index].Trim()))
                break;

            ReadEntry(
                lines, ref index, indent,
                diagnostics, filePath, firstLineNumber,
                map);
        }

        return map;
    }

    private static List<string> ParseSequence(
        IReadOnlyList<string> lines,
        ref int index,
        int indent)
    {
        var items = new List<string>();

        while (index < lines.Count)
        {
            if (IsIgnorable(lines[index]))
            {
                index++;
                continue;
            }

            var lineIndent = CountIndent(lines[index]);

            if (lineIndent < indent)
                break;

            if (lineIndent > indent)
            {
                index++;
                continue;
            }

            var content = lines[index].Trim();

            if (!IsSequenceItem(content))
                break;

            var item = Unquote(content[1..].Trim());

            if (item.Length > 0)
                items.Add(item);

            index++;
        }

        return items;
    }

    private static string ParseBlockScalar(
        IReadOnlyList<string> lines,
        ref int index,
        int parentIndent,
        string indicator)
    {
        var buffer = new List<string>();
        var contentIndent = -1;

        while (index < lines.Count)
        {
            var line = lines[index];

            if (line.Trim().Length == 0)
            {
                buffer.Add(string.Empty);
                index++;
                continue;
            }

            var indent = CountIndent(line);

            if (indent <= parentIndent)
                break;

            if (contentIndent < 0)
                contentIndent = indent;

            buffer.Add(indent >= contentIndent ? line[contentIndent..] : line.TrimStart());
            index++;
        }

        while (buffer.Count > 0 && buffer[^1].Length == 0)
            buffer.RemoveAt(buffer.Count - 1);

        return indicator[0] == '>' ? Fold(buffer) : string.Join("\n", buffer);
    }

    private static string Fold(IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        var pendingBreak = false;

        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                pendingBreak = true;
                continue;
            }

            if (builder.Length > 0)
                builder.Append(pendingBreak ? '\n' : ' ');

            builder.Append(line);
            pendingBreak = false;
        }

        return builder.ToString();
    }

    private static object ParseValue(string raw)
    {
        var value = raw.Trim();

        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
            return SplitInlineList(value[1..^1]);

        return Unquote(value);
    }

    private static List<string> SplitInlineList(string inner)
    {
        var items = new List<string>();
        var buffer = new StringBuilder();
        var quote = '\0';

        foreach (var ch in inner)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';

                buffer.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                buffer.Append(ch);
                continue;
            }

            if (ch == ',')
            {
                Flush();
                continue;
            }

            buffer.Append(ch);
        }

        Flush();
        return items;

        void Flush()
        {
            var item = Unquote(buffer.ToString().Trim());

            if (item.Length > 0)
                items.Add(item);

            buffer.Clear();
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2)
            return value;

        if (value[0] == '"' && value[^1] == '"')
            return Unescape(value[1..^1]);

        if (value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");

        return value;
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;

        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                builder.Append(value[i]);
                continue;
            }

            var next = value[++i];

            builder.Append(next switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '0' => '\0',
                _ => next
            });
        }

        return builder.ToString();
    }

    private static int FindKeySeparator(string content)
    {
        var quote = '\0';

        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];

            if (quote != '\0')
            {
                if (ch == '\\' && quote == '"')
                {
                    i++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';

                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == ':' && (i + 1 == content.Length || content[i + 1] is ' ' or '\t'))
                return i;
        }

        return -1;
    }

    private static bool IsBlockScalar(string value)
    {
        if (value.Length == 0 || value[0] is not ('|' or '>'))
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] is not ('-' or '+') && !char.IsDigit(value[i]))
                return false;
        }

        return true;
    }

    private static bool IsSequenceItem(string trimmed) =>
        trimmed.Length > 0 && trimmed[0] == '-' && (trimmed.Length == 1 || trimmed[1] == ' ');

    private static bool IsIgnorable(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    private static int CountIndent(string line)
    {
        var count = 0;

        while (count < line.Length && line[count] is ' ' or '\t')
            count++;

        return count;
    }

    private static int NextSignificant(IReadOnlyList<string> lines, int start)
    {
        for (var i = start; i < lines.Count; i++)
        {
            if (!IsIgnorable(lines[i]))
                return i;
        }

        return -1;
    }

    private static string Shorten(string value) =>
        value.Length <= 60 ? value : value[..60] + "…";
}
