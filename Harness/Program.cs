using System.Text;
using Mono.LLM.Agent.Harness.Cli;
using Mono.LLM.Agent.Harness.Interactive;

namespace Mono.LLM.Agent.Harness;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var options = AppOptions.Parse(args);

        if (options.ShowHelp)
        {
            Console.WriteLine(HelpText.Usage);
            return 0;
        }

        if (options.ParseError != null)
        {
            Console.Error.WriteLine($"参数错误：{options.ParseError}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(HelpText.Usage);
            return 2;
        }

        return await AgentConsole.RunAsync(options, Console.Out, Console.In).ConfigureAwait(false);
    }
}
