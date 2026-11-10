using Microsoft.EntityFrameworkCore;
using Synthesia2Midi.Data.Entities;

namespace Synthesia2Midi.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Conversion> Conversions => Set<Conversion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversion>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Status).HasConversion<string>();
            entity.HasIndex(c => c.CreatedAt);
        });
    }
}
