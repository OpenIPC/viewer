using System;
using System.Collections.Generic;
using System.IO;
using OpenIPC.Viewer.Core.Notifications;

namespace OpenIPC.Viewer.Infrastructure.Notifications;

// How event data may reach the command line of the "run a program" action.
// ArgumentList keeps every value a single argument, but a shell or interpreter
// chosen as the program parses its arguments again: cmd.exe re-reads the line
// after /c, and PowerShell -Command and sh -c run their argument as code. A
// camera's name may come from the device itself, so it is untrusted input:
//   - cmd.exe (like .bat/.cmd) gets its metacharacters stripped from event data;
//   - for cmd, PowerShell and POSIX shells, placeholders are only accepted as
//     arguments of the script being run, never in the code or the script path.
// Any other interpreter is the user's own choice; the settings hint tells them
// to read event data from the OPENIPC_* environment variables instead.
public static class ProgramCommandLine
{
    private enum Interpreter { None, Cmd, PowerShell, PosixShell }

    private static readonly HashSet<string> PosixShells = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "dash", "zsh", "ksh", "mksh", "ash", "fish",
    };

    // How one event value is cleaned before it goes into an argument or an
    // OPENIPC_* variable of this program.
    public static Func<string, string> CleanerFor(string path, bool windows)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var interpreter = Detect(path, windows);
        if (windows && ext is ".bat" or ".cmd" || interpreter == Interpreter.Cmd)
            return EventActionTemplate.StripCmdMetacharacters;
        // Windows PowerShell's own command-line parser mishandles embedded quotes.
        if (ext == ".ps1" || interpreter == Interpreter.PowerShell)
            return v => EventActionTemplate.StripControlChars(v).Replace("\"", "");
        return EventActionTemplate.StripControlChars;
    }

    // Splits the user's argument template and renders each argument on its own.
    // Throws NotSupportedException when a placeholder sits where the program
    // would run it as code.
    public static IReadOnlyList<string> RenderArguments(
        string path, string? template, EventActionContext context, bool windows)
    {
        var clean = CleanerFor(path, windows);
        var args = EventActionTemplate.SplitArguments(template);
        var firstData = FirstDataArgument(Detect(path, windows), args);

        var rendered = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            if (i < firstData && EventActionTemplate.HasPlaceholders(args[i]))
                throw new NotSupportedException(
                    $"{Path.GetFileName(path)} would run the event placeholders in its arguments as code. " +
                    "Pass them to a script instead (e.g. -File script.ps1 {camera}), " +
                    "or read the event from the OPENIPC_* environment variables.");
            rendered.Add(EventActionTemplate.Render(args[i], context, clean));
        }
        return rendered;
    }

    private static Interpreter Detect(string path, bool windows)
    {
        // A script picked directly (.ps1, .sh…) is run as a script, not as an interpreter.
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ("" or ".exe")) return Interpreter.None;

        var name = Path.GetFileNameWithoutExtension(path);
        if (windows && name.Equals("cmd", StringComparison.OrdinalIgnoreCase)) return Interpreter.Cmd;
        if (name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)) return Interpreter.PowerShell;
        return PosixShells.Contains(name) ? Interpreter.PosixShell : Interpreter.None;
    }

    // Index of the first argument that the program passes on as plain data;
    // int.MaxValue when there is none.
    private static int FirstDataArgument(Interpreter interpreter, IReadOnlyList<string> args)
    {
        switch (interpreter)
        {
            case Interpreter.Cmd:
                // cmd /c <command> <args…> (/k and /r work alike; "/cnotify.bat" is accepted too).
                for (var i = 0; i < args.Count; i++)
                {
                    var a = args[i];
                    if (a.Length >= 2 && a[0] == '/' && char.ToLowerInvariant(a[1]) is 'c' or 'k' or 'r')
                        return a.Length == 2 ? i + 2 : i + 1;
                }
                return int.MaxValue;

            case Interpreter.PowerShell:
                // Only -File <script> <args…> passes values as data. Windows PowerShell
                // treats bare arguments as a command, and -Command / -EncodedCommand /
                // -CommandWithArgs run code; switch names may be abbreviated. Only a
                // "-" spelling counts as -File, so a "/f…" path value can't pass for it.
                for (var i = 0; i < args.Count; i++)
                {
                    if (!IsSwitch(args[i], out var name)) continue;
                    if (args[i][0] == '-' && IsPrefixOf(name, "file")) return i + 2;
                    if (IsPrefixOf(name, "command") || IsPrefixOf(name, "encodedcommand") ||
                        name is "ec" or "cwa" || name.StartsWith("commandw", StringComparison.Ordinal))
                        return int.MaxValue;
                }
                return int.MaxValue;

            case Interpreter.PosixShell:
                // sh [options] script <args…>; any -c (also in a cluster like -ec) means code.
                foreach (var a in args)
                    if (a.Length >= 2 && a[0] == '-' && a[1] != '-' && a.IndexOf('c', 1) >= 0)
                        return int.MaxValue;
                for (var i = 0; i < args.Count; i++)
                    if (args[i].Length == 0 || args[i][0] is not ('-' or '+'))
                        return i + 1;
                return int.MaxValue;

            default:
                return 0;
        }
    }

    private static bool IsSwitch(string arg, out string name)
    {
        name = arg.Length >= 2 && arg[0] is '-' or '/' ? arg.Substring(1).ToLowerInvariant() : "";
        return name.Length > 0;
    }

    private static bool IsPrefixOf(string name, string full) =>
        full.StartsWith(name, StringComparison.Ordinal);
}
