namespace Mono.LLM.Agent.Harness.Cli;

/// <summary>演示应用的命令行参数。</summary>
public sealed class AppOptions
{
    private const string DefaultModel = "deepseek-chat";
    private const int DefaultMaxTokens = 1200;

    /// <summary>是否显示帮助。</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>API Key，来自环境变量 <c>DEEPSEEK_API_KEY</c>。</summary>
    public string? ApiKey { get; private set; }

    /// <summary>模型标识。</summary>
    public string ModelId { get; private set; } = DefaultModel;

    /// <summary>技能目录，为空时由 <see cref="ResolveSkillsDirectory"/> 自动推断。</summary>
    public string? SkillsDirectory { get; private set; }

    /// <summary>单次回复的最大 token 数。</summary>
    public int MaxOutputTokens { get; private set; } = DefaultMaxTokens;

    /// <summary>参数非法时的提示。</summary>
    public string? ParseError { get; private set; }

    /// <summary>解析命令行参数。</summary>
    public static AppOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new AppOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
        };

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    options.ShowHelp = true;
                    break;

                case "--model" or "-m":
                    if (!TryReadValue(args, ref i, out var model))
                    {
                        options.ParseError = "--model 需要一个模型标识，例如 deepseek-chat。";
                        return options;
                    }

                    options.ModelId = model;
                    break;

                case "--skills" or "-s":
                    if (!TryReadValue(args, ref i, out var directory))
                    {
                        options.ParseError = "--skills 需要一个目录路径。";
                        return options;
                    }

                    options.SkillsDirectory = Path.GetFullPath(directory);
                    break;

                case "--max-tokens":
                    if (!TryReadValue(args, ref i, out var tokens) ||
                        !int.TryParse(tokens, out var value) ||
                        value <= 0)
                    {
                        options.ParseError = "--max-tokens 需要一个大于 0 的整数。";
                        return options;
                    }

                    options.MaxOutputTokens = value;
                    break;

                default:
                    options.ParseError = $"无法识别的参数：{args[i]}";
                    return options;
            }
        }

        return options;
    }

    /// <summary>
    /// 解析技能目录：优先命令行指定，其次当前目录下的 <c>skills</c>，
    /// 最后回落到随程序发布的 <c>skills</c>。
    /// </summary>
    public string ResolveSkillsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(SkillsDirectory))
            return SkillsDirectory;

        var local = Path.Combine(Directory.GetCurrentDirectory(), "skills");

        if (Directory.Exists(local))
            return local;

        return Path.Combine(AppContext.BaseDirectory, "skills");
    }

    private static bool TryReadValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }
}
