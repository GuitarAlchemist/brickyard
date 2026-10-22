using System.Globalization;

// The language of the shelf's labels: English, unless the brain's "language" line (Link) or --lang says French. Each
// label is written in both languages where it is used, with Say.
internal static class Words
{
    public static readonly string[] Languages = ["en", "fr"];

    private static volatile string language = "en";

    // Set from the link's thread, read by the shelf's loaders and the render thread.
    public static string Language
    {
        get => language;
        set => language = Languages.Contains(value) ? value : "en";
    }

    public static string Say(string en, string fr) => language == "fr" ? fr : en;

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(language == "fr" ? "fr-FR" : "en-US");
}
