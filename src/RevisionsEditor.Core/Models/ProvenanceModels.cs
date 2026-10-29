using System.Text.Json.Serialization;

namespace RevisionsEditor.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthorshipOriginSource
{
    HandWrittenInWpf,
    HandWrittenInGoogleDocs,
    SupervisorTrackedEdit,
    ExternalPaste,
    InitialSeed
}

/// <summary>
/// One change to a sentence: who made it, in which commit, where the text came from, and the text before
/// and after. (Ledgers written by earlier versions also hold typing counts and a derived "human score";
/// those fields are ignored when read.)
/// </summary>
public sealed class SentenceRevisionStep
{
    public string CommitSha { get; set; } = string.Empty;
    public int BlockIndex { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public string Author { get; set; } = string.Empty;
    public AuthorshipOriginSource Source { get; set; } = AuthorshipOriginSource.HandWrittenInWpf;
    public string BeforeText { get; set; } = string.Empty;
    public string AfterText { get; set; } = string.Empty;
    public double SimilarityToPrevious { get; set; } = 1.0;
}

public sealed class SentenceProvenanceRecord
{
    public string SentenceId { get; set; } = $"s-{Guid.NewGuid():N}"[..12];
    public string ParagraphId { get; set; } = string.Empty;
    public string CurrentCanonicalText { get; set; } = string.Empty;
    public string CurrentNormalizedHash { get; set; } = string.Empty;
    public string OriginCommitSha { get; set; } = string.Empty;
    public string OriginAuthor { get; set; } = string.Empty;
    public AuthorshipOriginSource OriginSource { get; set; } = AuthorshipOriginSource.HandWrittenInWpf;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<SentenceRevisionStep> RevisionHistory { get; set; } = [];

    public int TotalRevisions => RevisionHistory.Count;
}

public sealed class CanonicalSentenceLeaf
{
    public string SentenceId { get; set; } = string.Empty;
    public string ParagraphId { get; set; } = string.Empty;
    public int ParagraphOrder { get; set; }
    public int SentenceOrder { get; set; }
    public string CanonicalText { get; set; } = string.Empty;
    public string LeafHash { get; set; } = string.Empty;
}

/// <summary>
/// A signed record of the manuscript text at one commit. Version 1 blocks (earlier releases) formed a single
/// linear chain on one computer; version 2 blocks are stored with the commits (as Git notes), link to the
/// blocks of the commit's parents so the chain follows branches and merges, and carry the public key that
/// signed them, so blocks signed on different computers verify anywhere.
/// </summary>
public sealed class TextChainBlock
{
    public int Version { get; set; } = 1;
    /// <summary>Position in the ledger. Part of the signed header for version 1 blocks only.</summary>
    public int BlockIndex { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorKeyFingerprint { get; set; } = string.Empty;
    public string PreviousBlockHash { get; set; } = new('0', 64);
    public string NormalizedTextMerkleRoot { get; set; } = string.Empty;
    public string SentenceProvenanceRoot { get; set; } = string.Empty;
    public string CanonicalManuscriptSnapshot { get; set; } = string.Empty;
    public int SentenceCount { get; set; }
    public int ParagraphCount { get; set; }
    public string BlockHash { get; set; } = string.Empty;
    public string SignatureBase64 { get; set; } = string.Empty;

    /// <summary>Version 2: hashes of the nearest signed blocks on each parent commit (none for the first).</summary>
    public List<string> ParentBlockHashes { get; set; } = [];
    /// <summary>Version 2: the key that signed this block (version 1 blocks migrated from a ledger file also carry it).</summary>
    public string AuthorPublicKeyPem { get; set; } = string.Empty;
    public AuthorshipOriginSource OriginSource { get; set; } = AuthorshipOriginSource.HandWrittenInWpf;
    /// <summary>Paragraph ids of the snapshot's lines, so sentence history can be rebuilt from the blocks.</summary>
    public List<string> ParagraphIds { get; set; } = [];
}

public sealed class ProvenanceLedger
{
    public string LedgerVersion { get; set; } = "1.0";
    public string DocumentId { get; set; } = "doc-main";
    /// <summary>This computer's signing key.</summary>
    public string AuthorPublicKeyPem { get; set; } = string.Empty;
    public string AuthorKeyFingerprint { get; set; } = string.Empty;
    /// <summary>Blocks in history order: parents before children.</summary>
    public List<TextChainBlock> Blocks { get; set; } = [];
    /// <summary>Sentence history, rebuilt by replaying the blocks.</summary>
    public List<SentenceProvenanceRecord> Sentences { get; set; } = [];
    /// <summary>Parents of every commit in the repository, used to check the links between blocks.</summary>
    public Dictionary<string, List<string>> CommitParents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BlockVerificationStatus
{
    public int BlockIndex { get; set; }
    public string CommitSha { get; set; } = string.Empty;
    public string BlockHash { get; set; } = string.Empty;
    public bool PreviousHashMatches { get; set; }
    public bool MerkleRootMatches { get; set; }
    public bool BlockHashMatches { get; set; }
    public bool SignatureValid { get; set; }
    public bool IsValid => PreviousHashMatches && MerkleRootMatches && BlockHashMatches && SignatureValid;
    public string StatusSummary { get; set; } = string.Empty;
}

public sealed class ChainVerificationResult
{
    public bool IsValid { get; set; }
    public int VerifiedBlockCount { get; set; }
    public int? TamperedBlockIndex { get; set; }
    public string SummaryMessage { get; set; } = string.Empty;
    public List<BlockVerificationStatus> BlockStatuses { get; set; } = [];
}
