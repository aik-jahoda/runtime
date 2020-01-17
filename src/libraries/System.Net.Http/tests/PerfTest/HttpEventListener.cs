using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Text;

namespace HttpStress
{
    /// <summary>EventListener that dumps HTTP events out to either the console or a stream writer.</summary>
    internal sealed class HttpEventListener : EventListener
    {
        private readonly StreamWriter? _writer;

        public HttpEventListener(StreamWriter? writer = null) => _writer = writer;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-System-Net-Http")
                EnableEvents(eventSource, EventLevel.LogAlways);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            lock (Console.Out)
            {
                if (_writer != null)
                {
                    var sb = new StringBuilder().Append($"[{eventData.EventName}] ");
                    for (int i = 0; i < eventData.Payload?.Count; i++)
                    {
                        if (i > 0)
                            sb.Append(", ");
                        sb.Append(eventData.PayloadNames?[i]).Append(": ").Append(eventData.Payload[i]);
                    }
                    _writer.WriteLine(sb);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write($"[{eventData.EventName}] ");
                    Console.ResetColor();
                    for (int i = 0; i < eventData.Payload?.Count; i++)
                    {
                        if (i > 0)
                            Console.Write(", ");
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write(eventData.PayloadNames?[i] + ": ");
                        Console.ResetColor();
                        Console.Write(eventData.Payload[i]);
                    }
                    Console.WriteLine();
                }
            }
        }
    }
}