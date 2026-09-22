using Microsoft.EntityFrameworkCore;
using PresenterAi.Infrastructure.Persistence.Entities;

namespace PresenterAi.Infrastructure.Persistence;

public sealed class PresenterAiDbContext(DbContextOptions<PresenterAiDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Presentation> Presentations => Set<Presentation>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionTurn> SessionTurns => Set<SessionTurn>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Id).HasColumnName("id");
            entity.Property(user => user.Email).HasColumnName("email").IsRequired();
            entity.Property(user => user.DisplayName).HasColumnName("display_name");
            entity.Property(user => user.Role).HasColumnName("role").IsRequired();
            entity.Property(user => user.IsDisabled).HasColumnName("is_disabled").IsRequired();
            entity.Property(user => user.AuthMethod).HasColumnName("auth_method").IsRequired();
            entity.Property(user => user.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            entity.Property(user => user.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
            entity.Property(user => user.LastSignInAt).HasColumnName("last_sign_in_at").HasColumnType("timestamp with time zone");
            entity.HasIndex(user => user.Email).IsUnique().HasDatabaseName("ux_users_email_lower");
        });

        modelBuilder.Entity<ExternalLogin>(entity =>
        {
            entity.ToTable("external_logins");
            entity.HasKey(login => login.Id);
            entity.Property(login => login.Id).HasColumnName("id");
            entity.Property(login => login.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(login => login.Provider).HasColumnName("provider").IsRequired();
            entity.Property(login => login.Subject).HasColumnName("subject").IsRequired();
            entity.Property(login => login.ProviderEmail).HasColumnName("provider_email").IsRequired();
            entity.Property(login => login.ProviderEmailVerified).HasColumnName("provider_email_verified").IsRequired();
            entity.Property(login => login.ProviderDisplayName).HasColumnName("provider_display_name");
            entity.Property(login => login.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            entity.HasIndex(login => new { login.Provider, login.Subject })
                .IsUnique()
                .HasDatabaseName("ux_external_logins_provider_subject");
            entity.HasIndex(login => login.UserId).HasDatabaseName("ix_external_logins_user_id");
            entity.HasOne(login => login.User)
                .WithMany(user => user.ExternalLogins)
                .HasForeignKey(login => login.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(token => token.Id);
            entity.Property(token => token.Id).HasColumnName("id");
            entity.Property(token => token.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(token => token.TokenHash).HasColumnName("token_hash").IsRequired();
            entity.Property(token => token.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
            entity.Property(token => token.IsRevoked).HasColumnName("is_revoked").IsRequired();
            entity.Property(token => token.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            entity.Property(token => token.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamp with time zone");
            entity.HasIndex(token => token.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_token_hash");
            entity.HasIndex(token => new { token.UserId, token.IsRevoked }).HasDatabaseName("ix_refresh_tokens_user_id_is_revoked");
            entity.HasOne(token => token.User)
                .WithMany(user => user.RefreshTokens)
                .HasForeignKey(token => token.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Presentation>(entity =>
        {
            entity.ToTable("presentations");
            entity.HasKey(presentation => presentation.Id);
            entity.Property(presentation => presentation.Id).HasColumnName("id");
            entity.Property(presentation => presentation.OwnerId).HasColumnName("owner_id").IsRequired();
            entity.Property(presentation => presentation.Slug).HasColumnName("slug").IsRequired();
            entity.Property(presentation => presentation.Title).HasColumnName("title").IsRequired();
            entity.Property(presentation => presentation.Deck).HasColumnName("deck").IsRequired();
            entity.Property(presentation => presentation.Driver).HasColumnName("driver").IsRequired();
            entity.Property(presentation => presentation.Script).HasColumnName("script").IsRequired();
            entity.Property(presentation => presentation.Context).HasColumnName("context");
            entity.Property(presentation => presentation.Frontmatter).HasColumnName("frontmatter").HasColumnType("jsonb");
            entity.Property(presentation => presentation.SlideCount).HasColumnName("slide_count").IsRequired();
            entity.Property(presentation => presentation.Version).HasColumnName("version").IsRequired();
            entity.Property(presentation => presentation.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone");
            entity.Property(presentation => presentation.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone");
            entity.HasIndex(presentation => new { presentation.OwnerId, presentation.Slug })
                .IsUnique()
                .HasDatabaseName("ux_presentations_owner_id_slug");
            entity.HasIndex(presentation => presentation.OwnerId).HasDatabaseName("ix_presentations_owner_id");
            entity.HasOne(presentation => presentation.Owner)
                .WithMany(user => user.Presentations)
                .HasForeignKey(presentation => presentation.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Session>(entity =>
        {
            entity.ToTable("sessions");
            entity.HasKey(session => session.Id);
            entity.Property(session => session.Id).HasColumnName("id");
            entity.Property(session => session.PresentationId).HasColumnName("presentation_id").IsRequired();
            entity.Property(session => session.UserId).HasColumnName("user_id").IsRequired();
            entity.Property(session => session.StartedAt).HasColumnName("started_at").HasColumnType("timestamp with time zone");
            entity.Property(session => session.EndedAt).HasColumnName("ended_at").HasColumnType("timestamp with time zone");
            entity.Property(session => session.UsageSeconds).HasColumnName("usage_seconds").IsRequired();
            entity.Property(session => session.Upstream).HasColumnName("upstream").IsRequired();
            entity.Property(session => session.UpstreamSessionId).HasColumnName("upstream_session_id");
            entity.Property(session => session.CloseReason).HasColumnName("close_reason");
            entity.HasIndex(session => new { session.UserId, session.StartedAt })
                .HasDatabaseName("ix_sessions_user_id_started_at");
            entity.HasIndex(session => session.PresentationId).HasDatabaseName("ix_sessions_presentation_id");
            entity.HasOne(session => session.Presentation)
                .WithMany(presentation => presentation.Sessions)
                .HasForeignKey(session => session.PresentationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(session => session.User)
                .WithMany(user => user.Sessions)
                .HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionTurn>(entity =>
        {
            entity.ToTable("session_turns");
            entity.HasKey(turn => turn.Id);
            entity.Property(turn => turn.Id).HasColumnName("id");
            entity.Property(turn => turn.SessionId).HasColumnName("session_id").IsRequired();
            entity.Property(turn => turn.Ordinal).HasColumnName("ordinal").IsRequired();
            entity.Property(turn => turn.Role).HasColumnName("role").IsRequired();
            entity.Property(turn => turn.Text).HasColumnName("text").IsRequired();
            entity.Property(turn => turn.SlideNo).HasColumnName("slide_no");
            entity.Property(turn => turn.At).HasColumnName("at").HasColumnType("timestamp with time zone");
            entity.HasIndex(turn => new { turn.SessionId, turn.Ordinal })
                .HasDatabaseName("ix_session_turns_session_id_ordinal");
            entity.HasOne(turn => turn.Session)
                .WithMany(session => session.Turns)
                .HasForeignKey(turn => turn.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
