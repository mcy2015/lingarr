using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Server-side authentication sessions (P0-2 ticket revocation) plus the
/// must_change_password flag for the first-boot forced password change
/// (P0-3).
///
/// auth_sessions stores the serialized cookie authentication tickets behind
/// opaque keys: the cookie only ever carries the key, so deleting the row
/// (sign-out, password change, expiry) invalidates every replay of the old
/// cookie immediately.
///
/// must_change_password is seeded "false" for existing databases — installs
/// that already run keep working without a forced change; only a freshly
/// seeded default admin/admin account (StartupService) sets it to "true".
/// </summary>
[Migration(2)]
public class M0002_AddAuthSessions : Migration
{
    private const string MustChangePasswordKey = "must_change_password";

    public override void Up()
    {
        Create.Table("auth_sessions")
            .WithColumn("key").AsCustom("TEXT").PrimaryKey()
            .WithColumn("value").AsCustom("BLOB").NotNullable()
            .WithColumn("user_id").AsInt32().Nullable()
            .WithColumn("expires_at").AsDateTime().NotNullable();

        Create.Index("ix_auth_sessions_expires_at")
            .OnTable("auth_sessions")
            .OnColumn("expires_at");
        Create.Index("ix_auth_sessions_user_id")
            .OnTable("auth_sessions")
            .OnColumn("user_id");

        // Existing databases (and fresh ones, where StartupService only
        // flips it when it seeds default admin/admin credentials) start with
        // the forced change disabled.
        Insert.IntoTable("settings").Row(new
        {
            key = MustChangePasswordKey,
            value = "false"
        });
    }

    public override void Down()
    {
        Delete.Table("auth_sessions");
        Delete.FromTable("settings").Row(new { key = MustChangePasswordKey });
    }
}
