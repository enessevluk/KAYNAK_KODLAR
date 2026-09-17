using Xunit;
using Microsoft.Data.Sqlite;

public sealed class LicenseAndSessionTests
{
    [Fact]
    public void LegacyDeviceHash_IsAcceptedForOneTimeMigration()
    {
        var license = CreateLicense(deviceHash: "legacy-hash");

        var result = LicenseRules.CheckForValidation(
            license, "stable-v2-hash", "legacy-hash", TimeSpan.FromHours(24));

        Assert.Null(result);
        Assert.True(LicenseRules.IsLegacyDeviceMatch(license, "stable-v2-hash", "legacy-hash"));
    }

    [Fact]
    public void DifferentDevice_IsRejected()
    {
        var license = CreateLicense(deviceHash: "bound-device");

        var result = LicenseRules.CheckForValidation(
            license, "other-device", "other-legacy", TimeSpan.FromHours(24));

        Assert.NotNull(result);
        Assert.Equal("DEVICE_MISMATCH", result.Value.Code);
    }

    [Fact]
    public void AuthSession_CanOnlyBeConsumedOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RavenServerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var db = new RavenDb(Path.Combine(directory, "test.db"));
            db.Initialize();
            const string sessionId = "test-session";
            db.CreateAuthSession(sessionId, DateTimeOffset.UtcNow.AddMinutes(5));
            db.MarkAuthSessionVerified(sessionId, "76561198000000000");

            Assert.True(db.TryConsumeAuthSession(sessionId, DateTimeOffset.UtcNow));
            Assert.False(db.TryConsumeAuthSession(sessionId, DateTimeOffset.UtcNow));
            Assert.NotNull(db.GetAuthSession(sessionId)?.UsedUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExpiredAuthSession_CannotBeConsumed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RavenServerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var db = new RavenDb(Path.Combine(directory, "test.db"));
            db.Initialize();
            const string sessionId = "expired-session";
            db.CreateAuthSession(sessionId, DateTimeOffset.UtcNow.AddMinutes(-1));
            db.MarkAuthSessionVerified(sessionId, "76561198000000000");

            Assert.False(db.TryConsumeAuthSession(sessionId, DateTimeOffset.UtcNow));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("SUSPENDED", "LICENSE_SUSPENDED")]
    [InlineData("REVOKED", "LICENSE_REVOKED")]
    [InlineData("EXPIRED", "PURCHASE_REQUIRED")]
    public void InactiveLicenseStates_AreRejected(string status, string expectedCode)
    {
        var license = CreateLicense(deviceHash: "bound-device") with { Status = status };

        var result = LicenseRules.CheckForValidation(
            license, "bound-device", null, TimeSpan.FromHours(24));

        Assert.NotNull(result);
        Assert.Equal(expectedCode, result.Value.Code);
    }

    [Fact]
    public void ShopierOrder_IsIdempotent_AndDowngradeWaitsForCurrentPlanExpiry()
    {
        WithDatabase(db =>
        {
            const string steamId = "76561198000000000";
            var premium = new ParsedShopierOrder("ORDER-1", steamId, "PREMIUM", "Premium", "paid", steamId, DateTimeOffset.UtcNow);
            var first = db.ApplyShopierOrder(premium, 30, TimeSpan.FromHours(24), EntitlementPlans.Premium);
            var duplicate = db.ApplyShopierOrder(premium, 30, TimeSpan.FromHours(24), EntitlementPlans.Premium);
            var standard = new ParsedShopierOrder("ORDER-2", steamId, "STANDARD", "Standard", "paid", steamId, DateTimeOffset.UtcNow);
            var renewed = db.ApplyShopierOrder(standard, 30, TimeSpan.FromHours(24), EntitlementPlans.Standard);

            Assert.NotNull(first);
            Assert.Equal(first!.Id, duplicate!.Id);
            Assert.Equal(EntitlementPlans.Premium, renewed!.Plan);
            Assert.Equal(EntitlementPlans.Standard, renewed.NextPlan);
            Assert.Equal(first.ExpiresUtc, renewed.PlanChangesUtc);
            Assert.True(renewed.ExpiresUtc > renewed.PlanChangesUtc);

            db.RefreshScheduledPlans(renewed.PlanChangesUtc!.Value.AddSeconds(1));
            var afterTransition=db.GetLicense(renewed.Id);
            Assert.Equal(EntitlementPlans.Standard,afterTransition!.Plan);
            Assert.Null(afterTransition.NextPlan);
            Assert.Null(afterTransition.PlanChangesUtc);
            Assert.Single(db.ListLicenses(10));
            Assert.Equal(2, db.ListShopierOrders(10).Count);
        });
    }

