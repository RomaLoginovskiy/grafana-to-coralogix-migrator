using GrafanaToCx.Cli.Cli;

namespace GrafanaToCx.Core.Tests;

public class ArgumentParserBackupTests
{
    [Fact]
    public void Parse_Backup_DefaultsToMigrationSettingsAndNonInteractive()
    {
        var parsed = ArgumentParser.Parse(["backup"]);

        Assert.Equal(CommandKind.Backup, parsed.Command);
        Assert.Equal("migration-settings.json", parsed.Get("settings"));
        Assert.Null(parsed.Get("output"));
        Assert.Null(parsed.Get("region"));
        Assert.False(parsed.GetBool("interactive"));
    }

    [Theory]
    [InlineData("-s", "-o", "-r", "-I")]
    [InlineData("--settings", "--output", "--region", "--interactive")]
    public void Parse_Backup_ReadsShortAndLongFlags(string settings, string output, string region, string interactive)
    {
        var parsed = ArgumentParser.Parse([
            "backup",
            settings, "custom.json",
            output, "out/boards.zip",
            region, "eu2",
            interactive
        ]);

        Assert.Equal(CommandKind.Backup, parsed.Command);
        Assert.Equal("custom.json", parsed.Get("settings"));
        Assert.Equal("out/boards.zip", parsed.Get("output"));
        Assert.Equal("eu2", parsed.Get("region"));
        Assert.True(parsed.GetBool("interactive"));
    }

    [Fact]
    public void Parse_Backup_IsCaseInsensitive()
    {
        Assert.Equal(CommandKind.Backup, ArgumentParser.Parse(["BACKUP"]).Command);
    }

    [Fact]
    public void Parse_Backup_TrailingFlagWithoutValue_KeepsDefault()
    {
        var parsed = ArgumentParser.Parse(["backup", "--output"]);

        Assert.Equal(CommandKind.Backup, parsed.Command);
        Assert.Null(parsed.Get("output"));
    }

    [Fact]
    public void Parse_Backup_DoesNotDisturbOtherCommands()
    {
        Assert.Equal(CommandKind.Migrate, ArgumentParser.Parse(["migrate"]).Command);
        Assert.Equal(CommandKind.Import, ArgumentParser.Parse(["import", "./dir"]).Command);
        Assert.Equal(CommandKind.Interactive, ArgumentParser.Parse([]).Command);
    }
}
