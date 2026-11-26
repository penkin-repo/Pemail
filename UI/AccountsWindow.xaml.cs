using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Pemail.Models;
using Pemail.Net;
using Pemail.Storage;

namespace Pemail.UI
{
    public partial class AccountsWindow : Window
    {
        private readonly ArchiveStore _store;
        private AccountConfig _editing;
        private bool _suppressPreset;

        public bool Changed { get; private set; }

        public AccountsWindow(ArchiveStore store)
        {
            InitializeComponent();
            _store = store;
            RefreshList();
            if (_store.Accounts.Count > 0)
            {
                LstAccounts.SelectedIndex = 0;
            }
            else
            {
                StartNew();
            }
        }

        private void RefreshList()
        {
            LstAccounts.ItemsSource = null;
            LstAccounts.ItemsSource = _store.Accounts;
        }

        private void StartNew()
        {
            _editing = null;
            LstAccounts.SelectedIndex = -1;
            _suppressPreset = false;
            CmbProvider.SelectedIndex = 0;
            TxtDisplayName.Text = string.Empty;
            TxtEmail.Text = string.Empty;
            PwdBox.Password = string.Empty;
            ApplyProviderPreset(MailProvider.Gmail);
            ChkMarkRead.IsChecked = false;
            TxtStatus.Text = string.Empty;
        }

        private void ApplyProviderPreset(MailProvider provider)
        {
            if (provider == MailProvider.Gmail)
            {
                TxtImapHost.Text = "imap.gmail.com";
                TxtImapPort.Text = "993";
                TxtSmtpHost.Text = "smtp.gmail.com";
                TxtSmtpPort.Text = "465";
                ChkImplicitTls.IsChecked = true;
            }
            else if (provider == MailProvider.Yandex)
            {
                TxtImapHost.Text = "imap.yandex.ru";
                TxtImapPort.Text = "993";
                TxtSmtpHost.Text = "smtp.yandex.ru";
                TxtSmtpPort.Text = "465";
                ChkImplicitTls.IsChecked = true;
            }
        }

        private void CmbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressPreset || !IsLoaded) return;
            if (CmbProvider.SelectedIndex == 0) ApplyProviderPreset(MailProvider.Gmail);
            else if (CmbProvider.SelectedIndex == 1) ApplyProviderPreset(MailProvider.Yandex);
        }

        private void LstAccounts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AccountConfig acc = LstAccounts.SelectedItem as AccountConfig;
            if (acc == null) return;
            _editing = acc;
            _suppressPreset = true;
            CmbProvider.SelectedIndex = (int)acc.Provider;
            TxtDisplayName.Text = acc.DisplayName;
            TxtEmail.Text = acc.Email;
            PwdBox.Password = string.Empty;
            TxtImapHost.Text = acc.ImapHost;
            TxtImapPort.Text = acc.ImapPort.ToString();
            TxtSmtpHost.Text = acc.SmtpHost;
            TxtSmtpPort.Text = acc.SmtpPort.ToString();
            ChkImplicitTls.IsChecked = acc.SmtpImplicitTls;
            ChkMarkRead.IsChecked = acc.MarkReadOnServer;
            TxtStatus.Text = string.Empty;
            _suppressPreset = false;
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            StartNew();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            AccountConfig acc = LstAccounts.SelectedItem as AccountConfig;
            if (acc == null) return;
            if (MessageBox.Show("Удалить аккаунт \"" + acc.DisplayName + "\" из настроек? Локальный архив на диске останется нетронутым.",
                "Удаление аккаунта", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            _store.Accounts.Remove(acc);
            _store.SaveAccounts();
            Changed = true;
            RefreshList();
            StartNew();
        }

        private AccountConfig BuildFromForm(out string error)
        {
            error = null;
            string email = TxtEmail.Text.Trim();
            if (string.IsNullOrEmpty(email) || !email.Contains("@"))
            {
                error = "Укажите корректный Email.";
                return null;
            }

            int imapPort;
            int smtpPort;
            if (!int.TryParse(TxtImapPort.Text.Trim(), out imapPort) || imapPort <= 0)
            {
                error = "Некорректный IMAP порт.";
                return null;
            }
            if (!int.TryParse(TxtSmtpPort.Text.Trim(), out smtpPort) || smtpPort <= 0)
            {
                error = "Некорректный SMTP порт.";
                return null;
            }

            string pwd = PwdBox.Password;
            if (_editing == null && string.IsNullOrEmpty(pwd))
            {
                error = "Введите пароль приложения.";
                return null;
            }

            AccountConfig target = _editing ?? new AccountConfig();
            target.Provider = (MailProvider)Math.Max(0, CmbProvider.SelectedIndex);
            target.Email = email;
            target.DisplayName = string.IsNullOrWhiteSpace(TxtDisplayName.Text) ? email : TxtDisplayName.Text.Trim();
            target.ImapHost = TxtImapHost.Text.Trim();
            target.ImapPort = imapPort;
            target.SmtpHost = TxtSmtpHost.Text.Trim();
            target.SmtpPort = smtpPort;
            target.SmtpImplicitTls = ChkImplicitTls.IsChecked == true;
            target.MarkReadOnServer = ChkMarkRead.IsChecked == true;
            if (!string.IsNullOrEmpty(pwd))
            {
                target.SetPassword(pwd);
            }
            return target;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string err;
            AccountConfig cfg = BuildFromForm(out err);
            if (cfg == null)
            {
                TxtStatus.Text = err;
                return;
            }

            if (_editing == null)
            {
                _store.Accounts.Add(cfg);
                _editing = cfg;
            }

            _store.SaveAccounts();
            Changed = true;
            RefreshList();
            LstAccounts.SelectedItem = _editing;
            TxtStatus.Text = "Сохранено.";
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            string err;
            AccountConfig cfg = BuildFromForm(out err);
            if (cfg == null)
            {
                TxtStatus.Text = err;
                return;
            }

            BtnTest.IsEnabled = false;
            TxtStatus.Text = "Подключение к " + cfg.ImapHost + "...";
            try
            {
                string host = cfg.ImapHost;
                int port = cfg.ImapPort;
                string email = cfg.Email;
                string pwd = cfg.GetPassword();

                string summary = await Task.Run(() =>
                {
                    using (ImapClient imap = new ImapClient())
                    {
                        imap.ConnectAndLogin(host, port, email, pwd);
                        var folders = imap.ListFolders();
                        long usedKb, limitKb;
                        bool hasQ = imap.TryGetQuota(out usedKb, out limitKb);
                        string qText = hasQ
                            ? string.Format(", квота: {0:0.00} / {1:0.0} ГБ", usedKb / 1048576.0, limitKb / 1048576.0)
                            : string.Empty;
                        return "Успешно! Найдено папок: " + folders.Count + qText;
                    }
                });
                TxtStatus.Text = summary;
            }
            catch (Exception ex)
            {
                TxtStatus.Text = "Ошибка: " + ex.Message;
            }
            finally
            {
                BtnTest.IsEnabled = true;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
