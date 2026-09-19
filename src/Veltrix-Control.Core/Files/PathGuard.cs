using System.Text;

namespace VeltrixControl.Core.Files;

public static class PathGuard
{
    public static string ResolveWithinRoot(string root, string? relativePath, bool allowRoot = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        if (relativePath is not null)
        {
            if (relativePath.Contains('\0'))
            {
                throw new UnauthorizedAccessException("The requested path contains invalid characters.");
            }
            if (relativePath.Contains(':'))
            {
                throw new UnauthorizedAccessException("The requested path is outside the managed root.");
            }
            if (Path.IsPathRooted(relativePath))
            {
                throw new UnauthorizedAccessException("The requested path is outside the managed root.");
            }
        }

        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath ?? string.Empty));
        if (!IsWithin(fullRoot, candidate))
        {
            throw new UnauthorizedAccessException("The requested path is outside the managed root.");
        }
        if (!allowRoot && string.Equals(fullRoot, candidate, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The managed root itself cannot be modified by this operation.");
        }

        EnsureNoReparseEscape(fullRoot, candidate);
        return candidate;
    }

    public static string ResolveArchiveEntry(string root, string entryName, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            throw new UnauthorizedAccessException("The archive contains an empty entry name.");
        }

        var normalized = entryName.Replace('\\', Path.DirectorySeparatorChar);
        if (normalized.Contains('\0'))
        {
            throw new UnauthorizedAccessException("The archive contains an invalid entry name.");
        }
        return ResolveWithinRoot(root, normalized, allowRoot);
    }

    public static bool IsWithin(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        if (string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase)) return true;
        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullCandidate.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string RelativeToRoot(string root, string fullPath) =>
        Path.GetRelativePath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), Path.GetFullPath(fullPath));

    private static void EnsureNoReparseEscape(string fullRoot, string candidate)
    {
        var relative = Path.GetRelativePath(fullRoot, candidate);
        if (relative == ".") return;

        var current = fullRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") throw new UnauthorizedAccessException("The requested path is outside the managed root.");
            current = Path.Combine(current, segment);

            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) break;

            var target = info.ResolveLinkTarget(true);
            if (target is not null)
            {
                var targetPath = Path.GetFullPath(target.FullName);
                if (!IsWithin(fullRoot, targetPath))
                {
                    throw new UnauthorizedAccessException("The requested path escapes the managed root through a reparse point.");
                }
                current = targetPath;
            }
        }
    }

    public static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    public static Encoding DetectEncoding(Stream stream, out int preambleLength)
    {
        Span<byte> preamble = stackalloc byte[4];
        var read = stream.Read(preamble);
        stream.Position = 0;
        preambleLength = 0;
        if (read >= 3 && preamble[0] == 0xEF && preamble[1] == 0xBB && preamble[2] == 0xBF)
        {
            preambleLength = 3;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }
        if (read >= 4 && preamble[0] == 0xFF && preamble[1] == 0xFE && preamble[2] == 0x00 && preamble[3] == 0x00)
        {
            preambleLength = 4;
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        }
        if (read >= 2 && preamble[0] == 0xFF && preamble[1] == 0xFE)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }
        if (read >= 2 && preamble[0] == 0xFE && preamble[1] == 0xFF)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }
        if (read >= 4 && preamble[0] == 0x00 && preamble[1] == 0x00 && preamble[2] == 0xFE && preamble[3] == 0xFF)
        {
            preambleLength = 4;
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
