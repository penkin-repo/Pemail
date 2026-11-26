using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace Pemail.Models
{
    public class MailFolderInfo
    {
        public string RawName { get; set; }
        public string DisplayName { get; set; }
        public string Attributes { get; set; }
        public bool IsSelectable { get; set; }

        public bool IsTrash
        {
            get
            {
                string a = (Attributes ?? string.Empty).ToLowerInvariant();
                string d = (DisplayName ?? string.Empty).ToLowerInvariant();
                return a.Contains("\\trash") || d == "trash" || d == "корзина" || d.EndsWith("/trash") || d.EndsWith("/корзина");
            }
        }

        public bool IsSent
        {
            get
            {
                string a = (Attributes ?? string.Empty).ToLowerInvariant();
                string d = (DisplayName ?? string.Empty).ToLowerInvariant();
                return a.Contains("\\sent") || d == "sent" || d == "отправленные" || d.EndsWith("/sent") || d.EndsWith("/отправленные");
            }
        }

        public bool IsAllMail
        {
            get
            {
                string a = (Attributes ?? string.Empty).ToLowerInvariant();
                return a.Contains("\\all");
            }
        }
    }

    public class AttachmentStyleInfo
    {
        public string BackgroundHex { get; set; }
        public string BorderHex { get; set; }
        public string ForegroundHex { get; set; }
        public string TagBgHex { get; set; }
    }

    public class AttachmentBadge
    {
        public string FileName { get; set; }
        public string ShortName { get; set; }
        public string Extension { get; set; }
        public long SizeBytes { get; set; }

        [ScriptIgnore]
        public string DisplayLabel
        {
            get { return ShortName; }
        }

        [ScriptIgnore]
        public string ToolTipText
        {
            get { return string.Format("{0} ({1})", FileName, FormatSize(SizeBytes)); }
        }

        [ScriptIgnore]
        public string BadgeBg
        {
            get { return GetStyleForFile(FileName, Extension).BackgroundHex; }
        }

        [ScriptIgnore]
        public string BadgeBorder
        {
            get { return GetStyleForFile(FileName, Extension).BorderHex; }
        }

        [ScriptIgnore]
        public string BadgeFg
        {
            get { return GetStyleForFile(FileName, Extension).ForegroundHex; }
        }

        [ScriptIgnore]
        public string ExtLabel
        {
            get
            {
                string ext = (Extension ?? string.Empty).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(ext))
                {
                    ext = Path.GetExtension(FileName ?? string.Empty).TrimStart('.').ToUpperInvariant();
                }
                if (string.IsNullOrEmpty(ext)) return "FILE";
                return ext.Length > 4 ? ext.Substring(0, 4) : ext;
            }
        }

        public static AttachmentStyleInfo GetStyleForFile(string fileName, string extension = null)
        {
            string ext = !string.IsNullOrWhiteSpace(extension)
                ? extension.Trim().TrimStart('.').ToLowerInvariant()
                : Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();

            if (ext == "pdf")
            {
                return new AttachmentStyleInfo { BackgroundHex = "#FEF2F2", BorderHex = "#FCA5A5", ForegroundHex = "#DC2626", TagBgHex = "#FEE2E2" };
            }
            if (ext == "xls" || ext == "xlsx" || ext == "xlsm" || ext == "xlsb" || ext == "csv" || ext == "ods")
            {
                return new AttachmentStyleInfo { BackgroundHex = "#F0FDF4", BorderHex = "#86EFAC", ForegroundHex = "#15803D", TagBgHex = "#DCFCE7" };
            }
            if (ext == "doc" || ext == "docx" || ext == "rtf" || ext == "odt")
            {
                return new AttachmentStyleInfo { BackgroundHex = "#EFF6FF", BorderHex = "#93C5FD", ForegroundHex = "#1D4ED8", TagBgHex = "#DBEAFE" };
            }
            if (ext == "ppt" || ext == "pptx" || ext == "odp")
            {
                return new AttachmentStyleInfo { BackgroundHex = "#FFF7ED", BorderHex = "#FDBA74", ForegroundHex = "#C2410C", TagBgHex = "#FFEDD5" };
            }
            if (ext == "png" || ext == "jpg" || ext == "jpeg" || ext == "gif" || ext == "bmp" || ext == "webp" || ext == "svg" || ext == "tif" || ext == "tiff")
            {
                return new AttachmentStyleInfo { BackgroundHex = "#F5F3FF", BorderHex = "#C4B5FD", ForegroundHex = "#7C3AED", TagBgHex = "#EDE9FE" };
            }
            if (ext == "zip" || ext == "rar" || ext == "7z" || ext == "gz" || ext == "tar" || ext == "bz2")
            {
                return new AttachmentStyleInfo { BackgroundHex = "#FFFBEB", BorderHex = "#FCD34D", ForegroundHex = "#B45309", TagBgHex = "#FEF3C7" };
            }
            if (ext == "txt" || ext == "log" || ext == "xml" || ext == "json" || ext == "html" || ext == "htm" || ext == "md")
            {
                return new AttachmentStyleInfo { BackgroundHex = "#F0FDFA", BorderHex = "#99F6E4", ForegroundHex = "#0F766E", TagBgHex = "#CCFBF1" };
            }
            return new AttachmentStyleInfo { BackgroundHex = "#F1F5F9", BorderHex = "#CBD5E1", ForegroundHex = "#475569", TagBgHex = "#E2E8F0" };
        }

        public static AttachmentBadge From(string fileName, long sizeBytes)
        {
            string clean = string.IsNullOrWhiteSpace(fileName) ? "file.bin" : fileName.Trim();
            string ext = Path.GetExtension(clean);
            if (ext == null) ext = string.Empty;
            ext = ext.TrimStart('.').ToUpperInvariant();
            if (ext.Length > 5) ext = ext.Substring(0, 5);

            string shortName = MakeShortName(clean, 16);
            AttachmentBadge b = new AttachmentBadge();
            b.FileName = clean;
            b.ShortName = shortName;
            b.Extension = ext;
            b.SizeBytes = sizeBytes;
            return b;
        }

        public static string MakeShortName(string fileName, int maxLen)
        {
            if (string.IsNullOrEmpty(fileName) || fileName.Length <= maxLen) return fileName;
            string ext = Path.GetExtension(fileName);
            string nameNoExt = Path.GetFileNameWithoutExtension(fileName);
            if (ext == null) ext = string.Empty;
            int budget = maxLen - ext.Length - 1;
            if (budget < 4) return fileName.Substring(0, maxLen - 1) + "…";
            return nameNoExt.Substring(0, Math.Min(nameNoExt.Length, budget)) + "…" + ext;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " Б";
            if (bytes < 1048576) return string.Format("{0:0.#} КБ", bytes / 1024.0);
            if (bytes < 1073741824) return string.Format("{0:0.##} МБ", bytes / 1048576.0);
            return string.Format("{0:0.##} ГБ", bytes / 1073741824.0);
        }
    }

    public class MailIndexEntry
    {
        public string Id { get; set; }
        public string AccountId { get; set; }
        public string FolderRaw { get; set; }
        public string FolderDisplay { get; set; }
        public long Uid { get; set; }
        public long UidValidity { get; set; }
        public string MessageId { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public string Subject { get; set; }
        public DateTime DateUtc { get; set; }
        public long SizeBytes { get; set; }
        public bool IsRead { get; set; }
        public bool OnServer { get; set; }
        public bool IsArchived { get; set; }
        public string RelativePath { get; set; }
        public string Snippet { get; set; }
        public string SearchText { get; set; }
        public List<AttachmentBadge> Attachments { get; set; }

        [ScriptIgnore]
        internal object SearchCache { get; set; }

        public MailIndexEntry()
        {
            Attachments = new List<AttachmentBadge>();
            OnServer = true;
            IsArchived = false;
        }

        [ScriptIgnore]
        public bool HasAttachments
        {
            get { return Attachments != null && Attachments.Count > 0; }
        }

        [ScriptIgnore]
        public string FromShort
        {
            get
            {
                if (string.IsNullOrWhiteSpace(From)) return "(без отправителя)";
                int idx = From.IndexOf('<');
                if (idx > 1) return From.Substring(0, idx).Trim(' ', '"', '\'');
                return From.Trim();
            }
        }

        [ScriptIgnore]
        public string DateShort
        {
            get
            {
                DateTime local = DateUtc.ToLocalTime();
                if (local.Date == DateTime.Today) return local.ToString("HH:mm");
                if (local.Year == DateTime.Today.Year) return local.ToString("dd.MM");
                return local.ToString("dd.MM.yy");
            }
        }

        [ScriptIgnore]
        public string SizeShort
        {
            get { return AttachmentBadge.FormatSize(SizeBytes); }
        }

        [ScriptIgnore]
        public string StorageBadge
        {
            get
            {
                if (!OnServer) return "Архив";
                if (IsArchived) return "Сервер+Архив";
                return "Сервер";
            }
        }

        [ScriptIgnore]
        public string StorageBadgeBg
        {
            get
            {
                if (!OnServer) return "#DCFCE7";
                if (IsArchived) return "#DBEAFE";
                return "#F3F4F6";
            }
        }

        [ScriptIgnore]
        public string StorageBadgeFg
        {
            get
            {
                if (!OnServer) return "#15803D";
                if (IsArchived) return "#1D4ED8";
                return "#6B7280";
            }
        }
    }

    public class ContactEntry
    {
        public string Email { get; set; }
        public string DisplayName { get; set; }
        public int UseCount { get; set; }
        public DateTime LastUsedUtc { get; set; }

        [ScriptIgnore]
        public string Formatted
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DisplayName) && !DisplayName.Equals(Email, StringComparison.OrdinalIgnoreCase))
                {
                    return DisplayName + " <" + Email + ">";
                }
                return Email;
            }
        }
    }

    public class MailingListEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public List<string> Recipients { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public MailingListEntry()
        {
            Recipients = new List<string>();
        }

        [ScriptIgnore]
        public string DisplayLabel
        {
            get
            {
                int cnt = Recipients != null ? Recipients.Count : 0;
                return string.Format("{0} ({1})", string.IsNullOrWhiteSpace(Name) ? "(без названия)" : Name, cnt);
            }
        }
    }

    public class ParsedAttachment
    {
        public string FileName { get; set; }
        public string ContentType { get; set; }
        public string ContentId { get; set; }
        public bool IsInline { get; set; }
        public byte[] Data { get; set; }

        public string ShortName
        {
            get { return AttachmentBadge.MakeShortName(FileName, 22); }
        }

        public string SizeText
        {
            get { return AttachmentBadge.FormatSize(Data != null ? Data.Length : 0); }
        }

        public bool IsImage
        {
            get
            {
                string ct = (ContentType ?? string.Empty).ToLowerInvariant();
                string ext = Path.GetExtension(FileName ?? string.Empty).ToLowerInvariant();
                return ct.StartsWith("image/") || ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".bmp" || ext == ".webp";
            }
        }
    }

    public class ParsedEmail
    {
        public string From { get; set; }
        public string To { get; set; }
        public string Cc { get; set; }
        public string Subject { get; set; }
        public string MessageId { get; set; }
        public string References { get; set; }
        public DateTime DateUtc { get; set; }
        public string TextBody { get; set; }
        public string HtmlBody { get; set; }
        public List<ParsedAttachment> Attachments { get; set; }

        public ParsedEmail()
        {
            Attachments = new List<ParsedAttachment>();
            DateUtc = DateTime.UtcNow;
        }
    }
}
