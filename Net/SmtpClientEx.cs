using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Pemail.Models;

namespace Pemail.Net
{
    public class SmtpClientEx
    {
        public static byte[] BuildMimeMessage(string fromDisplay, string fromEmail, string toList, string ccList, string subject, string textBody, string inReplyTo, string references, List<string> attachmentPaths, string htmlBody = null)
        {
            string mixedBoundary = "----Pemail_Mix_" + Guid.NewGuid().ToString("N");
            string altBoundary = "----Pemail_Alt_" + Guid.NewGuid().ToString("N");
            string msgId = "<" + Guid.NewGuid().ToString("N") + "@pemail.local>";
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("From: {0} <{1}>\r\n", EncodeHeader(fromDisplay), fromEmail);
            sb.AppendFormat("To: {0}\r\n", toList.Trim());
            if (!string.IsNullOrWhiteSpace(ccList)) sb.AppendFormat("Cc: {0}\r\n", ccList.Trim());
            sb.AppendFormat("Subject: {0}\r\n", EncodeHeader(subject ?? string.Empty));
            DateTimeOffset now = DateTimeOffset.Now;
            string tzSign = now.Offset < TimeSpan.Zero ? "-" : "+";
            TimeSpan tzAbs = now.Offset.Duration();
            string rfcDate = now.ToString("ddd, dd MMM yyyy HH:mm:ss ", System.Globalization.CultureInfo.InvariantCulture)
                + tzSign + tzAbs.Hours.ToString("D2") + tzAbs.Minutes.ToString("D2");
            sb.AppendFormat("Date: {0}\r\n", rfcDate);
            sb.AppendFormat("Message-ID: {0}\r\n", msgId);
            if (!string.IsNullOrWhiteSpace(inReplyTo)) sb.AppendFormat("In-Reply-To: {0}\r\n", inReplyTo.Trim());
            if (!string.IsNullOrWhiteSpace(references)) sb.AppendFormat("References: {0}\r\n", references.Trim());
            sb.Append("MIME-Version: 1.0\r\n");

            bool hasHtml = !string.IsNullOrWhiteSpace(htmlBody);
            bool hasAtt = attachmentPaths != null && attachmentPaths.Count > 0;

            if (!hasAtt && !hasHtml)
            {
                sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
                sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
                sb.Append(WrapBase64(Encoding.UTF8.GetBytes(textBody ?? string.Empty)));
                return Encoding.ASCII.GetBytes(sb.ToString());
            }

            if (!hasAtt && hasHtml)
            {
                AppendAlternativeBody(sb, altBoundary, textBody, htmlBody);
                return Encoding.ASCII.GetBytes(sb.ToString());
            }

            sb.AppendFormat("Content-Type: multipart/mixed; boundary=\"{0}\"\r\n\r\n", mixedBoundary);
            sb.AppendFormat("--{0}\r\n", mixedBoundary);
            if (hasHtml)
            {
                AppendAlternativeBody(sb, altBoundary, textBody, htmlBody);
            }
            else
            {
                sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
                sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
                sb.Append(WrapBase64(Encoding.UTF8.GetBytes(textBody ?? string.Empty)));
            }
            sb.Append("\r\n");

            foreach (string path in attachmentPaths)
            {
                if (!File.Exists(path)) continue;
                string fileName = Path.GetFileName(path);
                byte[] fileBytes = File.ReadAllBytes(path);
                sb.AppendFormat("--{0}\r\n", mixedBoundary);
                sb.AppendFormat("Content-Type: application/octet-stream; name=\"{0}\"\r\n", EncodeHeader(fileName));
                sb.AppendFormat("Content-Disposition: attachment; filename=\"{0}\"\r\n", EncodeHeader(fileName));
                sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
                sb.Append(WrapBase64(fileBytes));
                sb.Append("\r\n");
            }

            sb.AppendFormat("--{0}--\r\n", mixedBoundary);
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static void AppendAlternativeBody(StringBuilder sb, string altBoundary, string textBody, string htmlBody)
        {
            sb.AppendFormat("Content-Type: multipart/alternative; boundary=\"{0}\"\r\n\r\n", altBoundary);
            sb.AppendFormat("--{0}\r\n", altBoundary);
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(WrapBase64(Encoding.UTF8.GetBytes(textBody ?? string.Empty)));
            sb.Append("\r\n");
            sb.AppendFormat("--{0}\r\n", altBoundary);
            sb.Append("Content-Type: text/html; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            sb.Append(WrapBase64(Encoding.UTF8.GetBytes(htmlBody ?? string.Empty)));
            sb.Append("\r\n");
            sb.AppendFormat("--{0}--\r\n", altBoundary);
        }

        public sealed class SmtpSession : IDisposable
        {
            private TcpClient _tcp;
            private Stream _stream;
            private StreamReader _reader;
            private StreamWriter _writer;
            private bool _connected;

            public void ConnectAndAuth(AccountConfig account)
            {
                Dispose();
                string password = account.GetPassword();
                _tcp = new TcpClient();
                _tcp.ReceiveTimeout = 60000;
                _tcp.SendTimeout = 60000;
                _tcp.Connect(account.SmtpHost, account.SmtpPort);

                _stream = _tcp.GetStream();
                if (account.SmtpImplicitTls)
                {
                    SslStream ssl = new SslStream(_stream, false);
                    ssl.AuthenticateAsClient(account.SmtpHost);
                    _stream = ssl;
                }

                _reader = new StreamReader(_stream, Encoding.ASCII);
                _writer = new StreamWriter(_stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

                ExpectCode(_reader, 220);
                _writer.WriteLine("EHLO pemail.local");
                ExpectCode(_reader, 250);

                if (!account.SmtpImplicitTls)
                {
                    _writer.WriteLine("STARTTLS");
                    ExpectCode(_reader, 220);
                    SslStream ssl = new SslStream(_tcp.GetStream(), false);
                    ssl.AuthenticateAsClient(account.SmtpHost);
                    _stream = ssl;
                    _reader = new StreamReader(_stream, Encoding.ASCII);
                    _writer = new StreamWriter(_stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    _writer.WriteLine("EHLO pemail.local");
                    ExpectCode(_reader, 250);
                }

                _writer.WriteLine("AUTH LOGIN");
                ExpectCode(_reader, 334);
                _writer.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(account.Email)));
                ExpectCode(_reader, 334);
                _writer.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)));
                ExpectCode(_reader, 235);
                _connected = true;
            }

