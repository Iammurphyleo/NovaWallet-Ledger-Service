namespace NovaWallet.Domain.Entities;

public sealed class OutboxMessage
{
    private OutboxMessage() { } // EF Core constructor

    public static OutboxMessage Create(string type, string payload)
    {
        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = payload,
            CreatedAt = DateTime.UtcNow
        };
    }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; private set; }
}
