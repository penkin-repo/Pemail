using System;
using System.Collections.Generic;
using System.Text;
using Pemail.Models;

namespace Pemail.Storage
{
    internal sealed class SearchEntryCache
    {
        public string FromNorm;
        public string ToNorm;
        public string AttNorm;
        public string HeaderNorm;
        public string BodyNorm;
        public string[] Words;

        public static SearchEntryCache GetOrBuild(MailIndexEntry entry)
        {
            SearchEntryCache cache = entry.SearchCache as SearchEntryCache;
            if (cache != null) return cache;

            cache = new SearchEntryCache();
            cache.FromNorm = SmartSearch.Normalize(entry.From);
            cache.ToNorm = SmartSearch.Normalize(entry.To);
            string subjNorm = SmartSearch.Normalize(entry.Subject);

            StringBuilder attSb = new StringBuilder();
            if (entry.Attachments != null && entry.Attachments.Count > 0)
            {
                for (int i = 0; i < entry.Attachments.Count; i++)
                {
                    AttachmentBadge att = entry.Attachments[i];
                    if (att != null && !string.IsNullOrEmpty(att.FileName))
                    {
                        if (attSb.Length > 0) attSb.Append(' ');
                        attSb.Append(SmartSearch.Normalize(att.FileName));
                    }
                }
            }
            cache.AttNorm = attSb.ToString();

            StringBuilder hdrSb = new StringBuilder(
                cache.FromNorm.Length + cache.ToNorm.Length + subjNorm.Length + cache.AttNorm.Length + 4);
            hdrSb.Append(cache.FromNorm).Append(' ')
                 .Append(cache.ToNorm).Append(' ')
                 .Append(subjNorm).Append(' ')
                 .Append(cache.AttNorm);
            cache.HeaderNorm = hdrSb.ToString();
            cache.BodyNorm = SmartSearch.Normalize(entry.SearchText);

            HashSet<string> wordSet = new HashSet<string>(StringComparer.Ordinal);
            ExtractWordsInto(cache.HeaderNorm, wordSet, 200);
            ExtractWordsInto(cache.BodyNorm, wordSet, 500);

            string[] words = new string[wordSet.Count];
            wordSet.CopyTo(words);
            cache.Words = words;

            entry.SearchCache = cache;
            return cache;
        }

        private static void ExtractWordsInto(string text, HashSet<string> target, int maxTotal)
        {
            if (string.IsNullOrEmpty(text) || target.Count >= maxTotal) return;
            int len = text.Length;
            int start = -1;
            for (int i = 0; i <= len; i++)
            {
                bool isWordChar = i < len && char.IsLetterOrDigit(text[i]);
                if (isWordChar)
                {
                    if (start < 0) start = i;
                }
                else if (start >= 0)
                {
                    int wLen = i - start;
                    if (wLen >= 3 && wLen <= 40)
                    {
                        target.Add(text.Substring(start, wLen));
                        if (target.Count >= maxTotal) return;
                    }
                    start = -1;
                }
            }
        }
    }

    internal enum SearchFieldScope
    {
        Any,
        From,
        To,
        Attachment
    }

    internal sealed class SearchToken
    {
        public SearchFieldScope Scope;
        public string Term;
        public string Swapped;
        public bool IsQuoted;
        public bool AllowFuzzy;
    }

    internal static class SmartSearch
    {
        private const string EnLayout = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
        private const string RuLayout = "йцукенгшщзхъфывапролджэячсмитьбюе";

        public static string Normalize(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            string lower = input.ToLowerInvariant();
            if (lower.IndexOf('ё') >= 0)
            {
                lower = lower.Replace('ё', 'е');
            }
            return lower;
        }

        public static string SwapKeyboardLayout(string normalized)
        {
            if (string.IsNullOrEmpty(normalized)) return null;
            if (normalized.IndexOf('@') >= 0) return null;

            int enCount = 0;
            int ruCount = 0;
            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                if (c >= 'a' && c <= 'z') enCount++;
                else if (c >= 'а' && c <= 'я') ruCount++;
            }

            if (enCount == 0 && ruCount == 0) return null;
            if (enCount > 0 && ruCount > 0) return null;

            bool enToRu = enCount > 0;
            StringBuilder sb = new StringBuilder(normalized.Length);
            int convertedLetters = 0;

            for (int i = 0; i < normalized.Length; i++)
            {
                char c = normalized[i];
                int idx = enToRu ? EnLayout.IndexOf(c) : RuLayout.IndexOf(c);
                if (idx >= 0)
                {
                    char mapped = enToRu ? RuLayout[idx] : EnLayout[idx];
                    sb.Append(mapped);
                    if (char.IsLetter(mapped)) convertedLetters++;
                }
                else
                {
                    sb.Append(c);
                }
            }

