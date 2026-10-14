using AutoUnlockService.Session;
using Xunit;

namespace AutoUnlockService.Tests;

public class HelperProtocolTests
{
    [Fact]
    public void Prompt_ida_y_vuelta()
    {
        var line = HelperProtocol.BuildPrompt(7, 15, 5);

        Assert.True(HelperProtocol.TryParseCommand(line, out var cmd));
        var prompt = Assert.IsType<HelperCommand.Prompt>(cmd);
        Assert.Equal(7, prompt.Id);
        Assert.Equal(15, prompt.TimeoutSeconds);
        Assert.Equal(5, prompt.SnoozeMinutes);
    }

    [Fact]
    public void Cancel_ida_y_vuelta()
    {
        Assert.True(HelperProtocol.TryParseCommand(HelperProtocol.BuildCancel(3), out var cmd));
        Assert.Equal(3, Assert.IsType<HelperCommand.Cancel>(cmd).Id);
    }

    [Fact]
    public void Lock_ida_y_vuelta()
    {
        Assert.True(HelperProtocol.TryParseCommand(HelperProtocol.BuildLock(), out var cmd));
        Assert.IsType<HelperCommand.Lock>(cmd);
    }

    [Fact]
    public void Notify_conserva_un_mensaje_con_espacios()
    {
        var line = HelperProtocol.BuildNotify("密码验证失败，自动解锁已暂停");

        Assert.True(HelperProtocol.TryParseCommand(line, out var cmd));
        Assert.Equal("密码验证失败，自动解锁已暂停", Assert.IsType<HelperCommand.Notify>(cmd).Message);
    }

    [Fact]
    public void Notify_con_saltos_de_linea_sigue_siendo_una_sola_linea()
    {
        var line = HelperProtocol.BuildNotify("uno\r\ndos\ntres");

        Assert.Single(line.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.True(HelperProtocol.TryParseCommand(line, out var cmd));
        Assert.IsType<HelperCommand.Notify>(cmd);
    }

    [Fact]
    public void Los_numeros_usan_formato_invariante()
    {
        // En una maquina con coma decimal esto rompería si no se fijara la cultura
        var line = HelperProtocol.BuildPrompt(1, 12.5, 2.5);

        Assert.Contains("12.5", line);
        Assert.True(HelperProtocol.TryParseCommand(line, out _));
    }

    [Theory]
    [InlineData(PromptOutcome.Reject)]
    [InlineData(PromptOutcome.Snooze)]
    [InlineData(PromptOutcome.Timeout)]
    [InlineData(PromptOutcome.Dismissed)]
    public void Response_ida_y_vuelta(PromptOutcome outcome)
    {
        var line = HelperProtocol.BuildResponse(42, outcome);

        Assert.True(HelperProtocol.TryParseResponse(line, out var id, out var parsed));
        Assert.Equal(42, id);
        Assert.Equal(outcome, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PROMPT")]
    [InlineData("PROMPT a b c")]
    [InlineData("PROMPT 1 NaN 5")]
    [InlineData("PROMPT 1 -3 5")]
    [InlineData("QUE_ES_ESTO 1")]
    public void TryParseCommand_rechaza_basura(string line)
        => Assert.False(HelperProtocol.TryParseCommand(line, out _));

    [Theory]
    [InlineData("RESPONSE 1 QUIZA")]
    [InlineData("RESPONSE 1 99")]
    [InlineData("RESPONSE x REJECT")]
    [InlineData("HELLO 1")]
    public void TryParseResponse_rechaza_basura(string line)
        => Assert.False(HelperProtocol.TryParseResponse(line, out _, out _));

    [Fact]
    public void Hello_y_Locked_terminan_en_salto_de_linea()
    {
        Assert.EndsWith("\n", HelperProtocol.BuildHello(1));
        Assert.EndsWith("\n", HelperProtocol.BuildLocked());
    }
}
