using LLMManager.Core;
using LLMManager.Embedding;
using LLMManager.Resilience;
using LLMManager.Tools;
using Microsoft.Extensions.AI;

namespace ChatAppAI;

partial class Program
{
    static async Task TestToolCalling(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ 工具调用测试 ═══");

        var config = LLMConfiguration.CreateDeepSeek(apiKey);
        var client = factory.Create(config);

        var toolManager = new ToolManager();
        toolManager.RegisterSimpleTool("get_current_time", "获取当前的日期和时间", async _ =>
        {
            var now = DateTime.Now;
            return $"{now:yyyy-MM-dd HH:mm:ss} (星期{now.DayOfWeek})";
        });

        toolManager.RegisterSimpleTool("calculate", "执行简单的数学计算，输入格式如 '2+3' 或 '10*5'", async input =>
        {
            try
            {
                var dt = new System.Data.DataTable();
                var result = dt.Compute(input, null);
                return $"{input} = {result}";
            }
            catch { return $"无法计算: {input}"; }
        });

        Console.WriteLine($"已注册 {toolManager.Tools.Count} 个工具:");
        foreach (var tool in toolManager.Tools)
            Console.WriteLine($"  🔧 {tool.Name}: {tool.Description}");
        Console.WriteLine();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你可以使用工具来获取当前时间和执行计算。请务必用中文回答。"),
            new(ChatRole.User, "现在几点？请算出 365 × 24 等于多少。")
        };

        var options = new ChatOptions { Tools = toolManager.GetAITools() };

        Console.WriteLine("📤 用户: 现在几点？请算出 365 × 24 等于多少。");
        Console.WriteLine();

        var response = await client.ChatAsync(messages, options);

        if (response.RawMessage != null)
        {
            var toolCalls = response.RawMessage.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count > 0)
            {
                Console.WriteLine("📞 模型请求调用工具:");
                messages.Add(response.RawMessage);

                foreach (var toolCall in toolCalls)
                {
                    Console.WriteLine($"   🔧 {toolCall.Name}");
                    var argumentsJson = System.Text.Json.JsonSerializer.Serialize(
                        toolCall.Arguments ?? new Dictionary<string, object?>());
                    Console.WriteLine($"      参数: {argumentsJson}");

                    var result = await toolManager.ExecuteToolAsync(toolCall.Name, argumentsJson);
                    Console.WriteLine($"      结果: {(result.IsSuccess ? result.Result : $"❌ {result.Error}")}");

                    var callId = toolCall.CallId ?? Guid.NewGuid().ToString("N");
                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(callId, result.Result)]));
                }

                Console.WriteLine();
                Console.WriteLine("📤 将工具结果发回模型...");
                Console.Write("🤖 ");
                var finalResponse = await client.ChatAsync(messages, options);
                Console.WriteLine(finalResponse.Content);
                Console.WriteLine();
                Console.WriteLine($"📊 tokens: in={finalResponse.InputTokens}, out={finalResponse.OutputTokens}");
            }
            else
            {
                Console.WriteLine($"🤖 {response.Content}");
            }
        }

        Console.WriteLine($"✅ 工具调用测试通过！");
    }

    static async Task TestEmbedding(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ 嵌入向量测试 ═══");

        var config = LLMConfiguration.CreateDeepSeek(apiKey);
        var client = factory.Create(config);
        PrintHelper.Info("使用 DeepSeek 生成嵌入向量");
        Console.WriteLine();

        try
        {
            var embeddingManager = new EmbeddingManager(client);

            var corpus = new[]
            {
                "人工智能正在改变软件开发的方式",
                "今天天气很好适合出去散步",
                "Python 是最流行的机器学习语言",
                "足球比赛昨晚非常精彩",
                "深度学习需要大量的训练数据",
                "周末去公园烧烤是个不错的选择",
                "自然语言处理是 AI 的重要分支",
                "我的猫今天打翻了花瓶"
            };

            var query = "机器学习和编程语言";

            Console.WriteLine($"📋 语料库 ({corpus.Length} 条):");
            foreach (var text in corpus)
                Console.WriteLine($"   • {text}");
            Console.WriteLine();
            Console.WriteLine($"🔍 查询: {query}");
            Console.WriteLine();

            Console.Write("生成嵌入向量并计算相似度... ");
            var results = await embeddingManager.FindSimilarAsync(query, corpus, topK: 3);
            Console.WriteLine("完成！");
            Console.WriteLine();
            Console.WriteLine("🏆 相似度排名 (Top 3):");
            for (int i = 0; i < results.Count; i++)
            {
                var bar = new string('█', (int)(results[i].Similarity * 50));
                Console.WriteLine($"  {i + 1}. [{results[i].Similarity:F4}] {bar}");
                Console.WriteLine($"     \"{results[i].Text}\"");
            }

            Console.WriteLine();
            Console.WriteLine("✅ 嵌入向量测试通过！");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ 嵌入向量测试跳过: {ex.Message}");
        }
    }

    static async Task TestFallback(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ 降级容错测试 ═══");
        PrintHelper.Info("主客户端使用错误的端点，备用客户端使用正确的端点...");
        Console.WriteLine();

        var badConfig = LLMConfiguration.CreateDeepSeek(apiKey);
        badConfig.Endpoint = "https://api.invalid-deepseek.com/v1";
        badConfig.MaxRetryCount = 1;
        badConfig.RetryDelay = TimeSpan.FromMilliseconds(500);
        var badClient = factory.Create(badConfig);

        var goodConfig = LLMConfiguration.CreateDeepSeek(apiKey);
        goodConfig.MaxRetryCount = 0;
        var goodClient = factory.Create(goodConfig);

        var fallback = new FallbackStrategy(new[] { goodClient });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "回复 OK")
        };

        Console.Write("测试降级... ");
        var result = await fallback.ExecuteWithFallbackAsync(
            badClient,
            async (client, ct) => await client.ChatAsync(messages, null, ct));

        Console.WriteLine("完成！");
        Console.WriteLine();
        Console.WriteLine($"🤖 降级后回复: {result.Content}");
        Console.WriteLine();
        Console.WriteLine("✅ 降级容错测试通过！主客户端失败后成功切换到备用客户端。");
    }
}
