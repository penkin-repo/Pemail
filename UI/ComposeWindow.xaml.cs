using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Pemail.Models;
using Pemail.Net;
using Pemail.Storage;

namespace Pemail.UI
{
    public partial class ComposeWindow : Window
    {
        private readonly ArchiveStore _store;
        private readonly List<string> _attachments = new List<string>();
        private readonly string _inReplyTo;
        private readonly string _references;
        private TextBox _activeRecipientBox;
        private bool _suppressSuggest;

        public bool SentSuccessfully { get; private set; }

        public ComposeWindow(ArchiveStore store, AccountConfig defaultAccount, string to, string cc, string subject, string body, string inReplyTo, string references)
        {
            InitializeComponent();
            _store = store;
            _inReplyTo = inReplyTo;
            _references = references;

            CmbFrom.ItemsSource = _store.Accounts;
            if (defaultAccount != null)
            {
                CmbFrom.SelectedItem = defaultAccount;
            }
            else if (_store.Accounts.Count > 0)
            {
                CmbFrom.SelectedIndex = 0;
            }

            _suppressSuggest = true;
            TxtTo.Text = to ?? string.Empty;
            TxtCc.Text = cc ?? string.Empty;
            _suppressSuggest = false;

            TxtSubject.Text = subject ?? string.Empty;
            TxtBody.Text = body ?? string.Empty;
            if (!string.IsNullOrEmpty(subject)) Title = subject;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (BtnSend.IsEnabled)
                {
                    e.Handled = true;
                    PopupSuggest.IsOpen = false;
                    BtnSend_Click(BtnSend, new RoutedEventArgs());
                }
            }
        }

