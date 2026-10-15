using System.Text;

namespace Lingarr.Server.Modules.Subtitle;

/// <summary>
/// Content-based language detection for subtitle text whose filename carries no
/// language tag — the last-resort rescue before a file lands on "unknown".
/// Filename tags always win upstream; only the caller decides to consult this.
/// Pure static heuristics with a conservative bias (宁可漏判不可错判): CJK verdicts
/// come from script statistics, Latin verdicts from stopword hit rates, and
/// anything ambiguous returns null.
/// </summary>
public static class SubtitleContentLanguageDetector
{
    // --- script-share bars. Shares are relative to ALL letters; ASCII digits,
    //     punctuation and timing lines never enter the denominator. ---
    private const double MinHanShare = 0.30;
    private const double MaxMinorLatinShare = 0.25;
    private const double MinDominantShare = 0.50;

    // A Latin verdict additionally refuses any meaningful CJK share, so 中英
    // dual-language mixes stay unknown instead of guessing one side.
    private const double MaxCjkShareForLatin = 0.20;

    // Kana never occurs in Chinese/Korean text while Japanese always mixes it
    // with kanji — a small but consistent share settles it.
    private const int MinKanaChars = 6;
    private const double MinKanaShare = 0.05;

    private const int MinLetters = 16;
    private const int MinLatinWords = 8;
    private const double MinTopStopwordRate = 0.08;
    private const double LatinSeparationFactor = 2.5;
    private const int MinVariantEvidence = 2;

    /// <summary>Simplified-only characters (counterparts live in <see cref="TraditionalOnlyChars"/>).</summary>
    private static readonly HashSet<char> SimplifiedOnlyChars = new(
        "们说对发生过还这现后见问间让与为会个门边经关开无应学觉写读听电车语话请谢长风飞汉简体单双难认识续结级备战场岁离统转输张刘马龙凤岛爱恋忆讲谈计论议书钱铁银铜饭馆万亿优传伤价吗么团图圆钟钢网练组终给继绩维稳欢试诚证评词课谁调贵费资轮软轻载较辆达运进远连适选递随静顺须顾领频题颜鱼鸟鸡龟卫压县热观视览规责质赶赞轨轿违迟遗针钓铃铅铝镇闪闻阅队阶际陆陈险隐页顶项预飘饱驱骗鸣该没点妈带买卖广乐习");

    /// <summary>
    /// Traditional-only characters. Shared characters (的 天 好 …) live in
    /// neither set; 简/繁 is decided purely on the differing ones.
    /// </summary>
    private static readonly HashSet<char> TraditionalOnlyChars = new(
        "們說對發過還這現後見問間讓與為會個門邊經關開無應學覺寫讀聽電車語話請謝長風飛漢簡體單雙難認識續結級備戰場歲離統轉輸張劉馬龍鳳島愛戀憶講談計論議書錢鐵銀銅飯館萬億優傳傷價嗎麼團圖圓鐘鋼網練組終給繼績維穩歡試誠證評詞課誰調貴費資輪軟輕載較輛達運進遠連適選遞隨靜順須顧領頻題顏魚鳥雞龜衛壓縣熱觀視覽規責質趕贊軌轎違遲遺針釣鈴鉛鋁鎮閃聞閱隊階際陸陳險隱頁頂項預飄飽驅騙鳴該沒點媽帶買賣廣樂習");

