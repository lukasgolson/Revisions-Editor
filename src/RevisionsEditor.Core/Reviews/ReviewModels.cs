using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevisionsEditor.Core.Reviews;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewCommentStatus
{
    Open,
    /// <summary>Changed in the manuscript.</summary>
    Addressed,
    /// <summary>Answered without changing the manuscript (explained in the response).</summary>
    Answered
}

/// <summary>One point raised by a reviewer or editor, with the author's response.</summary>
public sealed class ReviewComment
{
    /// <summary>"R1.3" (reviewer 1, point 3) or "E.1" for the editor.</summary>
    public string Id { get; set; } = string.Empty;
    public string Reviewer { get; set; } = "Reviewer 1";
    public string Text { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public ReviewCommentStatus Status { get; set; } = ReviewCommentStatus.Open;
    /// <summary>Paragraphs changed in response, in manuscript order.</summary>
    public List<string> ParagraphIds { get; set; } = [];
    /// <summary>Commits that say they address this comment.</summary>
    public List<string> CommitShas { get; set; } = [];

    public ReviewComment Clone() => new()
    {
        Id = Id,
        Reviewer = Reviewer,
        Text = Text,
        Response = Response,
        Status = Status,
        ParagraphIds = [.. ParagraphIds],
        CommitShas = [.. CommitShas]
    };
}

/// <summary>A round of review: the version that was submitted and the comments it received.</summary>
public sealed class ReviewRound
{
    public string Id { get; set; } = $"round-{Guid.NewGuid():N}"[..14];
    public string Name { get; set; } = "Revision 1";
    /// <summary>Git tag of the submitted version the reviewers read (e.g. "submitted-v1").</summary>
    public string? SubmittedTag { get; set; }
    /// <summary>The submitted commit, in case the tag is missing.</summary>
    public string? SubmittedCommit { get; set; }
    public string ManuscriptNumber { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Opening paragraph of the response letter.</summary>
    public string LetterIntroduction { get; set; } = string.Empty;
    public List<ReviewComment> Comments { get; set; } = [];

    public int OpenCount => Comments.Count(c => c.Status == ReviewCommentStatus.Open);
}

/// <summary>The review rounds of a paper, committed as <c>.revisions/reviews.json</c> so every computer sees them.</summary>
public sealed class ReviewStore
{
    public const string FilePath = ".revisions/reviews.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<ReviewRound> Rounds { get; set; } = [];

    public static ReviewStore Load(string workspacePath)
    {
        string path = Path.Combine(workspacePath, FilePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : new ReviewStore();
        }
        catch (IOException)
        {
            return new ReviewStore();
        }
    }

    public static ReviewStore Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ReviewStore>(json) ?? new ReviewStore();
        }
        catch (JsonException)
        {
            return new ReviewStore();
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions) + "\n";

    public void Save(string workspacePath)
    {
        string path = Path.Combine(workspacePath, FilePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Combines the review rounds of two computers (for sync): rounds and comments are matched by id; where both
    /// changed the same comment, the one with more progress wins (a response, then a later status).
    /// </summary>
    public static ReviewStore Merge(ReviewStore? baseStore, ReviewStore ours, ReviewStore theirs)
    {
        var result = new ReviewStore();
        var baseRounds = (baseStore?.Rounds ?? []).ToDictionary(r => r.Id, StringComparer.Ordinal);
        var theirRounds = theirs.Rounds.ToDictionary(r => r.Id, StringComparer.Ordinal);

        foreach (var round in ours.Rounds)
        {
            if (!theirRounds.TryGetValue(round.Id, out var other))
            {
                // Deleted on the other computer only if it existed in the base and was unchanged here.
                if (!baseRounds.ContainsKey(round.Id) || JsonSerializer.Serialize(round) != JsonSerializer.Serialize(baseRounds[round.Id]))
                {
                    result.Rounds.Add(round);
                }
                continue;
            }
            baseRounds.TryGetValue(round.Id, out var b);
            result.Rounds.Add(MergeRound(b, round, other));
        }
        foreach (var round in theirs.Rounds.Where(r => ours.Rounds.All(o => o.Id != r.Id)))
        {
            if (!baseRounds.ContainsKey(round.Id))
            {
                result.Rounds.Add(round);
            }
        }
        return result;
    }

    private static ReviewRound MergeRound(ReviewRound? b, ReviewRound ours, ReviewRound theirs)
    {
        static string Pick(string? baseValue, string mine, string other) => mine != baseValue ? mine : other;

        var merged = new ReviewRound
        {
            Id = ours.Id,
            Name = Pick(b?.Name, ours.Name, theirs.Name),
            SubmittedTag = ours.SubmittedTag ?? theirs.SubmittedTag,
            SubmittedCommit = ours.SubmittedCommit ?? theirs.SubmittedCommit,
            ManuscriptNumber = Pick(b?.ManuscriptNumber, ours.ManuscriptNumber, theirs.ManuscriptNumber),
            CreatedUtc = ours.CreatedUtc,
            LetterIntroduction = Pick(b?.LetterIntroduction, ours.LetterIntroduction, theirs.LetterIntroduction)
        };
        var baseComments = (b?.Comments ?? []).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var theirComments = theirs.Comments.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var mine in ours.Comments)
        {
            if (!theirComments.TryGetValue(mine.Id, out var other))
            {
                merged.Comments.Add(mine);
                continue;
            }
            baseComments.TryGetValue(mine.Id, out var baseComment);
            merged.Comments.Add(new ReviewComment
            {
                Id = mine.Id,
                Reviewer = mine.Reviewer,
                Text = Pick(baseComment?.Text, mine.Text, other.Text),
                Response = Pick(baseComment?.Response, mine.Response, other.Response),
                Status = mine.Status != (baseComment?.Status ?? ReviewCommentStatus.Open) ? mine.Status : other.Status,
                ParagraphIds = mine.ParagraphIds.Union(other.ParagraphIds, StringComparer.Ordinal).ToList(),
                CommitShas = mine.CommitShas.Union(other.CommitShas, StringComparer.OrdinalIgnoreCase).ToList()
            });
        }
        merged.Comments.AddRange(theirs.Comments.Where(c => ours.Comments.All(o => o.Id != c.Id) && !baseComments.ContainsKey(c.Id)));
        return merged;
    }
}
