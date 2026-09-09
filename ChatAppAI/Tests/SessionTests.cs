using LLMManager.Core;
using LLMManager.Sessions;
using Microsoft.Extensions.AI;

namespace ChatAppAI;

partial class Program
{
    static async Task TestSessionManager(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ 全局会话管理 - 自动演示 ═══");
        Console.WriteLine();

        var config = LLMConfiguration.CreateDeepSeek(apiKey);
        var sessionManager = new SessionManager(factory, config);

        Console.WriteLine("📌 步骤 1: 创建多个会话");
        var sess1 = await sessionManager.CreateSessionAsync("编程助手", systemPrompt: "你是一个经验丰富的编程导师，擅长用简单的方式解释复杂概念。", tags: new[] { "编程", "教育" });
        Console.WriteLine($"  ✅ 创建会话: {sess1}");
        var sess2 = await sessionManager.CreateSessionAsync("翻译专家", systemPrompt: "你是一个专业的翻译人员，精通中英双语翻译。回答要准确简洁。", tags: new[] { "翻译", "语言" });
        Console.WriteLine($"  ✅ 创建会话: {sess2}");
        var sess3 = await sessionManager.CreateSessionAsync("创意写作", systemPrompt: "你是一个富有想象力的创意作家，擅长写故事和诗歌。", tags: new[] { "写作", "创意" });
        Console.WriteLine($"  ✅ 创建会话: {sess3}");

        Console.WriteLine();
        Console.WriteLine($"📊 当前共 {sessionManager.SessionCount} 个会话");

        Console.WriteLine();
        Console.WriteLine("📌 步骤 2: 在不同会话发送消息");
        Console.WriteLine("  ── 编程助手 ──");
        Console.Write("  🤖 ");
        var reply1 = await sessionManager.SendAsync(sess1.Id, "请用一句话解释什么是递归。");
        Console.WriteLine(reply1.Content);

        Console.WriteLine("  ── 翻译专家 ──");
        Console.Write("  🤖 ");
        var reply2 = await sessionManager.SendAsync(sess2.Id, "把'人工智能正在改变世界'翻译成英文。");
        Console.WriteLine(reply2.Content);

        Console.WriteLine("  ── 创意写作 ──");
        Console.Write("  🤖 ");
        var reply3 = await sessionManager.SendAsync(sess3.Id, "用两句话写一个关于机器人梦见电子羊的微小说开头。");
        Console.WriteLine(reply3.Content);

        Console.WriteLine();
        Console.WriteLine("📌 步骤 3: 同一会话多轮对话");
        Console.Write("  🤖 ");
        var reply4 = await sessionManager.SendAsync(sess1.Id, "用你刚才解释的递归概念，举一个具体的代码例子。");
        Console.WriteLine(reply4.Content);

        Console.WriteLine();
        Console.WriteLine("📌 步骤 4: 搜索和删除");
        Console.WriteLine("  搜索 '写作':");
        foreach (var s in sessionManager.SearchSessions("写作"))
            Console.WriteLine($"    → {s.Name}");
        await sessionManager.DeleteSessionAsync(sess2.Id);
        Console.WriteLine($"  🗑️  已删除会话，剩余 {sessionManager.SessionCount} 个");

        Console.WriteLine();
        Console.WriteLine("✅ 全局会话管理演示完成！");
    }

    static async Task InteractiveSessionManager(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ 交互式多会话管理 ═══");
        var config = LLMConfiguration.CreateDeepSeek(apiKey);
        var sessionManager = new SessionManager(factory, config);
        var defaultSession = await sessionManager.CreateSessionAsync("默认会话", systemPrompt: "你是一个有用的助手，用中文简洁回答。");

        while (true)
        {
            var activeId = sessionManager.GetActiveSessionId();
            var activeSession = sessionManager.GetSession(activeId);
            var activeName = activeSession?.Name ?? "(无)";

            Console.WriteLine($"── 当前会话: [{activeName}] ──");
            Console.WriteLine("  /new <名称>  创建新会话");
            Console.WriteLine("  /switch <ID> 切换会话");
            Console.WriteLine("  /list        列出所有会话");
            Console.WriteLine("  /rename <新名> 重命名当前会话");
            Console.WriteLine("  /history     查看当前会话历史");
            Console.WriteLine("  /delete <ID> 删除会话");
            Console.WriteLine("  /clear       清空当前会话历史");
            Console.WriteLine("  /quit        退出");
            Console.WriteLine("  直接输入文本 → 发送消息到当前会话");
            Console.WriteLine();

            Console.Write("👤 你: ");
            var input = Console.ReadLine() ?? "";
            if (string.IsNullOrWhiteSpace(input)) continue;

            try
            {
                if (input.StartsWith("/new "))
                {
                    var session = await sessionManager.CreateSessionAsync(input[5..].Trim());
                    sessionManager.SetActiveSession(session.Id);
                    Console.WriteLine($"  ✅ 创建并切换到会话: {session}");
                }
                else if (input.StartsWith("/switch "))
                {
                    var match = sessionManager.SearchSessions(input[8..].Trim()).FirstOrDefault();
                    if (match != null) { sessionManager.SetActiveSession(match.Id); Console.WriteLine($"  ✅ 切换到: {match}"); }
                    else Console.WriteLine($"  ❌ 未找到");
                }
                else if (input == "/list")
                {
                    foreach (var s in sessionManager.GetAllSessions())
                    {
                        var marker = s.Id == activeId ? " ★" : "";
                        Console.WriteLine($"    [{s.Id,-12}] {s.Name,-16} msgs:{s.MessageCount}{marker}");
                    }
                }
                else if (input.StartsWith("/rename "))
                {
                    await sessionManager.RenameSessionAsync(activeId, input[8..].Trim());
                    Console.WriteLine($"  ✅ 已重命名");
                }
                else if (input == "/history")
                {
                    foreach (var msg in sessionManager.GetSessionHistory(activeId))
                    {
                        var role = msg.Role == ChatRole.User ? "👤" : msg.Role == ChatRole.Assistant ? "🤖" : "⚙️";
                        var preview = msg.Text is { Length: > 80 } t ? t[..80] + "..." : msg.Text;
                        Console.WriteLine($"  {role} {preview}");
                    }
                }
                else if (input.StartsWith("/delete "))
                {
                    var targetId = input[8..].Trim();
                    var s = sessionManager.GetSession(targetId);
                    if (s != null)
                    {
                        Console.Write($"  ⚠️ 确认删除 '{s.Name}'? (y/N): ");
                        if (Console.ReadLine()?.Trim().ToUpper() == "Y")
                        {
                            await sessionManager.DeleteSessionAsync(targetId);
                            Console.WriteLine($"  ✅ 已删除");
                            if (sessionManager.SessionCount == 0)
                                sessionManager.SetActiveSession((await sessionManager.CreateSessionAsync("默认会话")).Id);
                        }
                    }
                }
                else if (input == "/clear") { sessionManager.ClearSessionHistory(activeId); Console.WriteLine("  🗑️ 已清空"); }
                else if (input == "/quit") break;
                else if (input.StartsWith("/")) Console.WriteLine($"  ❌ 未知命令: {input}");
                else
                {
                    Console.Write("🤖 ");
                    await foreach (var update in sessionManager.SendStreamingAsync(activeId, input))
                        if (!string.IsNullOrEmpty(update.Text)) Console.Write(update.Text);
                    Console.WriteLine();
                }
            }
            catch (Exception ex) { Console.WriteLine($"❌ 错误: {ex.Message}"); }
            Console.WriteLine();
        }
    }
}
