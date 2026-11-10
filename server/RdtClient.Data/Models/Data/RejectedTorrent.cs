using System.ComponentModel.DataAnnotations;

namespace RdtClient.Data.Models.Data;

public class RejectedTorrent
{
    [Key]
    public String Hash { get; set; } = null!;

    public String Reason { get; set; } = null!;

    public DateTimeOffset Rejected { get; set; }
}
