using System.Globalization;
using System.Text;
using BleUnlockManager.Core.Management;

namespace BleUnlockManager.Core;

/// <summary>样本 CSV 导出（Task 4）：表头 <c>timestamp_iso,rssi_dbm</c>；ISO-8601 UTC（毫秒精度）。</summary>
public static class SampleCsv
{
    public const string Header = "timestamp_iso,rssi_dbm";

    /// <summary>构建 CSV 文本（CRLF 行尾；数值 InvariantCulture，避免本地化小数点）。</summary>
    public static string Build(IEnumerable<SamplePoint> samples)
    {
        var builder = new StringBuilder(Header).Append("\r\n");
        foreach (var sample in samples)
        {
            var iso = DateTimeOffset.FromUnixTimeMilliseconds(sample.TsMs)
                .UtcDateTime
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            builder.Append(iso)
                .Append(',')
                .Append(sample.Rssi.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("\r\n");
        }

        return builder.ToString();
    }
}
