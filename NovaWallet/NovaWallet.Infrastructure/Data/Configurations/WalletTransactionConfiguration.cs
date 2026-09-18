using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Domain.Entities;

namespace NovaWallet.Infrastructure.Data.Configurations;

public sealed class WalletTransactionConfiguration : IEntityTypeConfiguration<WalletTransaction>
{
    public void Configure(EntityTypeBuilder<WalletTransaction> builder)
    {
        builder.ToTable("wallet_transactions");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(t => t.WalletId).HasColumnName("wallet_id").IsRequired();
        builder.Property(t => t.Reference).HasColumnName("reference").HasMaxLength(100).IsRequired();
        builder.Property(t => t.Type).HasColumnName("type").HasConversion<string>().IsRequired();
        builder.Property(t => t.AmountKobo).HasColumnName("amount_kobo").IsRequired();
        builder.Property(t => t.BalanceBeforeKobo).HasColumnName("balance_before_kobo").IsRequired();
        builder.Property(t => t.BalanceAfterKobo).HasColumnName("balance_after_kobo").IsRequired();
        builder.Property(t => t.Description).HasColumnName("description").HasMaxLength(500);
        builder.Property(t => t.CounterpartWalletId).HasColumnName("counterpart_wallet_id");
        builder.Property(t => t.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(255);
        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(t => t.Reference).IsUnique().HasDatabaseName("ix_wallet_transactions_reference");
        builder.HasIndex(t => new { t.WalletId, t.CreatedAt }).HasDatabaseName("ix_wallet_transactions_wallet_created");

        builder.ToTable(t => t.HasCheckConstraint("ck_wallet_transactions_amount_positive", "amount_kobo > 0"));
    }
}