    /// <summary>High-frequency function words per language; shared tokens cancel out in the ratio test.</summary>
    private static readonly Dictionary<string, HashSet<string>> Stopwords = new()
    {
        ["en"] = new HashSet<string>([
            "the", "you", "i", "to", "a", "and", "it", "that", "of", "in", "is", "me", "my",
            "do", "what", "we", "he", "they", "be", "are", "was", "for", "on", "with", "so",
            "not", "no", "don", "t", "s", "re", "ve", "ll", "d", "m", "this", "these",
            "those", "there", "here", "all", "just", "like", "get", "got", "go", "going",
            "know", "think", "really", "out", "up", "down", "about", "from", "as", "by",
            "than", "then", "too", "very", "want", "need", "let", "come", "look", "well",
            "now", "one", "back", "gonna", "can", "will", "would", "could", "should",
            "have", "has", "had", "did", "doesn", "didn", "won", "isn", "aren", "weren",
            "her", "his", "their", "our", "your", "him", "them", "she", "who", "when",
            "where", "why", "how", "yes", "mr", "dr"
        ], StringComparer.Ordinal),
        ["fr"] = new HashSet<string>([
            "le", "la", "les", "l", "d", "j", "n", "c", "un", "une", "des", "du", "de", "et",
            "est", "en", "je", "tu", "il", "elle", "nous", "vous", "ils", "elles", "que",
            "qui", "quoi", "ne", "pas", "plus", "pour", "avec", "dans", "sur", "mais", "ou",
            "où", "au", "aux", "ce", "cette", "ces", "son", "sa", "ses", "leur", "leurs",
            "eux", "me", "te", "se", "y", "a", "ai", "as", "ont", "être", "avoir", "fait",
            "faire", "dit", "dire", "ça", "toujours", "très", "bien", "monsieur", "madame",
            "oui", "non", "alors", "aussi", "comme", "par", "parce", "si", "va", "vais",
            "vas", "sais", "était", "étaient", "chez", "sans", "sous", "rien", "personne",
            "tout", "tous", "toute", "toutes", "autre", "autres", "même", "encore", "déjà",
            "ici", "là", "quand", "pourquoi", "comment", "merci", "veux", "veut", "dois",
            "aller", "moi", "toi"
        ], StringComparer.Ordinal),
        ["de"] = new HashSet<string>([
            "ich", "du", "er", "sie", "es", "wir", "ihr", "und", "der", "die", "das", "den",
            "dem", "des", "ein", "eine", "einen", "einem", "einer", "nicht", "ist", "bin",
            "bist", "sind", "war", "waren", "hat", "habe", "hast", "haben", "hatte",
            "wird", "werden", "zu", "von", "mit", "für", "auf", "aus", "bei", "nach",
            "über", "unter", "vor", "durch", "gegen", "ohne", "um", "ja", "nein", "auch",
            "noch", "nur", "schon", "dann", "wenn", "weil", "doch", "mal", "was", "wie",
            "wo", "warum", "gut", "sehr", "hier", "da", "dort", "immer", "alles",
            "etwas", "nichts", "man", "kann", "muss", "soll", "will", "danke", "bitte",
            "herr", "frau", "mein", "dein", "sein", "unser", "euer", "kein", "keine",
            "meine", "deine", "dich", "mich", "dir", "mir", "ihn", "ihm", "uns", "euch",
            "ihnen", "jetzt", "so"
        ], StringComparer.Ordinal),
        ["es"] = new HashSet<string>([
            "el", "la", "los", "las", "un", "una", "unos", "unas", "y", "o", "a", "pero",
            "si", "no", "de", "del", "al", "que", "quién", "qué", "cuándo", "cuánto",
            "dónde", "cómo", "por", "para", "con", "sin", "sobre", "entre", "hasta",
            "desde", "muy", "más", "menos", "tan", "ser", "es", "soy", "eres", "somos",
            "son", "está", "estás", "están", "estoy", "hay", "tengo", "tienes", "tiene",
            "tienen", "hacer", "hago", "puedo", "puedes", "puede", "pueden", "quiero",
            "quieres", "quiere", "yo", "tú", "él", "ella", "nosotros", "vosotros",
            "ellos", "ellas", "me", "te", "se", "lo", "le", "les", "mi", "tu", "su",
            "sus", "nuestro", "esto", "eso", "aquí", "allí", "ahora", "luego",
            "siempre", "nunca", "también", "sólo", "solo", "como", "porque", "pues",
            "bien", "gracias", "hola", "ver", "veo", "vas", "va", "voy", "vamos",
            "dice", "digo", "nada", "todo", "todos", "señor", "señora", "claro",
            "verdad", "anda"
        ], StringComparer.Ordinal),
        ["nl"] = new HashSet<string>([
            "ik", "je", "jij", "hij", "zij", "we", "wij", "jullie", "ze", "het", "de",
            "een", "en", "of", "maar", "dat", "die", "dit", "deze", "wat", "wie",
            "waar", "wanneer", "waarom", "hoe", "niet", "geen", "ben", "bent", "is",
            "zijn", "was", "waren", "heeft", "heb", "hebben", "had", "zal", "zullen",
            "kan", "kunnen", "moet", "wil", "willen", "ga", "gaat", "gaan", "naar",
            "van", "in", "op", "aan", "met", "voor", "over", "door", "bij", "uit",
            "om", "te", "toen", "dan", "nog", "al", "ook", "heel", "erg", "nu",
            "hier", "daar", "altijd", "nooit", "iets", "niets", "alles", "iedereen",
            "goed", "dank", "als", "dus", "want", "ja", "nee", "komen", "komt", "kom",
            "zien", "zie", "doen", "doet", "zeggen", "zegt", "zei", "weten", "weet",
            "denken", "denkt", "maken", "maakt", "nou", "hoor"
        ], StringComparer.Ordinal),
        ["pt"] = new HashSet<string>([
            "não", "que", "de", "a", "o", "as", "os", "um", "uma", "em", "no", "na",
            "nos", "nas", "do", "da", "dos", "das", "por", "para", "com", "sem",
            "sobre", "até", "desde", "e", "mas", "se", "mais", "muito", "tão", "quem",
            "qual", "quando", "onde", "como", "porque", "pois", "sim", "eu", "tu",
            "ele", "ela", "nós", "eles", "elas", "você", "vocês", "sou", "é",
            "somos", "são", "está", "estão", "estar", "ser", "ter", "tenho", "tem",
            "têm", "fazer", "faço", "faz", "pode", "posso", "podem", "quero", "quer",
            "vai", "vão", "meu", "minha", "meus", "minhas", "seu", "sua", "seus",
            "suas", "nosso", "isso", "isto", "aqui", "agora", "então", "sempre",
            "nunca", "também", "só", "bem", "obrigado", "ver", "vejo", "diz", "digo",
            "tudo", "nada", "todo", "todos", "gente", "senhor", "senhora", "coisa",
            "claro", "mim", "comigo"
        ], StringComparer.Ordinal)
    };

