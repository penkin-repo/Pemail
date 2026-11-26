using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Pemail.Models;
using Pemail.Net;
using Pemail.Storage;

namespace Pemail.UI
{
    public partial class CleanupWindow : Window
    {
        private readonly ArchiveStore _store;
        private List<MailIndexEntry> _candidates = new List<MailIndexEntry>();

        public bool Modified { get; private set; }

        public CleanupWindow(ArchiveStore store)
        {
            InitializeComponent();
            _store = store;
            CmbAccount.ItemsSource = _store.Accounts;
            if (_store.Accounts.Count > 0)
            {
                CmbAccount.SelectedIndex = 0;
            }
            Recalculate();
        }

        private void FilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            Recalculate();
        }

        private void Recalculate()
        {
            AccountConfig acc = CmbAccount.SelectedItem as AccountConfig;
            if (acc == null)
            {
                _candidates = new List<MailIndexEntry>();
                GridCandidates.ItemsSource = _candidates;
                TxtSummary.Text = "Нет добавленных аккаунтов.";
                BtnRun.IsEnabled = false;
                return;
            }

            int ageDays = GetSelectedTagInt(CmbAge);
            long minBytes = GetSelectedTagLong(CmbSize);
            DateTime cutoffUtc = ageDays > 0 ? DateTime.UtcNow.AddDays(-ageDays) : DateTime.MaxValue;

            _candidates = _store.Entries
                .Where(e =>
                    e.AccountId == acc.Id &&
                    e.OnServer &&
                    e.Uid > 0 &&
                    e.DateUtc <= cutoffUtc &&
                    e.SizeBytes >= minBytes &&
                    _store.VerifyLocalCopy(e))
                .OrderByDescending(e => e.SizeBytes)
                .ToList();

            GridCandidates.ItemsSource = _candidates;
            long totalBytes = _candidates.Sum(e => e.SizeBytes);
            TxtSummary.Text = string.Format("Готово к очистке с сервера: {0} писем (~{1}) — все проверены локально.",
                _candidates.Count, AttachmentBadge.FormatSize(totalBytes));
            BtnRun.IsEnabled = _candidates.Count > 0;
        }

        private static int GetSelectedTagInt(ComboBox cmb)
        {
            ComboBoxItem item = cmb.SelectedItem as ComboBoxItem;
            int val;
            if (item != null && item.Tag != null && int.TryParse(item.Tag.ToString(), out val)) return val;
            return 0;
        }

        private static long GetSelectedTagLong(ComboBox cmb)
        {
            ComboBoxItem item = cmb.SelectedItem as ComboBoxItem;
            long val;
            if (item != null && item.Tag != null && long.TryParse(item.Tag.ToString(), out val)) return val;
            return 0;
        }

        private void AppendLog(string line)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                TxtLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine);
                TxtLog.ScrollToEnd();
            }));
        }

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            AccountConfig acc = CmbAccount.SelectedItem as AccountConfig;
            if (acc == null || _candidates.Count == 0) return;

            long totalBytes = _candidates.Sum(x => x.SizeBytes);
            if (MessageBox.Show(
                string.Format("Удалить с сервера {0} писем (~{1}) из аккаунта \"{2}\"?\n\nЛокальные копии останутся в архиве Pemail.",
                    _candidates.Count, AttachmentBadge.FormatSize(totalBytes), acc.DisplayName),
                "Подтверждение очистки сервера", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            BtnRun.IsEnabled = false;
            PrgCleanup.Visibility = Visibility.Visible;
            PrgCleanup.IsIndeterminate = true;
            bool emptyTrash = ChkEmptyTrash.IsChecked == true;
            List<MailIndexEntry> batch = new List<MailIndexEntry>(_candidates);

            try
            {
                await Task.Run(() =>
                {
                    AppendLog("Подключение к " + acc.ImapHost + "...");
                    using (ImapClient imap = new ImapClient())
                    {
                        imap.ConnectAndLogin(acc.ImapHost, acc.ImapPort, acc.Email, acc.GetPassword());
                        List<MailFolderInfo> folders = imap.ListFolders();
                        MailFolderInfo trash = folders.FirstOrDefault(f => f.IsTrash && f.IsSelectable);
                        string trashRaw = trash != null ? trash.RawName : null;

                        var byFolder = batch.GroupBy(x => x.FolderRaw);
                        int removedCount = 0;

                        foreach (var grp in byFolder)
                        {
                            string rawFolder = grp.Key;
                            List<MailIndexEntry> items = grp.ToList();
                            AppendLog("Папка " + items[0].FolderDisplay + ": удаление " + items.Count + " писем...");

                            // Удаляем порциями по 50 UID, чтобы не превысить лимит длины IMAP-команды
                            for (int i = 0; i < items.Count; i += 50)
                            {
                                List<MailIndexEntry> chunk = items.Skip(i).Take(50).ToList();
                                List<long> uids = chunk.Select(c => c.Uid).ToList();
                                imap.DeleteMessagesFromServer(rawFolder, uids, trashRaw);
                                foreach (MailIndexEntry c in chunk)
                                {
                                    c.OnServer = false;
                                    c.IsArchived = true;
                                }
                                removedCount += chunk.Count;
                            }
                        }

                        if (emptyTrash && !string.IsNullOrEmpty(trashRaw))
                        {
                            AppendLog("Очистка Корзины на сервере (" + trash.DisplayName + ")...");
                            imap.EmptyTrash(trashRaw);
                        }

                        long usedKb, limitKb;
                        if (imap.TryGetQuota(out usedKb, out limitKb))
                        {
                            acc.QuotaUsedKb = usedKb;
                            acc.QuotaLimitKb = limitKb;
                            _store.SaveAccounts();
                            AppendLog("Текущая квота: " + acc.FormatQuota());
                        }

                        _store.SaveIndex();
                        AppendLog("Готово! Удалено с сервера писем: " + removedCount + ".");
                    }
                });

                Modified = true;
                Recalculate();
            }
            catch (Exception ex)
            {
                AppendLog("ОШИБКА: " + ex.Message);
                MessageBox.Show("Ошибка при очистке: " + ex.Message, "Очистка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                PrgCleanup.Visibility = Visibility.Collapsed;
                PrgCleanup.IsIndeterminate = false;
                BtnRun.IsEnabled = _candidates.Count > 0;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
