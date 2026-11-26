using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Pemail.Models;

namespace Pemail.Mime
{
    public static class MimeParser
    {
        private static readonly Regex Rfc2047Regex = new Regex(
            @"=\?([^?]+)\?([bBqQ])\?([^?]*)\?=",
            RegexOptions.Compiled);

        private static readonly Regex ScriptRegex = new Regex(
            @"<script\b[^>]*>[\s\S]*?</script>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex EventAttrRegex = new Regex(
            @"\son[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static ParsedEmail Parse(byte[] rawBytes)
        {
            ParsedEmail result = new ParsedEmail();
            if (rawBytes == null || rawBytes.Length == 0) return result;

            int headerEnd;
            int bodyStart;
            FindHeaderBodySplit(rawBytes, out headerEnd, out bodyStart);

            string headerText = Encoding.ASCII.GetString(rawBytes, 0, headerEnd);
            Dictionary<string, string> headers = ParseHeaders(headerText);

            result.From = DecodeHeaderValue(GetHeader(headers, "from"));
            result.To = DecodeHeaderValue(GetHeader(headers, "to"));
            result.Cc = DecodeHeaderValue(GetHeader(headers, "cc"));
            result.Subject = DecodeHeaderValue(GetHeader(headers, "subject"));
            result.MessageId = GetHeader(headers, "message-id").Trim();
            result.References = GetHeader(headers, "references").Trim();
            result.DateUtc = ParseDate(GetHeader(headers, "date"));

            byte[] bodyBytes = new byte[Math.Max(0, rawBytes.Length - bodyStart)];
            if (bodyBytes.Length > 0)
            {
                Buffer.BlockCopy(rawBytes, bodyStart, bodyBytes, 0, bodyBytes.Length);
            }

            ParseEntity(headers, bodyBytes, result, 0);
            return result;
        }

        private static void ParseEntity(Dictionary<string, string> headers, byte[] bodyBytes, ParsedEmail email, int depth)
        {
            if (depth > 12) return;

            string contentTypeHeader = GetHeader(headers, "content-type");
            if (string.IsNullOrWhiteSpace(contentTypeHeader)) contentTypeHeader = "text/plain; charset=utf-8";

            string mediaType = ExtractMainValue(contentTypeHeader).ToLowerInvariant();
            string charset = ExtractParam(contentTypeHeader, "charset");
            string boundary = ExtractParam(contentTypeHeader, "boundary");
            string transferEncoding = GetHeader(headers, "content-transfer-encoding").Trim().ToLowerInvariant();
            string disposition = GetHeader(headers, "content-disposition");
            string contentId = GetHeader(headers, "content-id").Trim().Trim('<', '>');

            if (mediaType.StartsWith("multipart/") && !string.IsNullOrEmpty(boundary))
            {
                List<byte[]> parts = SplitMultipart(bodyBytes, boundary);
                foreach (byte[] part in parts)
                {
                    int hEnd;
                    int bStart;
                    FindHeaderBodySplit(part, out hEnd, out bStart);
                    string pHeaderText = Encoding.ASCII.GetString(part, 0, hEnd);
                    Dictionary<string, string> pHeaders = ParseHeaders(pHeaderText);
                    byte[] pBody = new byte[Math.Max(0, part.Length - bStart)];
                    if (pBody.Length > 0) Buffer.BlockCopy(part, bStart, pBody, 0, pBody.Length);
                    ParseEntity(pHeaders, pBody, email, depth + 1);
                }
                return;
            }

            byte[] decoded = DecodeTransferEncoding(bodyBytes, transferEncoding);
            string fileName = DecodeHeaderValue(ExtractParam(disposition, "filename"));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = DecodeHeaderValue(ExtractParam(contentTypeHeader, "name"));
            }

            bool isAttachment = disposition.ToLowerInvariant().StartsWith("attachment") || !string.IsNullOrWhiteSpace(fileName);
            bool isInline = disposition.ToLowerInvariant().StartsWith("inline") || !string.IsNullOrWhiteSpace(contentId);

            if (!isAttachment && mediaType == "text/plain" && string.IsNullOrEmpty(email.TextBody))
            {
                email.TextBody = DecodeBytesToString(decoded, charset);
                return;
            }

            if (!isAttachment && mediaType == "text/html" && string.IsNullOrEmpty(email.HtmlBody))
            {
                email.HtmlBody = DecodeBytesToString(decoded, charset);
                return;
            }

            if (isAttachment || isInline || (!mediaType.StartsWith("text/") && decoded.Length > 0))
            {
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    string ext = GuessExtension(mediaType);
                    fileName = (!string.IsNullOrEmpty(contentId) ? contentId : "attachment_" + (email.Attachments.Count + 1)) + ext;
                }

                ParsedAttachment att = new ParsedAttachment();
                att.FileName = SanitizeFileName(fileName);
                att.ContentType = mediaType;
                att.ContentId = contentId;
                att.IsInline = isInline && !disposition.ToLowerInvariant().StartsWith("attachment");
                att.Data = decoded;
                email.Attachments.Add(att);
            }
        }