    /// <summary>
    /// Detects the language of subtitle text (the caller passes the first 64 KB —
    /// plenty of signal). Returns a canonical language code (zh / zh-TW / ja / ko /
    /// en / fr / de / es / nl / pt) or null when the text is empty, too short or
    /// ambiguous. Never throws.
    /// </summary>
    public static string? DetectLanguage(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        int han = 0, hiragana = 0, katakana = 0, hangul = 0, latin = 0, other = 0;
        var words = new List<string>();
        var word = new StringBuilder();

        void Flush()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        foreach (var c in text)
        {
            if (IsHan(c))
            {
                han++;
                Flush();
            }
            else if (c is >= '\u3041' and <= '\u309F')
            {
                hiragana++;
                Flush();
            }
            else if (c is >= '\u30A0' and <= '\u30FF')
            {
                katakana++;
                Flush();
            }
            else if (c is >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F' or >= '\uAC00' and <= '\uD7A3')
            {
                hangul++;
                Flush();
            }
            else if (IsLatinLetter(c))
            {
                latin++;
                word.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsLetter(c))
            {
                other++; // Greek/Cyrillic/… — counted so they dilute shares but never match
                Flush();
            }
            else
            {
                Flush();
            }
        }

        Flush();

        var kana = hiragana + katakana;
        var total = (double)(han + kana + hangul + latin + other);
        if (total < MinLetters)
        {
            return null;
        }

        // Japanese first: kana settles it before the Han branch below can claim
        // the kanji as Chinese. Pure-kanji Japanese still falls through to the
        // Han branch — an accepted miss, nothing in the content marks it Japanese.
        if (kana >= MinKanaChars && kana / total >= MinKanaShare)
        {
            return "ja";
        }

        // Korean: hangul must dominate; scattered hanja cannot flip it.
        if (hangul > 0 && hangul >= han && hangul >= latin && hangul / total >= MinDominantShare)
        {
            return "ko";
        }

        // Chinese: Han is the largest script with a 30% share and Latin stays
        // minor — 中英 dual-language mixes are deliberately left undetected.
        if (han > 0 && han >= kana && han >= hangul && han >= latin
            && han / total >= MinHanShare && latin / total <= MaxMinorLatinShare)
        {
            return ClassifyChinese(text);
        }

        // Latin: only on a clean majority, then stopword hit rates decide.
        if (latin > 0 && latin >= han && latin >= hangul && latin >= kana && latin >= other
            && latin / total >= MinDominantShare
            && (han + kana + hangul) / total <= MaxCjkShareForLatin)
        {
            return ClassifyLatin(words);
        }

        return null;
    }

    /// <summary>Splits Han onto zh / zh-TW by counting script-unique characters; ties and thin evidence return null.</summary>
    private static string? ClassifyChinese(string text)
    {
        int simplified = 0, traditional = 0;
        foreach (var c in text)
        {
            if (SimplifiedOnlyChars.Contains(c))
            {
                simplified++;
            }
            else if (TraditionalOnlyChars.Contains(c))
            {
                traditional++;
            }
        }

        if (simplified + traditional < MinVariantEvidence)
        {
            return null;
        }

        if (simplified > traditional)
        {
            return "zh";
        }

        return traditional > simplified ? "zh-TW" : null;
    }

    /// <summary>
    /// Stopword hit-rate comparison. Two bars: an absolute floor (short or
    /// proper-noun heavy samples) and a 2.5× separation from the runner-up —
    /// es/pt and en/nl share enough words that a close race means "undetectable".
    /// </summary>
    private static string? ClassifyLatin(List<string> words)
    {
        if (words.Count < MinLatinWords)
        {
            return null;
        }

        var rates = Stopwords
            .Select(kv => (Language: kv.Key, Rate: (double)words.Count(kv.Value.Contains) / words.Count))
            .OrderByDescending(r => r.Rate)
            .ThenBy(r => r.Language, StringComparer.Ordinal)
            .ToList();

        var top = rates[0];
        var second = rates[1];

        if (top.Rate < MinTopStopwordRate)
        {
            return null;
        }

        if (second.Rate > 0 && top.Rate < LatinSeparationFactor * second.Rate)
        {
            return null;
        }

        return top.Language;
    }

    private static bool IsHan(char c) =>
        c is >= '\u3400' and <= '\u4DBF'    // Extension A
            or >= '\u4E00' and <= '\u9FFF'  // URO
            or >= '\uF900' and <= '\uFAFF'; // Compatibility Ideographs

    private static bool IsLatinLetter(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')
            || (c >= '\u00C0' && c <= '\u024F' && c != '\u00D7' && c != '\u00F7') // Latin-1 supplement + Extended-A/B
            || (c >= '\u1E00' && c <= '\u1EFF'); // Latin Extended Additional (Vietnamese)
}
