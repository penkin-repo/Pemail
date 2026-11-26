using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Pemail.Models;

namespace Pemail.Net
{
    public class ImapSelectResult
    {
        public long UidValidity { get; set; }
        public int Exists { get; set; }
    }

    public class ImapFetchedMessage
    {
        public long Uid { get; set; }
        public bool IsRead { get; set; }
        public byte[] RawBytes { get; set; }
    }

    public class ImapClient : IDisposable
    {
        private TcpClient _tcp;
        private SslStream _ssl;
        private int _tagCounter;

        public void ConnectAndLogin(string host, int port, string email, string password)
        {
            _tcp = new TcpClient();
            _tcp.ReceiveTimeout = 60000;
            _tcp.SendTimeout = 60000;
            _tcp.Connect(host, port);
            _ssl = new SslStream(_tcp.GetStream(), false);
            _ssl.AuthenticateAsClient(host);

            string greeting = ReadLineAscii();
            if (greeting == null || (!greeting.StartsWith("* OK", StringComparison.OrdinalIgnoreCase) && !greeting.StartsWith("* PREAUTH", StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException("Неожиданное приветствие IMAP: " + greeting);
            }

            List<string> loginResp = SendCommand("LOGIN " + QuoteImap(email) + " " + QuoteImap(password));
            EnsureOk(loginResp, "Ошибка входа IMAP (проверьте логин и пароль приложения)");
        }

        public List<MailFolderInfo> ListFolders()
        {
            List<string> lines = SendCommand("LIST \"\" \"*\"");
            EnsureOk(lines, "Не удалось получить список папок");
            List<MailFolderInfo> result = new List<MailFolderInfo>();
            Regex rx = new Regex(@"^\*\s+LIST\s+\(([^)]*)\)\s+(?:""([^""]*)""|NIL)\s+(.*)$", RegexOptions.IgnoreCase);

            foreach (string line in lines)
            {
                Match m = rx.Match(line);
                if (!m.Success) continue;
                string attrs = m.Groups[1].Value;
                string rawName = m.Groups[3].Value.Trim();
                if (rawName.StartsWith("\"") && rawName.EndsWith("\"") && rawName.Length >= 2)
                {
                    rawName = rawName.Substring(1, rawName.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
                }

                bool noSelect = attrs.IndexOf("\\Noselect", StringComparison.OrdinalIgnoreCase) >= 0;
                MailFolderInfo info = new MailFolderInfo();
                info.RawName = rawName;
                info.DisplayName = DecodeModifiedUtf7(rawName);
                info.Attributes = attrs;
                info.IsSelectable = !noSelect;
                result.Add(info);
            }
            return result;
        }

        public bool TryGetQuota(out long usedKb, out long limitKb)
        {
            usedKb = 0;
            limitKb = 0;
            try
            {
                List<string> lines = SendCommand("GETQUOTAROOT \"INBOX\"");
                Regex rx = new Regex(@"STORAGE\s+(\d+)\s+(\d+)", RegexOptions.IgnoreCase);
                foreach (string line in lines)
                {
                    Match m = rx.Match(line);
                    if (m.Success)
                    {
                        long.TryParse(m.Groups[1].Value, out usedKb);
                        long.TryParse(m.Groups[2].Value, out limitKb);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public ImapSelectResult SelectFolder(string rawFolderName, bool readOnly)
        {
            string cmd = (readOnly ? "EXAMINE " : "SELECT ") + QuoteImap(rawFolderName);
            List<string> lines = SendCommand(cmd);
            EnsureOk(lines, "Не удалось открыть папку " + rawFolderName);

            ImapSelectResult res = new ImapSelectResult();
            foreach (string line in lines)
            {
                Match mv = Regex.Match(line, @"UIDVALIDITY\s+(\d+)", RegexOptions.IgnoreCase);
                if (mv.Success)
                {
                    long uv;
                    if (long.TryParse(mv.Groups[1].Value, out uv)) res.UidValidity = uv;
                }
                Match me = Regex.Match(line, @"^\*\s+(\d+)\s+EXISTS", RegexOptions.IgnoreCase);
                if (me.Success)
                {
                    int ex;
                    if (int.TryParse(me.Groups[1].Value, out ex)) res.Exists = ex;
                }
            }
            return res;
        }

        public List<long> SearchAllUids()
        {
            List<string> lines = SendCommand("UID SEARCH ALL");
            EnsureOk(lines, "Не удалось получить список UID");
            List<long> uids = new List<long>();
            foreach (string line in lines)
            {
                if (line.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = line.Substring(8).Trim();
                    if (string.IsNullOrEmpty(rest)) continue;
                    string[] parts = rest.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string p in parts)
                    {
                        long u;
                        if (long.TryParse(p, out u)) uids.Add(u);
                    }
                }
            }
            return uids;
        }

        public ImapFetchedMessage FetchMessageByUid(long uid, bool markAsRead)
        {
            string tag = NextTag();
            string bodyItem = markAsRead ? "BODY[]" : "BODY.PEEK[]";
            WriteLineAscii(tag + " UID FETCH " + uid + " (UID FLAGS " + bodyItem + ")");

            ImapFetchedMessage msg = new ImapFetchedMessage();
            msg.Uid = uid;

            while (true)
            {
                string line = ReadLineAscii();
                if (line == null) throw new IOException("Соединение IMAP разорвано.");

                if (line.StartsWith(tag + " ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!line.StartsWith(tag + " OK", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("Ошибка FETCH UID " + uid + ": " + line);
                    }
                    break;
                }

                if (line.IndexOf("\\Seen", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    msg.IsRead = true;
                }

                Match lit = Regex.Match(line, @"\{(\d+)\}$" );
                if (lit.Success)
                {
                    int len = int.Parse(lit.Groups[1].Value);
                    msg.RawBytes = ReadExactBytes(len);
                    ReadLineAscii(); // trailing line after literal
                }
            }
            return msg;
        }

        public void DeleteMessagesFromServer(string rawFolderName, List<long> uids, string trashFolderRaw)
        {
            if (uids == null || uids.Count == 0) return;
            SelectFolder(rawFolderName, false);

            string uidSet = string.Join(",", uids);
            bool isAlreadyTrash = !string.IsNullOrEmpty(trashFolderRaw) && string.Equals(rawFolderName, trashFolderRaw, StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(trashFolderRaw) && !isAlreadyTrash)
            {
                List<string> copyResp = SendCommand("UID COPY " + uidSet + " " + QuoteImap(trashFolderRaw));
                EnsureOk(copyResp, "Не удалось перенести письма в корзину");
            }

            List<string> storeResp = SendCommand("UID STORE " + uidSet + " +FLAGS.SILENT (\\Deleted)");
            EnsureOk(storeResp, "Не удалось пометить письма на удаление");

            List<string> expResp = SendCommand("EXPUNGE");
            EnsureOk(expResp, "Не удалось выполнить EXPUNGE");
        }

        public void EmptyTrash(string trashFolderRaw)
        {
            if (string.IsNullOrEmpty(trashFolderRaw)) return;
            SelectFolder(trashFolderRaw, false);
            List<long> uids = SearchAllUids();
            if (uids.Count == 0) return;
            string uidSet = string.Join(",", uids);
            SendCommand("UID STORE " + uidSet + " +FLAGS.SILENT (\\Deleted)");
            SendCommand("EXPUNGE");
        }

        public void AppendMessage(string rawFolderName, byte[] rawEml)
        {
            string tag = NextTag();
            WriteLineAscii(tag + " APPEND " + QuoteImap(rawFolderName) + " (\\Seen) {" + rawEml.Length + "}");
            string cont = ReadLineAscii();
            if (cont == null || !cont.StartsWith("+"))
            {
                throw new IOException("Сервер отклонил APPEND: " + cont);
            }
            _ssl.Write(rawEml, 0, rawEml.Length);
            byte[] crlf = Encoding.ASCII.GetBytes("\r\n");
            _ssl.Write(crlf, 0, crlf.Length);
            _ssl.Flush();

            List<string> resp = ReadUntilTag(tag);
            EnsureOk(resp, "Ошибка APPEND в папку " + rawFolderName);
        }

        private List<string> SendCommand(string command)
        {
            string tag = NextTag();
            WriteLineAscii(tag + " " + command);
            return ReadUntilTag(tag);
        }

        private List<string> ReadUntilTag(string tag)
        {
            List<string> lines = new List<string>();
            while (true)
            {
                string line = ReadLineAscii();
                if (line == null) throw new IOException("Соединение IMAP закрыто сервером.");
                lines.Add(line);
                if (line.StartsWith(tag + " ", StringComparison.OrdinalIgnoreCase)) break;
            }
            return lines;
        }

        private void EnsureOk(List<string> lines, string errorPrefix)
        {
            if (lines.Count == 0) throw new IOException(errorPrefix);
            string last = lines[lines.Count - 1];
            int sp = last.IndexOf(' ');
            if (sp < 0 || !last.Substring(sp + 1).StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(errorPrefix + ": " + last);
            }
        }

        private string NextTag()
        {
            _tagCounter++;
            return "A" + _tagCounter.ToString("D4");
        }

        private void WriteLineAscii(string line)
        {
            byte[] data = Encoding.ASCII.GetBytes(line + "\r\n");
            _ssl.Write(data, 0, data.Length);
            _ssl.Flush();
        }

        private string ReadLineAscii()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                while (true)
                {
                    int b = _ssl.ReadByte();
                    if (b < 0)
                    {
                        if (ms.Length == 0) return null;
                        break;
                    }
                    if (b == '\n') break;
                    if (b != '\r') ms.WriteByte((byte)b);
                }
                return Encoding.ASCII.GetString(ms.ToArray());
            }
        }

        private byte[] ReadExactBytes(int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = _ssl.Read(buffer, offset, count - offset);
                if (read <= 0) throw new IOException("Неожиданный конец потока при чтении письма.");
                offset += read;
            }
            return buffer;
        }

        private static string QuoteImap(string s)
        {
            if (s == null) s = string.Empty;
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        public static string DecodeModifiedUtf7(string input)
        {
            if (string.IsNullOrEmpty(input) || input.IndexOf('&') < 0) return input;
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < input.Length)
            {
                if (input[i] == '&')
                {
                    int dash = input.IndexOf('-', i + 1);
                    if (dash < 0)
                    {
                        sb.Append(input.Substring(i));
                        break;
                    }
                    if (dash == i + 1)
                    {
                        sb.Append('&');
                    }
                    else
                    {
                        string b64 = input.Substring(i + 1, dash - i - 1).Replace(',', '/');
                        int mod = b64.Length % 4;
                        if (mod > 0) b64 += new string('=', 4 - mod);
                        try
                        {
                            byte[] bytes = Convert.FromBase64String(b64);
                            sb.Append(Encoding.BigEndianUnicode.GetString(bytes));
                        }
                        catch
                        {
                            sb.Append(input.Substring(i, dash - i + 1));
                        }
                    }
                    i = dash + 1;
                }
                else
                {
                    sb.Append(input[i]);
                    i++;
                }
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            try
            {
                if (_ssl != null)
                {
                    WriteLineAscii(NextTag() + " LOGOUT");
                    _ssl.Dispose();
                }
            }
            catch { }
            try { if (_tcp != null) _tcp.Close(); } catch { }
        }
    }
}
