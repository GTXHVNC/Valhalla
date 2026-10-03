using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Valhalla
{
    internal sealed class LoadedPlugin
    {
        public string Name { get; internal set; }
        public string Version { get; internal set; }
        public string ServerPath { get; internal set; }
        public string ClientPath { get; internal set; }
        public string TypeName { get; internal set; }
        public AppDomain Domain { get; internal set; }
        public IValhallaPluginServer Server { get; internal set; }
        public bool Running { get; internal set; }
    }

    internal sealed class ValhallaPluginManager
    {
        private static readonly Regex ServerNameRegex = new Regex("^(?<name>[A-Za-z0-9]+)\\.server\\.dll$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex ClientNameRegex = new Regex("^(?<name>[A-Za-z0-9]+)\\.client\\.dll$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private readonly object sync = new object();
        private readonly Dictionary<string, LoadedPlugin> plugins = new Dictionary<string, LoadedPlugin>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<ValhallaPluginConnection[]> connectionSnapshot;
        private readonly Func<string, string, string, bool> sendText;
        private readonly Func<string, string, byte[], bool> sendBytes;
        private readonly Func<string, string, bool> unloadClient;
        private readonly Func<string, string, string, string, bool> sendFile;
        private readonly Func<string, string, string, long, string, bool> beginReceive;
        private readonly Func<string, string, bool> resumeFile;
        private readonly Func<string, string, bool> sendEvent;
        private readonly Func<string, string, bool> completeReceive;
        private readonly Action<string> log;

        public ValhallaPluginManager(
            Func<ValhallaPluginConnection[]> connectionSnapshot,
            Func<string, string, string, bool> sendText,
            Func<string, string, byte[], bool> sendBytes,
            Func<string, string, bool> unloadClient,
            Func<string, string, string, string, bool> sendFile,
            Func<string, string, bool> resumeFile,
            Func<string, string, bool> sendEvent,
            Func<string, string, string, long, string, bool> beginReceive,
            Func<string, string, bool> completeReceive,
            Action<string> log)
        {
            this.connectionSnapshot = connectionSnapshot;
            this.sendText = sendText;
            this.sendBytes = sendBytes;
            this.unloadClient = unloadClient;
            this.sendFile = sendFile;
            this.beginReceive = beginReceive;
            this.resumeFile = resumeFile;
            this.sendEvent = sendEvent;
            this.completeReceive = completeReceive;
            this.log = log;
        }

        public IReadOnlyList<LoadedPlugin> Snapshot()
        {
            lock (sync) return new ReadOnlyCollection<LoadedPlugin>(plugins.Values.ToList());
        }

        public bool TryValidateServerPath(string serverPath, out string pluginName, out string clientPath, out string error)
        {
            pluginName = null; clientPath = null; error = null;
            if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath)) { error = "Plugin server file does not exist."; return false; }
            Match match = ServerNameRegex.Match(Path.GetFileName(serverPath));
            if (!match.Success) { error = "Server DLL must use pluginname.server.dll with letters/numbers only in pluginname."; return false; }
            string validatedPluginName = match.Groups["name"].Value;
            pluginName = validatedPluginName;
            string directory = Path.GetDirectoryName(serverPath) ?? string.Empty;
            clientPath = Directory.GetFiles(directory, "*.client.dll", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(p => {
                    Match m = ClientNameRegex.Match(Path.GetFileName(p));
                    return m.Success && string.Equals(m.Groups["name"].Value, validatedPluginName, StringComparison.OrdinalIgnoreCase);
                });
            if (clientPath == null) { error = "Matching pluginname.client.dll was not found in the same directory."; return false; }
            try
            {
                AssemblyName.GetAssemblyName(serverPath);
                using (FileStream fs = File.OpenRead(clientPath))
                {
                    if (fs.Length < 2 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') { error = "Matching client DLL is not a valid Windows PE image."; return false; }
                }
            }
            catch (Exception ex) { error = "Plugin validation failed: " + ex.Message; return false; }
            return true;
        }

        public bool TryLoadServer(string serverPath, out LoadedPlugin loaded, out string error)
        {
            loaded = null; error = null;
            string name, clientPath;
            if (!TryValidateServerPath(serverPath, out name, out clientPath, out error)) return false;
            lock (sync) if (plugins.ContainsKey(name)) { error = "Plugin is already loaded."; return false; }
            try
            {
                string typeName, version;
                AppDomain inspector = AppDomain.CreateDomain("ValhallaPluginInspect_" + Guid.NewGuid().ToString("N"));
                try
                {
                    PluginLoader loader = (PluginLoader)inspector.CreateInstanceFromAndUnwrap(typeof(PluginLoader).Assembly.Location, typeof(PluginLoader).FullName);
                    PluginDescriptor descriptor = loader.Describe(serverPath);
                    if (descriptor == null) throw new InvalidDataException("No public IValhallaPluginServer implementation with a parameterless constructor was found.");
                    typeName = descriptor.TypeName; version = descriptor.Version;
                }
                finally
                {
                    try { AppDomain.Unload(inspector); } catch { }
                }
                loaded = new LoadedPlugin { Name = name, Version = string.IsNullOrWhiteSpace(version) ? "1.0" : version, ServerPath = serverPath, ClientPath = clientPath, TypeName = typeName, Running = false };
                lock (sync) plugins.Add(name, loaded);
                return true;
            }
            catch (Exception ex) { error = "Plugin API validation failed: " + ex.GetBaseException().Message; return false; }
        }

        public bool Start(string name, out string error)
        {
            error = null;
            LoadedPlugin plugin;
            lock (sync) { if (!plugins.TryGetValue(name, out plugin)) { error = "Plugin is not loaded."; return false; } if (plugin.Running) return true; }
            AppDomain domain = null;
            try
            {
                domain = AppDomain.CreateDomain("ValhallaPlugin_" + name + "_" + Guid.NewGuid().ToString("N"), null, new AppDomainSetup { ApplicationBase = Path.GetDirectoryName(plugin.ServerPath) ?? AppDomain.CurrentDomain.BaseDirectory });
                PluginLoader loader = (PluginLoader)domain.CreateInstanceFromAndUnwrap(typeof(PluginLoader).Assembly.Location, typeof(PluginLoader).FullName);
                IValhallaPluginServer server = loader.Create(plugin.ServerPath, plugin.TypeName);
                server.Initialize(new PluginContext(this, name));
                lock (sync) { plugin.Domain = domain; plugin.Server = server; plugin.Running = true; }
                return true;
            }
            catch (Exception ex)
            {
                if (domain != null) try { AppDomain.Unload(domain); } catch { }
                error = "Plugin start failed: " + ex.GetBaseException().Message;
                return false;
            }
        }

        public bool Unload(string name)
        {
            LoadedPlugin plugin;
            lock (sync) if (!plugins.TryGetValue(name, out plugin)) return false;
            if (plugin.Running && plugin.Server != null)
            {
                try { plugin.Server.Shutdown(); } catch (Exception ex) { log("Plugin shutdown failed: " + ex.Message); }
            }
            if (plugin.Domain != null)
            {
                try { AppDomain.Unload(plugin.Domain); } catch (Exception ex) { log("Plugin AppDomain unload failed: " + ex.Message); }
            }
            lock (sync)
            {
                plugin.Domain = null;
                plugin.Server = null;
                plugin.Running = false;
                plugins.Remove(name);
            }
            return true;
        }

        public void StopAll() { foreach (LoadedPlugin p in Snapshot()) Unload(p.Name); }

        public void RouteAgentMessage(string connectionId, string eventName, byte[] payload)
        {
            foreach (LoadedPlugin p in Snapshot())
            {
                try { p.Server.OnAgentMessage(connectionId, eventName, payload ?? new byte[0]); }
                catch (Exception ex) { log("Plugin '" + p.Name + "' message handler failed: " + ex.Message); }
            }
        }

        internal string ServerClientPath(string pluginName)
        {
            LoadedPlugin p;
            lock (sync) return plugins.TryGetValue(pluginName, out p) ? p.ClientPath : null;
        }

        private sealed class PluginContext : MarshalByRefObject, IValhallaPluginContext
        {
            private readonly ValhallaPluginManager owner;
            private readonly string pluginName;
            public PluginContext(ValhallaPluginManager owner, string pluginName) { this.owner = owner; this.pluginName = pluginName; }
            public ValhallaPluginConnection[] GetConnections() => owner.connectionSnapshot();
            public bool LoadClient(string connectionId)
            {
                string path = owner.ServerClientPath(pluginName);
                return !string.IsNullOrWhiteSpace(path) && owner.sendFile(connectionId, pluginName, path, Path.GetFileName(path));
            }
            public bool UnloadClient(string connectionId) => owner.unloadClient(connectionId, pluginName);
            public bool SendText(string connectionId, string eventName, string text) => owner.sendBytes(connectionId, pluginName, PrefixEvent(eventName, System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty)));
            public bool SendBytes(string connectionId, string eventName, byte[] payload) => owner.sendBytes(connectionId, pluginName, PrefixEvent(eventName, payload));
            public bool SendFile(string connectionId, string localPath, string remoteName) => owner.sendFile(connectionId, pluginName, localPath, remoteName);
            public bool SendFileChunk(string connectionId, string transferId, long offset, byte[] chunk, long totalLength, string sha256)
            {
                if (string.IsNullOrWhiteSpace(transferId) || offset < 0 || totalLength <= 0 || string.IsNullOrWhiteSpace(sha256)) return false;
                string header = transferId + "|" + offset + "|" + totalLength + "|" + sha256 + "\n";
                byte[] prefix = System.Text.Encoding.UTF8.GetBytes(header);
                byte[] value = chunk ?? new byte[0];
                byte[] framed = new byte[prefix.Length + value.Length];
                Buffer.BlockCopy(prefix, 0, framed, 0, prefix.Length);
                Buffer.BlockCopy(value, 0, framed, prefix.Length, value.Length);
                return owner.sendBytes(connectionId, pluginName, PrefixEvent("file.send.chunk", framed));
            }
            public bool ResumeFileSend(string connectionId, string transferId) => owner.resumeFile(connectionId, transferId);
            public bool SendEvent(string connectionId, string eventName) => owner.sendEvent(connectionId, eventName);
            public string BeginFileReceive(string connectionId, string transferId, string fileName, long totalLength, string sha256) => owner.beginReceive(connectionId, transferId, fileName, totalLength, sha256) ? transferId : null;
            public bool CompleteFileReceive(string connectionId, string transferId) => owner.completeReceive(connectionId, transferId);
            public void Log(string message) => owner.log("[Plugin " + pluginName + "] " + message);
            public override object InitializeLifetimeService() { return null; }
        }

        private static byte[] PrefixEvent(string eventName, byte[] payload)
        {
            byte[] prefix = System.Text.Encoding.UTF8.GetBytes(eventName + "\n");
            byte[] value = payload ?? new byte[0];
            byte[] result = new byte[prefix.Length + value.Length];
            Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
            Buffer.BlockCopy(value, 0, result, prefix.Length, value.Length);
            return result;
        }
    }

    internal sealed class PluginDescriptor : MarshalByRefObject
    {
        public string TypeName { get; set; }
        public string Version { get; set; }
        public override object InitializeLifetimeService() { return null; }
    }

    internal sealed class PluginLoader : MarshalByRefObject
    {
        public PluginDescriptor Describe(string path)
        {
            Assembly asm = Assembly.ReflectionOnlyLoadFrom(path);
            Type t = asm.GetExportedTypes().FirstOrDefault(x => x.IsClass && !x.IsAbstract && x.GetConstructor(Type.EmptyTypes) != null && x.GetInterfaces().Any(i => string.Equals(i.FullName, typeof(IValhallaPluginServer).FullName, StringComparison.Ordinal)));
            if (t == null) return null;
            return new PluginDescriptor { TypeName = t.FullName, Version = t.Assembly.GetName().Version == null ? null : t.Assembly.GetName().Version.ToString() };
        }
        public IValhallaPluginServer Create(string path, string typeName)
        {
            Assembly asm = Assembly.LoadFrom(path);
            Type t = asm.GetType(typeName, true);
            if (!typeof(IValhallaPluginServer).IsAssignableFrom(t)) throw new InvalidDataException("Plugin type does not implement IValhallaPluginServer.");
            return (IValhallaPluginServer)Activator.CreateInstance(t);
        }
        public override object InitializeLifetimeService() { return null; }
    }
}
