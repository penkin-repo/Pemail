using System;
using System.Security.Cryptography;
using System.Text;

namespace Pemail.Models
{
    public enum MailProvider
    {
        Gmail = 0,
        Yandex = 1,
        Custom = 2
    }

    public class AccountConfig
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public MailProvider Provider { get; set; }
        public string Email { get; set; }
        public string EncryptedPassword { get; set; }
        public string ImapHost { get; set; }
        public int ImapPort { get; set; }
        public string SmtpHost { get; set; }
        public int SmtpPort { get; set; }
        public bool SmtpImplicitTls { get; set; }
        public bool MarkReadOnServer { get; set; }
        public long QuotaUsedKb { get; set; }
        public long QuotaLimitKb { get; set; }

        public AccountConfig()
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 10);
            ImapPort = 993;
            SmtpPort = 465;
            SmtpImplicitTls = true;
            MarkReadOnServer = false;
        }

        public static AccountConfig CreatePreset(MailProvider provider, string email, string appPassword, string displayName)
        {
            AccountConfig cfg = new AccountConfig();
            cfg.Provider = provider;
            cfg.Email = (email ?? string.Empty).Trim();
            cfg.DisplayName = string.IsNullOrWhiteSpace(displayName) ? cfg.Email : displayName.Trim();
            cfg.SetPassword(appPassword ?? string.Empty);

            if (provider == MailProvider.Gmail)
            {
                cfg.ImapHost = "imap.gmail.com";
                cfg.ImapPort = 993;
                cfg.SmtpHost = "smtp.gmail.com";
                cfg.SmtpPort = 465;
                cfg.SmtpImplicitTls = true;
            }
            else if (provider == MailProvider.Yandex)
            {
                cfg.ImapHost = "imap.yandex.ru";
                cfg.ImapPort = 993;
                cfg.SmtpHost = "smtp.yandex.ru";
                cfg.SmtpPort = 465;
                cfg.SmtpImplicitTls = true;
            }
            return cfg;
        }

        public void SetPassword(string plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword))
            {
                EncryptedPassword = string.Empty;
                return;
            }
            byte[] raw = Encoding.UTF8.GetBytes(plainPassword);
            byte[] enc = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
            EncryptedPassword = Convert.ToBase64String(enc);
        }

        public string GetPassword()
        {
            if (string.IsNullOrEmpty(EncryptedPassword)) return string.Empty;
            try
            {
                byte[] enc = Convert.FromBase64String(EncryptedPassword);
                byte[] raw = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch
            {
                return string.Empty;
            }
        }

        public string FormatQuota()
        {
            if (QuotaLimitKb <= 0) return DisplayName + ": квота н/д";
            double usedGb = QuotaUsedKb / 1048576.0;
            double limitGb = QuotaLimitKb / 1048576.0;
            return string.Format("{0}: {1:0.00} / {2:0.0} ГБ", DisplayName, usedGb, limitGb);
        }
    }
}
