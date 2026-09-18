using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWallet.Domain.Entities;

namespace NovaWallet.Infrastructure.Data.Configurations;

public sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("wallets");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(w => w.CustomerId)
            .HasColumnName("customer_id")
            .HasMaxLength(100)
            .IsRequired();

        // Map private backing field; long integer — never float/double
        builder.Property(w => w.BalanceKobo)
            .HasColumnName("balance_kobo")
            .IsRequired()
            .HasDefaultValue(0L);

        builder.Property(w => w.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsRequired()
            .HasDefaultValue("NGN");

        builder.Property(w => w.IsActive).HasColumnName("is_active").IsRequired();

        builder.Property(w => w.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(w => w.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.HasIndex(w => w.CustomerId)
            .IsUnique()
            .HasDatabaseName("ix_wallets_customer_id");

        // Defence-in-depth: DB-level guard against negative balances.
        // The domain entity already throws InsufficientFundsException before we get here,
        // but the constraint provides an immovable safety net.
        builder.ToTable(t => t.HasCheckConstraint("ck_wallets_balance_non_negative", "balance_kobo >= 0"));
    }
}
