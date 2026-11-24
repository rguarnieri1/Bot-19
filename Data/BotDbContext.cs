using BotCripto.Models;
using BotCripto.Services;
using Microsoft.EntityFrameworkCore;

namespace BotCripto.Data;

// Contesto Entity Framework sul database SQL Server "bot" (autenticazione Windows).
// Le tabelle del Bot 17 stanno nello schema "bot17", così il database può essere condiviso con gli altri bot.
public class BotDbContext : DbContext
{
    public const string Schema = "bot17";

    // Valore fisso della colonna Bot nelle tabelle bot17.bot17_trades, bot17.bot17_signals, bot17.bot17_cycles
    // e bot17.bot17_operazioni_chiuse
    public const string BotName = "Bot 17";

    private readonly string _connectionString;

    public BotDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<SignalRecord> Signals => Set<SignalRecord>();
    public DbSet<ClosedTradeRecord> ClosedTrades => Set<ClosedTradeRecord>();
    public DbSet<CycleRecord> Cycles => Set<CycleRecord>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlServer(_connectionString, sql => sql.EnableRetryOnFailure(3));
    }

    // Prima di ogni salvataggio scrive "Bot 17" nella colonna Bot delle righe inserite o aggiornate
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        foreach (var entry in ChangeTracker.Entries())
            if (entry.State is EntityState.Added or EntityState.Modified
                && entry.Metadata.FindProperty(BotColumn) != null)
                entry.Property(BotColumn).CurrentValue = BotName;

        // Codice ISIN delle righe che non l'hanno ancora (se già scaricato da EODHD, vedi IsinService)
        foreach (var entry in ChangeTracker.Entries())
            if (entry.State is EntityState.Added or EntityState.Modified
                && entry.Metadata.FindProperty(IsinColumn) != null
                && entry.Metadata.FindProperty(SymbolColumn) != null
                && entry.Property(IsinColumn).CurrentValue == null)
                entry.Property(IsinColumn).CurrentValue = IsinService.GetCached(entry.Property(SymbolColumn).CurrentValue as string);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    private const string BotColumn = "Bot";
    private const string IsinColumn = "Isin";
    private const string SymbolColumn = "Symbol";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Trade>(e =>
        {
            e.ToTable("bot17_trades");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasMaxLength(36);
            e.Property(t => t.Symbol).HasMaxLength(50).IsRequired(false);
            e.Property(t => t.Isin).HasMaxLength(12);
            e.Property(t => t.Strategy).HasMaxLength(200).IsRequired(false);
            e.Property(t => t.Status).HasMaxLength(20).IsRequired(false);
            e.Property(t => t.ExitReason).HasMaxLength(100);

            // Stessa precisione delle tabelle degli altri bot (quantità frazionarie)
            foreach (var p in new[] { nameof(Trade.EntryPrice), nameof(Trade.ExitPrice), nameof(Trade.StopLoss),
                                      nameof(Trade.TargetPrice), nameof(Trade.PositionSize), nameof(Trade.Profit),
                                      nameof(Trade.ProfitPercentage) })
                e.Property(p).HasPrecision(38, 18);

            e.Ignore(t => t.IsPaperManaged);
            e.Property<string>(BotColumn).HasMaxLength(50).IsRequired();
            e.HasIndex(t => new { t.Symbol, t.Status });
        });

        modelBuilder.Entity<SignalRecord>(e =>
        {
            e.ToTable("bot17_signals");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).UseIdentityColumn();
            e.Property(s => s.Symbol).HasMaxLength(50);
            e.Property(s => s.Isin).HasMaxLength(12);
            e.Property(s => s.Strategy).HasMaxLength(200);
            e.Property(s => s.Signal).HasMaxLength(200);
            e.Property(s => s.Timeframe).HasMaxLength(10);
            e.Property(s => s.RejectionReason).HasMaxLength(100);
            e.Property(s => s.TradeId).HasMaxLength(36);

            foreach (var p in typeof(SignalRecord).GetProperties()
                         .Where(p => p.PropertyType == typeof(decimal) || p.PropertyType == typeof(decimal?)))
                e.Property(p.Name).HasPrecision(38, 18);

            e.Property<string>(BotColumn).HasMaxLength(50).IsRequired();
            e.HasIndex(s => new { s.Symbol, s.SignalCandleTime });
        });

        // Tabella creata con crea-tabella-operazioni-chiuse.sql
        modelBuilder.Entity<ClosedTradeRecord>(e =>
        {
            e.ToTable("bot17_operazioni_chiuse");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).UseIdentityColumn();
            e.Property(c => c.TradeId).HasMaxLength(36);
            e.Property(c => c.Symbol).HasMaxLength(50);
            e.Property(c => c.Isin).HasMaxLength(12);
            e.Property(c => c.Strategy).HasMaxLength(200);
            e.Property(c => c.ExitReason).HasMaxLength(100);
            e.Property(c => c.Status).HasMaxLength(20);

            foreach (var p in typeof(ClosedTradeRecord).GetProperties()
                         .Where(p => p.PropertyType == typeof(decimal) || p.PropertyType == typeof(decimal?)))
                e.Property(p.Name).HasPrecision(38, 18);

            e.Property<string>(BotColumn).HasMaxLength(50).IsRequired();
            e.HasIndex(c => c.TradeId).IsUnique();
        });

        // Tabella creata con crea-tabella-cycles.sql
        modelBuilder.Entity<CycleRecord>(e =>
        {
            e.ToTable("bot17_cycles");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).UseIdentityColumn();
            e.Property(c => c.Timeframe).HasMaxLength(10);
            e.Property(c => c.BtcRegime).HasMaxLength(20);
            e.Property(c => c.CapitalCommitted).HasPrecision(38, 18);
            e.Property(c => c.RealizedPnl).HasPrecision(38, 18);
            e.Property<string>(BotColumn).HasMaxLength(50).IsRequired();
            e.HasIndex(c => c.StartTime);
        });
    }
}
