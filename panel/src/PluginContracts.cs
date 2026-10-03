using System;
using System.Collections.Generic;

namespace Valhalla
{
    public interface IValhallaPluginServer
    {
        string Name { get; }
        string Version { get; }
        void Initialize(IValhallaPluginContext context);
        void Shutdown();
        void OnAgentMessage(string connectionId, string eventName, byte[] payload);
    }

    public interface IValhallaPluginContext
    {
        ValhallaPluginConnection[] GetConnections();
        bool LoadClient(string connectionId);
        bool UnloadClient(string connectionId);
        bool SendText(string connectionId, string eventName, string text);
        bool SendBytes(string connectionId, string eventName, byte[] payload);
        bool SendFile(string connectionId, string localPath, string remoteName);
        bool SendFileChunk(string connectionId, string transferId, long offset, byte[] chunk, long totalLength, string sha256);
        bool ResumeFileSend(string connectionId, string transferId);
        bool SendEvent(string connectionId, string eventName);
        string BeginFileReceive(string connectionId, string transferId, string fileName, long totalLength, string sha256);
        bool CompleteFileReceive(string connectionId, string transferId);
        void Log(string message);
    }

    [Serializable]
    public sealed class ValhallaPluginConnection
    {
        public string ConnectionId { get; set; }
        public string UserName { get; set; }
        public string ComputerName { get; set; }
        public string Fingerprint { get; set; }
        public string Version { get; set; }
        public string IpAddress { get; set; }
        public override string ToString() => (UserName ?? "Unknown") + "@" + (ComputerName ?? "Unknown");
    }
}
