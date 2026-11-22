using System.Globalization;
using System.Numerics;
using System.Text;

namespace LunaShoppingList.Core;

/// <summary>Outcome of reading a Teamcraft import link: the rows, the rows that could not be read, or an error.</summary>
/// <param name="Requirements">The rows read, merged per item in first-seen order; null when <paramref name="Error"/> is set.</param>
/// <param name="Unmatched">Readable descriptions of rows that were skipped ("Row 3: ...").</param>
/// <param name="Error">A message the player can read when nothing usable was found; otherwise null.</param>
public sealed record TeamcraftParseResult(IReadOnlyList<Requirement>? Requirements, IReadOnlyList<string> Unmatched, string? Error);

/// <summary>
/// Builds and reads Teamcraft import links: <c>https://ffxivteamcraft.com/import/</c> followed by the base64 (UTF-8) of
/// rows <c>itemId,recipeId|null,quantity</c> joined with <c>;</c>.
/// </summary>
public static class TeamcraftLink
{
    /// <summary>Link length above which a warning is worth showing.</summary>
    public const int LongLinkWarningLength = 8000;

    /// <summary>True only for an absolute http or https URL: the one guard before a link is handed to the operating system to open.</summary>
    public static bool IsSafeWebLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private const string Prefix = "https://ffxivteamcraft.com/import/";
    private const string ImportMarker = "/import/";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Builds an import link from the rows, or null when no row is usable. Rows with item id 0 or a quantity below 1 are
    /// dropped; duplicates are merged by summing (clamped to <see cref="CustomListOps.MaxQuantity"/>) in first-seen order.
    /// The recipe field is always <c>null</c>; the base64 has its <c>+</c>, <c>/</c> and <c>=</c> percent-encoded.
    /// </summary>
    public static string? Build(IReadOnlyList<Requirement> rows)
    {
        var order = new List<uint>();
        var totals = new Dictionary<uint, int>();
        foreach (var r in rows)
        {
            if (r.ItemId == 0 || r.Quantity < 1) continue;
            if (totals.TryGetValue(r.ItemId, out var have)) totals[r.ItemId] = CustomListOps.ClampQuantity((long)have + r.Quantity);
            else { order.Add(r.ItemId); totals[r.ItemId] = CustomListOps.ClampQuantity(r.Quantity); }
        }
        if (order.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var id in order)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(id.ToString(CultureInfo.InvariantCulture)).Append(",null,")
              .Append(totals[id].ToString(CultureInfo.InvariantCulture));
        }
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        return Prefix + b64.Replace("+", "%2B").Replace("/", "%2F").Replace("=", "%3D");
    }

    /// <summary>
    /// Reads a full import link (any scheme and host containing <c>/import/</c>) or a bare base64 string. The data ends at the first whitespace, <c>&amp;</c>, <c>?</c> or <c>#</c>, so a link followed by text works. Decodes as Teamcraft does (<c>%25</c> to <c>%</c>, percent-decode,
    /// base64), also accepting the URL-safe alphabet and missing padding. Never throws. Bad rows go to
    /// <see cref="TeamcraftParseResult.Unmatched"/>; when nothing usable is found, Error is set and Requirements is null.
    /// </summary>
    public static TeamcraftParseResult TryParse(string text)
    {
        var none = Array.Empty<string>();
        try
        {
            var payload = (text ?? string.Empty).Trim();
            if (payload.Length == 0) return Fail(none, "Paste a Teamcraft import link first");

            var marker = payload.IndexOf(ImportMarker, StringComparison.OrdinalIgnoreCase);
            var hasMarker = marker >= 0;
            if (hasMarker) payload = payload.Substring(marker + ImportMarker.Length);
            payload = payload.TrimStart();
            var cut = -1;
            for (var i = 0; i < payload.Length; i++)
                if (char.IsWhiteSpace(payload[i]) || payload[i] is '?' or '#' or '&') { cut = i; break; }
            if (cut >= 0) payload = payload.Substring(0, cut);
            payload = payload.Trim();
            if (payload.Length == 0) return Fail(none, "The link has no import data after /import/");

            var decoded = DecodeBase64(payload);
            if (decoded is null)
                return Fail(none, hasMarker ? "The import data in the link could not be decoded" : "That is not a Teamcraft import link");

            return ReadRows(decoded);
        }
        catch (Exception)
        {
            return Fail(Array.Empty<string>(), "That could not be read as a Teamcraft import link");
        }
    }

    private static string? DecodeBase64(string payload)
    {
        var s = payload.Replace("%25", "%");
        s = Uri.UnescapeDataString(s);
        s = s.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        foreach (var c in s)
        {
            var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/';
            if (!ok) return null;
        }
        switch (s.Length % 4)
        {
            case 1: return null;
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        try { return StrictUtf8.GetString(Convert.FromBase64String(s)); }
        catch (FormatException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static TeamcraftParseResult ReadRows(string decoded)
    {
        var parts = decoded.Split(';').ToList();
        if (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);

        var order = new List<uint>();
        var totals = new Dictionary<uint, int>();
        var unmatched = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var n = i + 1;
            var fields = parts[i].Split(',');
            if (fields.Length != 3)
            {
                unmatched.Add($"Row {n}: expected item,recipe,quantity but found '{Shorten(parts[i])}'");
                continue;
            }
            if (!uint.TryParse(fields[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
            {
                unmatched.Add($"Row {n}: '{Shorten(fields[0])}' is not a valid item id");
                continue;
            }
            if (!BigInteger.TryParse(fields[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var qty) || qty < 1)
            {
                unmatched.Add($"Row {n}: '{Shorten(fields[2])}' is not a valid quantity");
                continue;
            }
            var q = qty > CustomListOps.MaxQuantity ? CustomListOps.MaxQuantity : (int)qty;
            if (totals.TryGetValue(id, out var have)) totals[id] = CustomListOps.ClampQuantity((long)have + q);
            else { order.Add(id); totals[id] = q; }
        }

        if (order.Count == 0) return Fail(unmatched, "No valid rows were found in the link");
        var rows = new List<Requirement>(order.Count);
        foreach (var id in order) rows.Add(new Requirement(id, totals[id]));
        return new TeamcraftParseResult(rows, unmatched, null);
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s.Substring(0, 40) + "...";

    private static TeamcraftParseResult Fail(IReadOnlyList<string> unmatched, string error) => new(null, unmatched, error);
}
