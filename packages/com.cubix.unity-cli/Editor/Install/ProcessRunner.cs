using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace CubicEngine.UnityCli
{
    internal sealed class ProcessCommand
    {
        public string FileName { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
    }

    internal sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string StdOut { get; set; }
        public string StdErr { get; set; }
        public string CommandLine { get; set; }

        public bool Success => ExitCode == 0;
    }

    internal static class ProcessRunner
    {
        public static ProcessResult Run(ProcessCommand command, int timeoutMs = 120000)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments ?? string.Empty,
                WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? Environment.CurrentDirectory : command.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                try
                {
                    process.Start();
                }
                catch (Exception exception)
                {
                    return new ProcessResult
                    {
                        ExitCode = -1,
                        StdErr = exception.Message,
                        CommandLine = command.FileName + " " + command.Arguments
                    };
                }

                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMs))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    Task.WaitAll(new Task[] { stdOutTask, stdErrTask }, 5000);
                    return new ProcessResult
                    {
                        ExitCode = -2,
                        StdOut = stdOutTask.IsCompleted ? stdOutTask.Result : string.Empty,
                        StdErr = (stdErrTask.IsCompleted ? stdErrTask.Result : string.Empty) + Environment.NewLine + "Process timed out.",
                        CommandLine = command.FileName + " " + command.Arguments
                    };
                }

                Task.WaitAll(stdOutTask, stdErrTask);
                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = stdOutTask.Result,
                    StdErr = stdErrTask.Result,
                    CommandLine = command.FileName + " " + command.Arguments
                };
            }
        }
    }
}
