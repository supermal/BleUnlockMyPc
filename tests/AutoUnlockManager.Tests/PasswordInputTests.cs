using AutoUnlockManager.Core.Config;
using Xunit;

namespace AutoUnlockManager.Tests;

public sealed class PasswordInputTests
{
    [Fact]
    public void Load_Stored_KeepsItOutOfTheField()
    {
        var input = new PasswordInput();

        input.Load("secret");

        Assert.Equal("", input.Typed);
        Assert.True(input.HasStored);
        Assert.Equal("secret", input.Effective);
        Assert.False(input.CanReveal);
    }

    [Fact]
    public void Edit_NewPassword_ReplacesStoredAndCanReveal()
    {
        var input = new PasswordInput();
        input.Load("secret");

        input.Edit("new");

        Assert.Equal("new", input.Effective);
        Assert.True(input.CanReveal);
    }

    [Fact]
    public void Edit_BackToEmpty_KeepsStored()
    {
        var input = new PasswordInput();
        input.Load("secret");
        input.Edit("new");

        input.Edit("");

        Assert.Equal("secret", input.Effective);
        Assert.False(input.CanReveal);
    }

    [Fact]
    public void Load_Empty_HasNoStored()
    {
        var input = new PasswordInput();

        input.Load("");

        Assert.False(input.HasStored);
        Assert.Equal("", input.Effective);
    }

    [Fact]
    public void Load_AfterEditing_ClearsTyped()
    {
        var input = new PasswordInput();
        input.Load("");
        input.Edit("new");

        input.Load("new");

        Assert.Equal("", input.Typed);
        Assert.False(input.CanReveal);
        Assert.Equal("new", input.Effective);
    }
}
