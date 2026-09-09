using LLMManager.Core;
using LLMManager.Skills;

namespace ChatAppAI;

partial class Program
{
    static async Task TestSkillSystem(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ Skill 技能系统 - 自动演示 ═══");
        var registry = new SkillRegistry();
        var executor = new SkillExecutor(factory);
        var config = LLMConfiguration.CreateDeepSeek(apiKey);

        Console.WriteLine("📌 步骤 1: 注册预置技能");
        foreach (var skill in SkillCatalog.GetAll())
        {
            registry.Register(skill);
            Console.WriteLine($"  ✅ [{skill.Category}] {skill.Name} — {skill.Description}");
        }
        Console.WriteLine($"📊 已注册 {registry.Count} 个技能");
        Console.WriteLine();

        Console.WriteLine("📌 步骤 2: 执行「五岁小孩都能懂」");
        var ctx1 = executor.CreateContext(registry.Get("五岁小孩都能懂")!, config);
        Console.Write("  🤖 ");
        var r1 = await executor.ExecuteAsync(ctx1, "什么是黑洞？");
        Console.WriteLine(r1.Content);
        Console.WriteLine();

        Console.WriteLine("📌 步骤 3: 执行「翻译专家」");
        var ctx2 = executor.CreateContext(registry.Get("翻译专家")!, config);
        Console.Write("  🤖 ");
        var r2 = await executor.ExecuteAsync(ctx2, "LLMManager 是一个强大的大模型管理库，支持多提供商和智能切换。");
        Console.WriteLine(r2.Content);
        Console.WriteLine();

        Console.WriteLine("📌 步骤 4: 执行「内容摘要」");
        var ctx3 = executor.CreateContext(registry.Get("内容摘要")!, config);
        var longText = ".NET 10 带来了 JIT 优化（启动提升30%）、Microsoft.Extensions.AI 统一抽象、Blazor United、EF Core 10 改进和 Native AOT 完善。";
        var r3 = await executor.ExecuteAsync(ctx3, $"请对以下内容摘要：\n{longText}");
        Console.WriteLine($"  🤖 {r3.Content}");
        Console.WriteLine();

        Console.WriteLine("📌 步骤 5: 自定义技能");
        var custom = new SkillDefinition
        {
            Name = "美食评论家", Category = "生活",
            SystemPrompt = "你是一位苛刻但公正的美食评论家。用生动形象的语言点评食物。",
            Tags = { "美食" }
        };
        registry.Register(custom);
        var ctx4 = executor.CreateContext(custom, config);
        var r4 = await executor.ExecuteAsync(ctx4, "评价一碗红烧牛肉面");
        Console.WriteLine($"  🤖 {r4.Content}");

        Console.WriteLine();
        Console.WriteLine("✅ Skill 技能系统演示完成！");
    }

    static async Task InteractiveSkillChat(LLMClientFactory factory, string apiKey)
    {
        Console.WriteLine("═══ 交互式 Skill 技能执行 ═══");
        var registry = new SkillRegistry();
        var executor = new SkillExecutor(factory);
        var config = LLMConfiguration.CreateDeepSeek(apiKey);

        foreach (var skill in SkillCatalog.GetAll()) registry.Register(skill);
        registry.Register(new SkillDefinition
        {
            Name = "美食评论家", Category = "生活",
            SystemPrompt = "你是一位苛刻但公正的美食评论家。", Tags = { "美食" }
        });

        Console.WriteLine($"已加载 {registry.Count} 个技能:");
        foreach (var s in registry.GetAll())
            Console.WriteLine($"  [{s.Category}] {s.Name} — {s.Description}");
        Console.WriteLine();

        SkillExecutionContext? currentCtx = null;
        SkillDefinition? currentSkill = null;

        while (true)
        {
            Console.WriteLine($"── 当前技能: [{currentSkill?.Name ?? "(未选择)"}] ──");
            Console.WriteLine("  /skills /search <词> /use <名称> /info /category <类> /tag <标签> /quit");
            Console.WriteLine("  直接输入文本 → 用当前技能执行");
            Console.WriteLine();
            Console.Write("👤 你: ");
            var input = Console.ReadLine() ?? "";
            if (string.IsNullOrWhiteSpace(input)) continue;

            try
            {
                if (input == "/skills")
                {
                    foreach (var s in registry.GetAll())
                        Console.WriteLine($"    [{s.Category}] {s.Name} v{s.Version}{(currentSkill?.Name == s.Name ? " ★" : "")}");
                }
                else if (input.StartsWith("/search "))
                {
                    foreach (var s in registry.Search(input[8..].Trim()))
                        Console.WriteLine($"    [{s.Category}] {s.Name}");
                }
                else if (input.StartsWith("/use "))
                {
                    var skill = registry.Get(input[5..].Trim()) ?? registry.Search(input[5..].Trim()).FirstOrDefault();
                    if (skill != null) { currentSkill = skill; currentCtx = executor.CreateContext(skill, config); Console.WriteLine($"  ✅ {skill}"); }
                    else Console.WriteLine("  ❌ 未找到");
                }
                else if (input == "/info")
                {
                    if (currentSkill == null) Console.WriteLine("  ⚠️ 未选择技能");
                    else Console.WriteLine($"  名称: {currentSkill.Name}\n  提示词: {currentSkill.SystemPrompt}");
                }
                else if (input.StartsWith("/category "))
                {
                    foreach (var s in registry.GetByCategory(input[10..].Trim()))
                        Console.WriteLine($"    {s.Name} — {s.Description}");
                }
                else if (input.StartsWith("/tag "))
                {
                    foreach (var s in registry.GetByTag(input[5..].Trim()))
                        Console.WriteLine($"    {s.Name} — {s.Description}");
                }
                else if (input == "/quit") break;
                else if (input.StartsWith("/")) Console.WriteLine($"  ❌ 未知命令");
                else
                {
                    if (currentCtx == null) { Console.WriteLine("  ⚠️ 请先用 /use <名称> 选择技能"); continue; }
                    Console.Write("🤖 ");
                    await foreach (var update in executor.ExecuteStreamingAsync(currentCtx, input))
                        if (!string.IsNullOrEmpty(update.Text)) Console.Write(update.Text);
                    Console.WriteLine();
                }
            }
            catch (Exception ex) { Console.WriteLine($"❌ 错误: {ex.Message}"); }
            Console.WriteLine();
        }
    }
}
