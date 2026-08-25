using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Blackbox.UnityTeamGit.Editor
{
    internal sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
        public bool TimedOut { get; set; }

        public bool Success
        {
            get { return !TimedOut && ExitCode == 0; }
        }

        public string CombinedOutput
        {
            get
            {
                return ((StandardOutput ?? string.Empty) + Environment.NewLine + (StandardError ?? string.Empty)).Trim();
            }
        }
    }

    internal static class UnityTeamGitProcess
    {
        public static Task<CommandResult> RunAsync(
            string executable,
            IEnumerable<string> arguments,
            string workingDirectory,
            int timeoutMilliseconds = 120000)
        {
            var argumentList = arguments == null ? new string[0] : arguments.ToArray();

            return Task.Run(async () =>
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = BuildArguments(argumentList),
                        WorkingDirectory = workingDirectory,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    process.StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                    process.StartInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "Never";

                    try
                    {
                        process.Start();
                    }
                    catch (Exception exception)
                    {
                        return new CommandResult
                        {
                            ExitCode = -1,
                            StandardError = exception.Message
                        };
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    var exited = process.WaitForExit(timeoutMilliseconds);

                    if (!exited)
                    {
                        try { process.Kill(); }
                        catch { }
                    }

                    var output = await outputTask;
                    var error = await errorTask;

                    return new CommandResult
                    {
                        ExitCode = exited ? process.ExitCode : -1,
                        StandardOutput = output,
                        StandardError = error,
                        TimedOut = !exited
                    };
                }
            });
        }

        public static string FindGitExecutable()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe")
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            return "git";
        }

        public static string FindSshExecutable()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var openSsh = Path.Combine(windowsDirectory, "System32", "OpenSSH", "ssh.exe");
                if (File.Exists(openSsh))
                    return openSsh;

                var gitSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "ssh.exe");
                if (File.Exists(gitSsh))
                    return gitSsh;
            }

            return "ssh";
        }

        public static string Describe(string executable, IEnumerable<string> arguments)
        {
            return Path.GetFileName(executable) + " " + BuildArguments(arguments ?? new string[0]);
        }

        private static string BuildArguments(IEnumerable<string> arguments)
        {
            return string.Join(" ", arguments.Select(QuoteArgument));
        }

        private static string QuoteArgument(string argument)
        {
            if (argument == null)
                return "\"\"";

            if (argument.Length > 0 && argument.All(c => !char.IsWhiteSpace(c) && c != '"'))
                return argument;

            var builder = new StringBuilder();
            builder.Append('"');
            var backslashes = 0;

            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(character);
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }
    }
}
