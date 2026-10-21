using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace CafeDemo
{
    /// <summary>One development Play session, written one complete JSON object at a time.</summary>
    public sealed class RuntimeTraceJsonlWriter : IDisposable
    {
        private readonly StreamWriter stream;
        private long sequence;
        private readonly bool technicalOnly;
        private readonly long maximumBytes;
        private long writtenBytes;

        public RuntimeTraceJsonlWriter(string path, string sessionId, string scene, bool technicalOnly = false, long maximumBytes = long.MaxValue)
        {
            this.technicalOnly = technicalOnly; this.maximumBytes = maximumBytes;
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Trace path and session ID are required.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            stream = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            { AutoFlush = true };
            Path = System.IO.Path.GetFullPath(path);
            SessionId = sessionId;
            Scene = scene ?? string.Empty;
        }

        public string Path { get; }
        public string SessionId { get; }
        public string Scene { get; }
        public long Sequence => sequence;
        public bool IsOpen { get; private set; } = true;
        public string LastError { get; private set; } = string.Empty;
        public string LastWrittenLine { get; private set; } = string.Empty;

        public bool TryWrite(string category, string kind, string entityId, string traceId, string requestId,
            string runId, string eventId, string proposalId, object data, DateTime utc, double realtime)
        {
            if (!IsOpen) return false;
            try
            {
                var line = new Dictionary<string, object>
                {
                    ["schema_version"] = "runtime-trace/1.0",
                    ["session_id"] = SessionId,
                    ["seq"] = sequence + 1,
                    ["utc"] = utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["realtime"] = realtime,
                    ["scene"] = Scene,
                    ["category"] = category,
                    ["kind"] = kind,
                    ["entity_id"] = entityId,
                    ["trace_id"] = traceId,
                    ["request_id"] = requestId,
                    ["run_id"] = runId,
                    ["event_id"] = eventId,
                    ["proposal_id"] = proposalId,
                    ["data"] = technicalOnly ? EmbodiedNPC.Settings.DiagnosticsExport.ProjectLine(JsonConvert.SerializeObject(data)) ?? new Newtonsoft.Json.Linq.JObject() : data ?? new { },
                };
                string serialized = JsonConvert.SerializeObject(line, Formatting.None);
                int bytes = System.Text.Encoding.UTF8.GetByteCount(serialized) + 2;
                if (bytes > maximumBytes - writtenBytes) { LastError = "TRACE_SIZE_LIMIT"; Dispose(); return false; }
                stream.WriteLine(serialized);
                stream.Flush();
                writtenBytes += bytes;
                sequence++;
                LastWrittenLine = serialized;
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ObjectDisposedException)
            {
                LastError = error.GetType().Name;
                Dispose();
                return false;
            }
        }

        public void Dispose()
        {
            if (!IsOpen) return;
            IsOpen = false;
            try { stream.Dispose(); }
            catch (IOException error) { LastError = error.GetType().Name; }
        }
    }
}
