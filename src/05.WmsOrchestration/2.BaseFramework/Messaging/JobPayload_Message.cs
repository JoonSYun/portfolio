//using MassTransit;
//using Portfolio.WmsOrchestration.Exceptions;
//using System.Reflection;

namespace Portfolio.WmsOrchestration.Model
{
    public class JobPayload_Message<T>
    {
        public Guid JobId { get; set; }
        public T Payload { get; set; }
    }
}