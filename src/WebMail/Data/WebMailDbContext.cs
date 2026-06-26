using Microsoft.EntityFrameworkCore;
using WebMail.Domain;

namespace WebMail.Data;

public sealed class WebMailDbContext(DbContextOptions<WebMailDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Buyer> Buyers => Set<Buyer>();
    public DbSet<EmailAccount> EmailAccounts => Set<EmailAccount>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<AllowedSender> AllowedSenders => Set<AllowedSender>();
    public DbSet<BuyerSupplierAssignment> BuyerSupplierAssignments => Set<BuyerSupplierAssignment>();
    public DbSet<ActiveSyncWindow> ActiveSyncWindows => Set<ActiveSyncWindow>();
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>().HasIndex(x => x.UserName).IsUnique();
        modelBuilder.Entity<Buyer>().HasIndex(x => x.CardNo).IsUnique();
        modelBuilder.Entity<EmailAccount>().HasIndex(x => x.BuyerId).IsUnique();
        modelBuilder.Entity<EmailMessage>().HasIndex(x => x.BuyerId);
        modelBuilder.Entity<EmailMessage>().HasIndex(x => new { x.EmailAccountId, x.ProviderMessageId }).IsUnique();
        modelBuilder.Entity<AllowedSender>().HasIndex(x => x.EmailAddress).IsUnique();
        modelBuilder.Entity<BuyerSupplierAssignment>().HasIndex(x => x.BuyerId).IsUnique();
        modelBuilder.Entity<ActiveSyncWindow>().HasIndex(x => x.BuyerId).IsUnique();
    }
}
