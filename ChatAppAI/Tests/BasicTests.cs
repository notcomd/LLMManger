using LLMManager.Core;
using LLMManager.Conversation;
using Microsoft.Extensions.AI;
using System.Text;

namespace ChatAppAI;

partial class Program
{
    static async Task TestSingleChat(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ DeepSeek 单次对话测试 ═══");

        var config = LLMConfiguration.CreateDeepSeek(apiKey);
        var client = factory.Create(config);

        Console.WriteLine($"提供商: {config.ProviderType}");
        Console.WriteLine($"模型:   {config.ModelId}");
        Console.WriteLine();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是一个友好的技术助手，回答要简洁专业。"),
            new(ChatRole.User, "请用一句话介绍什么是大语言模型。")
        };

        Console.Write("发送请求... ");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await client.ChatAsync(messages);
        sw.Stop();

        Console.WriteLine($"完成！耗时 {sw.ElapsedMilliseconds}ms");
        Console.WriteLine();
        Console.WriteLine($"📝 回复内容:");
        Console.WriteLine(response.Content);
        Console.WriteLine();
        Console.WriteLine($"📊 统计: 输入 {response.InputTokens} tokens | 输出 {response.OutputTokens} tokens");
        Console.WriteLine($"✅ 单次对话测试通过！");
    }

    static async Task TestStreaming(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ DeepSeek 流式输出测试 ═══");

        var config = LLMConfiguration.CreateDeepSeek(apiKey);
        config.MaxOutputTokens = 500;
        var client = factory.Create(config);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "写一首关于人工智能的五言绝句。")
        };

        Console.WriteLine("📝 流式输出:");
        Console.Write("   ");

        var fullContent = new StringBuilder();
        await foreach (var update in client.StreamChatAsync(messages))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                Console.Write(update.Text);
                fullContent.Append(update.Text);
            }
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"✅ 流式输出测试通过！总字数: {fullContent.Length}");
    }

    static async Task TestConversationManager(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ 多轮对话管理测试 ═══");

        var config = LLMConfiguration.CreateDeepSeek(apiKey);
        var client = factory.Create(config);
        var conversation = new ConversationManager(client, "你是一个有帮助的助手。尽量用简短的回答。");

        Console.WriteLine("  📌 第 1 轮: 自我介绍");
        Console.Write("  🤖 ");
        var reply1 = await conversation.SendAsync("请用一句话介绍你自己。");
        Console.WriteLine(reply1.Content);
        Console.WriteLine($"     (tokens: in={reply1.InputTokens}, out={reply1.OutputTokens})");
        Console.WriteLine();

        Console.WriteLine("  📌 第 2 轮: 追问（依赖上下文）");
        Console.Write("  🤖 ");
        var reply2 = await conversation.SendAsync("请把你刚才那句话翻译成英文。");
        Console.WriteLine(reply2.Content);
        Console.WriteLine($"     (tokens: in={reply2.InputTokens}, out={reply2.OutputTokens})");
        Console.WriteLine();

        Console.WriteLine("  📌 第 3 轮: 再次追问");
        Console.Write("  🤖 ");
        var reply3 = await conversation.SendAsync("再翻译回中文。");
        Console.WriteLine(reply3.Content);
        Console.WriteLine($"     (tokens: in={reply3.InputTokens}, out={reply3.OutputTokens})");
        Console.WriteLine();

        Console.WriteLine($"📋 对话历史: 共 {conversation.MessageCount} 条消息");
        Console.WriteLine($"✅ 多轮对话管理测试通过！");
    }
}
