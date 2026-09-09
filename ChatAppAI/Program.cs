using LLMManager.Core;
using LLMManager.MultiAgent;
using System.Text;

namespace ChatAppAI;

partial class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║       LLMManager 大模型管理库 - DeepSeek 测试     ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        Console.WriteLine();

        var deepseekKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? "sk-a7c171c5d98a4718b0fb3b6db87c6079";

        Console.WriteLine("从环境变量读取配置:");
        Console.WriteLine($"  DEEPSEEK_API_KEY: {(string.IsNullOrEmpty(deepseekKey) ? "(未设置)" : new string('*', 8) + deepseekKey[^4..])}");
        Console.WriteLine();

        if (string.IsNullOrEmpty(deepseekKey))
        {
            Console.WriteLine("❌ 请设置环境变量 DEEPSEEK_API_KEY 后重试");
            return;
        }

        var factory = new LLMClientFactory();
        var deepseekConfig = LLMConfiguration.CreateDeepSeek(deepseekKey);

        while (true)
        {
            Console.WriteLine("──────────────────────────────────────────────────");
            Console.WriteLine("请选择测试项 (全部使用 DeepSeek):");
            Console.WriteLine("  1. 单次对话测试");
            Console.WriteLine("  2. 流式输出测试");
            Console.WriteLine("  3. 多轮对话管理（ConversationManager）");
            Console.WriteLine("  4. 工具调用（Tool Calling）");
            Console.WriteLine("  5. 嵌入向量（Embedding）");
            Console.WriteLine("  6. 降级容错（Fallback）");
            Console.WriteLine("  7. 全局会话管理 - 自动演示");
            Console.WriteLine("  8. 交互式多会话管理");
            Console.WriteLine("  9. Skill 技能系统 - 自动演示");
            Console.WriteLine("  A. 交互式 Skill 技能执行");
            Console.WriteLine("  B. Multi-Agent 多智能体 - 自动演示");
            Console.WriteLine("  C. 交互式 Multi-Agent 团队");
            Console.WriteLine("  D. 交互式对话");
            Console.WriteLine("  R. 运行全部测试");
            Console.WriteLine("  Q. 退出");
            Console.Write("请输入选项: ");

            var choice = Console.ReadLine()?.Trim().ToUpper() ?? "Q";
            Console.WriteLine();

            try
            {
                switch (choice)
                {
                    case "1": await TestSingleChat(factory, deepseekKey); break;
                    case "2": await TestStreaming(factory, deepseekKey); break;
                    case "3": await TestConversationManager(factory, deepseekKey); break;
                    case "4": await TestToolCalling(factory, deepseekKey); break;
                    case "5": await TestEmbedding(factory, deepseekKey); break;
                    case "6": await TestFallback(factory, deepseekKey); break;
                    case "7": await TestSessionManager(factory, deepseekKey); break;
                    case "8": await InteractiveSessionManager(factory, deepseekKey); break;
                    case "9": await TestSkillSystem(factory, deepseekKey); break;
                    case "A": await InteractiveSkillChat(factory, deepseekKey); break;
                    case "B": await TestMultiAgent(factory, deepseekKey); break;
                    case "C": await InteractiveMultiAgent(factory, deepseekKey); break;
                    case "D": await InteractiveChat(factory, deepseekKey); break;
                    case "R": await RunAllTests(factory, deepseekKey); break;
                    case "Q":
                        Console.WriteLine("再见！");
                        return;
                    default:
                        Console.WriteLine("无效选项，请重新输入。");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"❌ 测试失败: {ex.GetType().Name}");
                Console.WriteLine($"   详情: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"   内部异常: {ex.InnerException.Message}");
                Console.WriteLine();
            }

            Console.WriteLine();
        }
    }

    static AgentDefinition A(string name, string role, string prompt, LLMConfiguration config, int maxTokens) =>
        new() { Name = name, Role = role, SystemPrompt = prompt, Configuration = config, MaxReplyTokens = maxTokens };
}

static class PrintHelper
{
    public static void Info(string m) => Console.WriteLine($"ℹ️  {m}");
    public static void Warn(string m) => Console.WriteLine($"⚠️  {m}");
}
