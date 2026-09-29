using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace ZeroText.Matching
{
    /// <summary>
    /// Pure C# ultra-fast, zero-allocation fuzzy matching algorithms.
    /// Provides Levenshtein distance/similarity and Jaro-Winkler similarity with stack-allocated
    /// rolling buffers and zero heap allocations for typical strings (&lt;= 256 characters).
    /// </summary>
    public static class FastFuzzyMatch
    {
        private const int MaxStackallocThreshold = 256;

        /// <summary>
        /// Computes the Levenshtein edit distance between two character spans.
        /// Zero managed allocation for strings up to 256 characters.
        /// </summary>
        /// <param name="s1">First character sequence.</param>
        /// <param name="s2">Second character sequence.</param>
        /// <param name="ignoreCase">True to ignore casing; false for exact ordinal match.</param>
        /// <returns>Minimum number of single-character edits (insertions, deletions, or substitutions).</returns>
        public static int LevenshteinDistance(ReadOnlySpan<char> s1, ReadOnlySpan<char> s2, bool ignoreCase = false)
        {
            if (s1.IsEmpty) return s2.Length;
            if (s2.IsEmpty) return s1.Length;

            // Ensure s2 is the shorter sequence to minimize buffer requirements
            if (s1.Length < s2.Length)
            {
                var temp = s1;
                s1 = s2;
                s2 = temp;
            }

            int minLen = s2.Length;
            int bufferSize = minLen + 1;

            int[]? rented = null;
            Span<int> row = bufferSize <= MaxStackallocThreshold
                ? stackalloc int[bufferSize]
                : (rented = ArrayPool<int>.Shared.Rent(bufferSize)).AsSpan(0, bufferSize);

            try
            {
                for (int j = 0; j <= minLen; j++)
                {
                    row[j] = j;
                }

                for (int i = 1; i <= s1.Length; i++)
                {
                    char c1 = s1[i - 1];
                    int prevDiagonal = row[0];
                    row[0] = i;

                    for (int j = 1; j <= minLen; j++)
                    {
                        char c2 = s2[j - 1];
                        bool match = ignoreCase
                            ? char.ToUpperInvariant(c1) == char.ToUpperInvariant(c2)
                            : c1 == c2;

                        int cost = match ? 0 : 1;
                        int insertion = row[j] + 1;
                        int deletion = row[j - 1] + 1;
                        int substitution = prevDiagonal + cost;

                        prevDiagonal = row[j];
                        row[j] = Math.Min(Math.Min(insertion, deletion), substitution);
                    }
                }

                return row[minLen];
            }
            finally
            {
                if (rented != null)
                {
                    ArrayPool<int>.Shared.Return(rented);
                }
            }
        }

        /// <summary>
        /// Computes the normalized Levenshtein similarity ratio between 0.0 (completely dissimilar) and 1.0 (exact match).
        /// </summary>
        public static double LevenshteinSimilarity(ReadOnlySpan<char> s1, ReadOnlySpan<char> s2, bool ignoreCase = false)
        {
            if (s1.IsEmpty && s2.IsEmpty) return 1.0;
            int maxLen = Math.Max(s1.Length, s2.Length);
            if (maxLen == 0) return 1.0;

            int dist = LevenshteinDistance(s1, s2, ignoreCase);
            return 1.0 - ((double)dist / maxLen);
        }

        /// <summary>
        /// Computes the Jaro string similarity between 0.0 and 1.0.
        /// </summary>
        public static double JaroSimilarity(ReadOnlySpan<char> s1, ReadOnlySpan<char> s2, bool ignoreCase = false)
        {
            int len1 = s1.Length;
            int len2 = s2.Length;

            if (len1 == 0 && len2 == 0) return 1.0;
            if (len1 == 0 || len2 == 0) return 0.0;

            int matchWindow = Math.Max(Math.Max(len1, len2) / 2 - 1, 0);

            bool[]? rented1 = null;
            bool[]? rented2 = null;

            Span<bool> s1Matched = len1 <= MaxStackallocThreshold
                ? stackalloc bool[len1]
                : (rented1 = ArrayPool<bool>.Shared.Rent(len1)).AsSpan(0, len1);

            Span<bool> s2Matched = len2 <= MaxStackallocThreshold
                ? stackalloc bool[len2]
                : (rented2 = ArrayPool<bool>.Shared.Rent(len2)).AsSpan(0, len2);

            s1Matched.Clear();
            s2Matched.Clear();

            try
            {
                int matches = 0;

                for (int i = 0; i < len1; i++)
                {
                    char c1 = s1[i];
                    int start = Math.Max(0, i - matchWindow);
                    int end = Math.Min(i + matchWindow + 1, len2);

                    for (int j = start; j < end; j++)
                    {
                        if (s2Matched[j]) continue;

                        char c2 = s2[j];
                        bool match = ignoreCase
                            ? char.ToUpperInvariant(c1) == char.ToUpperInvariant(c2)
                            : c1 == c2;

                        if (match)
                        {
                            s1Matched[i] = true;
                            s2Matched[j] = true;
                            matches++;
                            break;
                        }
                    }
                }

                if (matches == 0) return 0.0;

                int k = 0;
                int transpositions = 0;

                for (int i = 0; i < len1; i++)
                {
                    if (!s1Matched[i]) continue;

                    while (!s2Matched[k]) k++;

                    char c1 = s1[i];
                    char c2 = s2[k];
                    bool match = ignoreCase
                        ? char.ToUpperInvariant(c1) == char.ToUpperInvariant(c2)
                        : c1 == c2;

                    if (!match)
                    {
                        transpositions++;
                    }

                    k++;
                }

                double halfTranspositions = transpositions / 2.0;
                return ((matches / (double)len1) + (matches / (double)len2) + ((matches - halfTranspositions) / matches)) / 3.0;
            }
            finally
            {
                if (rented1 != null) ArrayPool<bool>.Shared.Return(rented1);
                if (rented2 != null) ArrayPool<bool>.Shared.Return(rented2);
            }
        }

        /// <summary>
        /// Computes the Jaro-Winkler string similarity metric between 0.0 and 1.0.
        /// Boosts the score for strings with matching common prefixes up to 4 characters.
        /// </summary>
        /// <param name="s1">First string/span.</param>
        /// <param name="s2">Second string/span.</param>
        /// <param name="prefixScale">Standard prefix scaling factor (default 0.1, max 0.25).</param>
        /// <param name="ignoreCase">True to ignore character casing.</param>
        public static double JaroWinklerSimilarity(
            ReadOnlySpan<char> s1,
            ReadOnlySpan<char> s2,
            double prefixScale = 0.1,
            bool ignoreCase = false)
        {
            double jaro = JaroSimilarity(s1, s2, ignoreCase);

            int maxPrefix = Math.Min(4, Math.Min(s1.Length, s2.Length));
            int commonPrefix = 0;

            for (int i = 0; i < maxPrefix; i++)
            {
                char c1 = s1[i];
                char c2 = s2[i];
                bool match = ignoreCase
                    ? char.ToUpperInvariant(c1) == char.ToUpperInvariant(c2)
                    : c1 == c2;

                if (match)
                {
                    commonPrefix++;
                }
                else
                {
                    break;
                }
            }

            return jaro + (commonPrefix * prefixScale * (1.0 - jaro));
        }
    }
}
