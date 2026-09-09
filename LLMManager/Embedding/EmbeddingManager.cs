using Microsoft.Extensions.AI;
using LLMManager.Core;

namespace LLMManager.Embedding;

/// <summary>
/// 提供文本向量化（Embedding）生成、相似度计算和语义搜索功能。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EmbeddingManager"/> 封装了与 Embedding 模型的交互能力，主要提供三类功能：
/// </para>
/// <list type="bullet">
///   <item><see cref="GenerateAsync"/> / <see cref="GenerateBatchAsync"/> — 单条/批量生成文本向量；</item>
///   <item><see cref="CosineSimilarity"/> — 计算两个向量之间的余弦相似度；</item>
///   <item><see cref="FindSimilarAsync"/> — 在给定语料库中进行语义相似度检索。</item>
/// </list>
/// <para>
/// 所有向量运算均基于浮点数 (<see cref="float"/>) 类型的嵌入向量。
/// </para>
/// </remarks>
public class EmbeddingManager
{
    private readonly ILLMClient _client;

    /// <summary>
    /// 初始化 <see cref="EmbeddingManager"/> 的新实例。
    /// </summary>
    /// <param name="client">支持 Embedding 生成的 LLM 客户端实例。</param>
    public EmbeddingManager(ILLMClient client)
    {
        _client = client;
    }

    /// <summary>
    /// 为单条文本生成嵌入向量。
    /// </summary>
    /// <param name="text">要向量化的输入文本。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 表示文本语义的浮点数向量 <see cref="Embedding{T}"/>，其中 T 为 <see cref="float"/>。
    /// 向量的维度取决于底层模型配置。
    /// </returns>
    public async Task<Embedding<float>> GenerateAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        return await _client.GenerateEmbeddingAsync(text, cancellationToken);
    }

    /// <summary>
    /// 为多条文本批量生成嵌入向量。
    /// </summary>
    /// <param name="texts">要向量化的文本集合。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 与输入文本一一对应的嵌入向量列表。返回列表中的第 i 个元素对应输入集合中的第 i 条文本。
    /// </returns>
    /// <remarks>
    /// 批量生成通常比逐条调用 <see cref="GenerateAsync"/> 具有更高的吞吐效率，
    /// 建议在有大量文本需要向量化时优先使用此方法。
    /// </remarks>
    public async Task<IList<Embedding<float>>> GenerateBatchAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        return await _client.GenerateEmbeddingsAsync(texts, cancellationToken);
    }

    /// <summary>
    /// 计算两个嵌入向量之间的余弦相似度。
    /// </summary>
    /// <param name="a">第一个嵌入向量。</param>
    /// <param name="b">第二个嵌入向量。</param>
    /// <returns>
    /// 范围在 [-1.0, 1.0] 之间的余弦相似度值：
    /// <list type="bullet">
    ///   <item><c>1.0</c> 表示两向量方向完全一致（语义相同）；</item>
    ///   <item><c>0.0</c> 表示两向量正交（无关）；</item>
    ///   <item><c>-1.0</c> 表示两向量方向相反。</item>
    /// </list>
    /// 若任一向量为零向量，则返回 <c>0.0</c>。
    /// </returns>
    /// <exception cref="ArgumentException">
    /// 当两个向量的维度（长度）不一致时抛出。
    /// </exception>
    public float CosineSimilarity(Embedding<float> a, Embedding<float> b)
    {
        if (a.Vector.Length != b.Vector.Length)
        {
            throw new ArgumentException("Embeddings must have the same dimension.");
        }

        var spanA = a.Vector.Span;
        var spanB = b.Vector.Span;

        float dotProduct = 0;
        float normA = 0;
        float normB = 0;

        for (int i = 0; i < spanA.Length; i++)
        {
            dotProduct += spanA[i] * spanB[i];
            normA += spanA[i] * spanA[i];
            normB += spanB[i] * spanB[i];
        }

        if (normA == 0 || normB == 0)
        {
            return 0;
        }

        return dotProduct / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    /// <summary>
    /// 在给定语料库中查找与查询文本语义最相似的条目。
    /// </summary>
    /// <param name="query">查询文本，将先被向量化后再与语料库进行比较。</param>
    /// <param name="corpus">待检索的语料库文本集合。</param>
    /// <param name="topK">
    /// 返回的最相似结果数量上限。默认为 5。
    /// 若设为 0 或负数，将返回空列表。
    /// </param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <returns>
    /// 按相似度降序排列的前 <paramref name="topK"/> 个结果列表，
    /// 每个元素为包含原始文本及其相似度分数的元组 <c>(string Text, float Similarity)</c>。
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// 当批量生成的向量数量与输入文本数量不一致时抛出。
    /// </exception>
    /// <remarks>
    /// <para>
    /// 此方法的执行流程：
    /// </para>
    /// <list type="number">
    ///   <item>对 <paramref name="query"/> 调用 <see cref="GenerateAsync"/> 生成查询向量；</item>
    ///   <item>对 <paramref name="corpus"/> 调用 <see cref="GenerateBatchAsync"/> 批量生成语料向量；</item>
    ///   <item>逐一计算查询向量与各语料向量的 <see cref="CosineSimilarity"/>；</item>
    ///   <item>按相似度降序排序并截取前 <paramref name="topK"/> 条结果返回。</item>
    /// </list>
    /// <para>
    /// 在计算过程中会定期检查 <paramref name="cancellationToken"/>，支持长时间运行时的中途取消。
    /// </para>
    /// </remarks>
    public async Task<List<(string Text, float Similarity)>> FindSimilarAsync(
        string query,
        IEnumerable<string> corpus,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        if (corpus == null) return new List<(string, float)>();
        if (topK <= 0) return new List<(string, float)>();

        var queryEmbedding = await GenerateAsync(query, cancellationToken);
        var corpusList = corpus.ToList();

        if (corpusList.Count == 0) return new List<(string, float)>();

        var corpusEmbeddings = await GenerateBatchAsync(corpusList, cancellationToken);

        if (corpusEmbeddings == null || corpusEmbeddings.Count != corpusList.Count)
        {
            throw new InvalidOperationException("GenerateBatchAsync must return the same number of embeddings as input texts.");
        }

        var results = new List<(string Text, float Similarity)>(corpusList.Count);

        for (int i = 0; i < corpusEmbeddings.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var similarity = CosineSimilarity(queryEmbedding, corpusEmbeddings[i]);
            results.Add((corpusList[i], similarity));
        }

        return results.OrderByDescending(r => r.Similarity).Take(Math.Min(topK, results.Count)).ToList();
    }
}
