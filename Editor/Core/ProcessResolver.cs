// Editor/Core/ProcessResolver.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ArcForge.Hades.Editor.Core
{
    public struct RunResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
        public bool Success => ExitCode == 0;
    }

    public static class ProcessResolver
    {
        static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();

        public static string FindExecutable(string name)
        {
            if (_cache.TryGetValue(name, out var cached))
                return cached;

            var path = Resolve(name);
            if (path != null)
                _cache[name] = path;

            return path;
        }

        public static RunResult Run(string executable, string arguments, string workingDirectory, int timeoutMs = 120000)
        {
            var execPath = FindExecutable(executable);
            if (execPath == null)
                return new RunResult { ExitCode = -1, Error = $"Could not find '{executable}' on this system" };

            var psi = new ProcessStartInfo
            {
                FileName = execPath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var proc = Process.Start(psi))
                {
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    var output = proc.StandardOutput.ReadToEnd();

                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                        return new RunResult
                        {
                            ExitCode = -1,
                            Output = output.Trim(),
                            Error = "Process timed out"
                        };
                    }

                    return new RunResult
                    {
                        ExitCode = proc.ExitCode,
                        Output = output.Trim(),
                        Error = stderrTask.Result.Trim()
                    };
                }
            }
            catch (Exception ex)
            {
                return new RunResult { ExitCode = -1, Error = ex.Message };
            }
        }

        static string Resolve(string name)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            string fileName;
            string arguments;

            if (isWindows)
            {
                fileName = "cmd.exe";
                arguments = $"/c where {name}";
            }
            else
            {
                fileName = "/bin/bash";
                arguments = $"-lc \"which {name}\"";
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    var output = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(5000);

                    if (proc.ExitCode != 0 || string.IsNullOrEmpty(output))
                        return null;

                    var firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];

                    if (File.Exists(firstLine))
                        return firstLine;
                }
            }
            catch
            {
                // Shell not available
            }

            return null;
        }
    }
}
