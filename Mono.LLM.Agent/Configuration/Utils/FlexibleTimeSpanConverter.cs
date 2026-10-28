// ============================================================================
// 文件：FlexibleTimeSpanConverter.cs
// 用途：让 TimeSpan 在 JSON 里既能读人话、又能读标准格式。
// 实现方：FlexibleTimeSpanConverter
// 调用方：DI 装配（AddMonoLLMAgent）、应用启动
// 开发时间：2026-10-28
// 版本：0.5.0
// ============================================================================

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mono.LLM.Agent.Configuration;

/// <summary>
/// 让 <see cref="TimeSpan"/> 在 JSON 里既能读人话、又能读标准格式。
/// </summary>
/// <remarks>
/// <para>
/// 写入统一用 <c>c</c> 格式（<c>00:00:30</c>），保证往返稳定；
/// 读取额外接受 <c>30s</c>、<c>5m</c>、<c>2h</c>、<c>500ms</c>、<c>1d</c>，以及纯数字（按秒解释）。
/// </para>
/// <para>
/// 配置文件中写 <c>"timeout": "5m"</c> 比 <c>"00:05:00"</c> 更难写错——这也是加它的唯一理由。
/// </para>
/// </remarks>
public sealed class FlexibleTimeSpanConverter : JsonConverter<TimeSpan>
{
    /// <inheritdoc/>
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return TimeSpan.FromSeconds(reader.GetDouble());

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"无法把 {reader.TokenType} 解析为时间间隔。");

        var text = reader.GetString();

        return TryParse(text, out var value)
            ? value
            : throw new JsonException($"无法解析时间间隔 '{text}'。支持 30s / 5m / 2h / 500ms / 1d / 00:05:00。");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == Timeout.InfiniteTimeSpan ? "infinite" : value.ToString("c", CultureInfo.InvariantCulture));

    /// <summary>
    /// 解析时间间隔文本。
    /// </summary>
    /// <param name="text">待解析文本。</param>
    /// <param name="value">解析结果。</param>
    /// <returns>解析成功返回 <c>true</c>。</returns>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();

        if (trimmed.Equals("infinite", StringComparison.OrdinalIgnoreCase)
            || trimmed == "∞")
        {
            value = Timeout.InfiniteTimeSpan;
            return true;
        }

        var (numberPart, unit) = SplitUnit(trimmed);

        if (unit is not null
            && double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            value = unit switch
            {
                "ms" => TimeSpan.FromMilliseconds(amount),
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                "h" => TimeSpan.FromHours(amount),
                "d" => TimeSpan.FromDays(amount),
                _ => TimeSpan.Zero
            };

            return true;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            value = TimeSpan.FromSeconds(seconds);
            return true;
        }

        return TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out value);
    }

    private static (string Number, string? Unit) SplitUnit(string text)
    {
        var index = text.Length;

        while (index > 0 && char.IsLetter(text[index - 1]))
            index--;

        if (index == text.Length)
            return (text, null);

        var unit = text[index..].ToLowerInvariant();

        return unit is "ms" or "s" or "m" or "h" or "d"
            ? (text[..index].Trim(), unit)
            : (text, null);
    }
}

/// <summary>
/// <see cref="FlexibleTimeSpanConverter"/> 的可空版本，供「不填就用默认值」的字段使用。
/// </summary>
public sealed class FlexibleNullableTimeSpanConverter : JsonConverter<TimeSpan?>
{
    private static readonly FlexibleTimeSpanConverter Inner = new();

    /// <inheritdoc/>
    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        return Inner.Read(ref reader, typeof(TimeSpan), options);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        Inner.Write(writer, value.Value, options);
    }
}
