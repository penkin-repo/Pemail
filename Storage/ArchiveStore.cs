using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Pemail.Mime;
using Pemail.Models;

namespace Pemail.Storage
{
    public class ArchiveStore
    {
        private readonly string _rootDir;
        private readonly string _archiveDir;
        private readonly string _accountsFile;
        private readonly string _indexFile;
        private readonly string _contactsFile;
        private readonly object _sync = new object();
        private readonly JavaScriptSerializer _json;

        public List<AccountConfig> Accounts { get; private set; }
        public List<MailIndexEntry> Entries { get; private set; }
        public List<ContactEntry> Contacts { get; private set; }
        public bool CompressArchive { get; set; }

        public string RootDirectory { get { return _rootDir; } }

        public ArchiveStore(string rootDir)
        {
            _rootDir = rootDir;
            _archiveDir = Path.Combine(_rootDir, "Archive");
            _accountsFile = Path.Combine(_rootDir, "accounts.json");
            _indexFile = Path.Combine(_rootDir, "index.json");
            _contactsFile = Path.Combine(_rootDir, "contacts.json");
            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = int.MaxValue;
            CompressArchive = true;
            Accounts = new List<AccountConfig>();
            Entries = new List<MailIndexEntry>();
            Contacts = new List<ContactEntry>();

            Directory.CreateDirectory(_rootDir);
            Directory.CreateDirectory(_archiveDir);
            LoadAll();
        }

        public void LoadAll()
        {
            lock (_sync)
            {
                if (File.Exists(_accountsFile))
                {
                    try
                    {
                        string text = File.ReadAllText(_accountsFile, Encoding.UTF8);
                        List<AccountConfig> loaded = _json.Deserialize<List<AccountConfig>>(text);
                        if (loaded != null) Accounts = loaded;
                    }
                    catch { Accounts = new List<AccountConfig>(); }
                }

                if (File.Exists(_indexFile))
                {
                    try
                    {
                        string text = File.ReadAllText(_indexFile, Encoding.UTF8);
                        List<MailIndexEntry> loaded = _json.Deserialize<List<MailIndexEntry>>(text);
                        if (loaded != null)
                        {
                            Entries = loaded;
                            // Миграция записей из предыдущей версии: если письма уже нет на сервере, считаем его архивным
                            foreach (MailIndexEntry e in Entries)
                            {
                                if (!e.OnServer) e.IsArchived = true;
                            }
                        }
                    }
                    catch { Entries = new List<MailIndexEntry>(); }
                }

                if (File.Exists(_contactsFile))
                {
                    try
                    {
                        string text = File.ReadAllText(_contactsFile, Encoding.UTF8);
                        List<ContactEntry> loaded = _json.Deserialize<List<ContactEntry>>(text);
                        if (loaded != null) Contacts = loaded;
                    }
                    catch { Contacts = new List<ContactEntry>(); }
                }
            }
        }

        public void SaveAccounts()
        {
            lock (_sync)
            {
                string tmp = _accountsFile + ".tmp";
                File.WriteAllText(tmp, _json.Serialize(Accounts), Encoding.UTF8);
                ReplaceFile(tmp, _accountsFile);
            }
        }

        public void SaveIndex()
        {
            lock (_sync)
            {
                string tmp = _indexFile + ".tmp";
                File.WriteAllText(tmp, _json.Serialize(Entries), Encoding.UTF8);
                ReplaceFile(tmp, _indexFile);
            }
        }

        public void SaveContacts()
        {
            lock (_sync)
            {
                string tmp = _contactsFile + ".tmp";
                File.WriteAllText(tmp, _json.Serialize(Contacts), Encoding.UTF8);
                ReplaceFile(tmp, _contactsFile);
            }
        }

