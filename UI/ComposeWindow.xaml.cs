using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
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
        private bool _suppressListSelection;
        private bool _isBatchSending;
        private volatile bool _cancelBatchRequested;

        public bool SentSuccessfully { get; private set; }
        public int SentCount { get; private set; }

        public ComposeWindow(ArchiveStore store, AccountConfig defaultAccount, string to, string cc, string subject, string body, string inReplyTo, string references, bool startInBatchMode = false)
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

            RefreshSavedListsCombo(null);

            if (startInBatchMode)
            {
                TglBatch.IsChecked = true;
                ApplyBatchModeUi(true);
            }
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

        private void TglBatch_Click(object sender, RoutedEventArgs e)
        {
            ApplyBatchModeUi(TglBatch.IsChecked == true);
        }

        private void ApplyBatchModeUi(bool isBatch)
        {
            PopupSuggest.IsOpen = false;
            if (isBatch)
            {
                LblTo.Text = "Список:";
                LblTo.VerticalAlignment = VerticalAlignment.Top;
                LblTo.Margin = new Thickness(0, 5, 0, 0);
                TxtTo.Visibility = Visibility.Collapsed;
                PanelBatchTo.Visibility = Visibility.Visible;
                LblCc.Visibility = Visibility.Collapsed;
                TxtCc.Visibility = Visibility.Collapsed;

                if (string.IsNullOrWhiteSpace(TxtBatchTo.Text) && !string.IsNullOrWhiteSpace(TxtTo.Text))
                {
                    int ignored;
                    List<string> moved = ParseBatchRecipients(TxtTo.Text, out ignored);
                    if (moved.Count > 0)
                    {
                        TxtBatchTo.Text = string.Join(Environment.NewLine, moved);
                    }
                }

                TxtBtnSend.Text = "Разослать по списку";
                BtnSend.ToolTip = "Отправить каждому адресату по очереди отдельным письмом (Ctrl+Enter)";
                if (string.IsNullOrWhiteSpace(TxtSubject.Text))
                {
                    Title = "Рассылка по списку";
                }
                if (Height < 600) Height = 600;
                UpdateBatchCountUi();
            }
            else
            {
                LblTo.Text = "Кому:";
                LblTo.VerticalAlignment = VerticalAlignment.Center;
                LblTo.Margin = new Thickness(0);
                TxtTo.Visibility = Visibility.Visible;
                PanelBatchTo.Visibility = Visibility.Collapsed;
                LblCc.Visibility = Visibility.Visible;
                TxtCc.Visibility = Visibility.Visible;

                if (string.IsNullOrWhiteSpace(TxtTo.Text) && !string.IsNullOrWhiteSpace(TxtBatchTo.Text))
                {
                    int ignored;
                    List<string> moved = ParseBatchRecipients(TxtBatchTo.Text, out ignored);
                    if (moved.Count > 0)
                    {
                        _suppressSuggest = true;
                        TxtTo.Text = string.Join(", ", moved);
                        _suppressSuggest = false;
                    }
                }

                TxtBtnSend.Text = "Отправить (Ctrl+Enter)";
                BtnSend.ToolTip = "Отправить письмо (Ctrl+Enter)";
                if (Title == "Рассылка по списку")
                {
                    Title = "Новое письмо";
                }
            }
        }

        private void TxtBatchTo_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtBatchHint.Visibility = string.IsNullOrEmpty(TxtBatchTo.Text) ? Visibility.Visible : Visibility.Collapsed;
            UpdateBatchCountUi();
        }

        private void UpdateBatchCountUi()
        {
            int invalidLines;
            List<string> list = ParseBatchRecipients(TxtBatchTo.Text, out invalidLines);
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Адресатов: {0} (каждый получит отдельное письмо)", list.Count);
            if (invalidLines > 0)
            {
                sb.AppendFormat(" · без email: {0}", invalidLines);
            }
            if (list.Count > 50)
            {
                sb.Append(" · Внимание: >50 писем подряд может ограничить провайдер");
                TxtBatchCount.SetResourceReference(TextBlock.ForegroundProperty, "DangerBtnFg");
            }
            else
            {
                TxtBatchCount.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            }
            TxtBatchCount.Text = sb.ToString();
        }

        private static List<string> ParseBatchRecipients(string raw, out int invalidLines)
        {
            invalidLines = 0;
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                bool lineHadValid = false;
                string[] cells = line.Split(new char[] { ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string rawCell in cells)
                {
                    string addr = ExtractValidEmailFromCell(rawCell);
                    if (!string.IsNullOrEmpty(addr))
                    {
                        lineHadValid = true;
                        if (seen.Add(addr))
                        {
                            result.Add(addr);
                        }
                    }
                }

                if (!lineHadValid)
                {
                    invalidLines++;
                }
            }

            return result;
        }

        private static string ExtractValidEmailFromCell(string cell)
        {
            if (string.IsNullOrWhiteSpace(cell)) return null;
            string extracted = SmtpClientEx.ExtractEmailAddress(cell);
            if (IsValidEmail(extracted)) return extracted;

            string[] tokens = cell.Split(new char[] { ' ', '\t', '"', '\'', '(', ')', '<', '>' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string tok in tokens)
            {
                string clean = tok.Trim('.', ',', ';', ':');
                if (IsValidEmail(clean)) return clean;
            }
            return null;
        }

        private static bool IsValidEmail(string addr)
        {
            if (string.IsNullOrWhiteSpace(addr)) return false;
            if (addr.IndexOfAny(new char[] { ' ', '\t', '\r', '\n', '<', '>', ',', ';' }) >= 0) return false;
            int at = addr.IndexOf('@');
            if (at <= 0 || at != addr.LastIndexOf('@') || at >= addr.Length - 3) return false;
            string domain = addr.Substring(at + 1);
            int dot = domain.IndexOf('.');
            if (dot <= 0 || dot >= domain.Length - 1) return false;
            if (domain.EndsWith(".")) return false;
            return true;
        }

        private void RefreshSavedListsCombo(string selectId)
        {
            _suppressListSelection = true;
            CmbSavedLists.ItemsSource = null;
            List<MailingListEntry> items = new List<MailingListEntry>(_store.MailingLists);
            CmbSavedLists.ItemsSource = items;
            if (!string.IsNullOrEmpty(selectId))
            {
                MailingListEntry match = items.FirstOrDefault(x => x.Id == selectId);
                CmbSavedLists.SelectedItem = match;
            }
            else
            {
                CmbSavedLists.SelectedItem = null;
            }
            BtnDeleteList.IsEnabled = CmbSavedLists.SelectedItem != null;
            _suppressListSelection = false;
        }

        private void CmbSavedLists_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressListSelection) return;
            MailingListEntry sel = CmbSavedLists.SelectedItem as MailingListEntry;
            BtnDeleteList.IsEnabled = sel != null;
            if (sel == null) return;

            List<string> list = sel.Recipients ?? new List<string>();
            TxtBatchTo.Text = string.Join(Environment.NewLine, list);
            TxtStatus.Text = string.Format("Загружен список «{0}» ({1})", sel.Name, list.Count);
        }

        private void BtnSaveList_Click(object sender, RoutedEventArgs e)
        {
            int ignored;
            List<string> recipients = ParseBatchRecipients(TxtBatchTo.Text, out ignored);
            if (recipients.Count == 0)
            {
                MessageBox.Show("Сначала добавьте хотя бы один корректный email в поле списка.", "Сохранение списка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MailingListEntry currentSel = CmbSavedLists.SelectedItem as MailingListEntry;
            string defaultName = currentSel != null ? currentSel.Name : string.Empty;
            string enteredName = PromptListName(defaultName, recipients.Count);
            if (string.IsNullOrWhiteSpace(enteredName)) return;

            string cleanName = enteredName.Trim();
            MailingListEntry existingByName = _store.MailingLists.FirstOrDefault(x => string.Equals(x.Name, cleanName, StringComparison.OrdinalIgnoreCase));
            if (existingByName != null && (currentSel == null || existingByName.Id != currentSel.Id))
            {
                MessageBoxResult overwrite = MessageBox.Show(
                    string.Format("Список «{0}» уже существует ({1} адресов). Перезаписать его текущим списком ({2} адресов)?",
                        existingByName.Name,
                        existingByName.Recipients != null ? existingByName.Recipients.Count : 0,
                        recipients.Count),
                    "Перезапись списка",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.Yes) return;
            }

            MailingListEntry saved = _store.UpsertMailingList(cleanName, recipients);
            RefreshSavedListsCombo(saved != null ? saved.Id : null);
            TxtStatus.Text = string.Format("Список «{0}» сохранён ({1})", cleanName, recipients.Count);
        }

        private void BtnDeleteList_Click(object sender, RoutedEventArgs e)
        {
            MailingListEntry sel = CmbSavedLists.SelectedItem as MailingListEntry;
            if (sel == null) return;

            MessageBoxResult res = MessageBox.Show(
                string.Format("Удалить сохранённый список «{0}»?", sel.Name),
                "Удаление списка",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            _store.DeleteMailingList(sel.Id);
            RefreshSavedListsCombo(null);
            TxtStatus.Text = "Список удалён.";
        }

        private string PromptListName(string initialName, int recipientCount)
        {
            Window dlg = new Window
            {
                Title = "Сохранить список рассылки",
                Owner = this,
                Width = 390,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };
            dlg.SetResourceReference(Window.BackgroundProperty, "BgWindow");
            dlg.SetResourceReference(Window.ForegroundProperty, "TextPrimary");

            StackPanel root = new StackPanel { Margin = new Thickness(14) };
            TextBlock label = new TextBlock
            {
                Text = string.Format("Название списка ({0} адресов):", recipientCount),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

            TextBox txt = new TextBox
            {
                Text = initialName ?? string.Empty,
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 0, 6, 0),
                FontSize = 12.5
            };

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Button btnOk = new Button
            {
                Content = "Сохранить",
                IsDefault = true,
                MinWidth = 88,
                Margin = new Thickness(0, 0, 6, 0)
            };
            btnOk.Style = (Style)FindResource("PrimaryButton");
            btnOk.Click += delegate
            {
                if (string.IsNullOrWhiteSpace(txt.Text))
                {
                    MessageBox.Show(dlg, "Введите название списка.", "Сохранение списка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    txt.Focus();
                    return;
                }
                dlg.DialogResult = true;
            };

            Button btnCancel = new Button
            {
                Content = "Отмена",
                IsCancel = true,
                MinWidth = 76
            };

            buttons.Children.Add(btnOk);
            buttons.Children.Add(btnCancel);

            root.Children.Add(label);
            root.Children.Add(txt);
            root.Children.Add(buttons);
            dlg.Content = root;

            dlg.Loaded += delegate
            {
                txt.Focus();
                txt.SelectAll();
            };

            return dlg.ShowDialog() == true ? txt.Text : null;
        }

        private void SetBatchUiBusy(bool busy)
        {
            BtnSend.IsEnabled = !busy;
            TglBatch.IsEnabled = !busy;
            CmbFrom.IsEnabled = !busy;
            TxtBatchTo.IsEnabled = !busy;
            CmbSavedLists.IsEnabled = !busy;
            BtnSaveList.IsEnabled = !busy;
            BtnDeleteList.IsEnabled = !busy && CmbSavedLists.SelectedItem != null;
            CmbBatchDelay.IsEnabled = !busy;
            TxtSubject.IsEnabled = !busy;
            TxtBody.IsEnabled = !busy;
            ChkSaveOnServer.IsEnabled = !busy;
            BtnCancel.Content = busy ? "Остановить" : "Отмена";
            BtnCancel.IsEnabled = true;
        }

        private void GetSelectedDelayRange(out int minMs, out int maxMs, out string label)
        {
            minMs = 3000;
            maxMs = 6000;
            label = "3–6 сек";
            ComboBoxItem item = CmbBatchDelay.SelectedItem as ComboBoxItem;
            if (item == null) return;
            if (item.Content != null) label = item.Content.ToString();
            string tag = item.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;
            string[] parts = tag.Split('-');
            int a, b;
            if (parts.Length == 2 && int.TryParse(parts[0], out a) && int.TryParse(parts[1], out b) && a >= 0 && b >= a)
            {
                minMs = a;
                maxMs = b;
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

            if (TglBatch.IsChecked == true)
            {
                await SendBatchAsync(acc);
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
                SentCount = 1;
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

        private async Task SendBatchAsync(AccountConfig acc)
        {
            int invalidLines;
            List<string> recipients = ParseBatchRecipients(TxtBatchTo.Text, out invalidLines);
            if (recipients.Count == 0)
            {
                MessageBox.Show("Добавьте хотя бы один корректный email в список получателей.", "Рассылка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string subj = TxtSubject.Text.Trim();
            if (string.IsNullOrEmpty(subj))
            {
                MessageBoxResult noSubj = MessageBox.Show(
                    "Тема письма не указана. Письма без темы чаще попадают в спам.\n\nПродолжить рассылку без темы?",
                    "Рассылка",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (noSubj != MessageBoxResult.Yes) return;
            }

            int minMs;
            int maxMs;
            string delayLabel;
            GetSelectedDelayRange(out minMs, out maxMs, out delayLabel);

            string confirmText = string.Format(
                "Отправить {0} отдельных писем с аккаунта {1}?\n\n" +
                "• Каждый адресат получит личное письмо (без копии и скрытых адресов).\n" +
                "• Случайная пауза между письмами: {2}.",
                recipients.Count, acc.Email, delayLabel);
            if (invalidLines > 0)
            {
                confirmText += string.Format("\n• Строк без email (будут пропущены): {0}.", invalidLines);
            }
            if (MessageBox.Show(confirmText, "Подтверждение рассылки", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            bool saveServer = ChkSaveOnServer.IsChecked == true;
            string body;
            string htmlBody;
            ExportDocument(TxtBody.Document, out body, out htmlBody);
            List<string> files = new List<string>(_attachments);

            _isBatchSending = true;
            _cancelBatchRequested = false;
            SetBatchUiBusy(true);
            TxtStatus.Text = "Подключение к SMTP...";

            int sentNow = 0;
            List<string> failedDetails = new List<string>();
            List<string> skippedByCancel = new List<string>();
            string fatalError = null;

            try
            {
                await Task.Run(() =>
                {
                    Random rng = new Random();
                    string sentRaw = "Sent";
                    string sentDisplay = "Отправленные";
                    bool hasServerSentFolder = false;
                    ImapClient imap = null;

                    try
                    {
                        if (saveServer)
                        {
                            try
                            {
                                imap = new ImapClient();
                                imap.ConnectAndLogin(acc.ImapHost, acc.ImapPort, acc.Email, acc.GetPassword());
                                List<MailFolderInfo> folders = imap.ListFolders();
                                MailFolderInfo sentFolder = folders.FirstOrDefault(f => f.IsSent && f.IsSelectable);
                                if (sentFolder != null)
                                {
                                    sentRaw = sentFolder.RawName;
                                    sentDisplay = sentFolder.DisplayName;
                                    hasServerSentFolder = true;
                                }
                                if (acc.Provider == MailProvider.Gmail)
                                {
                                    // Gmail сам помещает отправленные через SMTP письма в Sent
                                    imap.Dispose();
                                    imap = null;
                                }
                            }
                            catch
                            {
                                if (imap != null) { try { imap.Dispose(); } catch { } imap = null; }
                            }
                        }

                        using (SmtpClientEx.SmtpSession smtp = new SmtpClientEx.SmtpSession())
                        {
                            smtp.ConnectAndAuth(acc);

                            for (int i = 0; i < recipients.Count; i++)
                            {
                                if (_cancelBatchRequested)
                                {
                                    for (int rem = i; rem < recipients.Count; rem++)
                                    {
                                        skippedByCancel.Add(recipients[rem]);
                                    }
                                    break;
                                }

                                string rcpt = recipients[i];
                                int currentNum = i + 1;
                                Dispatcher.BeginInvoke(new Action(delegate
                                {
                                    TxtStatus.Text = string.Format("Отправка {0} из {1}: {2}", currentNum, recipients.Count, rcpt);
                                }));

                                byte[] rawMime = SmtpClientEx.BuildMimeMessage(
                                    acc.DisplayName, acc.Email, rcpt, string.Empty,
                                    subj, body, null, null, files, htmlBody);

                                List<string> singleRcpt = new List<string> { rcpt };
                                bool delivered = false;
                                Exception lastEx = null;

                                for (int attempt = 0; attempt < 2 && !delivered; attempt++)
                                {
                                    try
                                    {
                                        if (!smtp.IsConnected)
                                        {
                                            smtp.ConnectAndAuth(acc);
                                        }
                                        smtp.SendMessage(acc.Email, singleRcpt, rawMime);
                                        delivered = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        lastEx = ex;
                                        string msg = ex.Message ?? string.Empty;
                                        // При обрыве соединения переподключаемся 1 раз; при отказе получателя (5xx) не повторяем
                                        bool isPermanentSmtpReject = msg.IndexOf(": 5", StringComparison.Ordinal) >= 0;
                                        if (attempt == 0 && !isPermanentSmtpReject)
                                        {
                                            try { smtp.ConnectAndAuth(acc); }
                                            catch { }
                                        }
                                        else
                                        {
                                            smtp.TryReset();
                                            break;
                                        }
                                    }
                                }

                                if (delivered)
                                {
                                    sentNow++;
                                    _store.RecordSentContacts(rcpt, string.Empty);

                                    bool onServer = false;
                                    if (saveServer)
                                    {
                                        if (acc.Provider == MailProvider.Gmail)
                                        {
                                            onServer = hasServerSentFolder;
                                        }
                                        else if (hasServerSentFolder)
                                        {
                                            try
                                            {
                                                if (imap == null)
                                                {
                                                    imap = new ImapClient();
                                                    imap.ConnectAndLogin(acc.ImapHost, acc.ImapPort, acc.Email, acc.GetPassword());
                                                }
                                                imap.AppendMessage(sentRaw, rawMime);
                                                onServer = true;
                                            }
                                            catch
                                            {
                                                if (imap != null) { try { imap.Dispose(); } catch { } imap = null; }
                                            }
                                        }
                                    }

                                    _store.SaveMessage(acc.Id, sentRaw, sentDisplay, 0, 0, rawMime, true, onServer, !onServer);
                                }
                                else
                                {
                                    failedDetails.Add(rcpt + " — " + (lastEx != null ? lastEx.Message : "ошибка SMTP"));
                                }

                                // Антиспам-пауза перед следующим письмом (с проверкой отмены каждые 200 мс)
                                if (i < recipients.Count - 1 && !_cancelBatchRequested)
                                {
                                    int delayMs = maxMs > minMs ? rng.Next(minMs, maxMs + 1) : minMs;
                                    int waited = 0;
                                    int nextNum = i + 2;
                                    while (waited < delayMs && !_cancelBatchRequested)
                                    {
                                        int remSec = (int)Math.Ceiling((delayMs - waited) / 1000.0);
                                        Dispatcher.BeginInvoke(new Action(delegate
                                        {
                                            TxtStatus.Text = string.Format("Отправлено {0}/{1} · пауза {2} с перед №{3}...", sentNow, recipients.Count, remSec, nextNum);
                                        }));
                                        int step = Math.Min(200, delayMs - waited);
                                        Thread.Sleep(step);
                                        waited += step;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        fatalError = ex.Message;
                    }
                    finally
                    {
                        if (imap != null) { try { imap.Dispose(); } catch { } }
                        if (sentNow > 0)
                        {
                            _store.SaveIndex();
                        }
                    }
                });
            }
            finally
            {
                _isBatchSending = false;
                SetBatchUiBusy(false);
            }

            if (sentNow > 0)
            {
                SentSuccessfully = true;
                SentCount += sentNow;
            }

            if (!string.IsNullOrEmpty(fatalError))
            {
                TxtStatus.Text = string.Format("Остановлено с ошибкой (отправлено: {0})", sentNow);
                MessageBox.Show("Ошибка SMTP при рассылке: " + fatalError + (sentNow > 0 ? "\n\nУспешно отправлено до ошибки: " + sentNow : string.Empty),
                    "Рассылка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (failedDetails.Count == 0 && skippedByCancel.Count == 0)
            {
                TxtStatus.Text = string.Format("Готово: отправлено {0}", sentNow);
                MessageBox.Show(string.Format("Рассылка завершена! Успешно отправлено писем: {0}.", sentNow),
                    "Рассылка", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
                return;
            }

            StringBuilder report = new StringBuilder();
            report.AppendFormat("Отправлено успешно: {0} из {1}\n", sentNow, recipients.Count);
            if (skippedByCancel.Count > 0)
            {
                report.AppendFormat("Отменено пользователем: {0}\n", skippedByCancel.Count);
            }
            if (failedDetails.Count > 0)
            {
                report.AppendFormat("\nОшибки ({0}):\n", failedDetails.Count);
                int showMax = Math.Min(failedDetails.Count, 12);
                for (int i = 0; i < showMax; i++)
                {
                    report.AppendLine("• " + failedDetails[i]);
                }
                if (failedDetails.Count > showMax)
                {
                    report.AppendFormat("... и ещё {0}\n", failedDetails.Count - showMax);
                }
            }

            TxtStatus.Text = string.Format("Отправлено: {0}, ошибок: {1}", sentNow, failedDetails.Count);
            MessageBox.Show(report.ToString(), "Итог рассылки", MessageBoxButton.OK,
                failedDetails.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isBatchSending)
            {
                e.Cancel = true;
                _cancelBatchRequested = true;
                TxtStatus.Text = "Остановка после текущего письма...";
                BtnCancel.IsEnabled = false;
                return;
            }
            base.OnClosing(e);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_isBatchSending)
            {
                _cancelBatchRequested = true;
                TxtStatus.Text = "Остановка после текущего письма...";
                BtnCancel.IsEnabled = false;
                return;
            }
            Close();
        }
    }
}
