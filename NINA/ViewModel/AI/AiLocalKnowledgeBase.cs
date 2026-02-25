#region "copyright"

/*
    Copyright (c) 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NINA.ViewModel.AI {

    public class AiLocalKnowledgeBase : IAiKnowledgeBase {
        private static readonly object FileLock = new object();
        private static readonly HashSet<string> SupportedDocExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".md",
            ".txt",
            ".json",
            ".yaml",
            ".yml"
        };

        private readonly string docsDirectory;
        private readonly string memoryFilePath;
        private readonly string eventsFilePath;

        public AiLocalKnowledgeBase() : this(Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AI", "KnowledgeBase")) {
        }

        public AiLocalKnowledgeBase(string rootDirectory) {
            RootDirectory = rootDirectory ?? Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AI", "KnowledgeBase");
            docsDirectory = Path.Combine(RootDirectory, "docs");
            memoryFilePath = Path.Combine(RootDirectory, "memory.md");
            eventsFilePath = Path.Combine(RootDirectory, "events.jsonl");
        }

        public string RootDirectory { get; }

        public Task<string> BuildContextAsync(string prompt, int maxChars = 2600) {
            return Task.Run(() => {
                try {
                    EnsureInitialized();

                    var builder = new StringBuilder();
                    var remaining = Math.Max(400, maxChars);

                    AppendSection(builder, "Local memory", ReadMemorySection(900), ref remaining);
                    AppendSection(builder, "Relevant docs", ReadDocsSection(prompt, 2, 1200), ref remaining);
                    AppendSection(builder, "Recent events", ReadEventsSection(prompt, 10, 900), ref remaining);

                    var context = builder.ToString().Trim();
                    if (context.Length > maxChars) {
                        context = context.Substring(0, maxChars);
                    }
                    return context;
                } catch (Exception ex) {
                    Logger.Error("Failed to build AI local knowledge context", ex);
                    return string.Empty;
                }
            });
        }

        public Task AppendRecordAsync(AiKnowledgeRecord record) {
            if (record == null) {
                return Task.CompletedTask;
            }

            return Task.Run(() => {
                try {
                    EnsureInitialized();
                    var line = JsonConvert.SerializeObject(record);
                    lock (FileLock) {
                        File.AppendAllText(eventsFilePath, line + Environment.NewLine, Encoding.UTF8);
                    }
                } catch (Exception ex) {
                    Logger.Error("Failed to append AI knowledge record", ex);
                }
            });
        }

        private void EnsureInitialized() {
            if (!Directory.Exists(RootDirectory)) {
                Directory.CreateDirectory(RootDirectory);
            }
            if (!Directory.Exists(docsDirectory)) {
                Directory.CreateDirectory(docsDirectory);
            }

            if (!File.Exists(memoryFilePath)) {
                var initial = "# AI Local Memory\n\n- Put long-lived constraints and site-specific operating notes here.\n";
                File.WriteAllText(memoryFilePath, initial, Encoding.UTF8);
            }

            if (!File.Exists(eventsFilePath)) {
                File.WriteAllText(eventsFilePath, string.Empty, Encoding.UTF8);
            }
        }

        private string ReadMemorySection(int maxChars) {
            try {
                var content = File.ReadAllText(memoryFilePath, Encoding.UTF8);
                return Clamp(content, maxChars);
            } catch {
                return string.Empty;
            }
        }

        private string ReadDocsSection(string prompt, int maxDocs, int maxTotalChars) {
            try {
                var candidates = Directory.GetFiles(docsDirectory, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(path => SupportedDocExtensions.Contains(Path.GetExtension(path)))
                    .Select(path => BuildDocCandidate(path, prompt))
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => SafeGetLastWriteUtc(x.Path))
                    .Take(Math.Max(1, maxDocs))
                    .ToList();

                if (candidates.Count == 0) {
                    return string.Empty;
                }

                var builder = new StringBuilder();
                var remaining = maxTotalChars;
                foreach (var doc in candidates) {
                    if (remaining <= 80) {
                        break;
                    }

                    var excerpt = Clamp(doc.Content, Math.Min(700, remaining - 40));
                    if (string.IsNullOrWhiteSpace(excerpt)) {
                        continue;
                    }

                    var line = $"[{Path.GetFileName(doc.Path)}]\n{excerpt}\n";
                    if (line.Length > remaining) {
                        line = Clamp(line, remaining);
                    }
                    builder.AppendLine(line.TrimEnd());
                    remaining -= line.Length;
                }

                return builder.ToString().Trim();
            } catch {
                return string.Empty;
            }
        }

        private string ReadEventsSection(string prompt, int maxEvents, int maxTotalChars) {
            try {
                if (!File.Exists(eventsFilePath)) {
                    return string.Empty;
                }

                var tokens = Tokenize(prompt);
                var events = new List<AiKnowledgeRecord>();
                foreach (var line in ReadLastLines(eventsFilePath, 120)) {
                    if (string.IsNullOrWhiteSpace(line)) {
                        continue;
                    }
                    try {
                        var record = JsonConvert.DeserializeObject<AiKnowledgeRecord>(line);
                        if (record != null) {
                            events.Add(record);
                        }
                    } catch {
                    }
                }

                var selected = events
                    .OrderByDescending(e => ComputeKeywordScore(tokens, e.Prompt + " " + e.Summary))
                    .ThenByDescending(e => e.TimestampUtc)
                    .Take(Math.Max(1, maxEvents))
                    .OrderByDescending(e => e.TimestampUtc)
                    .ToList();

                if (selected.Count == 0) {
                    return string.Empty;
                }

                var builder = new StringBuilder();
                var remaining = maxTotalChars;
                foreach (var record in selected) {
                    if (remaining <= 80) {
                        break;
                    }

                    var summary = Clamp(record.Summary, 220);
                    if (string.IsNullOrWhiteSpace(summary)) {
                        continue;
                    }

                    var line = $"{record.TimestampUtc:O} [{record.Type}] {summary}";
                    if (line.Length > remaining) {
                        line = Clamp(line, remaining);
                    }
                    builder.AppendLine(line);
                    remaining -= line.Length + 1;
                }

                return builder.ToString().Trim();
            } catch {
                return string.Empty;
            }
        }

        private static void AppendSection(StringBuilder builder, string title, string content, ref int remaining) {
            if (remaining <= 0 || string.IsNullOrWhiteSpace(content)) {
                return;
            }

            var section = $"[{title}]\n{content.Trim()}\n";
            if (section.Length > remaining) {
                section = Clamp(section, remaining);
            }
            if (string.IsNullOrWhiteSpace(section)) {
                return;
            }

            builder.AppendLine(section.TrimEnd());
            builder.AppendLine();
            remaining -= section.Length + 1;
        }

        private static string SafeReadAllText(string filePath) {
            try {
                return File.ReadAllText(filePath, Encoding.UTF8);
            } catch {
                return string.Empty;
            }
        }

        private static DateTime SafeGetLastWriteUtc(string filePath) {
            try {
                return File.GetLastWriteTimeUtc(filePath);
            } catch {
                return DateTime.MinValue;
            }
        }

        private static IEnumerable<string> ReadLastLines(string path, int lineCount) {
            var queue = new Queue<string>();
            foreach (var line in File.ReadLines(path, Encoding.UTF8)) {
                queue.Enqueue(line);
                if (queue.Count > lineCount) {
                    queue.Dequeue();
                }
            }
            return queue.ToList();
        }

        private static string Clamp(string text, int maxChars) {
            if (string.IsNullOrWhiteSpace(text) || maxChars <= 0) {
                return string.Empty;
            }
            var normalized = text.Trim();
            return normalized.Length <= maxChars ? normalized : normalized.Substring(0, maxChars);
        }

        private static int ComputeKeywordScore(string prompt, string text) {
            return ComputeKeywordScore(Tokenize(prompt), text);
        }

        private static int ComputeKeywordScore(IEnumerable<string> tokens, string text) {
            if (tokens == null || string.IsNullOrWhiteSpace(text)) {
                return 0;
            }

            var hay = text.ToLowerInvariant();
            var score = 0;
            foreach (var token in tokens) {
                if (!string.IsNullOrWhiteSpace(token) && hay.Contains(token, StringComparison.OrdinalIgnoreCase)) {
                    score++;
                }
            }
            return score;
        }

        private static IReadOnlyList<string> Tokenize(string text) {
            if (string.IsNullOrWhiteSpace(text)) {
                return Array.Empty<string>();
            }
            return Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9_\u4e00-\u9fff]{2,}")
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
        }

        private static DocCandidate BuildDocCandidate(string path, string prompt) {
            var content = SafeReadAllText(path);
            var scoreText = Path.GetFileNameWithoutExtension(path) + " " + Clamp(content, 1800);
            return new DocCandidate {
                Path = path,
                Score = ComputeKeywordScore(prompt, scoreText),
                Content = content
            };
        }

        private sealed class DocCandidate {
            public string Path { get; set; } = string.Empty;
            public int Score { get; set; }
            public string Content { get; set; } = string.Empty;
        }
    }
}
