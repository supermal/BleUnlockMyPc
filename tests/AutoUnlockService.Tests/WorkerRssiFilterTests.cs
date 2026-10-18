using Xunit;

namespace AutoUnlockService.Tests;

// RSSI 哨兵值过滤（2026-10-18 三次修订）：-127/-128 或 +127 一带是 BLE 规范预留的
// 「RSSI 不可用」标记，不是测量结果。放它们进判定会凭空凑出「信号变弱」，
// 实测曾疑似造成一次误弹窗
public class WorkerRssiFilterTests
{
    [Theory]
    [InlineData((short)-127, false)]
    [InlineData((short)-128, false)]
    [InlineData((short)127, false)]
    [InlineData((short)-126, true)]
    [InlineData((short)-100, true)]
    [InlineData((short)-55, true)]
    [InlineData((short)0, true)]
    public void Solo_las_lecturas_utilizables_llegan_a_la_maquina(short rssi, bool expected)
        => Assert.Equal(expected, Worker.HasUsableRssi(rssi));
}
