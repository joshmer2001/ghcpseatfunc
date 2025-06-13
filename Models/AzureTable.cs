using Azure;
using Azure.Data.Tables;

namespace ghcpfunc.Models
{
    public class UserEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = default!;
        public string RowKey { get; set; } = default!;
        public DateTime LastActivity { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
    }
}