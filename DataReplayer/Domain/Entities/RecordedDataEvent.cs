using System.Collections.Generic;

namespace DataReplayer.Domain.Entities;

public class RecordedDataEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string Endpoint { get; set; } = string.Empty;
    public string? TrackerId { get; set; }
    public string Payload { get; set; } = string.Empty;
}