        public static string BuildSafeHtml(ParsedEmail email, bool allowExternalImages, bool darkTheme = false)
        {
            string bodyBg = darkTheme ? "#252A34" : "#FFFFFF";
            string bodyFg = darkTheme ? "#D8DEE9" : "#111827";
            string linkFg = darkTheme ? "#7CB3FF" : "#2563EB";

            string html = email.HtmlBody;
            if (string.IsNullOrWhiteSpace(html))
            {
                string plain = email.TextBody ?? string.Empty;
                string escaped = System.Net.WebUtility.HtmlEncode(plain);
                html = "<pre style='font-family:Segoe UI,sans-serif;font-size:13px;white-space:pre-wrap;word-wrap:break-word;margin:0;color:" + bodyFg + ";'>" + escaped + "</pre>";
            }
            else
            {
                html = ScriptRegex.Replace(html, string.Empty);
                html = EventAttrRegex.Replace(html, string.Empty);

                foreach (ParsedAttachment att in email.Attachments)
                {
                    if (!string.IsNullOrEmpty(att.ContentId) && att.Data != null && att.Data.Length > 0 && att.Data.Length < 4 * 1024 * 1024)
                    {
                        string dataUri = "data:" + (string.IsNullOrEmpty(att.ContentType) ? "application/octet-stream" : att.ContentType) + ";base64," + Convert.ToBase64String(att.Data);
                        html = Regex.Replace(html, "cid:" + Regex.Escape(att.ContentId), dataUri, RegexOptions.IgnoreCase);
                    }
                }

                if (!allowExternalImages)
                {
                    html = Regex.Replace(html, "(<img\\b[^>]*\\bsrc\\s*=\\s*[\"'])https?://[^\"']+", "$1about:blank", RegexOptions.IgnoreCase);
                }
            }

            string darkOverride = darkTheme
                ? "body{background:" + bodyBg + " !important;color:" + bodyFg + " !important;}table,td,div,p,span{color:inherit;}"
                : "body{background:" + bodyBg + ";color:" + bodyFg + ";}";

            return "<!DOCTYPE html><html><head><meta charset='utf-8'><meta http-equiv='X-UA-Compatible' content='IE=edge'>" +
                   "<style>html,body{font-family:Segoe UI,Arial,sans-serif;font-size:13px;margin:10px;line-height:1.45;background:" + bodyBg + ";color:" + bodyFg + ";}" + darkOverride + "img{max-width:100%;height:auto;}a{color:" + linkFg + ";}</style>" +
                   "</head><body>" + html + "</body></html>";
        }

