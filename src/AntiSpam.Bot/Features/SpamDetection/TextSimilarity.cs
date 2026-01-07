namespace AntiSpam.Bot.Features.SpamDetection;

/// <summary>
/// Вычисление похожести текстов на основе Jaccard similarity с шинглами
/// </summary>
public static class TextSimilarity
{
    private const int DefaultShingleSize = 3;

    /// <summary>
    /// Вычисляет Jaccard similarity между двумя строками
    /// </summary>
    /// <returns>Значение от 0.0 (разные) до 1.0 (идентичные)</returns>
    public static double Calculate(string text1, string text2, int shingleSize = DefaultShingleSize)
    {
        var shingles1 = GetShingles(Normalize(text1), shingleSize);
        var shingles2 = GetShingles(Normalize(text2), shingleSize);
        
        return JaccardIndex(shingles1, shingles2);
    }

    /// <summary>
    /// Jaccard index = |A ∩ B| / |A ∪ B|
    /// </summary>
    private static double JaccardIndex(HashSet<string> set1, HashSet<string> set2)
    {
        if (set1.Count == 0 && set2.Count == 0)
            return 1.0;
        
        if (set1.Count == 0 || set2.Count == 0)
            return 0.0;

        var intersection = set1.Count(set2.Contains);
        var union = set1.Count + set2.Count - intersection;
        
        return (double)intersection / union;
    }

    /// <summary>
    /// Генерирует шинглы (n-граммы символов)
    /// </summary>
    private static HashSet<string> GetShingles(string text, int size)
    {
        var shingles = new HashSet<string>();
        
        if (string.IsNullOrEmpty(text))
            return shingles;

        if (text.Length < size)
        {
            shingles.Add(text);
            return shingles;
        }

        for (var i = 0; i <= text.Length - size; i++)
        {
            shingles.Add(text.Substring(i, size));
        }

        return shingles;
    }

    /// <summary>
    /// Нормализует текст для сравнения
    /// </summary>
    public static string Normalize(string content)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        var normalized = content
            .ToLowerInvariant()
            .Replace('\u200b', ' ')  // zero-width space
            .Replace('\u200c', ' ')  // zero-width non-joiner
            .Replace('\u200d', ' ')  // zero-width joiner
            .Replace('\ufeff', ' '); // BOM
        
        return string.Join(' ', normalized.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));
    }
}
