using LLMManager.Core;
using LLMManager.Conversation;
using Microsoft.Extensions.AI;

namespace ChatAppAI;

partial class Program
{
    static async Task InteractiveChat(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ 交互式对话 - DeepSeek ═══");
        Console.WriteLine("输入 /quit 退出, /clear 清空历史");
        Console.WriteLine();

        var config = LLMConfiguration.CreateDeepSeek(apiKey);
        var client = factory.Create(config);
        var conversation = new ConversationManager(client, "你是一个有用的助手，用中文简洁回答。");

        while (true)
        {
            Console.Write("👤 你: ");
            var input = Console.ReadLine() ?? "";
            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.Trim() == "/quit") break;
            if (input.Trim() == "/clear") { conversation.ClearHistory(); Console.WriteLine("🗑️ 已清空\n"); continue; }

            Console.Write("🤖 ");
            try
            {
                await foreach (var update in conversation.SendStreamingAsync(input))
                    if (!string.IsNullOrEmpty(update.Text)) Console.Write(update.Text);
                Console.WriteLine();
            }
            catch (Exception ex) { Console.WriteLine($"\n❌ {ex.Message}"); }
            Console.WriteLine();
        }
    }

    static async Task RunAllTests(LLMClientFactory factory, string apiKey)
    {
        var tests = new (string Name, Func<Task> Action)[]
        {
            ("单次对话", () => TestSingleChat(factory, apiKey)),
            ("流式输出", () => TestStreaming(factory, apiKey)),
            ("多轮对话", () => TestConversationManager(factory, apiKey)),
            ("工具调用", () => TestToolCalling(factory, apiKey)),
            ("嵌入向量", () => TestEmbedding(factory, apiKey)),
            ("降级容错", () => TestFallback(factory, apiKey)),
            ("会话管理", () => TestSessionManager(factory, apiKey)),
            ("Skill系统", () => TestSkillSystem(factory, apiKey)),
            ("Multi-Agent", () => TestMultiAgent(factory, apiKey)),
        };

        Console.WriteLine("╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║              运行全部测试 (DeepSeek)              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        Console.WriteLine();

        int pass = 0, fail = 0, skip = 0;
        foreach (var (name, action) in tests)
        {
            try
            {
                Console.Write($"[{name}] ... ");
                await action();
                pass++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
            Console.WriteLine();
        }

        Console.WriteLine($"══════ 结果: ✅ {pass} 通过 | ❌ {fail} 失败 | ⚠️ {skip} 跳过 ══════");
    }
}
