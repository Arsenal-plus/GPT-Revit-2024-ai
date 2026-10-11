using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Horizun.Revit.Core
{
    // Flush each boundary before executing the next step. A crash leaves evidence,
    // never permission to replay: the dispatcher's durable claim remains in doubt.
    public sealed class WorkflowJournal : IDisposable
    {
        private readonly FileStream stream;
        public string Path { get; }
        public WorkflowJournal(string directory, string workflowId)
        {
            if (string.IsNullOrWhiteSpace(workflowId)) throw new ArgumentException("workflowId is required.");
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, RequestFingerprint.Sha256Hex(workflowId) + ".jsonl");
            stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        public void Append(JObject record)
        {
            var copy = (JObject)record.DeepClone();
            copy["utc"] = DateTime.UtcNow.ToString("o");
            byte[] bytes = new UTF8Encoding(false).GetBytes(copy.ToString(Formatting.None) + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        public void Dispose() => stream.Dispose();
    }
}
