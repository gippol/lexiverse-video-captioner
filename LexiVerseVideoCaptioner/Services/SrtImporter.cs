using System.IO;
using System.Text.RegularExpressions;
using LexiVerseVideoCaptioner.Models;

namespace LexiVerseVideoCaptioner.Services;

public static class SrtImporter
{
    private static readonly Regex TimePattern = new(
        @"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})",
        RegexOptions.Compiled);

    public static List<SubtitleEntry> Import(string path)
    {
        var text = File.ReadAllText(path);
        // BOM 除去
        if (text.StartsWith('﻿')) text = text[1..];
        return Parse(text);
    }

    private static List<SubtitleEntry> Parse(string content)
    {
        var entries = new List<SubtitleEntry>();
        var blocks = content.Split(
            new[] { "\r\n\r\n", "\n\n" },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var lines = block.Trim().Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.None);

            if (lines.Length < 3) continue;
            if (!int.TryParse(lines[0].Trim(), out int index)) continue;

            var match = TimePattern.Match(lines[1]);
            if (!match.Success) continue;

            var start = new TimeSpan(0,
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value),
                int.Parse(match.Groups[4].Value));

            var end = new TimeSpan(0,
                int.Parse(match.Groups[5].Value),
                int.Parse(match.Groups[6].Value),
                int.Parse(match.Groups[7].Value),
                int.Parse(match.Groups[8].Value));

            var text = string.Join("\n", lines.Skip(2)).Trim();

            entries.Add(new SubtitleEntry
            {
                Index     = index,
                StartTime = start,
                EndTime   = end,
                Text      = text
            });
        }

        return entries;
    }
}
