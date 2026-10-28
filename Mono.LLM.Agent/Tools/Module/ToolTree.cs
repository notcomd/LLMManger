// ============================================================================
// 文件：ToolTree.cs
// 用途：按命名空间分组的工具树。
// 实现方：Mono.LLM.Agent（本文件定义）
// 调用方：ToolManager.GetToolTree()、运维与诊断视图
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

namespace Mono.LLM.Agent.Tools;

/// <summary>
/// 工具树节点的类型。
/// </summary>
public enum ToolTreeNodeKind
{
    /// <summary>根节点。</summary>
    Root = 0,

    /// <summary>命名空间节点，对应工具名前缀（MCP 服务器的名字）。</summary>
    Namespace = 1,

    /// <summary>工具叶子节点。</summary>
    Tool = 2
}

/// <summary>
/// 工具树中的一个节点。
/// </summary>
/// <remarks>
/// 工具名普遍带命名空间前缀：MCP 桥接把 <c>filesystem</c> 服务器的 <c>read_file</c>
/// 注册为 <c>filesystem__read_file</c>。按前缀分组后，「这台机器上有哪些能力」变得可读，
/// 也便于按来源裁剪或展示。
/// </remarks>
public sealed class ToolTreeNode
{
    internal ToolTreeNode(
        string name,
        string path,
        ToolTreeNodeKind kind,
        string description,
        ToolRegistration? tool,
        IReadOnlyList<ToolTreeNode> children)
    {
        Name = name;
        Path = path;
        Kind = kind;
        Description = description;
        Tool = tool;
        Children = children;
    }

    /// <summary>获取节点名称：命名空间或工具名。</summary>
    public string Name { get; }

    /// <summary>获取路径，例如 <c>filesystem/read_file</c>。</summary>
    public string Path { get; }

    /// <summary>获取节点类型。</summary>
    public ToolTreeNodeKind Kind { get; }

    /// <summary>获取描述。命名空间节点为空字符串。</summary>
    public string Description { get; }

    /// <summary>获取叶子节点对应的工具注册信息；命名空间与根节点为 <c>null</c>。</summary>
    public ToolRegistration? Tool { get; }

    /// <summary>获取子节点。</summary>
    public IReadOnlyList<ToolTreeNode> Children { get; }

    /// <summary>获取是否为工具叶子节点。</summary>
    public bool IsTool => Tool is not null;

    /// <summary>深度优先枚举自身及全部后代。</summary>
    /// <returns>节点序列。</returns>
    public IEnumerable<ToolTreeNode> SelfAndDescendants()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var node in child.SelfAndDescendants())
                yield return node;
        }
    }

    /// <summary>返回 <c>[类型] 路径</c> 形式的文本，便于日志输出。</summary>
    /// <returns>节点描述。</returns>
    public override string ToString() => $"[{Kind}] {Path}";
}

/// <summary>
/// 按命名空间分组的工具树。
/// </summary>
/// <remarks>
/// 只做分组与展示，不参与工具选择——工具是模型按 Schema 自行挑的，这里不引入额外路由。
/// 树由 <see cref="ToolManager.GetToolTree"/> 缓存，注册表变化时重建。
/// </remarks>
public sealed class ToolTree
{
    /// <summary>没有命名空间前缀的工具归入的分组名。</summary>
    public const string DefaultNamespace = "通用";

    /// <summary>命名空间分隔符，与 MCP 工具桥接保持一致。</summary>
    public const string Separator = "__";

    private ToolTree(ToolTreeNode root, IReadOnlyList<ToolRegistration> tools)
    {
        Root = root;
        Tools = tools;
    }

    /// <summary>获取根节点。</summary>
    public ToolTreeNode Root { get; }

    /// <summary>获取全部工具，按命名空间与名称排序。</summary>
    public IReadOnlyList<ToolRegistration> Tools { get; }

    /// <summary>获取工具数量。</summary>
    public int Count => Tools.Count;

    /// <summary>
    /// 从工具注册信息构建树。
    /// </summary>
    /// <param name="tools">工具注册信息集合。</param>
    /// <param name="rootName">根节点名称，默认「工具」。</param>
    /// <returns>工具树；输入为空时返回只有根节点的空树。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="tools"/> 为 <c>null</c> 时抛出。</exception>
    public static ToolTree Build(IEnumerable<ToolRegistration> tools, string rootName = "工具")
    {
        ArgumentNullException.ThrowIfNull(tools);

        var ordered = tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .OrderBy(tool => ResolveNamespace(tool.Name), StringComparer.OrdinalIgnoreCase)
            .ThenBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var namespaces = new List<ToolTreeNode>();

        foreach (var group in ordered.GroupBy(
                     tool => ResolveNamespace(tool.Name),
                     StringComparer.OrdinalIgnoreCase))
        {
            var leaves = group
                .Select(tool =>
                {
                    var shortName = StripPrefix(tool.Name, group.Key);

                    return new ToolTreeNode(
                        shortName,
                        $"{group.Key}/{shortName}",
                        ToolTreeNodeKind.Tool,
                        tool.Description,
                        tool,
                        Array.Empty<ToolTreeNode>());
                })
                .ToList();

            namespaces.Add(new ToolTreeNode(
                group.Key,
                group.Key,
                ToolTreeNodeKind.Namespace,
                string.Empty,
                null,
                leaves));
        }

        var root = new ToolTreeNode(
            rootName,
            string.Empty,
            ToolTreeNodeKind.Root,
            string.Empty,
            null,
            namespaces);

        return new ToolTree(root, ordered);
    }

    /// <summary>
    /// 按名称或路径查找节点。
    /// </summary>
    /// <param name="pathOrName">工具名、命名空间或完整路径。</param>
    /// <returns>找到的节点；不存在时为 <c>null</c>。</returns>
    public ToolTreeNode? Find(string pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
            return null;

        return Root.SelfAndDescendants().FirstOrDefault(node =>
            node.Path.Equals(pathOrName, StringComparison.OrdinalIgnoreCase)
            || node.Name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase)
            || (node.Tool is not null
                && node.Tool.Name.Equals(pathOrName, StringComparison.OrdinalIgnoreCase)));
    }

    private static string StripPrefix(string toolName, string @namespace)
    {
        if (@namespace == DefaultNamespace)
            return toolName;

        var prefixLength = @namespace.Length + Separator.Length;

        return toolName.Length > prefixLength && toolName.StartsWith(@namespace + Separator, StringComparison.Ordinal)
            ? toolName[prefixLength..]
            : toolName;
    }

    /// <summary>
    /// 解析工具名所属的命名空间。
    /// </summary>
    /// <param name="toolName">工具名，例如 <c>filesystem__read_file</c>。</param>
    /// <returns>前缀；没有前缀时返回 <see cref="DefaultNamespace"/>。</returns>
    public static string ResolveNamespace(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return DefaultNamespace;

        var index = toolName.IndexOf(Separator, StringComparison.Ordinal);

        return index <= 0 ? DefaultNamespace : toolName[..index];
    }
}