            public bool IsConnected
            {
                get { return _connected && _tcp != null && _tcp.Connected; }
            }

            public void SendMessage(string fromEmail, List<string> recipients, byte[] rawMime)
            {
                if (!IsConnected) throw new IOException("Нет активного SMTP-соединения.");

                _writer.WriteLine("MAIL FROM:<" + fromEmail + ">");
                ExpectCode(_reader, 250);

                foreach (string rcpt in recipients)
                {
                    string addr = ExtractEmailAddress(rcpt);
                    if (string.IsNullOrEmpty(addr)) continue;
                    _writer.WriteLine("RCPT TO:<" + addr + ">");
                    ExpectCode(_reader, 250);
                }

                _writer.WriteLine("DATA");
                ExpectCode(_reader, 354);

                string mimeText = Encoding.ASCII.GetString(rawMime);
                string dotStuffed = mimeText.Replace("\r\n.", "\r\n..");
                if (dotStuffed.StartsWith(".")) dotStuffed = "." + dotStuffed;
                _writer.Write(dotStuffed);
                if (!dotStuffed.EndsWith("\r\n")) _writer.Write("\r\n");
                _writer.WriteLine(".");
                ExpectCode(_reader, 250);
            }

            public void TryReset()
            {
                if (!IsConnected) return;
                try
                {
                    _writer.WriteLine("RSET");
                    ExpectCode(_reader, 250);
                }
                catch
                {
                    _connected = false;
                }
            }

            public void Dispose()
            {
                if (_connected && _writer != null)
                {
                    try { _writer.WriteLine("QUIT"); }
                    catch { }
                }
                _connected = false;
                if (_writer != null) { try { _writer.Dispose(); } catch { } _writer = null; }
                if (_reader != null) { try { _reader.Dispose(); } catch { } _reader = null; }
                if (_stream != null) { try { _stream.Dispose(); } catch { } _stream = null; }
                if (_tcp != null) { try { _tcp.Close(); } catch { } _tcp = null; }
            }
        }

        public static void Send(AccountConfig account, List<string> recipients, byte[] rawMime)
        {
            using (SmtpSession session = new SmtpSession())
            {
                session.ConnectAndAuth(account);
                session.SendMessage(account.Email, recipients, rawMime);
            }
        }

        public static List<string> ParseRecipients(string input)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrWhiteSpace(input)) return list;
            string[] parts = input.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                string addr = ExtractEmailAddress(p);
                if (!string.IsNullOrEmpty(addr)) list.Add(addr);
            }
            return list;
        }

        public static string ExtractEmailAddress(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            int lt = raw.IndexOf('<');
            int gt = raw.IndexOf('>');
            if (lt >= 0 && gt > lt) return raw.Substring(lt + 1, gt - lt - 1).Trim();
            return raw.Trim();
        }

        private static void ExpectCode(StreamReader reader, int expectedCode)
        {
            string lastLine = null;
            while (true)
            {
                string line = reader.ReadLine();
                if (line == null) throw new IOException("SMTP сервер закрыл соединение.");
                lastLine = line;
                if (line.Length < 4 || line[3] != '-') break;
            }
            int code;
            if (lastLine.Length < 3 || !int.TryParse(lastLine.Substring(0, 3), out code) || code != expectedCode)
            {
                throw new IOException("Ошибка SMTP (ожидался " + expectedCode + "): " + lastLine);
            }
        }

        private static string EncodeHeader(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            bool asciiOnly = true;
            foreach (char c in text)
            {
                if (c > 127) { asciiOnly = false; break; }
            }
            if (asciiOnly) return text;
            return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "?=";
        }

        private static string WrapBase64(byte[] data)
        {
            string b64 = Convert.ToBase64String(data);
            StringBuilder sb = new StringBuilder(b64.Length + b64.Length / 30);
            for (int i = 0; i < b64.Length; i += 76)
            {
                int len = Math.Min(76, b64.Length - i);
                sb.Append(b64, i, len);
                sb.Append("\r\n");
            }
            return sb.ToString();
        }
    }
}
