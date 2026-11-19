namespace Agent.Core.Language;

/// <summary>Per-reply language selection by script. Arabic wins when Arabic letters outnumber Latin letters.</summary>
public static class LanguageDetector
{
    public static string Detect(string text, string fallbackLocale)
    {
        var arabic = 0;
        var latin = 0;
        foreach (var c in text)
        {
            if (IsArabicScript(c))
            {
                arabic++;
            }
            else if (char.IsLetter(c) && c < 0x0250)
            {
                latin++;
            }
        }

        if (arabic == 0 && latin == 0)
        {
            return fallbackLocale;
        }

        return arabic > latin ? "ar" : "en";
    }

    public static string DirectionOf(string locale) => locale == "ar" ? "rtl" : "ltr";

    private static bool IsArabicScript(char c) =>
        c is >= '؀' and <= 'ۿ'
            or >= 'ݐ' and <= 'ݿ'
            or >= 'ࢠ' and <= 'ࣿ'
            or >= 'ﭐ' and <= '﷿'
            or >= 'ﹰ' and <= '﻿';
}
