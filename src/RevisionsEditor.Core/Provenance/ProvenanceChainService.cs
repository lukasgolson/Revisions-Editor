using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RevisionsEditor.Core.Models;

namespace RevisionsEditor.Core.Provenance;

public sealed class ProvenanceChainService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new();

    public const string LegacyLedgerFileName = "provenance-chain.json";
    private static readonly string GenesisHash = new('0', 64);

    private sealed class StoredKeyMaterial
    {
        public string PublicKeyPem { get; set; } = string.Empty;
        public string PrivateKeyPem { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
    }

    public static (ECDsa Signer, string PublicKeyPem, string Fingerprint) LoadOrCreateAuthorKey(string revisionsDir)
    {
        var stored = LoadOrCreateKeyMaterial(revisionsDir);
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(stored.PrivateKeyPem);
        return (ecdsa, stored.PublicKeyPem, stored.Fingerprint);
    }

    private static StoredKeyMaterial LoadOrCreateKeyMaterial(string revisionsDir)
    {
        Directory.CreateDirectory(revisionsDir);
        string keyPath = Path.Combine(revisionsDir, "author-key.json");

        if (File.Exists(keyPath))
        {
            var stored = JsonSerializer.Deserialize<StoredKeyMaterial>(File.ReadAllText(keyPath), JsonOptions);
            if (stored != null && !string.IsNullOrWhiteSpace(stored.PrivateKeyPem))
            {
                return stored;
            }
        }

        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string pubPem = newKey.ExportSubjectPublicKeyInfoPem();
        var material = new StoredKeyMaterial
        {
            PublicKeyPem = pubPem,
            PrivateKeyPem = newKey.ExportPkcs8PrivateKeyPem(),
            Fingerprint = FingerprintOf(pubPem)
        };
        File.WriteAllText(keyPath, JsonSerializer.Serialize(material, JsonOptions));
        return material;
    }

    public static string FingerprintOf(string publicKeyPem)
        => "ECDSA-P256:" + TextCanonicalizer.ComputeSha256Hex(publicKeyPem)[..16].ToUpperInvariant();

    /// <summary>
    /// The ledger file kept by earlier versions in .revisions (migrated into the repository on first use),
    /// or an empty ledger carrying this computer's signing identity.
    /// </summary>
    public static ProvenanceLedger LoadOrCreateLedger(string revisionsDir, string documentId = "doc-main")
    {
        Directory.CreateDirectory(revisionsDir);
        string ledgerPath = Path.Combine(revisionsDir, LegacyLedgerFileName);

        // Only the public identity is needed here; the private key is imported solely when signing.
        var keyMaterial = LoadOrCreateKeyMaterial(revisionsDir);

        if (File.Exists(ledgerPath))
        {
            var existing = JsonSerializer.Deserialize<ProvenanceLedger>(File.ReadAllText(ledgerPath), JsonOptions);
            if (existing != null)
            {
                if (string.IsNullOrWhiteSpace(existing.AuthorPublicKeyPem))
                {
                    existing.AuthorPublicKeyPem = keyMaterial.PublicKeyPem;
                    existing.AuthorKeyFingerprint = keyMaterial.Fingerprint;
                }
                return existing;
            }
        }

        return CreateEmptyLedger(revisionsDir, documentId);
    }

    public static ProvenanceLedger CreateEmptyLedger(string revisionsDir, string documentId = "doc-main")
    {
        var keyMaterial = LoadOrCreateKeyMaterial(revisionsDir);
        return new ProvenanceLedger
        {
            DocumentId = documentId,
            AuthorPublicKeyPem = keyMaterial.PublicKeyPem,
            AuthorKeyFingerprint = keyMaterial.Fingerprint
        };
    }

    public static string SerializeBlock(TextChainBlock block) => JsonSerializer.Serialize(block, CompactJsonOptions);

    public static TextChainBlock? DeserializeBlock(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TextChainBlock>(json, CompactJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Assembles the ledger from the blocks stored with the commits: <paramref name="commitsParentsFirst"/>
    /// lists every commit (parents before children) with its parents. Blocks are ordered like their commits
    /// (blocks migrated from an earlier ledger file first, in their original order) and the sentence history
    /// is rebuilt by replaying them.
    /// </summary>
    public static ProvenanceLedger BuildLedger(
        string revisionsDir,
        string documentId,
        IReadOnlyList<(string Sha, List<string> Parents)> commitsParentsFirst,
        IReadOnlyDictionary<string, TextChainBlock> blocksByCommit)
    {
        var ledger = CreateEmptyLedger(revisionsDir, documentId);
        foreach (var (sha, parents) in commitsParentsFirst)
        {
            ledger.CommitParents[sha] = parents;
        }

        var ordered = new List<TextChainBlock>();
        ordered.AddRange(blocksByCommit.Values.Where(b => b.Version < 2).OrderBy(b => b.BlockIndex));
        int nextIndex = ordered.Count == 0 ? 0 : ordered.Max(b => b.BlockIndex) + 1;
        foreach (var (sha, _) in commitsParentsFirst)
        {
            if (blocksByCommit.TryGetValue(sha, out var block) && block.Version >= 2)
            {
                block.BlockIndex = nextIndex++;
                ordered.Add(block);
            }
        }

        foreach (var block in ordered)
        {
            ledger.Blocks.Add(block);
            UpdateSentenceProvenanceLineage(ledger, LeavesOf(block), block.BlockIndex, block.CommitSha, block.AuthorName, block.OriginSource, block.TimestampUtc);
        }
        return ledger;
    }

    /// <summary>The sentences of a block's snapshot, as they were when it was signed.</summary>
    private static List<CanonicalSentenceLeaf> LeavesOf(TextChainBlock block)
    {
        var leaves = new List<CanonicalSentenceLeaf>();
        string[] lines = block.CanonicalManuscriptSnapshot.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int p = 0; p < lines.Length; p++)
        {
            string paragraphId = p < block.ParagraphIds.Count ? block.ParagraphIds[p] : $"line-{p + 1:D3}";
            int s = 0;
            foreach (string sentence in TextCanonicalizer.SplitIntoSentences(lines[p]))
            {
                s++;
                leaves.Add(new CanonicalSentenceLeaf
                {
                    SentenceId = $"{paragraphId}-s{s:D2}",
                    ParagraphId = paragraphId,
                    ParagraphOrder = p + 1,
                    SentenceOrder = s,
                    CanonicalText = sentence
                });
            }
        }
        return leaves;
    }

    /// <summary>
    /// Hashes of the nearest signed blocks behind each of <paramref name="parentCommits"/>: a parent's own
    /// block, or for a commit made outside the app, the blocks behind it.
    /// </summary>
    public static List<string> NearestParentBlockHashes(ProvenanceLedger ledger, IEnumerable<string> parentCommits)
    {
        var blockByCommit = new Dictionary<string, TextChainBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in ledger.Blocks)
        {
            blockByCommit[block.CommitSha] = block;
        }

        var hashes = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(parentCommits);
        while (pending.Count > 0)
        {
            string sha = pending.Pop();
            if (!visited.Add(sha))
            {
                continue;
            }
            if (blockByCommit.TryGetValue(sha, out var block))
            {
                hashes.Add(block.BlockHash);
            }
            else if (ledger.CommitParents.TryGetValue(sha, out var grandParents))
            {
                foreach (string g in grandParents)
                {
                    pending.Push(g);
                }
            }
        }
        return hashes.ToList();
    }

    /// <summary>
    /// Signs the canonical text of <paramref name="document"/> at <paramref name="commitSha"/> with this
    /// computer's key and adds the block to <paramref name="ledger"/> (storing it is up to the caller).
    /// Without <paramref name="parentCommits"/> the block follows the ledger's last block.
    /// </summary>
    public static TextChainBlock AppendSignedBlock(
        string revisionsDir,
        ProvenanceLedger ledger,
        ManuscriptDocument document,
        string commitSha,
        string commitMessage,
        string authorName,
        AuthorshipOriginSource originSource,
        IReadOnlyList<string>? parentCommits = null)
    {
        var (signer, pubPem, fingerprint) = LoadOrCreateAuthorKey(revisionsDir);
        using (signer)
        {
            ledger.AuthorPublicKeyPem = pubPem;
            ledger.AuthorKeyFingerprint = fingerprint;

            var parents = parentCommits?.ToList()
                ?? (ledger.Blocks.Count == 0 ? [] : [ledger.Blocks[^1].CommitSha]);
            ledger.CommitParents[commitSha] = parents;
            var parentHashes = NearestParentBlockHashes(ledger, parents);

            int nextBlockIndex = ledger.Blocks.Count == 0 ? 0 : ledger.Blocks.Max(b => b.BlockIndex) + 1;
            string canonicalSnapshot = TextCanonicalizer.SerializeCanonicalTextSnapshot(document);
            string textMerkleRoot = TextCanonicalizer.ComputeMerkleRootFromCanonicalSnapshot(canonicalSnapshot);
            DateTimeOffset timestampUtc = DateTimeOffset.UtcNow;

            var currentLeaves = TextCanonicalizer.ExtractCanonicalLeaves(document);
            UpdateSentenceProvenanceLineage(ledger, currentLeaves, nextBlockIndex, commitSha, authorName, originSource, timestampUtc);

            var block = new TextChainBlock
            {
                Version = 2,
                BlockIndex = nextBlockIndex,
                CommitSha = commitSha,
                CommitMessage = commitMessage,
                TimestampUtc = timestampUtc,
                AuthorName = authorName,
                AuthorKeyFingerprint = fingerprint,
                AuthorPublicKeyPem = pubPem,
                OriginSource = originSource,
                ParentBlockHashes = parentHashes,
                PreviousBlockHash = parentHashes.FirstOrDefault() ?? GenesisHash,
                NormalizedTextMerkleRoot = textMerkleRoot,
                SentenceProvenanceRoot = ComputeSentenceProvenanceRoot(ledger.Sentences),
                CanonicalManuscriptSnapshot = canonicalSnapshot,
                ParagraphIds = document.Blocks
                    .Where(b => TextCanonicalizer.SplitIntoSentences(b.Text).Count > 0)
                    .Select(b => b.ParagraphId)
                    .ToList(),
                SentenceCount = currentLeaves.Count,
                ParagraphCount = document.Blocks.Count
            };

            block.BlockHash = ComputeBlockHeaderHash(block);
            byte[] signatureBytes = signer.SignData(Encoding.UTF8.GetBytes(block.BlockHash), HashAlgorithmName.SHA256);
            block.SignatureBase64 = Convert.ToBase64String(signatureBytes);

            ledger.Blocks.Add(block);
            return block;
        }
    }

    public static string ComputeBlockHeaderHash(TextChainBlock block)
    {
        string payload = block.Version >= 2
            ? string.Join("|",
                "v2",
                block.CommitSha,
                string.Join(",", block.ParentBlockHashes.Order(StringComparer.Ordinal)),
                block.TimestampUtc.ToUnixTimeMilliseconds(),
                block.AuthorName,
                block.AuthorKeyFingerprint,
                TextCanonicalizer.ComputeSha256Hex(block.AuthorPublicKeyPem),
                block.NormalizedTextMerkleRoot,
                block.SentenceProvenanceRoot,
                block.OriginSource)
            : string.Join("|",
                block.BlockIndex,
                block.CommitSha,
                block.TimestampUtc.ToUnixTimeMilliseconds(),
                block.AuthorName,
                block.AuthorKeyFingerprint,
                block.PreviousBlockHash,
                block.NormalizedTextMerkleRoot,
                block.SentenceProvenanceRoot);

        return TextCanonicalizer.ComputeSha256Hex(payload);
    }

    private static string ComputeSentenceProvenanceRoot(IReadOnlyList<SentenceProvenanceRecord> sentences)
    {
        if (sentences.Count == 0)
        {
            return TextCanonicalizer.ComputeSha256Hex("NO_SENTENCES");
        }

        // Commits each sentence's identity, current text, number of revisions and origin. Blocks written by
        // earlier versions committed a typing-based score instead; their stored roots remain valid because
        // verification checks the signed header, not this computation.
        var hashes = sentences
            .Select(s => TextCanonicalizer.ComputeSha256Hex(
                $"{s.SentenceId}:{s.CurrentNormalizedHash}:{s.RevisionHistory.Count}:{s.OriginSource}"))
            .ToList();

        return TextCanonicalizer.ComputeMerkleRoot(hashes);
    }

    private static void UpdateSentenceProvenanceLineage(
        ProvenanceLedger ledger,
        List<CanonicalSentenceLeaf> currentLeaves,
        int blockIndex,
        string commitSha,
        string authorName,
        AuthorshipOriginSource originSource,
        DateTimeOffset timestampUtc)
    {
        var matchedExistingIds = new HashSet<string>(StringComparer.Ordinal);

        // Index existing records once: exact matches by hash (in ledger order) and word sets for similarity.
        // A record's text only changes after it is matched, and matched records are never candidates again,
        // so the cached word sets stay valid for the whole pass.
        var recordsByHash = new Dictionary<string, List<SentenceProvenanceRecord>>(StringComparer.Ordinal);
        var candidateWordSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var record in ledger.Sentences)
        {
            if (!recordsByHash.TryGetValue(record.CurrentNormalizedHash, out var bucket))
            {
                bucket = [];
                recordsByHash[record.CurrentNormalizedHash] = bucket;
            }
            bucket.Add(record);
            candidateWordSets[record.SentenceId] = TextCanonicalizer.ExtractWordSet(record.CurrentCanonicalText);
        }

        foreach (var leaf in currentLeaves)
        {
            string normHash = TextCanonicalizer.ComputeSha256Hex(leaf.CanonicalText);

            // 1. Check for exact unchanged match
            var exactMatch = recordsByHash.TryGetValue(normHash, out var sameHash)
                ? sameHash.FirstOrDefault(s => !matchedExistingIds.Contains(s.SentenceId))
                : null;

            if (exactMatch != null)
            {
                matchedExistingIds.Add(exactMatch.SentenceId);
                exactMatch.ParagraphId = leaf.ParagraphId;
                continue;
            }

            // 2. Check for evolutionary edit match (similarity >= 0.35)
            SentenceProvenanceRecord? bestEvolutionMatch = null;
            double bestSimilarity = 0.0;
            var leafWords = TextCanonicalizer.ExtractWordSet(leaf.CanonicalText);

            foreach (var candidate in ledger.Sentences)
            {
                if (matchedExistingIds.Contains(candidate.SentenceId))
                {
                    continue;
                }

                double sim = string.Equals(candidate.CurrentCanonicalText, leaf.CanonicalText, StringComparison.OrdinalIgnoreCase)
                    ? 1.0
                    : TextCanonicalizer.ComputeWordSetSimilarity(candidateWordSets[candidate.SentenceId], leafWords);
                if (string.Equals(candidate.ParagraphId, leaf.ParagraphId, StringComparison.Ordinal))
                {
                    sim += 0.08; // Boost same-paragraph continuity
                }

                if (sim >= 0.35 && sim > bestSimilarity)
                {
                    bestSimilarity = Math.Min(1.0, sim);
                    bestEvolutionMatch = candidate;
                }
            }

            var step = new SentenceRevisionStep
            {
                CommitSha = commitSha,
                BlockIndex = blockIndex,
                TimestampUtc = timestampUtc,
                Author = authorName,
                Source = originSource,
                AfterText = leaf.CanonicalText
            };

            if (bestEvolutionMatch != null)
            {
                matchedExistingIds.Add(bestEvolutionMatch.SentenceId);
                step.BeforeText = bestEvolutionMatch.CurrentCanonicalText;
                step.SimilarityToPrevious = Math.Round(bestSimilarity, 2);

                bestEvolutionMatch.ParagraphId = leaf.ParagraphId;
                bestEvolutionMatch.CurrentCanonicalText = leaf.CanonicalText;
                bestEvolutionMatch.CurrentNormalizedHash = normHash;
                bestEvolutionMatch.RevisionHistory.Add(step);
            }
            else
            {
                // Newly authored sentence
                var newRecord = new SentenceProvenanceRecord
                {
                    SentenceId = $"sent-{ledger.Sentences.Count + 1:D4}",
                    ParagraphId = leaf.ParagraphId,
                    CurrentCanonicalText = leaf.CanonicalText,
                    CurrentNormalizedHash = normHash,
                    OriginCommitSha = commitSha,
                    OriginAuthor = authorName,
                    OriginSource = originSource,
                    CreatedAtUtc = timestampUtc
                };

                step.BeforeText = string.Empty;
                step.SimilarityToPrevious = 0.0;
                newRecord.RevisionHistory.Add(step);

                ledger.Sentences.Add(newRecord);
                matchedExistingIds.Add(newRecord.SentenceId);
            }
        }
    }

    /// <summary>
    /// Checks every block: its links (version 1: the previous block in the linear chain; version 2: the
    /// nearest signed blocks of the commit's parents, so a removed or replaced block is noticed), the Merkle
    /// root of its text, its header hash, and its ECDSA P-256 signature with the key it names.
    /// </summary>
    public static ChainVerificationResult VerifyChainIntegrity(ProvenanceLedger ledger)
    {
        var result = new ChainVerificationResult();

        if (ledger.Blocks.Count == 0)
        {
            result.IsValid = true;
            result.SummaryMessage = "No signed blocks yet. The first commit starts the provenance chain.";
            return result;
        }

        var verifiers = new Dictionary<string, ECDsa?>(StringComparer.Ordinal);
        try
        {
            string previousV1Hash = GenesisHash;
            foreach (var block in ledger.Blocks)
            {
                bool linksMatch;
                if (block.Version >= 2)
                {
                    var expected = ledger.CommitParents.TryGetValue(block.CommitSha, out var parents)
                        ? NearestParentBlockHashes(ledger, parents)
                        : [];
                    linksMatch = expected.SequenceEqual(block.ParentBlockHashes.Order(StringComparer.Ordinal), StringComparer.Ordinal);
                }
                else
                {
                    linksMatch = string.Equals(block.PreviousBlockHash, previousV1Hash, StringComparison.Ordinal);
                    previousV1Hash = block.BlockHash;
                }

                string recomputedMerkle = TextCanonicalizer.ComputeMerkleRootFromCanonicalSnapshot(block.CanonicalManuscriptSnapshot);
                bool merkleMatches = string.Equals(block.NormalizedTextMerkleRoot, recomputedMerkle, StringComparison.Ordinal);
                bool blockHashMatches = string.Equals(block.BlockHash, ComputeBlockHeaderHash(block), StringComparison.Ordinal);

                string keyPem = string.IsNullOrWhiteSpace(block.AuthorPublicKeyPem) ? ledger.AuthorPublicKeyPem : block.AuthorPublicKeyPem;
                bool keyMatchesFingerprint = string.Equals(FingerprintOf(keyPem), block.AuthorKeyFingerprint, StringComparison.Ordinal);
                bool signatureValid = false;
                if (keyMatchesFingerprint && GetVerifier(verifiers, keyPem) is { } verifier)
                {
                    try
                    {
                        signatureValid = verifier.VerifyData(
                            Encoding.UTF8.GetBytes(block.BlockHash),
                            Convert.FromBase64String(block.SignatureBase64),
                            HashAlgorithmName.SHA256);
                    }
                    catch (FormatException)
                    {
                        signatureValid = false;
                    }
                }

                var status = new BlockVerificationStatus
                {
                    BlockIndex = block.BlockIndex,
                    CommitSha = block.CommitSha,
                    BlockHash = block.BlockHash,
                    PreviousHashMatches = linksMatch,
                    MerkleRootMatches = merkleMatches,
                    BlockHashMatches = blockHashMatches,
                    SignatureValid = signatureValid
                };

                string shortSha = block.CommitSha[..Math.Min(7, block.CommitSha.Length)];
                if (status.IsValid)
                {
                    status.StatusSummary = $"Block #{block.BlockIndex} ({shortSha}) verified: Merkle root and ECDSA P-256 signature intact.";
                    result.VerifiedBlockCount++;
                }
                else
                {
                    var reasons = new List<string>();
                    if (!linksMatch) reasons.Add(block.Version >= 2 ? "links to earlier blocks do not match the history (a block was removed or replaced)" : "broken link to the previous block");
                    if (!merkleMatches) reasons.Add("text Merkle root mismatch (historical text was modified)");
                    if (!blockHashMatches) reasons.Add("block header hash mismatch");
                    if (!keyMatchesFingerprint) reasons.Add("signing key does not match its fingerprint");
                    else if (!signatureValid) reasons.Add("ECDSA P-256 signature is not valid");

                    status.StatusSummary = $"Tampering detected at block #{block.BlockIndex} ({shortSha}): {string.Join("; ", reasons)}.";
                    result.TamperedBlockIndex ??= block.BlockIndex;
                }

                result.BlockStatuses.Add(status);
            }
        }
        finally
        {
            foreach (var v in verifiers.Values)
            {
                v?.Dispose();
            }
        }

        result.IsValid = result.TamperedBlockIndex == null;
        int keys = ledger.Blocks.Select(b => b.AuthorKeyFingerprint).Distinct(StringComparer.Ordinal).Count();
        string keyNote = keys > 1 ? $" (signed on {keys} computers)" : string.Empty;
        result.SummaryMessage = result.IsValid
            ? $"Provenance chain verified: all {result.VerifiedBlockCount} signed blocks are intact{keyNote}."
            : $"Provenance chain integrity failure: tampering detected starting at block #{result.TamperedBlockIndex}.";

        return result;
    }

    private static ECDsa? GetVerifier(Dictionary<string, ECDsa?> cache, string pem)
    {
        if (!cache.TryGetValue(pem, out var verifier))
        {
            verifier = ECDsa.Create();
            try
            {
                verifier.ImportFromPem(pem);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                verifier.Dispose();
                verifier = null;
            }
            cache[pem] = verifier;
        }
        return verifier;
    }

    /// <summary>
    /// Exports a standalone, human-readable HTML Authorship & Cryptographic Provenance Certificate.
    /// </summary>
    public static string ExportAuditCertificateHtml(ProvenanceLedger ledger, ManuscriptDocument document)
    {
        var verification = VerifyChainIntegrity(ledger);
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        sb.AppendLine($"<title>Authorship Provenance Certificate — {WebUtility.HtmlEncode(document.Title)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: 'Segoe UI', system-ui, sans-serif; max-width: 980px; margin: 32px auto; color: #1e293b; line-height: 1.5; }");
        sb.AppendLine(".badge { display: inline-block; padding: 6px 12px; border-radius: 6px; font-weight: 700; background: #dcfce7; color: #166534; }");
        sb.AppendLine(".card { border: 1px solid #cbd5e1; border-radius: 8px; padding: 16px; margin-bottom: 16px; background: #f8fafc; }");
        sb.AppendLine("code { font-family: Consolas, monospace; font-size: 12px; background: #e2e8f0; padding: 2px 6px; border-radius: 4px; }");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>Cryptographic Authorship &amp; Provenance Certificate</h1>");
        sb.AppendLine($"<p><strong>Manuscript:</strong> {WebUtility.HtmlEncode(document.Title)}</p>");

        var keys = ledger.Blocks
            .GroupBy(b => b.AuthorKeyFingerprint, StringComparer.Ordinal)
            .Select(g => (Fingerprint: g.Key, Authors: string.Join(", ", g.Select(b => b.AuthorName).Distinct()), Count: g.Count()))
            .ToList();
        sb.AppendLine("<p><strong>Signing keys</strong> (one per computer):</p><ul>");
        foreach (var key in keys)
        {
            sb.AppendLine($"<li><code>{WebUtility.HtmlEncode(key.Fingerprint)}</code> — {key.Count} blocks, by {WebUtility.HtmlEncode(key.Authors)}</li>");
        }
        sb.AppendLine("</ul>");
        sb.AppendLine($"<p><span class='badge'>{WebUtility.HtmlEncode(verification.SummaryMessage)}</span></p>");

        sb.AppendLine("<h2>1. Cryptographic Text Hash Chain (Formatting-Independent)</h2>");
        foreach (var block in ledger.Blocks)
        {
            sb.AppendLine("<div class='card'>");
            sb.AppendLine($"<strong>Block #{block.BlockIndex}</strong> — Commit <code>{WebUtility.HtmlEncode(block.CommitSha)}</code> — {WebUtility.HtmlEncode(block.CommitMessage)}<br/>");
            sb.AppendLine($"<small>Author: {WebUtility.HtmlEncode(block.AuthorName)} | Timestamp: {block.TimestampUtc:u} | Key: <code>{WebUtility.HtmlEncode(block.AuthorKeyFingerprint)}</code></small><br/>");
            if (block.Version >= 2)
            {
                string parents = block.ParentBlockHashes.Count == 0 ? "(first block)" : string.Join(", ", block.ParentBlockHashes.Select(h => h[..Math.Min(16, h.Length)]));
                sb.AppendLine($"<small>Parent blocks: <code>{WebUtility.HtmlEncode(parents)}</code></small><br/>");
            }
            else
            {
                sb.AppendLine($"<small>PreviousBlockHash: <code>{block.PreviousBlockHash}</code></small><br/>");
            }
            sb.AppendLine($"<small>NormalizedTextMerkleRoot: <code>{block.NormalizedTextMerkleRoot}</code></small><br/>");
            sb.AppendLine($"<small>BlockHash: <code>{block.BlockHash}</code></small><br/>");
            sb.AppendLine($"<small>ECDSA Signature: <code>{block.SignatureBase64[..Math.Min(48, block.SignatureBase64.Length)]}...</code></small>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("<h2>2. Sentence-Level Provenance</h2>");
        foreach (var sent in ledger.Sentences)
        {
            var authors = sent.RevisionHistory.Select(r => r.Author).Where(a => a.Length > 0).Distinct();
            sb.AppendLine("<div class='card'>");
            sb.AppendLine($"<strong>{WebUtility.HtmlEncode(sent.SentenceId)}</strong> (Paragraph <code>{WebUtility.HtmlEncode(sent.ParagraphId)}</code>) — ");
            sb.AppendLine($"Origin: {WebUtility.HtmlEncode(sent.OriginSource.ToString())} by {WebUtility.HtmlEncode(sent.OriginAuthor)} | Revisions: {sent.TotalRevisions} | Revised by: {WebUtility.HtmlEncode(string.Join(", ", authors))}<br/>");
            sb.AppendLine($"<p>{WebUtility.HtmlEncode(sent.CurrentCanonicalText)}</p>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
