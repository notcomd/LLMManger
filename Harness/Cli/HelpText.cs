namespace Mono.LLM.Agent.Harness.Cli;

/// <summary>命令行帮助文本。</summary>
public static class HelpText
{
    public const string Usage = """
        Mono.LLM.Agent · Agent Console

        一个可以直接对话的 agent 演示：连续多轮、按需调用工具、随时切换技能。

        用法
          dotnet run --project Harness -- [选项]

        首次运行请先设置密钥：
          Windows        setx DEEPSEEK_API_KEY "sk-xxxx"     （新开终端生效）
          macOS / Linux  export DEEPSEEK_API_KEY='sk-xxxx'

        选项
          -m, --model <id>       模型标识，默认 deepseek-chat
          -s, --skills <目录>    技能目录，默认取 ./skills，其次取随程序发布的 skills
              --max-tokens <n>   单次回复上限，默认 1200
          -h, --help             显示本页

        进入后输入 /help 查看会话内命令。
        """;
}