        public static string ExtractPlainTextForSearch(ParsedEmail email)
        {
            if (!string.IsNullOrWhiteSpace(email.TextBody)) return CollapseWhitespace(email.TextBody, 4000);
            if (!string.IsNullOrWhiteSpace(email.HtmlBody))
            {
                string noScript = ScriptRegex.Replace(email.HtmlBody, " ");
                string noStyle = Regex.Replace(noScript, @"<style\b[^>]*>[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
                string noTags = Regex.Replace(noStyle, @"<[^>]+>", " ");
                string decoded = System.Net.WebUtility.HtmlDecode(noTags);
                return CollapseWhitespace(decoded, 4000);
            }
            return string.Empty;
        }

        private static string CollapseWhitespace(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string compact = Regex.Replace(text, @"\s+", " ").Trim();
            if (compact.Length > maxLen) return compact.Substring(0, maxLen);
            return compact;
        }

        private static void FindHeaderBodySplit(byte[] data, out int headerEnd, out int bodyStart)
        {
            for (int i = 0; i < data.Length - 1; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && i + 3 < data.Length && data[i + 2] == '\r' && data[i + 3] == '\n')
                {
                    headerEnd = i;
                    bodyStart = i + 4;
                    return;
                }
                if (data[i] == '\n' && data[i + 1] == '\n')
                {
                    headerEnd = i;
                    bodyStart = i + 2;
                    return;
                }
            }
            headerEnd = data.Length;
            bodyStart = data.Length;
        }

        private static Dictionary<string, string> ParseHeaders(string headerText)
        {
            Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = headerText.Replace("\r\n", "\n").Split('\n');
            string currentKey = null;
            StringBuilder currentVal = new StringBuilder();

            foreach (string line in lines)
            {
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                {
                    if (currentKey != null)
                    {
                        currentVal.Append(' ').Append(line.Trim());
                    }
                }
                else
                {
                    if (currentKey != null && !dict.ContainsKey(currentKey))
                    {
                        dict[currentKey] = currentVal.ToString();
                    }
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        currentKey = line.Substring(0, colon).Trim();
                        currentVal.Clear();
                        currentVal.Append(line.Substring(colon + 1).Trim());
                    }
                    else
                    {
                        currentKey = null;
                    }
                }
            }

