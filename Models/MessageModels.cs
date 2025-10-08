using Microsoft.EntityFrameworkCore;

namespace ChatApp.Api.Models
{
    public class MessageModel
    {
        public int Id { get; set; }
        public required string User { get; set; }
        public int UserId { get; set; }
        public required string Text { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public string? Sentiment { get; set; }
        public double? SentimentScore { get; set; }
        public UserModel? UserRef { get; set; }
    }

    public class UserModel
    {
        public int Id { get; set; }
        public required string Nickname { get; set; }
        public string? DisplayName { get; set; } // Kullanıcının yazdığı orijinal hali
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public ICollection<MessageModel> Messages { get; set; } = new List<MessageModel>();
    }

    public class ChatDbContext : DbContext
    {
        public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options) { }

        public DbSet<MessageModel> Messages => Set<MessageModel>();
        public DbSet<UserModel> Users => Set<UserModel>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MessageModel>(b =>
            {
                b.HasKey(m => m.Id);
                b.Property(m => m.User).HasMaxLength(64).IsRequired();
                b.Property(m => m.Text).HasMaxLength(4000).IsRequired();
                b.Property(m => m.Sentiment).HasMaxLength(16);
                b.HasOne(m => m.UserRef)
                    .WithMany(u => u.Messages)
                    .HasForeignKey(m => m.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<UserModel>(b =>
            {
                b.HasKey(u => u.Id);
                b.Property(u => u.Nickname).HasMaxLength(64).IsRequired();
                b.Property(u => u.DisplayName).HasMaxLength(64);
                b.HasIndex(u => u.Nickname).IsUnique();
            });
        }
    }
}
