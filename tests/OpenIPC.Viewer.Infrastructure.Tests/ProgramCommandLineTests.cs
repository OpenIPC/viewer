using OpenIPC.Viewer.Core.Entities;
using OpenIPC.Viewer.Core.Events;
using OpenIPC.Viewer.Core.Notifications;
using OpenIPC.Viewer.Infrastructure.Notifications;

namespace OpenIPC.Viewer.Infrastructure.Tests;

// The argument rules for shells and interpreters picked as the program. Pure
// checks: nothing is started.
public sealed class ProgramCommandLineTests
{
    private static EventActionContext Ctx(string camera = "Gate & Yard") => new(
        new CameraEvent(EventId.New(), new CameraId(Guid.NewGuid()), EventKind.Motion, EventSeverity.Info,
            DateTime.UtcNow.AddSeconds(-2), null, "motion", null),
        camera, "Motion", DateTime.UtcNow);

    [Theory]
    [InlineData("powershell.exe", "-Command \"Write-Output {camera}\"")]
    [InlineData("pwsh", "-c \"Write-Output {camera}\"")]
    [InlineData("powershell.exe", "-NoProfile -EncodedCommand {camera}")]
    [InlineData("pwsh", "-CommandWithArgs \"Write-Output $args\" {camera}")]
    [InlineData("powershell.exe", "\"Write-Output {camera}\"")]             // bare arguments are a command
    [InlineData("powershell.exe", "-WorkingDirectory /f -c \"Write-Output {camera}\"")]
    [InlineData("/bin/sh", "-c \"echo {camera}\"")]
    [InlineData("/usr/bin/bash", "-ec \"echo {camera}\"")]
    [InlineData("/bin/sh", "{camera}")]                                     // the script itself
    [InlineData("cmd.exe", "/c \"notify.bat {camera}\"")]
    [InlineData("cmd.exe", "/c {camera}")]
    [InlineData("cmd.exe", "{camera}")]
    public void Interpreter_PlaceholderInCode_IsRefused(string program, string arguments)
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            ProgramCommandLine.RenderArguments(program, arguments, Ctx(), windows: true));
        Assert.Contains("OPENIPC_", ex.Message);
    }

    [Theory]
    [InlineData("powershell.exe", "-NoProfile -File notify.ps1 {camera}", "Gate & Yard")]
    [InlineData("pwsh", "-ExecutionPolicy Bypass -f notify.ps1 {camera}", "Gate & Yard")]
    [InlineData("/bin/sh", "notify.sh {camera}", "Gate & Yard")]
    [InlineData("/bin/bash", "-e notify.sh {camera}", "Gate & Yard")]
    [InlineData("notify.exe", "{camera}", "Gate & Yard")]
    [InlineData("notify.ps1", "{camera}", "Gate & Yard")]
    public void ScriptArguments_KeepTheValueAsData(string program, string arguments, string expected)
    {
        var args = ProgramCommandLine.RenderArguments(program, arguments, Ctx(), windows: true);
        Assert.Equal(expected, args[^1]);
    }

    [Fact]
    public void Cmd_StripsMetacharacters_LikeABatchFile()
    {
        var args = ProgramCommandLine.RenderArguments(
            "C:/Windows/System32/cmd.exe", "/d /c notify.bat {camera}", Ctx("Gate \"&|<>^%!"), windows: true);

        Assert.Equal(new[] { "/d", "/c", "notify.bat", "Gate " }, args);
        Assert.Equal("Gate ", ProgramCommandLine.CleanerFor("cmd.exe", windows: true)("Gate \"&|<>^%!"));
    }

    [Fact]
    public void Cmd_OnlyCountsOnWindows()
    {
        // Off Windows a file called "cmd" is just some program.
        var args = ProgramCommandLine.RenderArguments("/opt/tools/cmd", "{camera}", Ctx(), windows: false);
        Assert.Equal(new[] { "Gate & Yard" }, args);
    }

    [Fact]
    public void Interpreter_WithoutPlaceholders_IsLeftAlone()
    {
        var args = ProgramCommandLine.RenderArguments(
            "pwsh", "-NoProfile -Command \"Get-Date\"", Ctx(), windows: false);
        Assert.Equal(new[] { "-NoProfile", "-Command", "Get-Date" }, args);
    }

    [Fact]
    public void PowerShell_DropsQuotesFromEventData()
    {
        var args = ProgramCommandLine.RenderArguments(
            "powershell.exe", "-File notify.ps1 {camera}", Ctx("Gate \"North\""), windows: true);
        Assert.Equal("Gate North", args[^1]);
    }
}
