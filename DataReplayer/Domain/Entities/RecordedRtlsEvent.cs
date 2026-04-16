using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataReplayer.Domain.Entities;

[Table("RecordedRtlsEvents")]
public class RecordedRtlsEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(100)]
    public string UwbMacAddress { get; set; } = default!;

    [Required]
    [Column(TypeName = "jsonb")]
    public string RawPayload { get; set; } = default!;
}
