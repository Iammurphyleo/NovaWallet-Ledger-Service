using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Domain.Entities;

namespace NovaWallet.Infrastructure.Data.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();

        // No FK to wallets — intentional. Audit logs are immutable and must outlive wallet records.
        builder.Property(a => a.WalletId).HasColumnName("wallet_id").IsRequired();
        builder.Property(a => a.EventType).HasColumnName("event_type").HasConversion<string>().IsRequired();
        builder.Property(a => a.AmountKobo).HasColumnName("amount_kobo");
        builder.Property(a => a.BalanceBeforeKobo).HasColumnName("balance_before_kobo").IsRequired();
        builder.Property(a => a.BalanceAfterKobo).HasColumnName("balance_after_kobo").IsRequired();
        builder.Property(a => a.ActorSubject).HasColumnName("actor_subject").HasMaxLength(200);
        builder.Property(a => a.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        builder.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(a => new { a.WalletId, a.CreatedAt }).HasDatabaseName("ix_audit_logs_wallet_created");
    }
}
