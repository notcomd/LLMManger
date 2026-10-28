using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Mono.LLM.Agent.Extensions;
using Mono.LLM.Agent.Skills;
using Mono.LLM.Agent.Tests.Harness;
using Mono.LLM.Agent.Tools;

namespace Mono.LLM.Agent.Tests.Skills;

/// <summary>
/// 覆盖标准技能模块：<c>SKILL.md</c> 解析、规范校验、目录加载、资源读取、索引渲染与按需加载工具。
/// </summary>
public class StandardSkillTests : SandboxedTest
{
    private string Root => Sandbox.Files.Root;

    // ── frontmatter 解析 ──

    [Fact]
    public void Parser_ReadsFrontmatterAndBody()
    {
        var result = SkillMarkdownParser.Parse("""
            ---
            name: pdf-processing
            description: 提取 PDF 文本。用于处理 .pdf 文件时。
            license: MIT
            version: 1.2
            ---

            # 步骤

            1. 先用 pdfplumber 抽取文本。
            """);

        Assert.True(result.Success, string.Join(" | ", result.Diagnostics));
        Assert.Equal("pdf-processing", result.Metadata!.Name);
        Assert.Equal("提取 PDF 文本。用于处理 .pdf 文件时。", result.Metadata.Description);
        Assert.Equal("MIT", result.Metadata.License);
        Assert.Equal("1.2", result.Metadata.Version);
        Assert.Contains("pdfplumber", result.Body);
        Assert.DoesNotContain("---", result.Body);
    }

    [Fact]
    public void Parser_ReadsBlockScalarDescription()
    {
        var result = SkillMarkdownParser.Parse("""
            ---
            name: report
            description: >-
              把零散记录整理成周报。
              用于每周五汇总进展时。
            ---
            正文内容足够长，用来通过最小长度检查。
            """);

        Assert.Equal("把零散记录整理成周报。 用于每周五汇总进展时。", result.Metadata!.Description);
    }

    [Fact]
    public void Parser_ReadsLiteralBlockScalar()
    {
        var result = SkillMarkdownParser.Parse("""
            ---
            name: steps
            description: 分步骤执行。用于多步任务时。
            metadata:
              notes: |
                line-one
                line-two
            ---
            正文内容足够长，用来通过最小长度检查。
            """);

        Assert.Equal("line-one\nline-two", result.Metadata!.Metadata["notes"]);
    }

    [Fact]
    public void Parser_ReadsInlineAndBlockLists()
    {
        var result = SkillMarkdownParser.Parse("""
            ---
            name: tools-demo
            description: 演示工具白名单。用于测试时。
            allowed-tools: [read_file, run_shell]
            metadata:
              tags:
                - 文档
                - 抽取
            ---
            正文内容足够长，用来通过最小长度检查。
            """);

        Assert.Equal(new[] { "read_file", "run_shell" }, result.Metadata!.AllowedTools);
        Assert.Equal(new[] { "文档", "抽取" }, result.Metadata.Tags);
    }

    [Fact]
    public void Parser_ReadsAllowedToolsAsSpaceSeparatedString()
    {
        var result = SkillMarkdownParser.Parse("""
            ---
            name: claude-style
            description: 使用空格分隔的工具声明。用于兼容时。
            allowed-tools: Read Write Bash
            ---
            正文内容足够长，用来通过最小长度检查。
            """);

        Assert.Equal(new[] { "Read", "Write", "Bash" }, result.Metadata!.AllowedTools);
    }

    [Fact]
    public void Parser_ReadsNestedMetadata()
    {
        var result = SkillMarkdownParser.Parse("""
            ---
            name: nested
            description: 带分类与标签。用于校验映射时。
            metadata:
              category: 文档
              icon: 📄
            ---
            正文内容足够长，用来通过最小长度检查。
            """);

        Assert.Equal("文档", result.Metadata!.Category);
        Assert.Equal("📄", result.Metadata.Icon);
    }

    [Fact]
    public void Parser_HandlesQuotedValuesWithColons()
    {
        var result = SkillMarkdownParser.Parse("""
            ---
            name: quoted
            description: "说明里带冒号：这不是新的键。用于验证引号处理时。"
            ---
            正文内容足够长，用来通过最小长度检查。
            """);

        Assert.Equal("说明里带冒号：这不是新的键。用于验证引号处理时。", result.Metadata!.Description);
    }