        public void RecordSentContacts(string toHeader, string ccHeader)
        {
            List<KeyValuePair<string, string>> parsed = new List<KeyValuePair<string, string>>();
            parsed.AddRange(ParseAddressPairs(toHeader));
            parsed.AddRange(ParseAddressPairs(ccHeader));
            if (parsed.Count == 0) return;

            lock (_sync)
            {
                foreach (KeyValuePair<string, string> pair in parsed)
                {
                    string email = pair.Key;
                    string name = pair.Value;
                    ContactEntry existing = Contacts.FirstOrDefault(c => string.Equals(c.Email, email, StringComparison.OrdinalIgnoreCase));
                    if (existing == null)
                    {
                        existing = new ContactEntry
                        {
                            Email = email,
                            DisplayName = name,
                            UseCount = 1,
                            LastUsedUtc = DateTime.UtcNow
                        };
                        Contacts.Add(existing);
                    }
                    else
                    {
                        existing.UseCount++;
                        existing.LastUsedUtc = DateTime.UtcNow;
                        if (!string.IsNullOrWhiteSpace(name)) existing.DisplayName = name;
                    }
                }
            }
            SaveContacts();
        }

        public List<ContactEntry> SuggestContacts(string prefix, int maxCount)
        {
            string q = (prefix ?? string.Empty).Trim().ToLowerInvariant();
            Dictionary<string, ContactEntry> map = new Dictionary<string, ContactEntry>(StringComparer.OrdinalIgnoreCase);

            lock (_sync)
            {
                foreach (ContactEntry c in Contacts)
                {
                    if (string.IsNullOrWhiteSpace(c.Email)) continue;
                    map[c.Email] = new ContactEntry
                    {
                        Email = c.Email,
                        DisplayName = c.DisplayName,
                        UseCount = c.UseCount + 1000, // Приоритет тем, кому уже отправляли
                        LastUsedUtc = c.LastUsedUtc
                    };
                }

                // Дополняем адресами из истории писем (кому отправляли и от кого получали)
                foreach (MailIndexEntry e in Entries)
                {
                    AddHistoryAddresses(map, e.To, e.DateUtc, 2);
                    AddHistoryAddresses(map, e.From, e.DateUtc, 1);
                }
            }

            IEnumerable<ContactEntry> res = map.Values;
            if (!string.IsNullOrEmpty(q))
            {
                res = res.Where(c =>
                    (c.Email != null && c.Email.ToLowerInvariant().Contains(q)) ||
                    (c.DisplayName != null && c.DisplayName.ToLowerInvariant().Contains(q)));
            }

            return res
                .OrderByDescending(c => c.UseCount)
                .ThenByDescending(c => c.LastUsedUtc)
                .Take(maxCount)
                .ToList();
        }

        private static void AddHistoryAddresses(Dictionary<string, ContactEntry> map, string rawHeader, DateTime dtUtc, int weight)
        {
            if (string.IsNullOrWhiteSpace(rawHeader)) return;
            foreach (KeyValuePair<string, string> pair in ParseAddressPairs(rawHeader))
            {
                ContactEntry existing;
                if (!map.TryGetValue(pair.Key, out existing))
                {
                    map[pair.Key] = new ContactEntry
                    {
                        Email = pair.Key,
                        DisplayName = pair.Value,
                        UseCount = weight,
                        LastUsedUtc = dtUtc
                    };
                }
                else
                {
                    existing.UseCount += weight;
                    if (dtUtc > existing.LastUsedUtc) existing.LastUsedUtc = dtUtc;
                    if (string.IsNullOrWhiteSpace(existing.DisplayName) && !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        existing.DisplayName = pair.Value;
                    }
                }
            }
        }