            if (currentKey != null && !dict.ContainsKey(currentKey))
            {
                dict[currentKey] = currentVal.ToString();
            }
            return dict;
        }

        private static string GetHeader(Dictionary<string, string> headers, string name)
        {
            string val;
            return headers.TryGetValue(name, out val) ? val : string.Empty;
        }

        private static string ExtractMainValue(string headerVal)
        {
            int semi = headerVal.IndexOf(';');
            return (semi >= 0 ? headerVal.Substring(0, semi) : headerVal).Trim();
        }

        private static string ExtractParam(string headerVal, string paramName)
        {
            if (string.IsNullOrEmpty(headerVal)) return string.Empty;
            Match m = Regex.Match(headerVal, @"(?:;|\s)" + Regex.Escape(paramName) + @"\s*=\s*(?:""([^""]*)""|([^;\s]+))", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
            }
            Match mStar = Regex.Match(headerVal, @"(?:;|\s)" + Regex.Escape(paramName) + @"\*\s*=\s*([^;\s]+)", RegexOptions.IgnoreCase);
            if (mStar.Success)
            {
                string raw = mStar.Groups[1].Value;
                int idx = raw.IndexOf("''", StringComparison.Ordinal);
                if (idx >= 0) return Uri.UnescapeDataString(raw.Substring(idx + 2));
                return Uri.UnescapeDataString(raw);
            }
            return string.Empty;
        }

        private static List<byte[]> SplitMultipart(byte[] bodyBytes, string boundary)
        {
            List<byte[]> parts = new List<byte[]>();
            byte[] marker = Encoding.ASCII.GetBytes("--" + boundary);
            List<int> positions = new List<int>();

            int pos = 0;
            while (pos <= bodyBytes.Length - marker.Length)
            {
                int found = IndexOfBytes(bodyBytes, marker, pos);
                if (found < 0) break;
                positions.Add(found);
                pos = found + marker.Length;
            }

            for (int i = 0; i < positions.Count - 1; i++)
            {
                int start = positions[i] + marker.Length;
                if (start + 1 < bodyBytes.Length && bodyBytes[start] == '-' && bodyBytes[start + 1] == '-') break;
                while (start < bodyBytes.Length && (bodyBytes[start] == '\r' || bodyBytes[start] == '\n')) start++;

                int end = positions[i + 1];
                while (end > start && (bodyBytes[end - 1] == '\r' || bodyBytes[end - 1] == '\n')) end--;

                if (end > start)
                {
                    byte[] part = new byte[end - start];
                    Buffer.BlockCopy(bodyBytes, start, part, 0, part.Length);
                    parts.Add(part);
                }
            }
            return parts;
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
        {
            int limit = haystack.Length - needle.Length;
            for (int i = start; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static byte[] DecodeTransferEncoding(byte[] data, string encoding)
        {
            if (encoding == "base64")
            {
                try
                {
                    string s = Encoding.ASCII.GetString(data);
                    StringBuilder sb = new StringBuilder(s.Length);
                    foreach (char c in s)
                    {
                        if (!char.IsWhiteSpace(c)) sb.Append(c);
                    }
                    return Convert.FromBase64String(sb.ToString());
                }
                catch
                {
                    return data;
                }
            }
            if (encoding == "quoted-printable")
            {
                return DecodeQuotedPrintable(data);
            }
            return data;
        }

        private static byte[] DecodeQuotedPrintable(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream(data.Length))
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] == '=' && i + 1 < data.Length)
                    {
                        if (data[i + 1] == '\r' && i + 2 < data.Length && data[i + 2] == '\n')
                        {
                            i += 2;
                            continue;
                        }
                        if (data[i + 1] == '\n')
                        {
                            i += 1;
                            continue;
                        }
                        if (i + 2 < data.Length)
                        {
                            int hi = HexVal(data[i + 1]);
                            int lo = HexVal(data[i + 2]);
                            if (hi >= 0 && lo >= 0)
                            {
                                ms.WriteByte((byte)((hi << 4) | lo));
                                i += 2;
                                continue;
                            }
                        }
                    }
                    ms.WriteByte(data[i]);
                }
                return ms.ToArray();
            }
        }

        private static int HexVal(byte b)
        {
            if (b >= '0' && b <= '9') return b - '0';
            if (b >= 'A' && b <= 'F') return b - 'A' + 10;
            if (b >= 'a' && b <= 'f') return b - 'a' + 10;
            return -1;
        }

        public static string DecodeHeaderValue(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string merged = Regex.Replace(input, @"\?=\s+=\?", "?==?");
            return Rfc2047Regex.Replace(merged, delegate(Match m)
            {
                string charset = m.Groups[1].Value;
                string enc = m.Groups[2].Value.ToUpperInvariant();
                string payload = m.Groups[3].Value;
                try
                {
                    byte[] bytes;
                    if (enc == "B")
                    {
                        bytes = Convert.FromBase64String(payload);
                    }
                    else
                    {
                        bytes = DecodeQuotedPrintable(Encoding.ASCII.GetBytes(payload.Replace('_', ' ')));
                    }
                    return DecodeBytesToString(bytes, charset);
                }
                catch
                {
                    return m.Value;
                }
            });
        }

        private static string DecodeBytesToString(byte[] bytes, string charset)
        {
            Encoding enc = ResolveEncoding(charset);
            return enc.GetString(bytes);
        }

        private static Encoding ResolveEncoding(string charset)
        {
            if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
            string c = charset.Trim().ToLowerInvariant();
            try
            {
                if (c == "utf8") c = "utf-8";
                if (c == "cp1251" || c == "win-1251") c = "windows-1251";
                return Encoding.GetEncoding(c);
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        private static DateTime ParseDate(string rawDate)
        {
            if (string.IsNullOrWhiteSpace(rawDate)) return DateTime.UtcNow;
            string cleaned = Regex.Replace(rawDate, @"\([^)]*\)", string.Empty).Trim();
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dto))
            {
                return dto.UtcDateTime;
            }
            return DateTime.UtcNow;
        }

        private static string GuessExtension(string mediaType)
        {
            if (mediaType == "image/png") return ".png";
            if (mediaType == "image/jpeg") return ".jpg";
            if (mediaType == "image/gif") return ".gif";
            if (mediaType == "application/pdf") return ".pdf";
            if (mediaType == "application/zip") return ".zip";
            return ".bin";
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in invalid) name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
