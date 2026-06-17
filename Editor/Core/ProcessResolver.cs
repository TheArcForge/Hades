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

        // Environment for `npm install` of native addons (tree-sitter, better-sqlite3).
        // Node 25's bundled V8 headers use C++20 constructs (std::ranges, requires),
        // but the bindings compile at the default C++17, so the build fails with
        // "no member named 'ranges' in namespace 'std'" unless we force the standard.
        // CXX only — setting CFLAGS breaks tree-sitter's C sources.
        public static readonly IReadOnlyDictionary<string, string> NativeBuildEnv =
            new Dictionary<string, string> { ["CXXFLAGS"] = "-std=c++20" };

        public static string FindExecutable(string name)
        {
            if (_cache.TryGetValue(name, out var cached))
                return cached;

            var path = Resolve(name);
            if (path != null)
                _cache[name] = path;

            return path;
        }

        public static RunResult Run(string executable, string arguments, string workingDirectory, int timeoutMs = 120000, IReadOnlyDictionary<string, string> environment = null)
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

            // Propagate a robust PATH to the child process. When Unity is launched
            // from Finder/Dock (the normal case on macOS), its environment has a
            // minimal PATH that excludes /opt/homebrew/bin, nvm, etc. Without this,
            // tools that re-resolve a binary by name — e.g. npm's `#!/usr/bin/env node`
            // shebang — fail with "env: node: No such file or directory" even though
            // we resolved npm's absolute path. Inject the resolved tool's dir and
            // node's dir (plus common install locations) ahead of the inherited PATH.
            ApplyChildPath(psi, execPath);

            if (environment != null)
            {
                foreach (var kv in environment)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }

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

        /// <summary>
        /// Spawns a process WITHOUT waiting for it. Returns the <see cref="Process"/> so the
        /// caller can poll <c>HasExited</c> (e.g. on <c>EditorApplication.update</c>) instead of
        /// blocking the main thread in <c>WaitForExit</c>. stdout/stderr are redirected — the
        /// caller MUST drain them (start <c>ReadToEndAsync</c> right away) to avoid a full-pipe
        /// deadlock, and Dispose the Process after it exits. Mirrors <see cref="Run"/>'s child
        /// PATH injection so a Finder-launched Unity can still resolve node/npm.
        /// </summary>
        public static Process Start(
            string executable, string arguments, string workingDirectory,
            IReadOnlyDictionary<string, string> environment = null)
        {
            var execPath = FindExecutable(executable) ?? executable;
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

            ApplyChildPath(psi, execPath);

            if (environment != null)
            {
                foreach (var kv in environment)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }

            var proc = new Process { StartInfo = psi };
            proc.Start();
            return proc;
        }

        static string Resolve(string name)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            if (isWindows)
                return ResolveViaShell("cmd.exe", $"/c where {name}");

            // Resolve through the user's actual login shell. Homebrew/nvm/Volta PATH
            // entries usually live in shell rc/profile files, which only a login
            // shell sees. Prefer $SHELL (the user's real shell — often zsh on macOS)
            // and fall back to /bin/bash. Only POSIX-compatible shells accept the
            // `-lc "which …"` form, so guard against fish and friends.
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell) || !File.Exists(shell) || !IsPosixShell(shell))
                shell = "/bin/bash";

            var viaShell = ResolveViaShell(shell, $"-lc \"which {name}\"");
            if (viaShell != null)
                return viaShell;

            // Fall back to probing common install locations directly. A login shell
            // can still miss the binary (e.g. nvm only mutates PATH in interactive,
            // non-login shells), so this is the safety net under a GUI-minimal env.
            foreach (var dir in CommonBinDirs())
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        static bool IsPosixShell(string shellPath)
        {
            var n = Path.GetFileName(shellPath);
            return n == "bash" || n == "zsh" || n == "sh" || n == "dash" || n == "ksh";
        }

        static string ResolveViaShell(string fileName, string arguments)
        {
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

        // Builds the child process PATH: resolved tool dir + node dir + common
        // install locations, ahead of whatever PATH the parent (Unity) inherited.
        static void ApplyChildPath(ProcessStartInfo psi, string execPath)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            var parts = new List<string>();

            void AddDir(string dir)
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !parts.Contains(dir))
                    parts.Add(dir);
            }

            // Directory of the resolved executable (e.g. npm).
            AddDir(Path.GetDirectoryName(execPath));

            // Directory of node, so children that invoke `node` by name resolve it.
            var nodePath = FindExecutable("node");
            if (nodePath != null)
                AddDir(Path.GetDirectoryName(nodePath));

            // Common install locations (covers macOS/Linux GUI-minimal PATH).
            if (!isWindows)
            {
                foreach (var dir in CommonBinDirs())
                    AddDir(dir);
            }

            var inherited = Environment.GetEnvironmentVariable("PATH") ?? "";
            var sep = Path.PathSeparator.ToString();

            psi.EnvironmentVariables["PATH"] = parts.Count > 0
                ? string.Join(sep, parts) + sep + inherited
                : inherited;
        }

        // Common Node/tool install directories on macOS and Linux, in priority order.
        static IEnumerable<string> CommonBinDirs()
        {
            yield return "/opt/homebrew/bin"; // Apple Silicon Homebrew
            yield return "/usr/local/bin";    // Intel Homebrew / common manual installs
            yield return "/usr/bin";
            yield return "/bin";

            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
                yield break;

            // nvm: ~/.nvm/versions/node/<version>/bin
            var nvmRoot = Path.Combine(home, ".nvm", "versions", "node");
            if (Directory.Exists(nvmRoot))
            {
                string[] versions = null;
                try { versions = Directory.GetDirectories(nvmRoot); } catch { }
                if (versions != null)
                {
                    Array.Sort(versions, StringComparer.Ordinal);
                    // Newest version first.
                    for (int i = versions.Length - 1; i >= 0; i--)
                        yield return Path.Combine(versions[i], "bin");
                }
            }

            yield return Path.Combine(home, ".volta", "bin"); // Volta
            yield return Path.Combine(home, ".fnm");          // fnm (default install)
            yield return Path.Combine(home, ".local", "bin"); // pipx-style / misc
        }
    }
}