        private static List<KeyValuePair<string, string>> ParseAddressPairs(string input)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(input)) return list;
            string[] parts = input.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string trimmed = raw.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                int lt = trimmed.IndexOf('<');
                int gt = trimmed.IndexOf('>');
                string email;
                string name = string.Empty;
                if (lt >= 0 && gt > lt)
                {
                    email = trimmed.Substring(lt + 1, gt - lt - 1).Trim();
                    name = trimmed.Substring(0, lt).Trim(' ', '"', '\'');
                }
                else
                {
                    email = trimmed;
                }
                if (email.Contains("@"))
                {
                    list.Add(new KeyValuePair<string, string>(email, name));
                }
            }
            return list;
        }

        public bool HasMessage(string accountId, string folderRaw, long uidValidity, long uid)
        {
            lock (_sync)
            {
                return Entries.Any(e =>
                    e.AccountId == accountId &&
                    string.Equals(e.FolderRaw, folderRaw, StringComparison.OrdinalIgnoreCase) &&
                    e.UidValidity == uidValidity &&
                    e.Uid == uid);
            }
        }

        /// <summary>
        /// Сверка локальной папки с актуальными UID на сервере:
        /// - Если письмо было явно заархивировано (IsArchived=true), просто снимаем флаг OnServer=false, локальная копия сохраняется.
        /// - Если письмо НЕ было заархивировано, но удалено на сервере (например, в веб-интерфейсе Яндекса/Gmail), удаляем его и локально.
        /// </summary>
        public int ReconcileFolderServerUids(string accountId, string folderRaw, long uidValidity, HashSet<long> serverUids)
        {
            List<MailIndexEntry> toRemove = new List<MailIndexEntry>();
            int changed = 0;

            lock (_sync)
            {
                foreach (MailIndexEntry e in Entries)
                {
                    if (e.AccountId != accountId) continue;
                    if (!string.Equals(e.FolderRaw, folderRaw, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!e.OnServer || e.Uid <= 0) continue;

                    bool missingOnServer = (e.UidValidity != uidValidity) || !serverUids.Contains(e.Uid);
                    if (!missingOnServer) continue;

                    if (e.IsArchived)
                    {
                        e.OnServer = false;
                        changed++;
                    }
                    else
                    {
                        toRemove.Add(e);
                        changed++;
                    }
                }

                foreach (MailIndexEntry rem in toRemove)
                {
                    Entries.Remove(rem);
                }
            }

            foreach (MailIndexEntry rem in toRemove)
            {
                TryDeleteLocalFile(rem);
            }

            return changed;
        }

        public void DeleteLocalEntry(MailIndexEntry entry)
        {
            if (entry == null) return;
            lock (_sync)
            {
                Entries.Remove(entry);
            }
            TryDeleteLocalFile(entry);
            SaveIndex();
        }

        private void TryDeleteLocalFile(MailIndexEntry entry)
        {
            try
            {
                if (entry == null || string.IsNullOrEmpty(entry.RelativePath)) return;
                string fullPath = Path.Combine(_archiveDir, entry.RelativePath);
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
            catch { }
        }

        public MailIndexEntry SaveMessage(string accountId, string folderRaw, string folderDisplay, long uidValidity, long uid, byte[] rawEml, bool isRead, bool onServer, bool isArchived = false)
        {
            ParsedEmail parsed = MimeParser.Parse(rawEml);
            DateTime dt = parsed.DateUtc == default(DateTime) ? DateTime.UtcNow : parsed.DateUtc;

            string safeFolder = SanitizePathPart(folderDisplay);
            string relDir = Path.Combine(accountId, safeFolder, dt.Year.ToString("D4"), dt.Month.ToString("D2"));
            string fullDir = Path.Combine(_archiveDir, relDir);
            Directory.CreateDirectory(fullDir);

            string ext = CompressArchive ? ".eml.gz" : ".eml";
            string fileName = uid > 0 ? (uid + ext) : (Guid.NewGuid().ToString("N").Substring(0, 12) + ext);
            string relPath = Path.Combine(relDir, fileName);
            string fullPath = Path.Combine(_archiveDir, relPath);

            if (CompressArchive)
            {
                using (FileStream fs = File.Create(fullPath))
                using (GZipStream gz = new GZipStream(fs, CompressionLevel.Fastest))
                {
                    gz.Write(rawEml, 0, rawEml.Length);
                }
            }
            else
            {
                File.WriteAllBytes(fullPath, rawEml);
            }

            string searchBody = MimeParser.ExtractPlainTextForSearch(parsed);
            string snippet = searchBody.Length > 140 ? searchBody.Substring(0, 140) + "…" : searchBody;

            MailIndexEntry entry = new MailIndexEntry();
            entry.Id = Guid.NewGuid().ToString("N").Substring(0, 12);
            entry.AccountId = accountId;
            entry.FolderRaw = folderRaw;
            entry.FolderDisplay = folderDisplay;
            entry.Uid = uid;
            entry.UidValidity = uidValidity;
            entry.MessageId = parsed.MessageId;
            entry.From = parsed.From;
            entry.To = parsed.To;
            entry.Subject = string.IsNullOrWhiteSpace(parsed.Subject) ? "(без темы)" : parsed.Subject;
            entry.DateUtc = dt;
            entry.SizeBytes = rawEml.Length;
            entry.IsRead = isRead;
            entry.OnServer = onServer;
            entry.IsArchived = isArchived || !onServer;
            entry.RelativePath = relPath;
            entry.Snippet = snippet;
            entry.SearchText = searchBody;
            entry.Attachments = new List<AttachmentBadge>();

            foreach (ParsedAttachment att in parsed.Attachments)
            {
                if (!att.IsInline)
                {
                    entry.Attachments.Add(AttachmentBadge.From(att.FileName, att.Data != null ? att.Data.Length : 0));
                }
            }

            lock (_sync)
            {
                Entries.Add(entry);
            }
            return entry;
        }

        public bool VerifyLocalCopy(MailIndexEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.RelativePath)) return false;
            string fullPath = Path.Combine(_archiveDir, entry.RelativePath);
            if (!File.Exists(fullPath)) return false;
            FileInfo fi = new FileInfo(fullPath);
            return fi.Length > 0;
        }

        public byte[] ReadRawEml(MailIndexEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.RelativePath)) return null;
            string fullPath = Path.Combine(_archiveDir, entry.RelativePath);
            if (!File.Exists(fullPath)) return null;

            if (fullPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using (FileStream fs = File.OpenRead(fullPath))
                using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
                using (MemoryStream ms = new MemoryStream())
                {
                    gz.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            return File.ReadAllBytes(fullPath);
        }

        public void ExportAsEml(MailIndexEntry entry, string targetFilePath)
        {
            byte[] raw = ReadRawEml(entry);
            if (raw == null) throw new FileNotFoundException("Локальный файл письма не найден.");
            File.WriteAllBytes(targetFilePath, raw);
        }

        public List<MailIndexEntry> Query(string accountId, string folderDisplay, string search, bool onlyWithAttachments, bool onlyLocal, bool onlyOnServer, bool onlyLarge)
        {
            lock (_sync)
            {
                IEnumerable<MailIndexEntry> q = Entries;
                if (!string.IsNullOrEmpty(accountId))
                {
                    q = q.Where(e => e.AccountId == accountId);
                }
                if (!string.IsNullOrEmpty(folderDisplay))
                {
                    q = q.Where(e => string.Equals(e.FolderDisplay, folderDisplay, StringComparison.OrdinalIgnoreCase));
                }
                if (onlyWithAttachments)
                {
                    q = q.Where(e => e.HasAttachments);
                }
                if (onlyLocal)
                {
                    q = q.Where(e => e.IsArchived || !e.OnServer);
                }
                if (onlyOnServer)
                {
                    q = q.Where(e => e.OnServer);
                }
                if (onlyLarge)
                {
                    q = q.Where(e => e.SizeBytes >= 1024 * 1024);
                }
                if (!string.IsNullOrWhiteSpace(search))
                {
                    string term = search.Trim().ToLowerInvariant();
                    q = q.Where(e => Matches(e, term));
                }
                return q.OrderByDescending(e => e.DateUtc).ToList();
            }
        }

        private static bool Matches(MailIndexEntry e, string term)
        {
            if (!string.IsNullOrEmpty(e.Subject) && e.Subject.ToLowerInvariant().Contains(term)) return true;
            if (!string.IsNullOrEmpty(e.From) && e.From.ToLowerInvariant().Contains(term)) return true;
            if (!string.IsNullOrEmpty(e.To) && e.To.ToLowerInvariant().Contains(term)) return true;
            if (!string.IsNullOrEmpty(e.SearchText) && e.SearchText.ToLowerInvariant().Contains(term)) return true;
            if (e.Attachments != null)
            {
                foreach (AttachmentBadge att in e.Attachments)
                {
                    if (!string.IsNullOrEmpty(att.FileName) && att.FileName.ToLowerInvariant().Contains(term)) return true;
                }
            }
            return false;
        }

        private static string SanitizePathPart(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Other";
            char[] invalid = Path.GetInvalidFileNameChars();
            string clean = name.Replace('/', '_').Replace('\\', '_');
            foreach (char c in invalid) clean = clean.Replace(c, '_');
            return clean.Trim();
        }

        private static void ReplaceFile(string tmpPath, string targetPath)
        {
            if (File.Exists(targetPath)) File.Delete(targetPath);
            File.Move(tmpPath, targetPath);
        }
    }
}