    [Fact]
    public void Parser_HandlesBomAndCrlf()
    {
        var markdown = "\uFEFF---\r\nname: crlf\r\ndescription: 换行符兼容。用于 Windows 时。\r\n---\r\n正文内容足够长，用来通过最小长度检查。\r\n";

        var result = SkillMarkdownParser.Parse(markdown);

        Assert.True(result.Success);
        Assert.Equal("crlf", result.Metadata!.Name);
        Assert.Contains("Windows", result.Metadata.Description);
    }

    [Fact]
    public void Parser_MissingFrontmatter_IsError()
    {
        var result = SkillMarkdownParser.Parse("# 没有元数据的技能\n\n正文。");

        Assert.Null(result.Metadata);
        Assert.Contains(result.Diagnostics, d => d.Code == SkillDiagnosticCode.MissingFrontmatter && d.IsError);
    }

    [Fact]
    public void Parser_UnclosedFrontmatter_IsError()
    {
        var result = SkillMarkdownParser.Parse("""
            ---
            name: broken
            description: 没有结束标记。用于验证容错时。
            """);

        Assert.Null(result.Metadata);
        Assert.Contains(result.Diagnostics, d => d.Code == SkillDiagnosticCode.UnclosedFrontmatter && d.IsError);
    }

    // ── 规范校验 ──

    [Fact]
    public void Validator_ConformingSkill_HasNoErrors()
    {
        var metadata = new SkillMetadata
        {
            Name = "pdf-processing",
            Description = "提取 PDF 文本与表格。用于处理 .pdf 文件时。"
        };

        var diagnostics = SkillValidator.Validate(metadata, "先抽取文本，再按页整理表格。");

        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }

    [Fact]
    public void Validator_StrictNames_RejectsChineseName()
    {
        var metadata = new SkillMetadata { Name = "文档处理", Description = "处理文档。用于整理资料时。" };

        var diagnostics = SkillValidator.Validate(
            metadata, "正文内容足够长，用来通过最小长度检查。",
            options: new SkillValidationOptions { StrictNames = true });

        Assert.Contains(diagnostics, d => d.Code == SkillDiagnosticCode.InvalidName && d.IsError);
    }

    [Fact]
    public void Validator_RelaxedNames_AllowsChineseName()
    {
        var metadata = new SkillMetadata { Name = "文档处理", Description = "处理文档。用于整理资料时。" };

        var diagnostics = SkillValidator.Validate(metadata, "正文内容足够长，用来通过最小长度检查。");

        Assert.DoesNotContain(diagnostics, d => d.Code == SkillDiagnosticCode.InvalidName);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("with/slash")]
    public void Validator_RelaxedNames_StillRejectsUnsafeCharacters(string name)
    {
        var metadata = new SkillMetadata { Name = name, Description = "描述。用于测试时。" };

        var diagnostics = SkillValidator.Validate(metadata, "正文内容足够长，用来通过最小长度检查。");

        Assert.Contains(diagnostics, d => d.Code == SkillDiagnosticCode.InvalidName && d.IsError);
    }

    [Fact]
    public void Validator_MissingDescription_IsError()
    {
        var metadata = new SkillMetadata { Name = "no-description" };

        var diagnostics = SkillValidator.Validate(metadata, "正文内容足够长，用来通过最小长度检查。");

        Assert.Contains(diagnostics, d => d.Code == SkillDiagnosticCode.MissingDescription && d.IsError);
    }

    [Fact]
    public void Validator_OverlongDescription_IsError()
    {
        var metadata = new SkillMetadata { Name = "long", Description = new string('x', 1025) };

        var diagnostics = SkillValidator.Validate(metadata, "正文内容足够长，用来通过最小长度检查。");

        Assert.Contains(diagnostics, d => d.Code == SkillDiagnosticCode.DescriptionTooLong && d.IsError);
    }