            if (convertedLetters < 2) return null;
            string result = sb.ToString();
            return string.Equals(result, normalized, StringComparison.Ordinal) ? null : result;
        }

        public static List<SearchToken> ParseQuery(string rawQuery)
        {
            List<SearchToken> tokens = new List<SearchToken>();
            if (string.IsNullOrWhiteSpace(rawQuery)) return tokens;

            int i = 0;
            int len = rawQuery.Length;
            while (i < len)
            {
                while (i < len && char.IsWhiteSpace(rawQuery[i])) i++;
                if (i >= len) break;

                bool quoted = false;
                string part;
                if (rawQuery[i] == '"')
                {
                    quoted = true;
                    i++;
                    int start = i;
                    while (i < len && rawQuery[i] != '"') i++;
                    part = rawQuery.Substring(start, i - start).Trim();
                    if (i < len && rawQuery[i] == '"') i++;
                }
                else
                {
                    int start = i;
                    while (i < len && !char.IsWhiteSpace(rawQuery[i])) i++;
                    part = rawQuery.Substring(start, i - start).Trim();
                }

                if (string.IsNullOrEmpty(part)) continue;

                SearchFieldScope scope = SearchFieldScope.Any;
                if (!quoted)
                {
                    part = ExtractFieldPrefix(part, out scope);
                    if (string.IsNullOrEmpty(part)) continue;
                }

                string norm = Normalize(part);
                if (string.IsNullOrEmpty(norm)) continue;

                string swapped = quoted ? null : SwapKeyboardLayout(norm);
                bool allowFuzzy = !quoted && norm.Length >= 4 && norm.IndexOf('@') < 0 && CountLetters(norm) >= 3;

                SearchToken tok = new SearchToken();
                tok.Scope = scope;
                tok.Term = norm;
                tok.Swapped = swapped;
                tok.IsQuoted = quoted;
                tok.AllowFuzzy = allowFuzzy;
                tokens.Add(tok);
            }

            return tokens;
        }

        public static bool MatchEntry(MailIndexEntry entry, List<SearchToken> tokens, out bool allExact)
        {
            allExact = true;
            if (tokens == null || tokens.Count == 0) return true;

            SearchEntryCache cache = SearchEntryCache.GetOrBuild(entry);

            for (int i = 0; i < tokens.Count; i++)
            {
                SearchToken tok = tokens[i];
                bool exactHit;
                if (!MatchToken(cache, tok, out exactHit))
                {
                    allExact = false;
                    return false;
                }
                if (!exactHit) allExact = false;
            }

            return true;
        }

        public static bool MatchesContact(ContactEntry contact, string queryNorm, string swappedNorm, out int priority)
        {
            priority = 0;
            if (contact == null) return false;
            if (string.IsNullOrEmpty(queryNorm))
            {
                priority = 1;
                return true;
            }

            string emailNorm = Normalize(contact.Email);
            string nameNorm = Normalize(contact.DisplayName);

            // 1. Точное совпадение с начала email (например "42" -> "422000@list.ru")
            if (emailNorm.StartsWith(queryNorm, StringComparison.Ordinal))
            {
                priority = 5;
                return true;
            }

            // 2. Совпадение с начала имени или любого слова в имени (например "дюж" -> "Иван Дюжев")
            if (StartsWithWord(nameNorm, queryNorm))
            {
                priority = 4;
                return true;
            }

            // 3. Подстрока в email или имени
            if (emailNorm.IndexOf(queryNorm, StringComparison.Ordinal) >= 0 ||
                nameNorm.IndexOf(queryNorm, StringComparison.Ordinal) >= 0)
            {
                priority = 3;
                return true;
            }

            // 4. Совпадение с исправленной раскладкой клавиатуры
            if (!string.IsNullOrEmpty(swappedNorm))
            {
                if (emailNorm.StartsWith(swappedNorm, StringComparison.Ordinal) ||
                    StartsWithWord(nameNorm, swappedNorm))
                {
                    priority = 3;
                    return true;
                }
                if (emailNorm.IndexOf(swappedNorm, StringComparison.Ordinal) >= 0 ||
                    nameNorm.IndexOf(swappedNorm, StringComparison.Ordinal) >= 0)
                {
                    priority = 2;
                    return true;
                }
            }

            // 5. Нечёткое совпадение с 1 опечаткой (от 4 букв)
            if (queryNorm.Length >= 4 && queryNorm.IndexOf('@') < 0 && CountLetters(queryNorm) >= 3)
            {
                int atIdx = emailNorm.IndexOf('@');
                string localPart = atIdx > 0 ? emailNorm.Substring(0, atIdx) : emailNorm;
                if (FuzzyMatchesTextWords(nameNorm, queryNorm, 1) ||
                    FuzzyMatchesTextWords(localPart, queryNorm, 1))
                {
                    priority = 1;
                    return true;
                }
                if (!string.IsNullOrEmpty(swappedNorm) &&
                    (FuzzyMatchesTextWords(nameNorm, swappedNorm, 1) ||
                     FuzzyMatchesTextWords(localPart, swappedNorm, 1)))
                {
                    priority = 1;
                    return true;
                }
            }

            return false;
        }