    [Fact]
    public void PremiumUpgrade_BecomesActiveImmediately()
    {
        WithDatabase(db =>
        {
            const string steamId="76561198000000000";
            var standard=new ParsedShopierOrder("STD-1",steamId,"STANDARD","Standard","paid",steamId,DateTimeOffset.UtcNow);
            db.ApplyShopierOrder(standard,30,TimeSpan.FromHours(24),EntitlementPlans.Standard);
            var premium=new ParsedShopierOrder("PREM-1",steamId,"PREMIUM","Premium","paid",steamId,DateTimeOffset.UtcNow);

            var upgraded=db.ApplyShopierOrder(premium,30,TimeSpan.FromHours(24),EntitlementPlans.Premium);

            Assert.Equal(EntitlementPlans.Premium,upgraded!.Plan);
            Assert.Null(upgraded.NextPlan);
            Assert.Null(upgraded.PlanChangesUtc);
        });
    }

    [Fact]
    public void RevokedLicense_CanBeDeleted_WithoutDeletingShopierOrderHistory()
    {
        WithDatabase(db =>
        {
            const string steamId="76561198000000000";
            const string orderId="DELETE-LICENSE-ORDER";
            var order=new ParsedShopierOrder(orderId,steamId,"STANDARD","Standard","paid",steamId,DateTimeOffset.UtcNow);
            var license=db.ApplyShopierOrder(order,30,TimeSpan.FromHours(24),EntitlementPlans.Standard)!;

            Assert.False(db.DeleteRevokedLicense(license.Id));
            db.SetLicenseStatus(license.Id,"REVOKED","Yönetici iptali");
            Assert.True(db.DeleteRevokedLicense(license.Id));

            Assert.Null(db.GetLicense(license.Id));
            var retainedOrder=db.GetShopierOrder(orderId);
            Assert.NotNull(retainedOrder);
            Assert.Null(retainedOrder!.LicenseId);
        });
    }

    [Fact]
    public void OfflineToken_DoesNotCarryPremiumPastScheduledDowngrade()
    {
        var now=DateTimeOffset.UtcNow;
        var change=now.AddHours(10);
        var license=CreateLicense("device") with
        {
            ExpiresUtc=now.AddDays(30),
            Plan=EntitlementPlans.Premium,
            NextPlan=EntitlementPlans.Standard,
            PlanChangesUtc=change
        };

        Assert.Equal(change,LicenseRules.GetOfflineTokenExpiry(license,TimeSpan.FromHours(24),now));
    }

