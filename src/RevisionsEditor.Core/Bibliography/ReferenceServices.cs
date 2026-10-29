using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RevisionsEditor.Core.Bibliography;

/// <summary>What the online check found for one reference.</summary>
public sealed record DoiCheckResult(string Key, string Doi, bool Resolves, string? Problem, string? Notice, string? CrossrefTitle);

/// <summary>
/// Online reference services: DOI lookup (BibTeX by content negotiation at doi.org, which covers Crossref and
/// DataCite DOIs), DOI and retraction checks with the Crossref REST API (which includes Retraction Watch data),
/// and the Zotero Better BibTeX citation picker and export on this computer.
/// </summary>
public sealed class ReferenceServices : IDisposable
{
    public const string ZoteroBase = "http://127.0.0.1:23119/better-bibtex";

    private readonly HttpClient _http;

    public ReferenceServices(HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler) : new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 });
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("RevisionsEditor/1.0 (manuscript editor; reference checks)");
    }

    public void Dispose() => _http.Dispose();

    public static string NormalizeDoi(string doi)
    {
        string d = doi.Trim();
        foreach (string prefix in new[] { "https://doi.org/", "http://doi.org/", "https://dx.doi.org/", "http://dx.doi.org/", "doi.org/", "doi:", "DOI:", "DOI " })
        {
            if (d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                d = d[prefix.Length..].Trim();
            }
        }
        return d.TrimEnd('.', ',', ';');
    }

    /// <summary>BibTeX for a DOI, from the registration agency via doi.org; null if the DOI does not resolve.</summary>
    public async Task<string?> FetchBibTeXAsync(string doi, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://doi.org/" + Uri.EscapeDataString(NormalizeDoi(doi)).Replace("%2F", "/"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-bibtex"));
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        string text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        return text.StartsWith('@') ? text : null;
    }

    /// <summary>Checks that each DOI exists and whether Crossref lists a retraction, withdrawal or expression of concern for it.</summary>
    public async Task<List<DoiCheckResult>> CheckDoisAsync(IEnumerable<(string Key, string Doi)> references, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<DoiCheckResult>();
        foreach (var (key, rawDoi) in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string doi = NormalizeDoi(rawDoi);
            progress?.Report($"Checking {key} ({doi})…");
            results.Add(await CheckOneAsync(key, doi, cancellationToken));
        }
        return results;
    }

    private async Task<DoiCheckResult> CheckOneAsync(string key, string doi, CancellationToken cancellationToken)
    {
        string escaped = Uri.EscapeDataString(doi);
        string? title = null;
        string? notice = null;
        bool resolves;
        try
        {
            using var work = await _http.GetAsync($"https://api.crossref.org/works/{escaped}", cancellationToken);
            if (work.StatusCode == HttpStatusCode.NotFound)
            {
                // Not a Crossref DOI (DataCite DOIs for datasets and software are common): ask doi.org itself.
                resolves = await HandleExistsAsync(doi, cancellationToken);
                return new DoiCheckResult(key, doi, resolves, resolves ? null : "The DOI does not resolve.", null, null);
            }
            work.EnsureSuccessStatusCode();
            resolves = true;
            using var doc = JsonDocument.Parse(await work.Content.ReadAsStringAsync(cancellationToken));
            var message = doc.RootElement.GetProperty("message");
            if (message.TryGetProperty("title", out var titles) && titles.ValueKind == JsonValueKind.Array && titles.GetArrayLength() > 0)
            {
                title = titles[0].GetString();
            }
            if (message.TryGetProperty("updated-by", out var updatedBy) && updatedBy.ValueKind == JsonValueKind.Array)
            {
                notice = DescribeUpdates(updatedBy, doi);
            }
        }
        catch (HttpRequestException ex)
        {
            return new DoiCheckResult(key, doi, false, $"Could not reach Crossref: {ex.Message}", null, null);
        }

        if (notice == null)
        {
            // Notices that update this DOI (retractions from publishers and from Retraction Watch).
            try
            {
                using var updates = await _http.GetAsync($"https://api.crossref.org/v1/works?filter=updates:{escaped}&rows=20", cancellationToken);
                if (updates.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await updates.Content.ReadAsStringAsync(cancellationToken));
                    if (doc.RootElement.GetProperty("message").TryGetProperty("items", out var items))
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.TryGetProperty("update-to", out var updateTo))
                            {
                                notice ??= DescribeUpdates(updateTo, doi);
                            }
                        }
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
        }
        return new DoiCheckResult(key, doi, resolves, null, notice, title);
    }

    private static string? DescribeUpdates(JsonElement updates, string doi)
    {
        foreach (var update in updates.EnumerateArray())
        {
            string type = update.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            string target = update.TryGetProperty("DOI", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            if (target.Length > 0 && !string.Equals(target, doi, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string kind = type.ToLowerInvariant().Replace('-', '_') switch
            {
                "retraction" => "Retracted",
                "withdrawal" or "removal" => "Withdrawn",
                "expression_of_concern" => "Expression of concern",
                _ => string.Empty
            };
            if (kind.Length == 0)
            {
                continue;
            }
            string source = update.TryGetProperty("source", out var s) && s.GetString() == "retraction-watch" ? " (Retraction Watch)" : string.Empty;
            string when = update.TryGetProperty("updated", out var u) && u.TryGetProperty("date-parts", out var parts)
                && parts.GetArrayLength() > 0 && parts[0].GetArrayLength() > 0
                ? $" in {parts[0][0].GetInt32()}"
                : string.Empty;
            return $"{kind}{when}{source}";
        }
        return null;
    }

    private async Task<bool> HandleExistsAsync(string doi, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync($"https://doi.org/api/handles/{Uri.EscapeDataString(doi).Replace("%2F", "/")}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc.RootElement.TryGetProperty("responseCode", out var code) && code.GetInt32() == 1;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    // ---- Zotero (Better BibTeX) --------------------------------------------------------------------

    /// <summary>True when Zotero is running with the Better BibTeX plugin.</summary>
    public async Task<bool> IsZoteroReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            string text = await _http.GetStringAsync($"{ZoteroBase}/cayw?probe=true", cts.Token);
            return text.Trim().Equals("ready", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens Zotero's citation picker and returns what the author picked as a Pandoc citation such as
    /// "[@white2016; @coops2007]", or null if they cancelled.
    /// </summary>
    public async Task<string?> PickCitationInZoteroAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        // The picker waits for the author, so this request has no timeout of its own.
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ZoteroBase}/cayw?format=pandoc&brackets=1");
        using var response = await client.SendAsync(request, cts.Token);
        string text = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>BibTeX for citation keys from the Zotero library (Better BibLaTeX), or null when Zotero is unavailable.</summary>
    public async Task<string?> ExportFromZoteroAsync(IReadOnlyList<string> citationKeys, CancellationToken cancellationToken = default)
    {
        if (citationKeys.Count == 0)
        {
            return string.Empty;
        }
        string payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "item.export", @params = new object[] { citationKeys, "Better BibLaTeX" }, id = 1 });
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{ZoteroBase}/json-rpc", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!doc.RootElement.TryGetProperty("result", out var result))
            {
                return null;
            }
            // Versions before 6.7.143 wrapped the result as [status, type, body].
            return result.ValueKind switch
            {
                JsonValueKind.String => result.GetString(),
                JsonValueKind.Array when result.GetArrayLength() >= 3 => result[2].GetString(),
                _ => null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}
