using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace BruteForce
{
    // Code updates by Ayman Elbanhawy (c) Softwaremile.com
    // This helper keeps all Hashcat process integration in one place so the
    // GUI and CLI can share the same executable lookup and cracking flow.
    public class HashcatRunResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }
        public string ShowOutput { get; set; }
        public string HashFilePath { get; set; }
        public string OutputFilePath { get; set; }
        public int HashcatMode { get; set; }
    }

    public static class HashcatRunner
    {
        // Prefer an explicit path first, then the installer-bundled Hashcat
        // folder, and finally a PATH lookup. Returning a full path lets the
        // process runner set Hashcat's working directory correctly so it can
        // find its sibling OpenCL/modules/rules folders.
        public static string ResolveHashcatPath(string hashcatPath = null)
        {
            if (!string.IsNullOrWhiteSpace(hashcatPath))
            {
                if (File.Exists(hashcatPath))
                {
                    return Path.GetFullPath(hashcatPath);
                }

                if (!hashcatPath.Contains(Path.DirectorySeparatorChar.ToString()) &&
                    !hashcatPath.Contains(Path.AltDirectorySeparatorChar.ToString()))
                {
                    return FindExecutableOnPath(hashcatPath) ?? hashcatPath;
                }

                return hashcatPath;
            }

            var bundledHashcatPath = Path.Combine(AppContext.BaseDirectory, "Hashcat", "hashcat.exe");

            if (File.Exists(bundledHashcatPath))
            {
                return bundledHashcatPath;
            }

            return FindExecutableOnPath("hashcat.exe") ?? "hashcat";
        }

        // Map extracted BruteShark hash models to the Hashcat mode numbers that
        // are needed for export and cracking.
        public static int GetHashcatMode(Hash hash)
        {
            if (hash is HttpDigestHash)
            {
                return 11400;
            }
            if (hash is CramMd5Hash)
            {
                return 16400;
            }
            if (hash is NtlmHash ntlmHash)
            {
                if (ntlmHash.NtHash.Length == 24)
                {
                    return 5500;
                }
                if (ntlmHash.NtHash.Length > 24)
                {
                    return 5600;
                }

                throw new NotSupportedHashcatHash("NTLM hash has nt part shorter than 24 chars");
            }
            if (hash is KerberosHash)
            {
                return 7500;
            }
            if (hash is KerberosAsRepHash asRepHash)
            {
                if (asRepHash.Etype == 23)
                {
                    return 18200;
                }

                throw new NotSupportedHashcatHash($"Kerberos AS-REP Etype {asRepHash.Etype} is not supported by Hashcat");
            }
            if (hash is KerberosTgsRepHash tgsRepHash)
            {
                if (tgsRepHash.Etype == 23)
                {
                    return 13100;
                }
                if (tgsRepHash.Etype == 17)
                {
                    return 19600;
                }
                if (tgsRepHash.Etype == 18)
                {
                    return 19700;
                }

                throw new NotSupportedHashcatHash($"Kerberos TGS-REP Etype {tgsRepHash.Etype} is not supported by Hashcat");
            }

            throw new NotSupportedHashcatHash("Hash type not supported");
        }

        public static HashcatRunResult CrackHashFile(
            string hashcatPath,
            int hashcatMode,
            string hashFilePath,
            string wordlistPath,
            string outputFilePath,
            string extraArguments = null)
        {
            if (!File.Exists(hashFilePath))
            {
                throw new FileNotFoundException("Hashcat hash file does not exist", hashFilePath);
            }
            if (!File.Exists(wordlistPath))
            {
                throw new FileNotFoundException("Hashcat wordlist file does not exist", wordlistPath);
            }

            var hashcatExecutable = ResolveHashcatPath(hashcatPath);
            // Build one command for the cracking run and a second --show pass so
            // the caller can display the recovered credentials immediately.
            var arguments = $"-m {hashcatMode} {Quote(hashFilePath)} {Quote(wordlistPath)} --outfile {Quote(outputFilePath)}";

            if (!string.IsNullOrWhiteSpace(extraArguments))
            {
                arguments += " " + extraArguments;
            }

            var crackResult = RunProcess(hashcatExecutable, arguments);
            var showResult = RunProcess(hashcatExecutable, $"-m {hashcatMode} --show {Quote(hashFilePath)}");

            return new HashcatRunResult
            {
                ExitCode = crackResult.exitCode,
                StandardOutput = crackResult.output,
                StandardError = crackResult.error,
                ShowOutput = showResult.output,
                HashFilePath = hashFilePath,
                OutputFilePath = outputFilePath,
                HashcatMode = hashcatMode
            };
        }

        private static (int exitCode, string output, string error) RunProcess(string fileName, string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (File.Exists(fileName))
            {
                // Hashcat expects to run from its own directory so relative
                // runtime assets like OpenCL and modules resolve correctly.
                startInfo.WorkingDirectory = Path.GetDirectoryName(fileName);
            }

            using (var process = new Process())
            {
                var output = new StringBuilder();
                var error = new StringBuilder();

                process.StartInfo = startInfo;
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        output.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        error.AppendLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                return (process.ExitCode, output.ToString(), error.ToString());
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string FindExecutableOnPath(string executableName)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var fileNames = Path.HasExtension(executableName)
                ? new[] { executableName }
                : new[] { executableName + ".exe", executableName };

            foreach (var directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                foreach (var fileName in fileNames)
                {
                    var candidate = Path.Combine(directory.Trim(), fileName);

                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }

            return null;
        }
    }
}