    [Fact]
    public void ExistingDatabase_GetsStandardPlanDuringMigration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RavenServerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "legacy.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
CREATE TABLE licenses(
 id INTEGER PRIMARY KEY AUTOINCREMENT, key_hash TEXT NOT NULL UNIQUE, key_last4 TEXT NOT NULL,
 status TEXT NOT NULL DEFAULT 'ACTIVE', steam_id TEXT NULL, device_hash TEXT NULL,
 activation_token_hash TEXT NULL, expires_utc TEXT NULL, created_utc TEXT NOT NULL,
 first_activation_utc TEXT NULL, last_seen_utc TEXT NULL, last_client_version TEXT NOT NULL DEFAULT '',
 note TEXT NOT NULL DEFAULT ''
);
INSERT INTO licenses(key_hash,key_last4,status,steam_id,created_utc) VALUES('legacy-hash','1234','ACTIVE','76561198000000000',$now);
""";
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }

            var db = new RavenDb(path);
            db.Initialize();

            Assert.Equal(EntitlementPlans.Standard, db.ListLicenses(10).Single().Plan);
            Assert.Equal(6, db.GetSchemaVersion());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AdminLoginForm_ContainsAntiforgeryToken()
    {
        var html = AdminPages.Login("csrf-test-token", true);

        Assert.Contains("name='__RequestVerificationToken'", html);
        Assert.Contains("value='csrf-test-token'", html);
        Assert.Contains("name='totpCode'", html);
    }

    [Fact]
    public void Totp_VerifiesRfcCompatibleSixDigitCodeWithinCurrentWindow()
    {
        const string secret="GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        var now=DateTimeOffset.FromUnixTimeSeconds(59);

        Assert.True(AdminTotp.Verify(secret,"287082",now));
        Assert.False(AdminTotp.Verify(secret,"287083",now));
    }

    [Fact]
    public void AdminAudit_IsPersistedWithoutSensitiveCredentials()
    {
        WithDatabase(db =>
        {
            db.RecordAdminAudit("ravenadmin", "LICENSE_ACTION", "42", "reset_device", "127.0.0.1");

            var entry = Assert.Single(db.ListAdminAudit(10));
            Assert.Equal("ravenadmin", entry.Actor);
            Assert.Equal("LICENSE_ACTION", entry.Action);
            Assert.Equal("42", entry.Target);
            Assert.Equal("reset_device", entry.Detail);
        });
    }

    [Fact]
    public void ExpiredLicense_KeepsActivationContext_ForHelpfulRenewalMessage()
    {
        WithDatabase(db =>
        {
            const string steamId = "76561198000000002";
            const string rawToken = "expired-test-activation-token";
            db.CreateManualEntitlement(steamId, DateTimeOffset.UtcNow.AddDays(-2), "expired-test", EntitlementPlans.Standard);
            var license = db.FindLatestLicenseBySteamId(steamId)!;
            db.ActivateLicense(license.Id, steamId, "device-test", ServerHelpers.Hash(rawToken), "1.7.0", DateTimeOffset.UtcNow.AddDays(-2));

            db.ExpireIfPastGrace(license.Id, TimeSpan.FromHours(24), DateTimeOffset.UtcNow);

            var found = db.FindLicenseByActivationToken(rawToken);
            Assert.NotNull(found);
            Assert.Equal("EXPIRED", found!.Status);
        });
    }

    [Fact]
    public void ExpiryScreenPreview_PreservesActivation_AndCanBeReactivated()
    {
        WithDatabase(db =>
        {
            const string steamId = "76561198000000003";
            const string rawToken = "expiry-preview-activation-token";
            db.CreateManualEntitlement(steamId, DateTimeOffset.UtcNow.AddDays(30), "expiry-preview", EntitlementPlans.Standard);
            var license = db.FindLatestLicenseBySteamId(steamId)!;
            db.ActivateLicense(license.Id, steamId, "preview-device", ServerHelpers.Hash(rawToken), "1.7.9", DateTimeOffset.UtcNow);

            db.SetLicenseStatus(license.Id, "EXPIRED");
            Assert.Equal("EXPIRED", db.FindLicenseByActivationToken(rawToken)!.Status);

            db.SetLicenseStatus(license.Id, "ACTIVE");
            Assert.Equal("ACTIVE", db.FindLicenseByActivationToken(rawToken)!.Status);
        });
    }

    [Fact]
    public void LicenseStatusReason_IsStored_AndClearedWhenReactivated()
    {
        WithDatabase(db =>
        {
            const string steamId = "76561198000000001";
            db.CreateManualEntitlement(steamId, null, "test", EntitlementPlans.Standard);
            var license = db.FindLatestLicenseBySteamId(steamId)!;

            db.SetLicenseStatus(license.Id, "REVOKED", "Ödeme doğrulaması için destekle görüşün.");
            var revoked = db.GetLicense(license.Id)!;
            Assert.Equal("REVOKED", revoked.Status);
            Assert.Contains("Ödeme doğrulaması", revoked.StatusReason);

            db.SetLicenseStatus(license.Id, "ACTIVE");
            var active = db.GetLicense(license.Id)!;
            Assert.Equal("ACTIVE", active.Status);
            Assert.Equal("", active.StatusReason);
        });
    }

    [Fact]
    public void OperationalSettings_MaintenanceAndAnnouncementRevisionAreStable()
    {
        WithDatabase(db =>
        {
            db.SetOperationalSettings(true, "Kısa bakım", "Yeni sürüm", "Hazır.");
            var first=db.GetOperationalSettings();
            db.SetOperationalSettings(true, "Kısa bakım", "Yeni sürüm", "Hazır.");
            var unchanged=db.GetOperationalSettings();
            db.SetOperationalSettings(false, "", "Yeni sürüm", "İçerik değişti.");
            var changed=db.GetOperationalSettings();
            var starts=DateTimeOffset.UtcNow.AddMinutes(-5);
            var ends=DateTimeOffset.UtcNow.AddDays(2);
            db.SetOperationalSettings(false,"","Premium içerikler","Yeni paket yayında.","UPDATE","https://example.com/raven.png","İncele","https://example.com",starts,ends);
            var detailed=db.GetOperationalSettings();

            Assert.True(first.MaintenanceEnabled);
            Assert.False(string.IsNullOrWhiteSpace(first.AnnouncementId));
            Assert.Equal(first.AnnouncementId,unchanged.AnnouncementId);
            Assert.NotEqual(first.AnnouncementId,changed.AnnouncementId);
            Assert.False(changed.MaintenanceEnabled);
            Assert.Equal("UPDATE",detailed.AnnouncementLevel);
            Assert.Equal("İncele",detailed.AnnouncementButtonText);
            Assert.Equal("https://example.com",detailed.AnnouncementButtonUrl);
            Assert.NotNull(detailed.AnnouncementStartsUtc);
            Assert.NotNull(detailed.AnnouncementEndsUtc);
        });
    }

    [Fact]
    public void FirstDeviceClaim_CannotBeOverwrittenBySecondActivationToken()
    {
        WithDatabase(db =>
        {
            const string steamId = "76561198000000888";
            const string tokenA = "activation-token-a";
            const string tokenB = "activation-token-b";
            db.CreateManualEntitlement(steamId, DateTimeOffset.UtcNow.AddDays(30), "activation-race", EntitlementPlans.Standard);
            var license = db.FindLatestLicenseBySteamId(steamId)!;

            var first = db.TryActivateLicense(license.Id, steamId, "device-a", null, ServerHelpers.Hash(tokenA), "1.7.0", DateTimeOffset.UtcNow);
            var second = db.TryActivateLicense(license.Id, steamId, "device-b", null, ServerHelpers.Hash(tokenB), "1.7.0", DateTimeOffset.UtcNow.AddMilliseconds(1));

            Assert.True(first);
            Assert.False(second);
            Assert.Equal("device-a", db.GetLicense(license.Id)!.DeviceHash);
            Assert.NotNull(db.FindLicenseByActivationToken(tokenA));
            Assert.Null(db.FindLicenseByActivationToken(tokenB));
        });
    }

    [Fact]
    public void RejectedShopierOrder_CanLaterBecomeAcceptedWithoutDoubleProcessing()
    {
        WithDatabase(db =>
        {
            const string orderId = "RETRY-ORDER-1";
            const string steamId = "76561198000000777";
            db.RecordShopierOrder(new ShopierOrderRecord(orderId, "", "STANDARD", "Standard", "paid", "not missing anymore",
                "REJECTED", "Sipariş notunda SteamID64 yoktu.", null, DateTimeOffset.UtcNow.AddMinutes(-20)));

            Assert.Equal("REJECTED", db.GetShopierOrderStatus(orderId));
            Assert.True(db.MarkShopierOrderRetryPending(orderId));
            var parsed = new ParsedShopierOrder(orderId, steamId, "STANDARD", "Standard", "paid", steamId, DateTimeOffset.UtcNow.AddMinutes(-20));
            var accepted = db.ApplyShopierOrder(parsed, 30, TimeSpan.FromHours(24), EntitlementPlans.Standard);
            var firstExpiry = accepted?.ExpiresUtc;
            var duplicate = db.ApplyShopierOrder(parsed, 30, TimeSpan.FromHours(24), EntitlementPlans.Standard);

            Assert.NotNull(accepted);
            Assert.Equal(accepted!.Id, duplicate!.Id);
            Assert.Equal(firstExpiry, duplicate.ExpiresUtc);
            Assert.Equal("ACCEPTED", db.GetShopierOrderStatus(orderId));
            Assert.Single(db.ListLicenses(10));
        });
    }

    [Fact]
    public void ShopierCursor_RewindsButNeverMovesForwardDuringManualRetry()
    {
        WithDatabase(db =>
        {
            var current = DateTimeOffset.UtcNow.AddHours(-1);
            db.SetShopierCursorUtc(current);
            var rewind = current.AddHours(-3);

            db.RewindShopierCursorUtc(rewind);
            Assert.Equal(rewind.ToUnixTimeSeconds(), db.GetShopierCursorUtc().ToUnixTimeSeconds());

            db.RewindShopierCursorUtc(current.AddHours(2));
            Assert.Equal(rewind.ToUnixTimeSeconds(), db.GetShopierCursorUtc().ToUnixTimeSeconds());
        });
    }

    private static LicenseRecord CreateLicense(string deviceHash) => new(
        1, "1234", "ACTIVE", "76561198000000000", deviceHash, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "1.3.0", "", "MANUAL", null, null, DateTimeOffset.UtcNow, EntitlementPlans.Standard, null, null);

    private static void WithDatabase(Action<RavenDb> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RavenServerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var db = new RavenDb(Path.Combine(directory, "test.db"));
            db.Initialize();
            action(db);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LifetimePremium_IsNotDowngradedByLifetimeStandardPurchase()
    {
        WithDatabase(db =>
        {
            var steamId = "76561198000000999";
            var premium = new ParsedShopierOrder("LIFE-PREM", steamId, "PREM", "Premium", "paid", steamId, DateTimeOffset.UtcNow);
            db.ApplyShopierOrder(premium, 0, TimeSpan.FromHours(24), EntitlementPlans.Premium);

            var standard = new ParsedShopierOrder("LIFE-STD", steamId, "STD", "Standard", "paid", steamId, DateTimeOffset.UtcNow);
            var result = db.ApplyShopierOrder(standard, 0, TimeSpan.FromHours(24), EntitlementPlans.Standard);

            Assert.NotNull(result);
            Assert.Equal(EntitlementPlans.Premium, result!.Plan);
            Assert.Null(result.ExpiresUtc);
            Assert.Null(result.NextPlan);
        });
    }
}
