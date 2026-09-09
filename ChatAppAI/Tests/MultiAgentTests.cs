using LLMManager.Core;
using LLMManager.MultiAgent;

namespace ChatAppAI;

partial class Program
{
    static async Task TestMultiAgent(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ Multi-Agent 多智能体 - 自动演示 ═══");
        var config = LLMConfiguration.CreateDeepSeek(apiKey);

        Console.WriteLine("📌 演示 1: 顺序执行（规划师 → 开发专家 → 代码审查员）");
        var planner = A("规划师", "策略规划师", "收到任务后制定清晰执行计划，拆分步骤。", config, 800);
        var dev = A("开发专家", "软件工程师", "根据需求写出高质量、可维护的代码。", config, 1200);
        var reviewer = A("代码审查员", "审查专家", "审查代码安全性、可读性和性能，给出改进建议。", config, 800);

        var seq = new SequentialOrchestrator(factory, new[] { planner, dev, reviewer });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r1 = await seq.RunAsync("设计一个简单的 TODO 列表 API，包含增删改查功能");
        sw.Stop();

        foreach (var msg in r1.Messages)
        {
            Console.WriteLine($"  ── [{msg.FromAgent}] ──");
            Console.WriteLine($"  {msg.Content[..Math.Min(120, msg.Content.Length)]}...");
        }
        Console.WriteLine($"  ✅ {sw.ElapsedMilliseconds}ms, {r1.TotalTokens} tokens\n");

        Console.WriteLine("📌 演示 2: 并行执行（乐观/悲观评论员 → 主持人汇总）");
        var pos = A("乐观评论员", "积极评价", "总是看到事物积极面，热情洋溢地评价。", config, 600);
        var neg = A("悲观评论员", "批判评价", "看到问题风险，用批判眼光审视。", config, 600);
        var mod = A("主持人", "综合评估", "综合各方观点给出客观平衡总结。", config, 800);

        var par = new ParallelOrchestrator(factory, new[] { pos, neg }, mod);
        sw.Restart();
        var r2 = await par.RunAsync("评价远程办公对软件开发团队的影响");
        sw.Stop();

        foreach (var msg in r2.Messages) Console.WriteLine($"  [{msg.FromAgent}]: {msg.Content[..Math.Min(100, msg.Content.Length)]}...");
        Console.WriteLine($"  ✅ {sw.ElapsedMilliseconds}ms, {r2.TotalTokens} tokens\n");

        Console.WriteLine("📌 演示 3: 辩论模式（正方 vs 反方 → 评委）");
        var pro = A("正方", "AI乐观主义者", "支持AI快速发展，强调医疗/教育/效率提升等好处。", config, 500);
        var con = A("反方", "AI谨慎主义者", "关注隐私/就业/安全风险，论点有理有据。", config, 500);
        var judge = A("评委", "辩论评委", "总结双方论点，指出优势不足，给出综合评判。", config, 800);

        var debate = new DebateOrchestrator(factory, new[] { pro, con }, judge, maxRounds: 2);
        sw.Restart();
        var r3 = await debate.RunAsync("AI应该被允许自主做出重要决策吗？");
        sw.Stop();
        Console.WriteLine($"  💬 {r3.Messages.Count}条发言 | {r3.TotalTokens} tokens\n");

        Console.WriteLine("📌 演示 4: 路由模式（路由器 → 专家）");
        var router = A("任务路由器", "智能分配", "分析请求类型，分配给最合适专家。", config, 400);
        var coder = A("编程专家", "代码专家", "精通各种语言框架，给出实用技术建议。", config, 800);
        var writer = A("写作大师", "创作专家", "精通各种文体，文字优美表达精准。", config, 800);

        var route = new RouterOrchestrator(factory, router, new[] { coder, writer });
        var r4 = await route.RunAsync("帮忙写一段招聘Python工程师的职位描述");
        Console.WriteLine($"  🔀 {r4.Messages.FirstOrDefault()?.FromAgent} → {r4.Messages.FirstOrDefault()?.ToAgent}");
        Console.WriteLine($"  🎯 {r4.FinalOutput[..Math.Min(150, r4.FinalOutput.Length)]}...\n");

        Console.WriteLine("✅ Multi-Agent 演示完成！展示了 Sequential / Parallel / Debate / Router 四种模式");
    }

    static async Task InteractiveMultiAgent(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ 交互式 Multi-Agent 团队 ═══");
        var config = LLMConfiguration.CreateDeepSeek(apiKey);

        var team = new AgentTeam(factory);
        team.AddMember(A("规划师", "任务规划", "将复杂任务拆解为清晰步骤。", config, 600))
            .AddMember(A("执行者", "内容生产", "根据规划产出高质量解决方案。", config, 1200))
            .AddMember(A("审核员", "质量把关", "检查准确性、完整性，提出改进。", config, 600))
            .UseSequential();

        Console.WriteLine("已创建团队: 规划师 → 执行者 → 审核员");
        Console.WriteLine("  /seq /para /debate /team /quit  |  直接输入文本执行");
        Console.WriteLine();

        while (true)
        {
            Console.Write($"── [{team.Mode}] 👤 你: ");
            var input = Console.ReadLine() ?? "";
            if (string.IsNullOrWhiteSpace(input)) continue;

            try
            {
                switch (input)
                {
                    case "/seq": team.UseSequential(); Console.WriteLine("  ✅ 顺序模式"); break;
                    case "/para": team.UseParallel(); Console.WriteLine("  ✅ 并行模式"); break;
                    case "/debate": team.UseDebate(2); Console.WriteLine("  ✅ 辩论模式(2轮)"); break;
                    case "/team":
                        foreach (var m in team.Members) Console.WriteLine($"    {m.Name} ({m.Role})");
                        break;
                    case "/quit": return;
                    default:
                        if (input.StartsWith("/")) { Console.WriteLine("  ❌ 未知命令"); break; }
                        Console.WriteLine("  ⏳ 团队协作中...");
                        var result = await team.RunAsync(input);
                        foreach (var msg in result.Messages)
                            Console.WriteLine($"  [{msg.FromAgent}]: {msg.Content[..Math.Min(120, msg.Content.Length)]}...");
                        Console.WriteLine($"  📊 {result.TotalTokens} tokens | {result.Duration.TotalSeconds:F2}s");
                        break;
                }
            }
            catch (Exception ex) { Console.WriteLine($"❌ {ex.Message}"); }
            Console.WriteLine();
        }
    }
}
