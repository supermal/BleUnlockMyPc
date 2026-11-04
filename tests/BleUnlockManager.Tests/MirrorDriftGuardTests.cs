using System.Globalization;
using System.Text.RegularExpressions;
using BleUnlockManager.Core;
using BleUnlockManager.Core.Config;
using Xunit;

namespace BleUnlockManager.Tests;

/// <summary>
/// 镜像漂移守卫（评审建议 1）：管理端的校验文案、上限常量与日志文件名模式是对服务端源码的手工镜像
/// （设计 §5.3 明确无共享引用）；本测试直接读取服务端源码文本做逐字/常量比对，
/// 服务端口径变化时在此失败并指出漂移项。
/// </summary>
public sealed class MirrorDriftGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "BleUnlockService", "Config", "OptionsValidator.cs")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException($"未能从 {AppContext.BaseDirectory} 向上定位仓库根（镜像守卫需要服务端源码）。");
    }

    private static string ServiceSourcePath(params string[] parts)
        => Path.Combine(new[] { RepoRoot, "src", "BleUnlockService" }.Concat(parts).ToArray());

    /// <summary>全字段非法配置：一次触发 13 条规则（上限 1 条为插值生成（SnoozeMinutes），其余 12 条为服务端字面量）。</summary>
    private static BleUnlockConfig AllInvalidConfig()
    {
        var config = new BleUnlockConfig();
        config.Device.Irk = "zz";                                 // 32 位十六进制
        config.Device.IdentityAddress = "xx";                     // 合法 MAC
        config.Credentials.UserName = "u\r\nx";                   // 控制字符（UserName 非空 → 不触发「不能为空」）
        config.Credentials.Password = "p\tx";                     // 控制字符
        config.Proximity.LockThresholdDbm = -80;
        config.Proximity.UnlockThresholdDbm = -80;                // <= Lock → 死区
        config.Proximity.UnlockSampleCount = 0;                   // 必须在 1 到 20 之间
        config.Proximity.LockPromptSampleCount = 0;               // 必须在 1 到 50 之间
        config.Proximity.LockConfirmSampleCount = 0;              // 必须在 1 到 50 之间
        config.Proximity.StartupGraceSeconds = -1;                // 不能为负数
        config.Proximity.UnlockPendingTimeoutSeconds = 0;         // 必须为正数
        config.Proximity.NoSignalDepartureSeconds = 10;           // 0 或 30–3600
        config.Notification.RejectSnoozeSeconds = 0;              // 必须为正数
        config.Notification.SnoozeMinutes = 300;                  // 超上限
        return config;
    }

    [Fact]
    public void ValidatorMessages_MatchServiceSourceVerbatim()
    {
        // Los textos del servicio viven ahora en sus recursos; se compara con la versión china,
        // que es el idioma fijado para los tests (las otras dos las vigila ResourceParityTests)
        var source = File.ReadAllText(ServiceSourcePath("Resources", "ServiceStrings.zh.resx"));
        var errors = ConfigValidator.Validate(AllInvalidConfig()).Errors;

        Assert.Equal(13, errors.Count);

        // 服务端以字符串插值拼接的上限文案：这里断言其字面前缀存在于源码
        foreach (var prefix in new[]
                 {
                     "Notification.SnoozeMinutes 不能超过 ",
                 })
        {
            Assert.Contains(prefix, source, StringComparison.Ordinal);
        }

        foreach (var error in errors)
        {
            if (error.Message.StartsWith("Notification.SnoozeMinutes 不能超过 ", StringComparison.Ordinal))
            {
                continue; // 由前缀断言与上限常量测试共同覆盖
            }

            Assert.True(source.Contains(error.Message, StringComparison.Ordinal),
                $"服务端 OptionsValidator 未逐字包含镜像文案（漂移）：{error.Message}");
        }

        // 渲染出的上限文案与常量数值一致
        Assert.Contains(errors, e => e.Message == "Notification.SnoozeMinutes 不能超过 240 分钟");
    }

    [Fact]
    public void UpperBounds_MatchHelperProtocolConstants()
    {
        var source = File.ReadAllText(ServiceSourcePath("Session", "HelperProtocol.cs"));

        Assert.Equal(ConfigValidator.MaxSnoozeMinutes, ReadConst(source, "MaxSnoozeMinutes"));
    }

    [Fact]
    public void LogFilePattern_MatchesServiceProgram()
    {
        var program = File.ReadAllText(ServiceSourcePath("Program.cs"));

        Assert.Contains("\"bleunlock-.log\"", program, StringComparison.Ordinal);
        Assert.Equal("bleunlock-*.log", AppPaths.LogFilePattern);
    }

    private static double ReadConst(string source, string name)
    {
        var match = Regex.Match(source, $@"{name}\s*=\s*([0-9.]+)");
        Assert.True(match.Success, $"服务端源码中未找到常量声明：{name}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
