static class UserIssueCatalog
{
    public static ApiProblemResponse Create(ServerRuntime runtime, string code, LicenseRecord? license = null, string? fallbackMessage = null)
    {
        code = (code ?? "UNKNOWN").Trim().ToUpperInvariant();
        var supportDetail = string.IsNullOrWhiteSpace(runtime.SupportMessage)
            ? "Sorununuz devam ederse Raven destek ekibine ulaşabilirsiniz."
            : runtime.SupportMessage;
        var reason = (license?.StatusReason ?? "").Trim();
        var paymentSuspension = reason.Contains("ÖDEME", StringComparison.OrdinalIgnoreCase) &&
                                (reason.Contains("ALINAMADI", StringComparison.OrdinalIgnoreCase) ||
                                 reason.Contains("ONAYLANMADI", StringComparison.OrdinalIgnoreCase));
        var expired = string.Equals(license?.Status, "EXPIRED", StringComparison.OrdinalIgnoreCase);

        return code switch
        {
            "PURCHASE_REQUIRED" => Problem(code,
                expired ? "Aboneliğiniz sona erdi" : "Raven erişimi gerekli",
                expired
                    ? "Raven Map Panel abonelik süreniz sona erdi. Kayıtlı haritalarınız ve ayarlarınız silinmedi."
                    : string.IsNullOrWhiteSpace(fallbackMessage)
                        ? "Bu Steam hesabında aktif Raven Map Panel erişimi bulunmuyor."
                        : fallbackMessage,
                expired
                    ? "Aboneliğinizi yeniledikten ve ödeme onaylandıktan sonra Lisansı Tekrar Kontrol Et düğmesine basın. Erişiminiz otomatik olarak yeniden açılır."
                    : "Satın alım yaptıktan sonra Raven siparişinizi otomatik olarak kontrol eder. Shopier sipariş notuna SteamID64 değerinizi eksiksiz yazın.",
                "warning",
                Buy(runtime, expired ? "Aboneliği yenile" : "Satın al", primary: true),
                Support(runtime, primary: false)),

            "LICENSE_REVOKED" => Problem(code,
                "Lisansınız iptal edildi",
                "Raven Map Panel erişiminiz yönetici tarafından iptal edildi.",
                string.IsNullOrWhiteSpace(reason)
                    ? "İptal nedeni hakkında bilgi almak için destek kanalımıza ulaşabilirsiniz. Yeni erişim almak istiyorsanız tekrar satın alabilir ve Shopier sipariş notuna SteamID64 değerinizi yazabilirsiniz."
                    : $"İşlem nedeni: {reason}. Yeni erişim için tekrar satın alabilir veya destek ekibiyle görüşebilirsiniz.",
                "error",
                Buy(runtime, "Tekrar satın al", primary: true),
                Support(runtime, primary: false)),

            "LICENSE_SUSPENDED" => Problem(code,
                paymentSuspension ? "Ödemeniz doğrulanamadı" : "Erişiminiz geçici olarak durduruldu",
                paymentSuspension
                    ? "Abonelik ödemeniz doğrulanamadığı için Raven Map Panel erişiminiz geçici olarak durduruldu."
                    : "Raven Map Panel erişiminiz geçici olarak durduruldu.",
                paymentSuspension
                    ? "Ödemenizi tamamladıysanız Steam hesabınızı yeniden doğrulayın. Erişim açılmazsa destek ekibine SteamID64 bilginizle ulaşın."
                    : string.IsNullOrWhiteSpace(reason)
                        ? supportDetail
                        : $"Yönetici açıklaması: {reason}. Ayrıntı veya yeniden etkinleştirme için destek ekibine ulaşabilirsiniz.",
                "error",
                Support(runtime, primary: true)),

            "DEVICE_MISMATCH" => Problem(code,
                "Cihaz doğrulaması başarısız",
                "Lisansınız farklı bir bilgisayara bağlı görünüyor.",
                "Bilgisayar değiştirdiyseniz cihaz sıfırlama işlemi gerekebilir. Destek ekibi hesabınızı kontrol edebilir.",
                "warning",
                Support(runtime, primary: true)),

            "STEAM_MISMATCH" => Problem(code,
                "Steam hesabı eşleşmiyor",
                "Bu Raven lisansı farklı bir Steam hesabına bağlı.",
                "Doğru Steam hesabıyla giriş yaptığınızdan emin olun. Hesap bağlantısının değiştirilmesi gerekiyorsa destek ekibine ulaşın.",
                "warning",
                Support(runtime, primary: true)),

            "STEAM_REQUIRED" => Problem(code,
                "Steam doğrulaması gerekli",
                string.IsNullOrWhiteSpace(fallbackMessage) ? "Steam hesabınız doğrulanamadı." : fallbackMessage,
                "Steam girişini yeniden başlatıp doğrulamayı tamamlayın.",
                "warning"),

            "SESSION_USED" => Problem(code,
                "Steam oturumunun süresi doldu",
                string.IsNullOrWhiteSpace(fallbackMessage) ? "Steam doğrulama oturumu artık kullanılamıyor." : fallbackMessage,
                "Steam ile giriş işlemini yeniden başlatın.",
                "warning"),

            "ACTIVATION_INVALID" => Problem(code,
                "Raven aktivasyonu geçersiz",
                "Bu cihazdaki Raven aktivasyonu artık geçerli değil.",
                "Steam hesabınızla yeniden doğrulama yapın. Sorun devam ederse destek ekibine ulaşın.",
                "warning",
                Support(runtime, primary: false)),

            "INVALID_REQUEST" => Problem(code,
                "İstek tamamlanamadı",
                string.IsNullOrWhiteSpace(fallbackMessage) ? "Raven isteği eksik veya geçersiz." : fallbackMessage,
                "İşlemi yeniden deneyin. Sorun devam ederse destek ekibine ulaşabilirsiniz.",
                "error",
                Support(runtime, primary: false)),

            _ => Problem(code,
                "Raven işlemi tamamlanamadı",
                string.IsNullOrWhiteSpace(fallbackMessage) ? "Beklenmeyen bir Raven hatası oluştu." : fallbackMessage,
                supportDetail,
                "error",
                Support(runtime, primary: false))
        };
    }

    private static ApiProblemResponse Problem(string code, string title, string message, string detail, string severity, params UserActionResponse?[] actions) =>
        new(false, code, title, message, detail, severity, actions.Where(x => x is not null).Cast<UserActionResponse>().ToList());

    private static UserActionResponse? Buy(ServerRuntime runtime, string label, bool primary)
    {
        if (string.IsNullOrWhiteSpace(runtime.ShopierBuyUrl)) return null;
        return new("BUY", label, runtime.ShopierBuyUrl, primary);
    }

    private static UserActionResponse? Support(ServerRuntime runtime, bool primary)
    {
        if (string.IsNullOrWhiteSpace(runtime.SupportUrl)) return null;
        return new("SUPPORT", string.IsNullOrWhiteSpace(runtime.SupportLabel) ? "Discord Destek" : runtime.SupportLabel, runtime.SupportUrl, primary);
    }
}
