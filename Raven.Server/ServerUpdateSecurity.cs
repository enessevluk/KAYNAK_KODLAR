using System.Security.Cryptography;

static class ServerUpdateSecurity
{
    public static bool VerifyPackage(string packagePath, string signaturePath, string publicKeyPath, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(packagePath)) { error = "Server update ZIP bulunamadı."; return false; }
            if (!File.Exists(signaturePath)) { error = "Server update imza dosyası (.sig) bulunamadı."; return false; }
            if (!File.Exists(publicKeyPath)) { error = "Update public key bulunamadı."; return false; }

            byte[] hash;
            using (var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                hash = SHA256.HashData(stream);

            var hashPath = packagePath + ".sha256";
            if (File.Exists(hashPath))
            {
                var expectedHash = File.ReadAllText(hashPath).Trim();
                var actualHash = Convert.ToHexString(hash).ToLowerInvariant();
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Server update SHA-256 doğrulaması başarısız.";
                    return false;
                }
            }

            var signature = Convert.FromBase64String(File.ReadAllText(signaturePath).Trim());
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(publicKeyPath));
            if (!rsa.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                error = "Server update RSA imzası geçersiz.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "Server update doğrulama hatası: " + ex.Message;
            return false;
        }
    }
}
