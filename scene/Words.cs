using System.Globalization;

// The language of the shelf's labels: English, unless the brain's "language" line (Link) or --lang says French. Each
// label is written in both languages where it is used, with Say. The scene runs without culture data
// (InvariantGlobalization), where asking for fr-FR or en-US throws, so dates and numbers are written here.
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

    private static readonly string[] MonthsEn = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    private static readonly string[] MonthsFr = ["janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre"];

    // "October 22, 19:13" or « 22 octobre, 19:13 », with the year when it is not this one.
    public static string Date(DateTime local)
    {
        var fr = language == "fr";
        var day = local.Day.ToString(CultureInfo.InvariantCulture);
        var month = (fr ? MonthsFr : MonthsEn)[local.Month - 1];
        var date = fr ? day + " " + month : month + " " + day;
        if (local.Year != DateTime.Now.Year)
        {
            date += (fr ? " " : ", ") + local.Year.ToString(CultureInfo.InvariantCulture);
        }

        return date + ", " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    // A number with at most one decimal, after a comma in French.
    public static string Number(double value)
    {
        var text = value.ToString("0.#", CultureInfo.InvariantCulture);
        return language == "fr" ? text.Replace('.', ',') : text;
    }
}