    [Fact]
    public void Validator_DescriptionWithoutUsageHint_Warns()
    {
        var metadata = new SkillMetadata { Name = "vague", Description = "一个处理文件的技能。" };

        var diagnostics = SkillValidator.Validate(metadata, "正文内容足够长，用来通过最小长度检查。");

        Assert.Contains(diagnostics, d => d.Code == SkillDiagnosticCode.MissingUsageHint
                                          && d.Severity == SkillDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Validator_OverlongBody_WarnsButDoesNotBlock()
    {
        var metadata = new SkillMetadata { Name = "long-body", Description = "很长的技能。用于测试时。" };
        var body = string.Join("\n", Enumerable.Repeat("一行说明。", 600));

        var diagnostics = SkillValidator.Validate(metadata, body);

        Assert.Contains(diagnostics, d => d.Code == SkillDiagnosticCode.BodyTooLong);
        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }

    // ── 目录加载 ──

    [Fact]
    public async Task Source_LoadsSkillsFromSubdirectories()
    {
        WriteSkill("pdf-processing", "name: pdf-processing\ndescription: 提取 PDF 文本。用于处理 .pdf 文件时。");
        WriteSkill("weekly-report", "name: weekly-report\ndescription: 整理周报。用于每周汇总时。");

        var source = new StandardSkillSource(Path.Combine(Root, "skills"));
        var skills = await source.LoadSkillsAsync();

        Assert.Equal(2, skills.Count);
        Assert.Contains(skills, s => s.Name == "pdf-processing" && s.Body.Contains("操作步骤"));
        Assert.DoesNotContain(source.Diagnostics, d => d.IsError);
    }

    [Fact]
    public async Task Source_LoadsSingleSkillDirectory()
    {
        var directory = Sandbox.Files.GetPath("single");
        Directory.CreateDirectory(directory);

        Sandbox.Files.Write("single/SKILL.md", """
            ---
            name: single
            description: 单技能目录。用于验证目录布局时。
            ---
            操作步骤：先做这个，再做那个。
            """);

        var source = new StandardSkillSource(directory);
        var skill = Assert.Single(await source.LoadSkillsAsync());

        Assert.Equal("single", skill.Name);
        Assert.Equal(directory, skill.DirectoryPath);
    }

    [Fact]
    public async Task Source_MapsMetadataToDefinition()
    {
        Sandbox.Files.Write("skills/doc/SKILL.md", """
            ---
            name: doc
            description: 处理文档。用于整理资料时。
            license: MIT
            version: 2.0
            allowed-tools: read_file
            metadata:
              category: 文档
              icon: doc
              tags: 文档, 整理
            ---
            操作步骤：读取源文件，整理结构，输出结果。
            """);

        var source = new StandardSkillSource(Path.Combine(Root, "skills"));
        var skill = Assert.Single(await source.LoadSkillsAsync());

        Assert.Equal("文档", skill.Category);
        Assert.Equal("MIT", skill.License);
        Assert.Equal("2.0", skill.Version);
        Assert.Equal("doc", skill.Icon);
        Assert.Equal(new[] { "文档", "整理" }, skill.Tags);
        Assert.Equal(new[] { "read_file" }, skill.AllowedTools);
        Assert.Equal(skill.Body, skill.SystemPrompt);
    }

    [Fact]
    public async Task Source_RejectsInvalidSkill_AndReportsReason()
    {
        WriteSkill("good", "name: good\ndescription: 合法技能。用于对照时。");
        WriteSkill("broken", "name: broken");
        Sandbox.Files.Write("skills/no-frontmatter/SKILL.md", "# 没有 frontmatter");

        var source = new StandardSkillSource(Path.Combine(Root, "skills"));
        var skills = await source.LoadSkillsAsync();

        Assert.Single(skills);
        Assert.Contains(source.Diagnostics, d => d.Code == SkillDiagnosticCode.MissingFrontmatter && d.IsError);
        Assert.Contains(source.Diagnostics, d => d.Code == SkillDiagnosticCode.MissingDescription && d.IsError);
    }

    [Fact]
    public async Task Source_MissingDirectory_ReturnsEmpty()
    {
        var source = new StandardSkillSource(Sandbox.Files.GetPath("not-exist"));

        Assert.Empty(await source.LoadSkillsAsync());
        Assert.Empty(source.Diagnostics);
    }

    [Fact]
    public async Task Source_DuplicateNames_KeepsLastAndWarns()
    {
        WriteSkill("a-first", "name: dup\ndescription: 第一个。用于测试时。");
        WriteSkill("b-second", "name: dup\ndescription: 第二个。用于测试时。");

        var source = new StandardSkillSource(Path.Combine(Root, "skills"));
        var skill = Assert.Single(await source.LoadSkillsAsync());

        Assert.Contains("第二个", skill.Description);
        Assert.Contains(source.Diagnostics, d => d.Code == SkillDiagnosticCode.DuplicateName);
    }

    [Fact]
    public async Task Source_ClassifiesResourcesAndSkipsSkillFile()
    {
        WriteSkill("with-resources", "name: with-resources\ndescription: 带资源的技能。用于测试时。");
        Sandbox.Files.Write("skills/with-resources/scripts/run.py", "print('hi')");
        Sandbox.Files.Write("skills/with-resources/references/GUIDE.md", "# 指南");
        Sandbox.Files.Write("skills/with-resources/assets/template.txt", "模板");

        var source = new StandardSkillSource(Path.Combine(Root, "skills"));
        var skill = Assert.Single(await source.LoadSkillsAsync());

        Assert.Equal(3, skill.Resources.Count);
        Assert.DoesNotContain(skill.Resources, r => r.RelativePath.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(skill.Resources, r => r.RelativePath == "scripts/run.py" && r.Kind == SkillResourceKind.Script);
        Assert.Contains(skill.Resources, r => r.RelativePath == "references/GUIDE.md" && r.Kind == SkillResourceKind.Reference);
        Assert.Contains(skill.Resources, r => r.RelativePath == "assets/template.txt" && r.Kind == SkillResourceKind.Asset);
    }

    [Fact]
    public async Task Source_SkipsHiddenDirectories()
    {
        WriteSkill("hidden-res", "name: hidden-res\ndescription: 忽略隐藏目录。用于测试时。");
        Sandbox.Files.Write("skills/hidden-res/.git/config", "x");

        var source = new StandardSkillSource(Path.Combine(Root, "skills"));
        var skill = Assert.Single(await source.LoadSkillsAsync());

        Assert.Empty(skill.Resources);
    }

    // ── 资源读取 ──

    [Fact]
    public async Task Reader_ReturnsContent()
    {
        WriteSkill("reader", "name: reader\ndescription: 读取资源。用于测试时。");
        Sandbox.Files.Write("skills/reader/references/GUIDE.md", "参考内容");

        var result = await SkillResourceReader.ReadAsync(
            Path.Combine(Root, "skills", "reader"), "references/GUIDE.md");

        Assert.Equal("参考内容", result.Content);
        Assert.False(result.Truncated);
        Assert.Equal(SkillResourceKind.Reference, result.Resource.Kind);
    }

    [Theory]
    [InlineData("../other/SKILL.md")]
    [InlineData("../../etc/passwd")]
    public async Task Reader_RejectsPathEscape(string path)
    {
        WriteSkill("escape", "name: escape\ndescription: 路径校验。用于测试时。");

        await Assert.ThrowsAsync<ArgumentException>(() => SkillResourceReader.ReadAsync(
            Path.Combine(Root, "skills", "escape"), path));
    }

    [Fact]
    public async Task Reader_TruncatesLargeFile()
    {
        WriteSkill("big", "name: big\ndescription: 截断读取。用于测试时。");
        Sandbox.Files.Write("skills/big/references/big.txt", new string('a', 4096));

        var result = await SkillResourceReader.ReadAsync(
            Path.Combine(Root, "skills", "big"), "references/big.txt", maxBytes: 128);

        Assert.True(result.Truncated);
        Assert.Equal(128, result.Content.Length);
    }

    // ── 索引（第一层）──

    [Fact]
    public void Index_ContainsNameAndDescriptionOnly()
    {
        var skill = new SkillDefinition
        {
            Name = "pdf",
            Description = "提取 PDF 文本。用于处理 .pdf 文件时。",
            Body = "这里是很长的正文，不应该出现在索引里。",
            Category = "文档"
        };

        var index = SkillIndexBuilder.Build([skill]);

        Assert.Contains("`pdf`", index);
        Assert.Contains("提取 PDF 文本", index);
        Assert.DoesNotContain("很长的正文", index);
        Assert.Contains("**文档**", index);
    }

    [Fact]
    public void Index_CollapsesWhitespaceAndTruncates()
    {
        var skill = new SkillDefinition
        {
            Name = "multi",
            Description = "第一行。\n\n第二行。",
            Category = "测试"
        };

        var index = SkillIndexBuilder.Build([skill]);

        Assert.Contains("第一行。 第二行。", index);
    }

    [Fact]
    public void Index_EmptyRegistry_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SkillIndexBuilder.Build([]));
        Assert.Equal("基础提示词", SkillIndexBuilder.Compose("基础提示词", []));
    }

    // ── 按需加载工具 ──

    [Fact]
    public async Task Toolset_RegistersBothTools()
    {
        var registry = await BuildRegistryAsync();
        var tools = new ToolManager();
        var toolset = new SkillToolset(registry);

        toolset.RegisterTools(tools);

        Assert.True(tools.IsRegistered(SkillToolset.DefaultLoadSkillToolName));
        Assert.True(tools.IsRegistered(SkillToolset.DefaultReadResourceToolName));
    }

    [Fact]
    public async Task Toolset_LoadSkill_ReturnsBodyAndResources()
    {
        var registry = await BuildRegistryAsync();
        var tools = new ToolManager();
        var toolset = new SkillToolset(registry);
        toolset.RegisterTools(tools);

        var result = await tools.ExecuteToolAsync(
            SkillToolset.DefaultLoadSkillToolName,
            """{"name":"pdf-processing"}""");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Contains("操作步骤", result.Result);
        Assert.Contains("references/GUIDE.md", result.Result);
        Assert.True(toolset.IsActivated("pdf-processing"));
    }

    [Fact]
    public async Task Toolset_LoadSkill_UnknownName_ListsAvailableSkills()
    {
        var registry = await BuildRegistryAsync();
        var tools = new ToolManager();
        new SkillToolset(registry).RegisterTools(tools);

        var result = await tools.ExecuteToolAsync(
            SkillToolset.DefaultLoadSkillToolName,
            """{"name":"does-not-exist"}""");

        Assert.False(result.IsSuccess);
        Assert.Contains("pdf-processing", result.Error);
    }

    [Fact]
    public async Task Toolset_ReadResource_ReturnsContent()
    {
        var registry = await BuildRegistryAsync();
        var tools = new ToolManager();
        new SkillToolset(registry).RegisterTools(tools);

        var result = await tools.ExecuteToolAsync(
            SkillToolset.DefaultReadResourceToolName,
            """{"skill":"pdf-processing","path":"references/GUIDE.md"}""");

        Assert.True(result.IsSuccess, result.Error);
        Assert.Contains("参考内容", result.Result);
    }

    [Fact]
    public async Task Toolset_ReadResource_TraversalIsRejected()
    {
        var registry = await BuildRegistryAsync();
        var tools = new ToolManager();
        new SkillToolset(registry).RegisterTools(tools);

        var result = await tools.ExecuteToolAsync(
            SkillToolset.DefaultReadResourceToolName,
            """{"skill":"pdf-processing","path":"../../secret.txt"}""");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Toolset_SystemPrompt_AppendsIndexWithoutBody()
    {
        var registry = await BuildRegistryAsync();
        var toolset = new SkillToolset(registry);

        var prompt = toolset.BuildSystemPrompt("你是一个助手。");

        Assert.Contains("你是一个助手。", prompt);
        Assert.Contains("pdf-processing", prompt);
        Assert.DoesNotContain("操作步骤", prompt);
    }

    [Fact]
    public async Task Toolset_ActivationEvent_FiresOncePerSkill()
    {
        var registry = await BuildRegistryAsync();
        var toolset = new SkillToolset(registry);
        var fired = 0;

        toolset.SkillActivated += (_, _) => fired++;

        toolset.Activate("pdf-processing");
        toolset.Activate("pdf-processing");

        Assert.Equal(1, fired);
        Assert.Single(toolset.ActivatedSkills);
    }

    [Fact]
    public async Task Toolset_WorksForJsonDefinedSkills()
    {
        var registry = new SkillRegistry();
        registry.Register(new SkillDefinition
        {
            Name = "json-skill",
            Description = "来自 JSON 的技能。用于验证兼容性时。",
            SystemPrompt = "这是写在 systemPrompt 里的说明。"
        });

        var text = new SkillToolset(registry).Activate("json-skill");

        Assert.Contains("systemPrompt 里的说明", text);
    }

    [Fact]
    public async Task Di_AddStandardSkills_RegistersLoadedSkills()
    {
        WriteSkill("di-skill", "name: di-skill\ndescription: DI 注册。用于验证加载链路时。");

        var services = new ServiceCollection();
        services.AddSkillSystem(loadCatalog: false);
        services.AddStandardSkills(Path.Combine(Root, "skills"));

        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ISkillRegistry>();

        Assert.NotNull(registry.Get("di-skill"));
    }

    private async Task<SkillRegistry> BuildRegistryAsync()
    {
        WriteSkill("pdf-processing", "name: pdf-processing\ndescription: 提取 PDF 文本。用于处理 .pdf 文件时。");
        Sandbox.Files.Write("skills/pdf-processing/references/GUIDE.md", "参考内容");

        var source = new StandardSkillSource(Path.Combine(Root, "skills"));
        var registry = new SkillRegistry();

        foreach (var skill in await source.LoadSkillsAsync())
            registry.Register(skill);

        return registry;
    }

    private void WriteSkill(string directoryName, string frontmatter, string body = "操作步骤：先抽取文本，再整理结构。")
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine(frontmatter);
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine(body);

        Sandbox.Files.Write($"skills/{directoryName}/SKILL.md", builder.ToString());
    }
}