        private static bool MatchToken(SearchEntryCache cache, SearchToken tok, out bool exactHit)
        {
            exactHit = true;

            if (tok.Scope == SearchFieldScope.From)
            {
                if (ContainsEither(cache.FromNorm, tok.Term, tok.Swapped)) return true;
                if (tok.AllowFuzzy)
                {
                    int maxDist = tok.Term.Length >= 7 ? 2 : 1;
                    if (FuzzyMatchesTextWords(cache.FromNorm, tok.Term, maxDist) ||
                        (!string.IsNullOrEmpty(tok.Swapped) && FuzzyMatchesTextWords(cache.FromNorm, tok.Swapped, maxDist)))
                    {
                        exactHit = false;
                        return true;
                    }
                }
                return false;
            }

            if (tok.Scope == SearchFieldScope.To)
            {
                if (ContainsEither(cache.ToNorm, tok.Term, tok.Swapped)) return true;
                if (tok.AllowFuzzy)
                {
                    int maxDist = tok.Term.Length >= 7 ? 2 : 1;
                    if (FuzzyMatchesTextWords(cache.ToNorm, tok.Term, maxDist) ||
                        (!string.IsNullOrEmpty(tok.Swapped) && FuzzyMatchesTextWords(cache.ToNorm, tok.Swapped, maxDist)))
                    {
                        exactHit = false;
                        return true;
                    }
                }
                return false;
            }

            if (tok.Scope == SearchFieldScope.Attachment)
            {
                if (ContainsEither(cache.AttNorm, tok.Term, tok.Swapped)) return true;
                if (tok.AllowFuzzy)
                {
                    int maxDist = tok.Term.Length >= 7 ? 2 : 1;
                    if (FuzzyMatchesTextWords(cache.AttNorm, tok.Term, maxDist) ||
                        (!string.IsNullOrEmpty(tok.Swapped) && FuzzyMatchesTextWords(cache.AttNorm, tok.Swapped, maxDist)))
                    {
                        exactHit = false;
                        return true;
                    }
                }
                return false;
            }

            // Общий поиск по заголовкам, вложениям и телу письма:
            // Сначала быстрый точный проход (IndexOf)
            if (cache.HeaderNorm.IndexOf(tok.Term, StringComparison.Ordinal) >= 0 ||
                cache.BodyNorm.IndexOf(tok.Term, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(tok.Swapped))
            {
                if (cache.HeaderNorm.IndexOf(tok.Swapped, StringComparison.Ordinal) >= 0 ||
                    cache.BodyNorm.IndexOf(tok.Swapped, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            // Если точного совпадения нет — нечёткий проход по уникальным словам письма
            if (tok.AllowFuzzy && cache.Words != null && cache.Words.Length > 0)
            {
                int maxDist = tok.Term.Length >= 7 ? 2 : 1;
                if (FuzzyMatchesWordArray(cache.Words, tok.Term, maxDist))
                {
                    exactHit = false;
                    return true;
                }
                if (!string.IsNullOrEmpty(tok.Swapped) && FuzzyMatchesWordArray(cache.Words, tok.Swapped, maxDist))
                {
                    exactHit = false;
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsEither(string haystack, string term, string swapped)
        {
            if (string.IsNullOrEmpty(haystack)) return false;
            if (haystack.IndexOf(term, StringComparison.Ordinal) >= 0) return true;
            if (!string.IsNullOrEmpty(swapped) && haystack.IndexOf(swapped, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static bool StartsWithWord(string text, string prefix)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(prefix)) return false;
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return true;
            int idx = text.IndexOf(prefix, StringComparison.Ordinal);
            while (idx > 0)
            {
                if (!char.IsLetterOrDigit(text[idx - 1])) return true;
                idx = text.IndexOf(prefix, idx + 1, StringComparison.Ordinal);
            }
            return false;
        }

        private static bool FuzzyMatchesWordArray(string[] words, string query, int maxDist)
        {
            int qLen = query.Length;
            int minLen = qLen - maxDist;
            if (minLen < 3) minLen = 3;
            int maxFullLen = qLen + maxDist;
            bool allowPrefix = qLen >= 5;
            int maxPrefixWordLen = qLen + 3;

            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                int wLen = w.Length;
                if (wLen < minLen) continue;

                if (wLen <= maxFullLen)
                {
                    if (maxDist == 1 && !PassesFirstTwoCharsFilter(query, w)) continue;
                    if (DamerauLevenshteinWithin(query, w, wLen, maxDist)) return true;
                }
                else if (allowPrefix && wLen <= maxPrefixWordLen)
                {
                    if (!PassesFirstTwoCharsFilter(query, w)) continue;
                    if (DamerauLevenshteinWithin(query, w, qLen, 1)) return true;
                }
            }
            return false;
        }

        private static bool FuzzyMatchesTextWords(string text, string query, int maxDist)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int len = text.Length;
            int qLen = query.Length;
            int minLen = qLen - maxDist;
            if (minLen < 3) minLen = 3;
            int maxFullLen = qLen + maxDist;
            bool allowPrefix = qLen >= 5;
            int maxPrefixWordLen = qLen + 3;

            int start = -1;
            for (int i = 0; i <= len; i++)
            {
                bool isWordChar = i < len && char.IsLetterOrDigit(text[i]);
                if (isWordChar)
                {
                    if (start < 0) start = i;
                }
                else if (start >= 0)
                {
                    int wLen = i - start;
                    if (wLen >= minLen)
                    {
                        if (wLen <= maxFullLen)
                        {
                            string w = text.Substring(start, wLen);
                            if ((maxDist > 1 || PassesFirstTwoCharsFilter(query, w)) &&
                                DamerauLevenshteinWithin(query, w, wLen, maxDist))
                            {
                                return true;
                            }
                        }
                        else if (allowPrefix && wLen <= maxPrefixWordLen)
                        {
                            string w = text.Substring(start, qLen);
                            if (PassesFirstTwoCharsFilter(query, w) &&
                                DamerauLevenshteinWithin(query, w, qLen, 1))
                            {
                                return true;
                            }
                        }
                    }
                    start = -1;
                }
            }
            return false;
        }

        // Для расстояния редактирования <= 1 хотя бы один из первых двух символов обязан совпасть
        private static bool PassesFirstTwoCharsFilter(string a, string b)
        {
            char a0 = a[0];
            char a1 = a[1];
            char b0 = b[0];
            char b1 = b[1];
            return a0 == b0 || a0 == b1 || a1 == b0 || a1 == b1;
        }

        // Ограниченное расстояние Дамерау–Левенштейна (включая перестановку соседних букв)
        private static bool DamerauLevenshteinWithin(string a, string b, int bLen, int maxDist)
        {
            int aLen = a.Length;
            if (Math.Abs(aLen - bLen) > maxDist) return false;

            int[] prev2 = new int[bLen + 1];
            int[] prev1 = new int[bLen + 1];
            int[] curr = new int[bLen + 1];

            for (int j = 0; j <= bLen; j++) prev1[j] = j;

            for (int i = 1; i <= aLen; i++)
            {
                curr[0] = i;
                int rowMin = curr[0];
                char ac = a[i - 1];

                for (int j = 1; j <= bLen; j++)
                {
                    int cost = (ac == b[j - 1]) ? 0 : 1;
                    int del = prev1[j] + 1;
                    int ins = curr[j - 1] + 1;
                    int sub = prev1[j - 1] + cost;

                    int best = del < ins ? del : ins;
                    if (sub < best) best = sub;

                    if (i > 1 && j > 1 && ac == b[j - 2] && a[i - 2] == b[j - 1])
                    {
                        int trans = prev2[j - 2] + 1;
                        if (trans < best) best = trans;
                    }

                    curr[j] = best;
                    if (best < rowMin) rowMin = best;
                }

                if (rowMin > maxDist) return false;

                int[] tmp = prev2;
                prev2 = prev1;
                prev1 = curr;
                curr = tmp;
            }

            return prev1[bLen] <= maxDist;
        }

        private static string ExtractFieldPrefix(string part, out SearchFieldScope scope)
        {
            scope = SearchFieldScope.Any;
            int colon = part.IndexOf(':');
            if (colon <= 0 || colon >= part.Length - 1) return part;

            string prefix = Normalize(part.Substring(0, colon));
            string rest = part.Substring(colon + 1);
            if (prefix == "from" || prefix == "от")
            {
                scope = SearchFieldScope.From;
                return rest;
            }
            if (prefix == "to" || prefix == "кому")
            {
                scope = SearchFieldScope.To;
                return rest;
            }
            if (prefix == "file" || prefix == "файл" || prefix == "att")
            {
                scope = SearchFieldScope.Attachment;
                return rest;
            }
            return part;
        }

        private static int CountLetters(string s)
        {
            int c = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsLetter(s[i])) c++;
            }
            return c;
        }
    }
}
