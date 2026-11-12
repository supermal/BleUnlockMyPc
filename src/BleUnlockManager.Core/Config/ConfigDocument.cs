using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using BleUnlockManager.Core.Resources;

namespace BleUnlockManager.Core.Config;

/// <summary>
/// 配置文件的读写（设计 §5.3 保存流水线）。
/// 读：容忍大小写键、字符串数字、注释与尾逗号（与服务端 Microsoft.Extensions.Configuration 绑定语义对齐），
/// 缺节回退默认值；写入：读原 JSON（<see cref="JsonNode"/> 原位改值，保留未知字段与键序）→
/// 同目录临时文件 → <see cref="File.Replace(string,string,string,bool)"/> 原子替换（保留 <c>.bak</c>）。
/// </summary>
public static class ConfigDocument
{
    private const int ReplaceAttempts = 3;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(150);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new TolerantEnumConverter<IrkByteOrder>(),
            new TolerantEnumConverter<NotifierMode>(),
        },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), // 中文/凭据字符不转义为 \uXXXX（仍是合法 JSON）
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 读盘加载配置；文件缺失/无法读取/JSON 非法时抛异常（VM 捕获后转横幅提示）。
    /// </summary>
    public static BleUnlockConfig Load(string path)
    {
        var root = ParseRoot(File.ReadAllText(path));
        var section = FindProperty(root, "BleUnlock");
        if (section is null)
        {
            return new BleUnlockConfig(); // 缺节 = 默认值（服务端同样以默认值绑定后校验）
        }

        var config = section.Deserialize<BleUnlockConfig>(ReadOptions) ?? new BleUnlockConfig();
        NormalizeNulls(config); // JSON null（合法输入）归一，与服务端“未设置/默认值”语义对齐
        return config;
    }

    /// <summary>
    /// 反序列化后的 null 归一：字符串 null → 空串、节 null → 默认实例。
    /// 服务端配置绑定视 null 为未设置；管理端保存/差异路径依赖非空，必须在此收敛（评审 I1）。
    /// </summary>
    private static void NormalizeNulls(BleUnlockConfig config)
    {
        config.Device ??= new DeviceConfig();
        config.Credentials ??= new CredentialsConfig();
        config.Proximity ??= new ProximityConfig();
        config.Notification ??= new NotificationConfig();
        config.Device.Irk ??= "";
        config.Credentials.UserName ??= "";
        config.Credentials.Password ??= "";
    }

    /// <summary>
    /// 保存（校验通过后调用）：原位改写 <c>BleUnlock</c> 下全部 15 字段，未知字段保留；
    /// 已废弃的旧键（窗口/最小样本/提示超时）不写入也不删除，留在文件里不影响服务端绑定。
    /// Irk 归一化为 32 位小写十六进制，IdentityAddress 留空写 <c>null</c>、否则归一化为 12 位小写十六进制。
    /// 原子替换失败（服务正在读取的瞬态锁）自动重试 <see cref="ReplaceAttempts"/> 次后抛异常。
    /// </summary>
    public static void Save(string path, BleUnlockConfig config)
    {
        var root = ParseRoot(File.ReadAllText(path));

        var section = GetOrAddObject(root, "BleUnlock");
        var device = GetOrAddObject(section, "Device");
        var credentials = GetOrAddObject(section, "Credentials");
        var proximity = GetOrAddObject(section, "Proximity");
        var notification = GetOrAddObject(section, "Notification");

        Set(device, "Irk", JsonValue.Create(ConfigValidator.NormalizeIrk(config.Device.Irk) ?? config.Device.Irk));
        Set(device, "IrkByteOrder", JsonValue.Create(config.Device.IrkByteOrder.ToString()));

        var address = string.IsNullOrWhiteSpace(config.Device.IdentityAddress)
            ? null
            : ConfigValidator.FormatAddress(ConfigValidator.NormalizeAddress(config.Device.IdentityAddress))
              ?? config.Device.IdentityAddress;
        Set(device, "IdentityAddress", address is null ? null : JsonValue.Create(address));

        Set(credentials, "UserName", JsonValue.Create(config.Credentials.UserName));
        Set(credentials, "Password", JsonValue.Create(config.Credentials.Password));

        Set(proximity, "LockThresholdDbm", JsonValue.Create(config.Proximity.LockThresholdDbm));
        Set(proximity, "UnlockThresholdDbm", JsonValue.Create(config.Proximity.UnlockThresholdDbm));
        Set(proximity, "UnlockSampleCount", JsonValue.Create(config.Proximity.UnlockSampleCount));
        Set(proximity, "LockPromptSampleCount", JsonValue.Create(config.Proximity.LockPromptSampleCount));
        Set(proximity, "LockConfirmSampleCount", JsonValue.Create(config.Proximity.LockConfirmSampleCount));
        Set(proximity, "SampleIntervalMs", JsonValue.Create(config.Proximity.SampleIntervalMs));
        Set(proximity, "StartupGraceSeconds", JsonValue.Create(config.Proximity.StartupGraceSeconds));
        Set(proximity, "UnlockPendingTimeoutSeconds", JsonValue.Create(config.Proximity.UnlockPendingTimeoutSeconds));
        Set(proximity, "NoSignalDepartureSeconds", JsonValue.Create(config.Proximity.NoSignalDepartureSeconds));
        Set(proximity, "SuddenChangeReferenceCount", JsonValue.Create(config.Proximity.SuddenChangeReferenceCount));
        Set(proximity, "SuddenChangeDeltaDb", JsonValue.Create(config.Proximity.SuddenChangeDeltaDb));
        Set(proximity, "SuddenDropConfirmSeconds", JsonValue.Create(config.Proximity.SuddenDropConfirmSeconds));
        Set(proximity, "SuddenRiseConfirmSeconds", JsonValue.Create(config.Proximity.SuddenRiseConfirmSeconds));

        Set(notification, "Mode", JsonValue.Create(config.Notification.Mode.ToString()));
        Set(notification, "RejectSnoozeSeconds", JsonValue.Create(config.Notification.RejectSnoozeSeconds));
        Set(notification, "SnoozeMinutes", JsonValue.Create(config.Notification.SnoozeMinutes));

        var json = root.ToJsonString(WriteOptions) + Environment.NewLine;
        WriteAtomically(path, json);
    }

    /// <summary>
    /// Idioma de la interfaz (<c>BleUnlock:Ui:Language</c>): system | zh | en | es. Fichero ausente,
    /// dañado o sin la clave = <c>system</c>; nunca lanza.
    /// </summary>
    public static string ReadLanguage(string path)
    {
        try
        {
            var root = ParseRoot(File.ReadAllText(path));
            var section = FindKey(root, "BleUnlock") is { } a ? root[a] as JsonObject : null;
            var ui = section is not null && FindKey(section, "Ui") is { } u ? section[u] as JsonObject : null;
            var value = ui is not null && FindKey(ui, "Language") is { } l ? ui[l] : null;
            return value is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : "system";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "system";
        }
    }

    /// <summary>
    /// Guarda solo <c>BleUnlock:Ui:Language</c>; el resto del fichero queda intacto. El servicio lo
    /// recarga en caliente como cualquier otro cambio. Misma escritura atómica que <see cref="Save"/>.
    /// </summary>
    public static void SaveLanguage(string path, string code)
    {
        var root = ParseRoot(File.ReadAllText(path));
        var ui = GetOrAddObject(GetOrAddObject(root, "BleUnlock"), "Ui");
        Set(ui, "Language", JsonValue.Create(code));
        WriteAtomically(path, root.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    private static JsonObject ParseRoot(string text)
    {
        var node = JsonNode.Parse(text, nodeOptions: null, documentOptions: DocumentOptions);
        return node as JsonObject
            ?? throw new JsonException(CoreStrings.Config_RootNotObject);
    }

    /// <summary>大小写不敏感查找既有键（避免同义键重复写入）。</summary>
    private static string? FindKey(JsonObject obj, string name)
    {
        foreach (var kvp in obj)
        {
            if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Key;
            }
        }

        return null;
    }

    private static JsonNode? FindProperty(JsonObject obj, string name)
        => FindKey(obj, name) is { } key ? obj[key] : null;

    /// <summary>原位设置值（保持既有键名大小写与键序；不存在则追加）。</summary>
    private static void Set(JsonObject obj, string name, JsonNode? value)
        => obj[FindKey(obj, name) ?? name] = value;

    /// <summary>取子对象；缺失或类型不符时原位替换为新对象（保持键序）。</summary>
    private static JsonObject GetOrAddObject(JsonObject parent, string name)
    {
        if (FindKey(parent, name) is { } key)
        {
            if (parent[key] is JsonObject existing)
            {
                return existing;
            }

            var replacement = new JsonObject();
            parent[key] = replacement;
            return replacement;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static void WriteAtomically(string path, string content)
    {
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";
        File.WriteAllText(tempPath, content, Utf8NoBom);

        Exception? lastFailure = null;
        for (var attempt = 0; attempt < ReplaceAttempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path); // 防御性分支：正常流程目标必存在（Save 前已读原文件）
                }

                return;
            }
            catch (IOException ex)
            {
                lastFailure = ex; // 服务读取窗口的瞬态共享冲突：短暂退避后重试
                Thread.Sleep(ReplaceRetryDelay);
            }
        }

        try
        {
            File.Delete(tempPath); // 清理失败不掩盖原错误
        }
        catch
        {
            // 忽略
        }

        throw lastFailure!;
    }

    /// <summary>枚举读取容错：忽略大小写、接受数字形式（对齐服务端 TypeConverter 绑定语义）。</summary>
    private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
            {
                return (T)Enum.ToObject(typeof(T), number);
            }

            throw new JsonException(string.Format(CoreStrings.Config_InvalidValue, typeof(T).Name));
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
