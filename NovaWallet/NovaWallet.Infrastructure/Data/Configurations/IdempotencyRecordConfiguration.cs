using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Domain.Entities;

namespace NovaWallet.Infrastructure.Data.Configurations;

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");

        builder.HasKey(r => r.Key);
        builder.Property(r => r.Key).HasColumnName("key").HasMaxLength(255).IsRequired();
        builder.Property(r => r.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsRequired();
        builder.Property(r => r.WalletId).HasColumnName("wallet_id").IsRequired();
        builder.Property(r => r.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        builder.Property(r => r.ResponseStatus).HasColumnName("response_status").IsRequired();
        builder.Property(r => r.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.ExpiresAt).HasColumnName("expires_at").IsRequired();
    }
}