        private void RecipientBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _activeRecipientBox = sender as TextBox;
            UpdateSuggestions(_activeRecipientBox, false);
        }

        private void RecipientBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!LstSuggest.IsKeyboardFocusWithin && !PopupSuggest.IsMouseOver)
            {
                PopupSuggest.IsOpen = false;
            }
        }

        private void RecipientBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSuggest) return;
            TextBox tb = sender as TextBox;
            _activeRecipientBox = tb;
            UpdateSuggestions(tb, true);
        }

        private void UpdateSuggestions(TextBox tb, bool showEvenIfEmpty)
        {
            if (tb == null) return;
            string currentToken = GetCurrentRecipientToken(tb.Text);
            if (!showEvenIfEmpty && string.IsNullOrEmpty(currentToken))
            {
                // При фокусе на пустом поле «Кому» можно сразу показать недавних получателей
                if (tb != TxtTo)
                {
                    PopupSuggest.IsOpen = false;
                    return;
                }
            }

            List<ContactEntry> matches = _store.SuggestContacts(currentToken, 8);
            if (matches.Count == 0)
            {
                PopupSuggest.IsOpen = false;
                return;
            }

            LstSuggest.ItemsSource = matches;
            LstSuggest.SelectedIndex = 0;
            PopupSuggest.PlacementTarget = tb;
            PopupSuggest.MinWidth = Math.Max(320, tb.ActualWidth);
            PopupSuggest.IsOpen = true;
        }

        private static string GetCurrentRecipientToken(string fullText)
        {
            if (string.IsNullOrEmpty(fullText)) return string.Empty;
            int idx = fullText.LastIndexOfAny(new char[] { ',', ';' });
            return (idx >= 0 ? fullText.Substring(idx + 1) : fullText).Trim();
        }

        private void ApplySelectedSuggestion(ContactEntry contact)
        {
            if (contact == null || _activeRecipientBox == null) return;
            string text = _activeRecipientBox.Text ?? string.Empty;
            int idx = text.LastIndexOfAny(new char[] { ',', ';' });
            string prefix = idx >= 0 ? text.Substring(0, idx + 1) + " " : string.Empty;

            _suppressSuggest = true;
            _activeRecipientBox.Text = prefix + contact.Formatted + ", ";
            _activeRecipientBox.CaretIndex = _activeRecipientBox.Text.Length;
            _suppressSuggest = false;
            PopupSuggest.IsOpen = false;
            _activeRecipientBox.Focus();
        }

        private void RecipientBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!PopupSuggest.IsOpen) return;

            if (e.Key == Key.Down)
            {
                if (LstSuggest.Items.Count > 0)
                {
                    LstSuggest.SelectedIndex = (LstSuggest.SelectedIndex + 1) % LstSuggest.Items.Count;
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (LstSuggest.Items.Count > 0)
                {
                    LstSuggest.SelectedIndex = (LstSuggest.SelectedIndex - 1 + LstSuggest.Items.Count) % LstSuggest.Items.Count;
                }
                e.Handled = true;
            }
            else if ((e.Key == Key.Enter || e.Key == Key.Tab) && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                ContactEntry sel = LstSuggest.SelectedItem as ContactEntry;
                if (sel != null)
                {
                    ApplySelectedSuggestion(sel);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                PopupSuggest.IsOpen = false;
                e.Handled = true;
            }
        }

        private void LstSuggest_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ContactEntry sel = LstSuggest.SelectedItem as ContactEntry;
            if (sel != null)
            {
                ApplySelectedSuggestion(sel);
            }
        }

        private void BtnAttach_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Multiselect = true;
            if (dlg.ShowDialog(this) != true) return;

            foreach (string path in dlg.FileNames)
            {
                if (!_attachments.Contains(path))
                {
                    _attachments.Add(path);
                }
            }
            RebuildAttachmentChips();
        }

        private void RebuildAttachmentChips()
        {
            PanelAttachments.Children.Clear();
            foreach (string path in _attachments)
            {
                string captured = path;
                string fileName = Path.GetFileName(path);
                string shortName = AttachmentBadge.MakeShortName(fileName, 20);
                AttachmentStyleInfo st = AttachmentBadge.GetStyleForFile(fileName);

                Border chip = new Border();
                chip.Background = (Brush)new BrushConverter().ConvertFromString(st.BackgroundHex);
                chip.BorderBrush = (Brush)new BrushConverter().ConvertFromString(st.BorderHex);
                chip.BorderThickness = new Thickness(1);
                chip.CornerRadius = new CornerRadius(4);
                chip.Padding = new Thickness(6, 2, 4, 2);
                chip.Margin = new Thickness(0, 2, 6, 2);
                chip.ToolTip = path;

                StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock
                {
                    Text = shortName,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)new BrushConverter().ConvertFromString(st.ForegroundHex),
                    VerticalAlignment = VerticalAlignment.Center
                });

                Button rm = new Button
                {
                    Content = "×",
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(4, 0, 0, 0),
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = (Brush)new BrushConverter().ConvertFromString(st.ForegroundHex),
                    FontSize = 11
                };
                rm.Click += delegate
                {
                    _attachments.Remove(captured);
                    RebuildAttachmentChips();
                };
                sp.Children.Add(rm);
                chip.Child = sp;
                PanelAttachments.Children.Add(chip);
            }
        }

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            AccountConfig acc = CmbFrom.SelectedItem as AccountConfig;
            if (acc == null)
            {
                MessageBox.Show("Выберите аккаунт отправителя.", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<string> recipients = new List<string>();
            recipients.AddRange(SmtpClientEx.ParseRecipients(TxtTo.Text));
            recipients.AddRange(SmtpClientEx.ParseRecipients(TxtCc.Text));
            if (recipients.Count == 0)
            {
                MessageBox.Show("Укажите хотя бы одного получателя в поле «Кому».", "Отправка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnSend.IsEnabled = false;
            TxtStatus.Text = "Отправка...";
            bool saveServer = ChkSaveOnServer.IsChecked == true;
            string to = TxtTo.Text.Trim().TrimEnd(',', ';').Trim();
            string cc = TxtCc.Text.Trim().TrimEnd(',', ';').Trim();
            string subj = TxtSubject.Text.Trim();
            string body = TxtBody.Text;
            List<string> files = new List<string>(_attachments);

            try
            {
                await Task.Run(() =>
                {
                    byte[] rawMime = SmtpClientEx.BuildMimeMessage(acc.DisplayName, acc.Email, to, cc, subj, body, _inReplyTo, _references, files);
                    SmtpClientEx.Send(acc, recipients, rawMime);

                    // Сохраняем адреса получателей в contacts.json для будущих автоподсказок
                    _store.RecordSentContacts(to, cc);

                    string sentRaw = "Sent";
                    string sentDisplay = "Отправленные";
                    bool appended = false;

                    if (saveServer)
                    {
                        try
                        {
                            using (ImapClient imap = new ImapClient())
                            {
                                imap.ConnectAndLogin(acc.ImapHost, acc.ImapPort, acc.Email, acc.GetPassword());
                                List<MailFolderInfo> folders = imap.ListFolders();
                                MailFolderInfo sent = folders.FirstOrDefault(f => f.IsSent && f.IsSelectable);
                                if (sent != null)
                                {
                                    sentRaw = sent.RawName;
                                    sentDisplay = sent.DisplayName;
                                    if (acc.Provider != MailProvider.Gmail)
                                    {
                                        imap.AppendMessage(sent.RawName, rawMime);
                                    }
                                    appended = true;
                                }
                            }
                        }
                        catch { }
                    }

                    _store.SaveMessage(acc.Id, sentRaw, sentDisplay, 0, 0, rawMime, true, appended, !appended);
                    _store.SaveIndex();
                });

                SentSuccessfully = true;
                Close();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = string.Empty;
                MessageBox.Show("Ошибка отправки: " + ex.Message, "SMTP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnSend.IsEnabled = true;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
