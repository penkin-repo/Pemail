using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
            SetPlainTextBody(body ?? string.Empty);
            DataObject.AddPastingHandler(TxtBody, TxtBody_Pasting);
            if (!string.IsNullOrEmpty(subject)) Title = subject;
        }

        private void SetPlainTextBody(string text)
        {
            TxtBody.Document.Blocks.Clear();
            string normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Paragraph p = new Paragraph();
                if (!string.IsNullOrEmpty(lines[i]))
                {
                    p.Inlines.Add(new Run(lines[i]));
                }
                TxtBody.Document.Blocks.Add(p);
            }
            if (TxtBody.Document.Blocks.Count == 0)
            {
                TxtBody.Document.Blocks.Add(new Paragraph());
            }
            TxtBody.CaretPosition = TxtBody.Document.ContentStart;
        }

        private void TxtBody_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.V &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                if (Clipboard.ContainsText())
                {
                    string plain = Clipboard.GetText();
                    TxtBody.BeginChange();
                    try
                    {
                        TxtBody.Selection.Text = plain ?? string.Empty;
                        TxtBody.CaretPosition = TxtBody.Selection.End;
                    }
                    finally
                    {
                        TxtBody.EndChange();
                    }
                    e.Handled = true;
                }
            }
        }

        private void TxtBody_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            string rawText = null;
            if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
            {
                rawText = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            }
            else if (e.DataObject.GetDataPresent(DataFormats.Text))
            {
                rawText = e.DataObject.GetData(DataFormats.Text) as string;
            }

            if (string.IsNullOrEmpty(rawText)) return;

            int colCount;
            List<List<string>> rows = ParseExcelTsv(rawText, out colCount);
            if (rows != null && rows.Count >= 1 && colCount >= 2)
            {
                e.CancelCommand();
                InsertTableAtCaret(rows, colCount);
                TxtStatus.Text = string.Format("Вставлена таблица {0}×{1}", rows.Count, colCount);
                return;
            }

            // Для обычного текста вставляем чистый Unicode без посторонних шрифтов/фонов
            if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
            {
                e.FormatToApply = DataFormats.UnicodeText;
            }
        }

        private static List<List<string>> ParseExcelTsv(string raw, out int maxCols)
        {
            maxCols = 0;
            if (string.IsNullOrEmpty(raw) || raw.IndexOf('\t') < 0) return null;

            List<List<string>> rows = new List<List<string>>();
            List<string> currentRow = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool inQuotes = false;
            bool atCellStart = true;

            int len = raw.Length;
            for (int i = 0; i < len; i++)
            {
                char c = raw[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < len && raw[i + 1] == '"')
                        {
                            cell.Append('"');
                            i++;
                        }
                        else if (i + 1 >= len || raw[i + 1] == '\t' || raw[i + 1] == '\r' || raw[i + 1] == '\n')
                        {
                            inQuotes = false;
                        }
                        else
                        {
                            cell.Append('"');
                        }
                    }
                    else
                    {
                        cell.Append(c);
                    }
                }
                else
                {
                    if (atCellStart && c == '"')
                    {
                        inQuotes = true;
                        atCellStart = false;
                    }
                    else if (c == '\t')
                    {
                        currentRow.Add(cell.ToString().Trim());
                        cell.Clear();
                        atCellStart = true;
                    }
                    else if (c == '\r' || c == '\n')
                    {
                        if (c == '\r' && i + 1 < len && raw[i + 1] == '\n') i++;
                        currentRow.Add(cell.ToString().Trim());
                        cell.Clear();
                        rows.Add(currentRow);
                        currentRow = new List<string>();
                        atCellStart = true;
                    }
                    else
                    {
                        cell.Append(c);
                        atCellStart = false;
                    }
                }
            }

            if (cell.Length > 0 || currentRow.Count > 0)
            {
                currentRow.Add(cell.ToString().Trim());
                rows.Add(currentRow);
            }

            // Убираем пустые завершающие строки, которые Excel всегда добавляет в конец буфера
            while (rows.Count > 0 && rows[rows.Count - 1].All(s => string.IsNullOrEmpty(s)))
            {
                rows.RemoveAt(rows.Count - 1);
            }

            if (rows.Count == 0) return null;

            foreach (List<string> r in rows)
            {
                if (r.Count > maxCols) maxCols = r.Count;
            }
            if (maxCols < 2) return null;

            // Не превращаем в таблицу одиночную строку с ведущим отступом табом ("\tтекст")
            if (rows.Count == 1 && rows[0].Count == 2 && string.IsNullOrEmpty(rows[0][0]))
            {
                return null;
            }

            foreach (List<string> r in rows)
            {
                while (r.Count < maxCols) r.Add(string.Empty);
            }

            return rows;
        }

        private void InsertTableAtCaret(List<List<string>> rows, int colCount)
        {
            // Оцениваем относительную ширину колонок по длине текста
            double[] weights = new double[colCount];
            for (int c = 0; c < colCount; c++)
            {
                int maxLen = 4;
                for (int r = 0; r < rows.Count; r++)
                {
                    string val = rows[r][c] ?? string.Empty;
                    int lineLen = 0;
                    foreach (string line in val.Split('\n'))
                    {
                        if (line.Length > lineLen) lineLen = line.Length;
                    }
                    if (lineLen > maxLen) maxLen = lineLen;
                }
                if (maxLen > 60) maxLen = 60;
                weights[c] = maxLen;
            }

            Table table = new Table();
            table.CellSpacing = 0;
            table.Margin = new Thickness(0, 6, 0, 6);
            table.BorderThickness = new Thickness(1, 1, 0, 0);
            table.SetResourceReference(Table.BorderBrushProperty, "BorderInput");

            for (int c = 0; c < colCount; c++)
            {
                TableColumn col = new TableColumn();
                col.Width = new GridLength(weights[c], GridUnitType.Star);
                table.Columns.Add(col);
            }

            TableRowGroup group = new TableRowGroup();
            for (int r = 0; r < rows.Count; r++)
            {
                TableRow tr = new TableRow();
                for (int c = 0; c < colCount; c++)
                {
                    TableCell tc = new TableCell();
                    tc.BorderThickness = new Thickness(0, 0, 1, 1);
                    tc.SetResourceReference(TableCell.BorderBrushProperty, "BorderInput");
                    tc.Padding = new Thickness(8, 5, 8, 5);

                    string val = (rows[r][c] ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
                    string[] cellLines = val.Split('\n');
                    Paragraph cellPara = new Paragraph();
                    cellPara.Margin = new Thickness(0);
                    for (int li = 0; li < cellLines.Length; li++)
                    {
                        if (li > 0) cellPara.Inlines.Add(new LineBreak());
                        if (!string.IsNullOrEmpty(cellLines[li]))
                        {
                            cellPara.Inlines.Add(new Run(cellLines[li]));
                        }
                    }
                    tc.Blocks.Add(cellPara);
                    tr.Cells.Add(tc);
                }
                group.Rows.Add(tr);
            }
            table.RowGroups.Add(group);

            TxtBody.BeginChange();
            try
            {
                if (!TxtBody.Selection.IsEmpty)
                {
                    TxtBody.Selection.Text = string.Empty;
                }

                BlockCollection blocks = TxtBody.Document.Blocks;
                Block topBlock = FindTopLevelBlock(TxtBody.CaretPosition);
                Paragraph afterPara = null;

                Paragraph curPara = topBlock as Paragraph;
                if (curPara != null)
                {
                    string paraText = new TextRange(curPara.ContentStart, curPara.ContentEnd).Text;
                    if (string.IsNullOrEmpty(paraText))
                    {
                        blocks.InsertBefore(curPara, table);
                        afterPara = curPara;
                    }
                    else
                    {
                        bool atStart = TxtBody.CaretPosition.CompareTo(curPara.ContentStart) <= 0;
                        bool atEnd = TxtBody.CaretPosition.CompareTo(curPara.ContentEnd) >= 0;
                        if (atStart)
                        {
                            blocks.InsertBefore(curPara, table);
                            afterPara = curPara;
                        }
                        else if (atEnd)
                        {
                            blocks.InsertAfter(curPara, table);
                            afterPara = table.NextBlock as Paragraph;
                            if (afterPara == null)
                            {
                                afterPara = new Paragraph();
                                blocks.InsertAfter(table, afterPara);
                            }
                        }
                        else
                        {
                            TextPointer newStart = TxtBody.CaretPosition.InsertParagraphBreak();
                            Paragraph splitRight = FindTopLevelBlock(newStart) as Paragraph;
                            if (splitRight != null)
                            {
                                blocks.InsertBefore(splitRight, table);
                                afterPara = splitRight;
                            }
                            else
                            {
                                blocks.InsertAfter(curPara, table);
                                afterPara = new Paragraph();
                                blocks.InsertAfter(table, afterPara);
                            }
                        }
                    }
                }
                else if (topBlock != null)
                {
                    blocks.InsertAfter(topBlock, table);
                    afterPara = table.NextBlock as Paragraph;
                    if (afterPara == null)
                    {
                        afterPara = new Paragraph();
                        blocks.InsertAfter(table, afterPara);
                    }
                }
                else
                {
                    blocks.Add(table);
                    afterPara = new Paragraph();
                    blocks.Add(afterPara);
                }

                if (afterPara != null)
                {
                    TxtBody.CaretPosition = afterPara.ContentStart;
                }
            }
            finally
            {
                TxtBody.EndChange();
            }
            TxtBody.Focus();
        }

        private Block FindTopLevelBlock(TextPointer pointer)
        {
            if (pointer == null) return null;
            DependencyObject node = pointer.Parent;
            Block lastBlock = null;
            while (node != null && node != TxtBody.Document)
            {
                Block b = node as Block;
                if (b != null) lastBlock = b;
                node = LogicalTreeHelper.GetParent(node);
            }
            return lastBlock;
        }

        private static void ExportDocument(FlowDocument doc, out string plainText, out string htmlBody)
        {
            StringBuilder plainSb = new StringBuilder();
            StringBuilder htmlSb = new StringBuilder();
            bool hasRich = false;

            foreach (Block block in doc.Blocks)
            {
                AppendBlockExport(block, plainSb, htmlSb, ref hasRich);
            }

            plainText = plainSb.ToString().TrimEnd('\r', '\n');
            if (hasRich)
            {
                htmlBody = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head>" +
                           "<body style=\"margin:0;padding:0;\">" +
                           "<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:13px;line-height:1.45;\">" +
                           htmlSb.ToString() +
                           "</div></body></html>";
            }
            else
            {
                htmlBody = null;
            }
        }

        private static void AppendBlockExport(Block block, StringBuilder plainSb, StringBuilder htmlSb, ref bool hasRich)
        {
            Paragraph p = block as Paragraph;
            if (p != null)
            {
                StringBuilder pPlain = new StringBuilder();
                StringBuilder pHtml = new StringBuilder();
                AppendInlinesExport(p.Inlines, pPlain, pHtml, ref hasRich);
                plainSb.AppendLine(pPlain.ToString());
                if (pHtml.Length == 0)
                {
                    htmlSb.Append("<p style=\"margin:0 0 8px 0;\">&nbsp;</p>");
                }
                else
                {
                    htmlSb.Append("<p style=\"margin:0 0 8px 0;\">").Append(pHtml.ToString()).Append("</p>");
                }
                return;
            }

            Table tbl = block as Table;
            if (tbl != null)
            {
                hasRich = true;
                htmlSb.Append("<table cellpadding=\"0\" cellspacing=\"0\" style=\"border-collapse:collapse;margin:8px 0;font-family:Segoe UI,Arial,sans-serif;font-size:13px;\">");
                foreach (TableRowGroup grp in tbl.RowGroups)
                {
                    foreach (TableRow row in grp.Rows)
                    {
                        htmlSb.Append("<tr>");
                        List<string> plainCells = new List<string>();
                        foreach (TableCell cell in row.Cells)
                        {
                            StringBuilder cellPlain = new StringBuilder();
                            StringBuilder cellHtml = new StringBuilder();
                            int bIdx = 0;
                            foreach (Block cb in cell.Blocks)
                            {
                                Paragraph cp = cb as Paragraph;
                                if (cp != null)
                                {
                                    if (bIdx > 0)
                                    {
                                        cellPlain.Append(" / ");
                                        cellHtml.Append("<br>");
                                    }
                                    AppendInlinesExport(cp.Inlines, cellPlain, cellHtml, ref hasRich);
                                    bIdx++;
                                }
                            }
                            plainCells.Add(cellPlain.ToString().Trim());
                            string cellContent = cellHtml.Length > 0 ? cellHtml.ToString() : "&nbsp;";
                            htmlSb.Append("<td style=\"border:1px solid #cbd5e1;padding:6px 10px;vertical-align:top;text-align:left;\">")
                                  .Append(cellContent)
                                  .Append("</td>");
                        }
                        plainSb.AppendLine(string.Join(" | ", plainCells));
                        htmlSb.Append("</tr>");
                    }
                }
                htmlSb.Append("</table>");
                return;
            }

            System.Windows.Documents.List list = block as System.Windows.Documents.List;
            if (list != null)
            {
                hasRich = true;
                htmlSb.Append("<ul style=\"margin:4px 0 8px 20px;padding:0;\">");
                foreach (ListItem li in list.ListItems)
                {
                    StringBuilder liPlain = new StringBuilder();
                    StringBuilder liHtml = new StringBuilder();
                    foreach (Block lb in li.Blocks)
                    {
                        Paragraph lp = lb as Paragraph;
                        if (lp != null)
                        {
                            AppendInlinesExport(lp.Inlines, liPlain, liHtml, ref hasRich);
                        }
                    }
                    plainSb.AppendLine("• " + liPlain.ToString());
                    htmlSb.Append("<li>").Append(liHtml.ToString()).Append("</li>");
                }
                htmlSb.Append("</ul>");
            }
        }

        private static void AppendInlinesExport(InlineCollection inlines, StringBuilder plainSb, StringBuilder htmlSb, ref bool hasRich)
        {
            foreach (Inline inline in inlines)
            {
                Run run = inline as Run;
                if (run != null)
                {
                    string t = run.Text ?? string.Empty;
                    plainSb.Append(t);
                    string enc = WebUtility.HtmlEncode(t);
                    bool isBold = run.FontWeight >= FontWeights.SemiBold;
                    bool isItalic = run.FontStyle == FontStyles.Italic;
                    bool isUnderline = run.TextDecorations != null && run.TextDecorations.Contains(TextDecorations.Underline[0]);
                    if (isBold || isItalic || isUnderline) hasRich = true;

                    if (isBold) htmlSb.Append("<b>");
                    if (isItalic) htmlSb.Append("<i>");
                    if (isUnderline) htmlSb.Append("<u>");
                    htmlSb.Append(enc);
                    if (isUnderline) htmlSb.Append("</u>");
                    if (isItalic) htmlSb.Append("</i>");
                    if (isBold) htmlSb.Append("</b>");
                    continue;
                }

                if (inline is LineBreak)
                {
                    plainSb.Append("\r\n");
                    htmlSb.Append("<br>");
                    continue;
                }

                Span span = inline as Span;
                if (span != null)
                {
                    bool isBold = span is Bold || span.FontWeight >= FontWeights.SemiBold;
                    bool isItalic = span is Italic || span.FontStyle == FontStyles.Italic;
                    bool isUnderline = span is Underline || (span.TextDecorations != null && span.TextDecorations.Contains(TextDecorations.Underline[0]));
                    if (isBold || isItalic || isUnderline) hasRich = true;

                    if (isBold) htmlSb.Append("<b>");
                    if (isItalic) htmlSb.Append("<i>");
                    if (isUnderline) htmlSb.Append("<u>");
                    AppendInlinesExport(span.Inlines, plainSb, htmlSb, ref hasRich);
                    if (isUnderline) htmlSb.Append("</u>");
                    if (isItalic) htmlSb.Append("</i>");
                    if (isBold) htmlSb.Append("</b>");
                }
            }
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
            string body;
            string htmlBody;
            ExportDocument(TxtBody.Document, out body, out htmlBody);
            List<string> files = new List<string>(_attachments);

            try
            {
                await Task.Run(() =>
                {
                    byte[] rawMime = SmtpClientEx.BuildMimeMessage(acc.DisplayName, acc.Email, to, cc, subj, body, _inReplyTo, _references, files, htmlBody);
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
