using System.Text.Json;

namespace AutoUnlockManager.Core.Management;

/// <summary>
/// 管理通道单行 JSON 帧解析（v1）。契约（设计 §5.5）：坏行、非对象、缺失/未知 t 一律返回 null
/// （调用方忽略继续），任何解析异常不得外抛；未知字段默认忽略（System.Text.Json 行为）。
/// </summary>
public static class ManagementFrameParser
{
    /// <summary>大小写不敏感绑定："t"↔T、"tsMs"↔TsMs；无需命名策略。仅解析器内部使用（不可变约定）。</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>解析一行帧；无法识别时返回 null（不抛）。</summary>
    public static ManagementFrame? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return t.GetString() switch
            {
                "hello" => TryDeserialize<HelloFrame>(root),
                "snapshot" => TryDeserialize<SnapshotFrame>(root),
                _ => null, // 未知帧类型：忽略（v2 前向兼容）
            };
        }
        catch
        {
            return null;
        }
    }

    private static T? TryDeserialize<T>(JsonElement root) where T : ManagementFrame
    {
        try
        {
            return root.Deserialize<T>(Options);
        }
        catch
        {
            return null;
        }
    }
}
