// Applies Revisions Editor patches to a Google Doc through the Google Docs API.
//
// The document is read once (documents.get), every patch is located and turned into minimal edit requests,
// and all requests are sent in a single documents.batchUpdate pinned to the revision that was read, so the
// edit is rejected rather than misapplied if someone changes the document in between.
//
// Only the words that differ are rewritten, so formatting on the unchanged text (bold, links, fonts) is kept;
// inserted words take the formatting of the text before them, as they would when typing.
//
// Loaded by background.js with importScripts. The planning functions are pure so they can be tested offline.
(function (root) {
  "use strict";

  const DOCS_API = "https://docs.googleapis.com/v1/documents";
  const OBJECT_PLACEHOLDER = "\uFFFC"; // stands in for images and other non-text elements
  const LEADING_SECTION_NUMBER = /^\d+(\.\d+)*\.?\s+/;
  const NAMED_STYLE_BY_ROLE = {
    Title: "TITLE",
    Heading1: "HEADING_1",
    Heading2: "HEADING_2",
    Heading3: "HEADING_3"
  };

  class AuthError extends Error {}
  class RevisionConflictError extends Error {}

  // ---- Text normalization (mirrors DiffEngineService.GoogleDocsMatchKey in the desktop app) ----------

  function normalizeChars(s) {
    return (s || "")
      .normalize("NFC")
      .replace(/[\u2018\u2019\u201A\u201B\u2032]/g, "'")
      .replace(/[\u201C\u201D\u201E\u201F\u2033]/g, '"')
      .replace(/[\u2013\u2014]/g, "-")
      .replace(/[\u00A0\u000B\u2028\u2029\t]/g, " ")
      .replace(/[\u200B-\u200D\u2060\uFEFF]/g, "");
  }

  /**
   * Key for finding a patch's paragraph in the live document. The patch text comes from Google's Word
   * export (via the desktop app) while the document text comes from the Docs API, and the two differ in
   * spacing and in leftover emphasis markers, so whitespace and asterisks are ignored. Comparing everything
   * else exactly still identifies paragraphs reliably.
   */
  function matchKey(s) {
    return normalizeChars(s).replace(/[\s*]+/g, "");
  }

  // Headings may carry manual numbers ("2. Method") that the desktop app's formatting rules strip.
  function looseMatchKey(s) {
    return matchKey(normalizeChars(s).trim().replace(LEADING_SECTION_NUMBER, ""));
  }

  function tokenKey(token) {
    return normalizeChars(token).replace(/\s+/g, " ");
  }

  // ---- Reading the document ----------------------------------------------------------------------

  /**
   * Flattens the body into paragraphs: { start, end, text, segments, inTable, style }.
   * `text` includes the trailing newline; segments map text offsets back to document indices.
   */
  function hasSuggestions(item) {
    return !!item && ["suggestedInsertionIds", "suggestedDeletionIds"].some((k) => Array.isArray(item[k]) && item[k].length > 0);
  }

  /**
   * What an inline object reads as in Google's Word export: smart chips become their visible text,
   * while footnote references, images, equations and breaks contribute no text.
   */
  function objectDisplayText(part) {
    if (part.person && part.person.personProperties) {
      return part.person.personProperties.name || part.person.personProperties.email || "";
    }
    if (part.richLink && part.richLink.richLinkProperties) {
      return part.richLink.richLinkProperties.title || part.richLink.richLinkProperties.uri || "";
    }
    if (part.dateElement && part.dateElement.dateElementProperties) {
      return part.dateElement.dateElementProperties.displayText || "";
    }
    return "";
  }

  function collectParagraphs(content, out = [], inTable = false) {
    const elements = content || [];
    for (let position = 0; position < elements.length; position++) {
      const element = elements[position];
      if (element.paragraph) {
        const segments = [];
        let text = "";
        let suggested = false;
        // `reading` is the paragraph as text (objects replaced by their display text, without the final
        // newline); readingMap[k] = { offset in `text`, isObject } for each of its characters.
        let reading = "";
        const readingMap = [];
        for (const part of element.paragraph.elements || []) {
          const length = Math.max(0, (part.endIndex || 0) - (part.startIndex || 0));
          if (part.textRun) {
            const content = part.textRun.content;
            segments.push({ offset: text.length, index: part.startIndex, length: content.length });
            // Snapshots are read without pending suggestions, so suggested insertions are not part of the
            // reading text (the paragraph is then refused as having suggestions rather than mismatched).
            const suggestedInsertion = Array.isArray(part.textRun.suggestedInsertionIds) && part.textRun.suggestedInsertionIds.length > 0;
            for (let k = 0; k < content.length && !suggestedInsertion; k++) {
              if (content[k] === "\n" && k === content.length - 1) break;
              reading += content[k];
              readingMap.push({ offset: text.length + k, isObject: false });
            }
            text += content;
            suggested = suggested || hasSuggestions(part.textRun);
          } else if (length > 0) {
            segments.push({ offset: text.length, index: part.startIndex, length });
            for (const ch of objectDisplayText(part)) {
              for (let unit = 0; unit < ch.length; unit++) {
                reading += ch[unit];
                readingMap.push({ offset: text.length, isObject: true });
              }
            }
            text += OBJECT_PLACEHOLDER.repeat(length);
            suggested = suggested || Object.values(part).some(hasSuggestions);
          }
        }
        // Google Docs refuses to delete the newline right before a table, table of contents or section break.
        const next = elements[position + 1];
        out.push({
          start: element.startIndex,
          end: element.endIndex,
          text,
          segments,
          reading,
          readingMap,
          inTable,
          hasSuggestions: suggested,
          beforeStructure: !!next && !!(next.table || next.tableOfContents || next.sectionBreak),
          style: element.paragraph.paragraphStyle && element.paragraph.paragraphStyle.namedStyleType
        });
      } else if (element.table) {
        for (const row of element.table.tableRows || []) {
          for (const cell of row.tableCells || []) {
            collectParagraphs(cell.content, out, true);
          }
        }
      } else if (element.tableOfContents) {
        collectParagraphs(element.tableOfContents.content, out, true);
      }
    }
    return out;
  }

  function visibleText(paragraph) {
    return paragraph.text.endsWith("\n") ? paragraph.text.slice(0, -1) : paragraph.text;
  }

  /** Document index of a character offset within a paragraph's text. */
  function offsetToIndex(paragraph, offset) {
    for (const seg of paragraph.segments) {
      if (offset >= seg.offset && offset < seg.offset + seg.length) {
        return seg.index + (offset - seg.offset);
      }
    }
    return paragraph.end - 1; // the paragraph's newline
  }

  // ---- Word diff -----------------------------------------------------------------------------------

  const TOKEN_PATTERN = /[\p{L}\p{N}_]+\s*|[^\s\p{L}\p{N}_]+\s*|\s+/gu;

  function tokenize(text) {
    const tokens = [];
    for (const m of text.matchAll(TOKEN_PATTERN)) {
      tokens.push({ text: m[0], start: m.index, key: tokenKey(m[0]) });
    }
    return tokens;
  }

  /**
   * Minimal edits turning `original` into `target`, as [{ from, to, insert }] in original-text offsets.
   * Tokens that differ only in quote style, dash style or spacing count as equal and are left untouched.
   */
  function computeHunks(original, target) {
    const a = tokenize(original);
    const b = tokenize(target);
    const n = a.length;
    const m = b.length;
    const width = m + 1;
    const dp = new Uint32Array((n + 1) * width);
    for (let i = n - 1; i >= 0; i--) {
      for (let j = m - 1; j >= 0; j--) {
        dp[i * width + j] = a[i].key === b[j].key
          ? 1 + dp[(i + 1) * width + j + 1]
          : Math.max(dp[(i + 1) * width + j], dp[i * width + j + 1]);
      }
    }

    const hunks = [];
    let current = null;
    const startHunk = (at) => { current = current || { from: at, to: at, insert: "" }; return current; };
    const flush = () => { if (current) { hunks.push(current); current = null; } };

    let i = 0;
    let j = 0;
    while (i < n || j < m) {
      if (i < n && j < m && a[i].key === b[j].key) {
        flush();
        i++;
        j++;
      } else if (j >= m || (i < n && dp[(i + 1) * width + j] >= dp[i * width + j + 1])) {
        const hunk = startHunk(a[i].start);
        hunk.to = a[i].start + a[i].text.length;
        i++;
      } else {
        const at = i < n ? a[i].start : original.length;
        const hunk = startHunk(at);
        hunk.insert += b[j].text;
        j++;
      }
    }
    flush();
    return hunks;
  }

  // ---- Planning edits ----------------------------------------------------------------------------

  function singleLine(text) {
    return (text || "").replace(/\r?\n/g, " ");
  }

  /**
   * Requests that rewrite paragraph `p` to read `target`, changing only the words that differ.
   *
   * The diff runs on the paragraph's reading text (smart chips as their text, other objects absent), then
   * maps back to document positions: deletions skip over footnote references, images and other objects so
   * they are never removed, and a change that would rewrite a chip's own text is refused.
   */
  function planParagraphEdit(p, target) {
    const manualNumber = p.reading.match(LEADING_SECTION_NUMBER);
    if (manualNumber && !LEADING_SECTION_NUMBER.test(target)) {
      target = manualNumber[0] + target; // keep the heading's own number
    }

    const visibleLength = visibleText(p).length;
    const requests = [];
    for (const h of computeHunks(p.reading, target).reverse()) {
      const deletedChars = p.readingMap.slice(h.from, h.to);
      if (deletedChars.some((c) => c.isObject)) {
        throw new Error("The change rewrites a smart chip (a person, date or link). Apply it manually.");
      }

      // Deleted characters as ranges of the paragraph text, split wherever an object sits between them.
      const ranges = [];
      for (const c of deletedChars) {
        const last = ranges[ranges.length - 1];
        if (last && last.to === c.offset) last.to++;
        else ranges.push({ from: c.offset, to: c.offset + 1 });
      }

      for (const r of ranges.slice(1).reverse()) {
        requests.push({ deleteContentRange: { range: { startIndex: offsetToIndex(p, r.from), endIndex: offsetToIndex(p, r.to) } } });
      }

      if (ranges.length > 0 && h.insert) {
        // Inserted text takes the formatting of the character before it, so insert after the first replaced
        // character: the replacement keeps the replaced words' formatting (e.g. bold), as when retyping a
        // selection. Characters outside the Basic Multilingual Plane (math letters, emoji) are two UTF-16
        // code units, and Google Docs rejects a range that splits one, so step over the whole character.
        const r0 = ranges[0];
        const startIndex = offsetToIndex(p, r0.from);
        const endIndex = offsetToIndex(p, r0.to);
        const first = p.text.codePointAt(r0.from) > 0xffff ? 2 : 1;
        const length = h.insert.length;
        requests.push({ insertText: { location: { index: startIndex + first }, text: h.insert } });
        if (endIndex > startIndex + first) {
          requests.push({ deleteContentRange: { range: { startIndex: startIndex + first + length, endIndex: endIndex + length } } });
        }
        requests.push({ deleteContentRange: { range: { startIndex, endIndex: startIndex + first } } });
      } else if (ranges.length > 0) {
        requests.push({ deleteContentRange: { range: { startIndex: offsetToIndex(p, ranges[0].from), endIndex: offsetToIndex(p, ranges[0].to) } } });
      } else if (h.insert) {
        const at = h.from < p.readingMap.length ? p.readingMap[h.from].offset : visibleLength;
        requests.push({ insertText: { location: { index: offsetToIndex(p, at) }, text: h.insert } });
      }
    }
    return requests;
  }

  /**
   * Turns patches into Docs API requests against `doc`.
   * Returns { requests, results } where results has one { patchId, ok, message } per patch.
   */
  function planRequests(doc, patches, tabId = null) {
    const tab = findTab(doc, tabId);
    if (!tab) {
      throw new Error("The selected document tab no longer exists. Choose a tab and try again.");
    }
    const paragraphs = collectParagraphs(tab.body && tab.body.content);
    const bodyParagraphs = paragraphs.filter((p) => !p.inTable);
    const lastBodyParagraph = bodyParagraphs[bodyParagraphs.length - 1];
    const used = new Set();
    let cursor = 0;
    const ops = [];
    const results = [];
    const deletions = [];
    const insertions = [];

    // Patches arrive in document order: search forward from the last match first, then everywhere,
    // trying an exact match before ignoring manual heading numbers.
    function findParagraph(text, { claim, preferBefore = false }) {
      const order = [];
      if (preferBefore) {
        for (let k = cursor - 1; k >= 0; k--) order.push(k);
        for (let k = cursor; k < paragraphs.length; k++) order.push(k);
      } else {
        for (let k = cursor; k < paragraphs.length; k++) order.push(k);
        for (let k = 0; k < cursor; k++) order.push(k);
      }
      for (const keyOf of [matchKey, looseMatchKey]) {
        const wanted = keyOf(text);
        if (!wanted) continue;
        for (const k of order) {
          if (claim && used.has(k)) continue;
          if (keyOf(paragraphs[k].reading) === wanted) {
            if (claim) {
              used.add(k);
              cursor = k + 1;
            }
            return paragraphs[k];
          }
        }
      }
      return null;
    }

    // Explains a failed match by showing where the most similar paragraph differs, which tells an edit made
    // in Google Docs apart from a text-conversion problem.
    function notFoundError(text) {
      const words = (s) => new Set(normalizeChars(s).toLowerCase().match(/[\p{L}\p{N}]+/gu) || []);
      const wanted = words(text);
      let best = null;
      let bestScore = 0;
      for (const p of paragraphs) {
        const have = words(p.reading);
        if (wanted.size === 0 || have.size === 0) continue;
        let shared = 0;
        for (const w of wanted) if (have.has(w)) shared++;
        const score = (2 * shared) / (wanted.size + have.size);
        if (score > bestScore) {
          bestScore = score;
          best = p;
        }
      }

      const base = "This paragraph no longer matches Google Docs, so it was not changed.";
      if (!best || bestScore < 0.5) {
        return new Error(`${base} No similar paragraph was found; it may have been deleted or rewritten in Google Docs. Apply it manually.`);
      }
      const a = normalizeChars(best.reading).replace(/\s+/g, " ").trim();
      const b = normalizeChars(text).replace(/\s+/g, " ").trim();
      let at = 0;
      while (at < a.length && at < b.length && a[at] === b[at]) at++;
      const excerpt = (s) => `${at > 30 ? "\u2026" : ""}${s.slice(Math.max(0, at - 30), at + 30)}${at + 30 < s.length ? "\u2026" : ""}`;
      return new Error(`${base} The closest paragraph differs here. Google Docs: \u201C${excerpt(a)}\u201D; expected: \u201C${excerpt(b)}\u201D. If you edited it in Google Docs after sending the document, apply this change manually.`);
    }
    const pendingSuggestions = "This paragraph has suggested edits in Google Docs. Accept or reject them, then apply again.";

    patches.forEach((patch, order) => {
      const kind = String(patch.kind || "Modify").toLowerCase();
      try {
        if (kind === "modify") {
          const p = findParagraph(patch.beforeText, { claim: true });
          if (!p) throw notFoundError(patch.beforeText);
          if (p.hasSuggestions) throw new Error(pendingSuggestions);
          const requests = planParagraphEdit(p, singleLine(patch.afterText));
          ops.push({ index: p.start, priority: 0, order, patchIds: [patch.patchId], requests });
        } else if (kind === "delete") {
          const p = findParagraph(patch.beforeText, { claim: true });
          if (!p) throw notFoundError(patch.beforeText);
          if (p.inTable) throw new Error("Paragraphs inside tables cannot be removed automatically. Apply it manually.");
          if (p.hasSuggestions) throw new Error(pendingSuggestions);
          // Ranges are worked out once every deletion is known (see below).
          deletions.push({ paragraph: p, order, patchId: patch.patchId });
        } else if (kind === "insert") {
          let anchor = null;
          if (!patch.anchorText) {
            if (!bodyParagraphs[0]) throw new Error("The document has no paragraphs to insert into.");
          } else {
            anchor = findParagraph(patch.anchorText, { claim: false, preferBefore: true });
            if (!anchor) throw new Error("The paragraph this one follows was not found in Google Docs. Apply it manually.");
            if (anchor.inTable) throw new Error("Paragraphs cannot be added inside tables automatically. Apply it manually.");
          }
          // Requests are built once deletions are known (see below), since they affect where to insert.
          insertions.push({
            anchor,
            text: singleLine(patch.afterText),
            namedStyleType: NAMED_STYLE_BY_ROLE[patch.role] || "NORMAL_TEXT",
            order,
            patchId: patch.patchId
          });
        } else {
          throw new Error(`Unknown change type '${patch.kind}'.`);
        }
        results.push({ patchId: patch.patchId, ok: true });
      } catch (err) {
        results.push({ patchId: patch.patchId, ok: false, message: err.message });
      }
    });

    // The document's final newline cannot be deleted. When the deletions reach the end of the document,
    // remove everything from the newline of the last kept paragraph instead, as one range covering the
    // whole trailing run of deleted paragraphs.
    const deleted = new Set(deletions.map((d) => d.paragraph));
    let trailingRunStart = null;
    if (deleted.has(lastBodyParagraph)) {
      const lastPosition = bodyParagraphs.indexOf(lastBodyParagraph);
      let keep = lastPosition - 1;
      while (keep >= 0 && deleted.has(bodyParagraphs[keep])) keep--;
      const failTrailing = (message, from) => {
        for (const d of deletions.filter((x) => x.paragraph.start >= from)) {
          const result = results.find((r) => r.patchId === d.patchId);
          result.ok = false;
          result.message = message;
        }
        deletions.splice(0, deletions.length, ...deletions.filter((x) => x.paragraph.start < from));
      };
      // Anything else between the last kept paragraph and the end (such as a table) would be removed too.
      const blocked = keep >= 0 && paragraphs.some((q) =>
        q.start >= bodyParagraphs[keep].end && q.start < lastBodyParagraph.end && !deleted.has(q));
      if (keep < 0) {
        failTrailing("Removing every paragraph would leave the document empty. Apply it manually.", 0);
      } else if (blocked) {
        failTrailing("These paragraphs are next to a table at the end of the document. Remove them manually.", bodyParagraphs[keep + 1].start);
      } else {
        const kept = bodyParagraphs[keep];
        trailingRunStart = bodyParagraphs[keep + 1].start;
        const covered = deletions.filter((d) => d.paragraph.start >= trailingRunStart);
        ops.push({
          index: kept.end - 1,
          priority: 0,
          order: Math.max(...covered.map((d) => d.order)),
          patchIds: covered.map((d) => d.patchId),
          requests: [{ deleteContentRange: { range: { startIndex: kept.end - 1, endIndex: lastBodyParagraph.end - 1 } } }]
        });
      }
    }
    for (const d of deletions) {
      if (trailingRunStart !== null && d.paragraph.start >= trailingRunStart) continue; // covered above
      const p = d.paragraph;
      // Before a table or section break the paragraph's newline must stay: remove only its text, leaving an
      // empty line (Google Docs keeps one before a table anyway).
      const endIndex = p.beforeStructure ? p.end - 1 : p.end;
      if (endIndex > p.start) {
        ops.push({
          index: p.start,
          priority: 0,
          order: d.order,
          patchIds: [d.patchId],
          requests: [{ deleteContentRange: { range: { startIndex: p.start, endIndex } } }]
        });
      }
    }

    for (const ins of insertions) {
      let insertAt;
      let inserted;
      let styleStart;
      // After the last paragraph, before a table (whose start is not a text position), or before a trailing
      // run that is being deleted, add the paragraph by inserting a newline at the end of the anchor's text.
      const atEndOfAnchor = ins.anchor && (ins.anchor === lastBodyParagraph
        || ins.anchor.beforeStructure
        || (trailingRunStart !== null && ins.anchor.end === trailingRunStart));
      if (!ins.anchor) {
        insertAt = bodyParagraphs[0].start;
        inserted = ins.text + "\n";
        styleStart = insertAt;
      } else if (atEndOfAnchor) {
        insertAt = ins.anchor.end - 1;
        inserted = "\n" + ins.text;
        styleStart = ins.anchor.end;
      } else {
        insertAt = ins.anchor.end;
        inserted = ins.text + "\n";
        styleStart = insertAt;
      }
      ops.push({
        index: insertAt,
        priority: 1,
        order: ins.order,
        patchIds: [ins.patchId],
        requests: [
          { insertText: { location: { index: insertAt }, text: inserted } },
          {
            updateParagraphStyle: {
              range: { startIndex: styleStart, endIndex: styleStart + ins.text.length + 1 },
              paragraphStyle: { namedStyleType: ins.namedStyleType },
              fields: "namedStyleType"
            }
          }
        ]
      });
    }

    // Apply from the end of the document backwards so earlier indices stay valid. At the same index, edits
    // to the following paragraph go before insertions, and later insertions before earlier ones, which keeps
    // several new paragraphs after the same anchor in their original order.
    ops.sort((x, y) => y.index - x.index || x.priority - y.priority || y.order - x.order);

    // owners[i] lists the patches that produced requests[i], so a rejected request can be traced back.
    const requests = [];
    const owners = [];
    for (const op of ops) {
      for (const request of op.requests) {
        // Without a tabId the Docs API edits the first tab, so target the chosen tab explicitly.
        if (tab.tabId) {
          for (const body of Object.values(request)) {
            if (body.location) body.location.tabId = tab.tabId;
            if (body.range) body.range.tabId = tab.tabId;
          }
        }
        requests.push(request);
        owners.push(op.patchIds);
      }
    }
    return { requests, owners, results };
  }

  // ---- Docs API calls -----------------------------------------------------------------------------

  async function readError(res) {
    try {
      const body = await res.json();
      return (body.error && body.error.message) || `HTTP ${res.status}`;
    } catch {
      return `HTTP ${res.status}`;
    }
  }

  // "Invalid requests[53].deleteContentRange: Invalid deletion range. ..." -> 53
  const REJECTED_REQUEST_PATTERN = /requests\[(\d+)\]/;

  // ---- Document tabs -------------------------------------------------------------------------------

  /**
   * All tabs (including nested child tabs) in display order: [{ tabId, title, depth, body }].
   * Documents read without tab content, or from before tabs existed, have a single unnamed tab.
   */
  function flattenTabs(doc) {
    if (!Array.isArray(doc.tabs) || doc.tabs.length === 0) {
      return [{ tabId: null, title: doc.title || "", depth: 0, body: doc.body, inlineObjects: doc.inlineObjects, positionedObjects: doc.positionedObjects }];
    }
    const out = [];
    const visit = (tabs, depth) => {
      for (const tab of tabs || []) {
        const props = tab.tabProperties || {};
        const documentTab = tab.documentTab || {};
        out.push({
          tabId: props.tabId || null,
          title: props.title || "Untitled tab",
          depth,
          body: documentTab.body,
          inlineObjects: documentTab.inlineObjects,
          positionedObjects: documentTab.positionedObjects
        });
        visit(tab.childTabs, depth + 1);
      }
    };
    visit(doc.tabs, 0);
    return out;
  }

  /** The tab with `tabId`, or the first tab when no tab is given. */
  function findTab(doc, tabId) {
    const tabs = flattenTabs(doc);
    return tabId ? tabs.find((t) => t.tabId === tabId) || null : tabs[0] || null;
  }

  const ROLE_BY_NAMED_STYLE = {
    TITLE: "Title",
    HEADING_1: "Heading1",
    HEADING_2: "Heading2",
    HEADING_3: "Heading3",
    HEADING_4: "Heading3",
    HEADING_5: "Heading3",
    HEADING_6: "Heading3"
  };

  /** Wraps bold/italic text in Markdown emphasis the way the desktop app's Word importer does. */
  function emphasize(text, style) {
    const marker = style.bold && style.italic ? "***" : style.bold ? "**" : style.italic ? "*" : "";
    const core = text.trim();
    if (!marker || !core) return text;
    const leading = text.slice(0, text.length - text.trimStart().length);
    const trailing = text.slice(text.trimEnd().length);
    return `${leading}${marker}${core}${marker}${trailing}`;
  }

  /** A paragraph's text, with bold and italic as Markdown emphasis and smart chips as their visible text. */
  function paragraphText(paragraph) {
    let text = "";
    for (const part of paragraph.elements || []) {
      if (part.textRun) {
        const run = part.textRun.content.replace(/\n$/, "").replace(/[\x0B\t]/g, " ");
        text += emphasize(run, part.textRun.textStyle || {});
      } else {
        text += objectDisplayText(part);
      }
    }
    return text.trim();
  }

  /** The image of an inline or positioned object, as { imageUri, objectId, description }, or null. */
  function imageOf(objects, objectId, propertiesKey) {
    const object = objects && objects[objectId];
    const embedded = object && object[propertiesKey] && object[propertiesKey].embeddedObject;
    const uri = embedded && embedded.imageProperties && (embedded.imageProperties.contentUri || embedded.imageProperties.sourceUri);
    return uri ? { imageUri: uri, objectId, description: embedded.description || embedded.title || "" } : null;
  }

  /**
   * One tab's content for the desktop app, in document order, skipping the generated table of contents:
   * paragraphs as { role, text }; images as { role: "Figure", imageUri, objectId } (the app pairs them with
   * the "Figure 2." paragraph under them); tables as { role: "Table", rows: [[cell text]] }.
   */
  function tabToBlocks(tab) {
    const blocks = [];
    for (const element of (tab.body && tab.body.content) || []) {
      if (element.paragraph) {
        for (const part of element.paragraph.elements || []) {
          const id = part.inlineObjectElement && part.inlineObjectElement.inlineObjectId;
          const image = id && imageOf(tab.inlineObjects, id, "inlineObjectProperties");
          if (image) blocks.push({ role: "Figure", text: "", ...image });
        }
        for (const id of element.paragraph.positionedObjectIds || []) {
          const image = imageOf(tab.positionedObjects, id, "positionedObjectProperties");
          if (image) blocks.push({ role: "Figure", text: "", ...image });
        }
        const text = paragraphText(element.paragraph);
        if (text) {
          const style = element.paragraph.paragraphStyle && element.paragraph.paragraphStyle.namedStyleType;
          blocks.push({ role: ROLE_BY_NAMED_STYLE[style] || "BodyParagraph", text });
        }
      } else if (element.table) {
        const rows = (element.table.tableRows || []).map((row) =>
          (row.tableCells || []).map((cell) =>
            (cell.content || []).filter((c) => c.paragraph).map((c) => paragraphText(c.paragraph)).filter(Boolean).join(" ")));
        if (rows.some((r) => r.some(Boolean))) blocks.push({ role: "Table", text: "", rows });
      }
    }
    return blocks;
  }

  function bytesToBase64(bytes) {
    let binary = "";
    for (let i = 0; i < bytes.length; i += 0x8000) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    }
    return btoa(binary);
  }

  const MAX_IMAGE_BYTES = 40 * 1024 * 1024;

  /**
   * Downloads the images of `blocks` (Google's content links last about half an hour and need no sign-in;
   * the token is tried if one is refused), replacing imageUri with imageBase64 and mimeType, or imageError.
   */
  async function downloadImages(blocks, token, fetchImpl) {
    for (const block of blocks) {
      if (!block.imageUri) continue;
      const uri = block.imageUri;
      delete block.imageUri;
      try {
        let res = await fetchImpl(uri);
        if (!res.ok && token) res = await fetchImpl(uri, { headers: { Authorization: `Bearer ${token}` } });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const bytes = new Uint8Array(await res.arrayBuffer());
        if (bytes.length > MAX_IMAGE_BYTES) throw new Error("the image is larger than 40 MB");
        block.imageBase64 = bytesToBase64(bytes);
        block.mimeType = (res.headers && res.headers.get && res.headers.get("content-type")) || "";
      } catch (err) {
        block.imageError = String(err && err.message || err);
      }
    }
    return blocks;
  }

  async function readDocument(documentId, headers, fetchImpl, query = "") {
    const res = await fetchImpl(`${DOCS_API}/${encodeURIComponent(documentId)}?includeTabsContent=true${query}`, { headers });
    if (res.status === 401) throw new AuthError("Google sign-in expired.");
    if (!res.ok) {
      const detail = await readError(res);
      if (res.status === 403 || res.status === 404) {
        throw new Error(`Google did not allow editing this document with the signed-in account (${detail}).`);
      }
      throw new Error(`Could not read the document from Google Docs (${detail}).`);
    }
    return res.json();
  }

  /**
   * Applies the patches and returns one { patchId, ok, message } per patch.
   *
   * A batchUpdate is all-or-nothing. If Google rejects one request, nothing is applied: the patch that
   * produced it is set aside with Google's reason and the rest are planned again against a fresh copy of the
   * document, so one change Google will not accept never blocks the others.
   */
  async function applyPatches({ token, documentId, tabId = null, patches, fetchImpl = fetch }) {
    const headers = { Authorization: `Bearer ${token}` };
    const rejected = new Map(); // patchId -> reason
    let remaining = patches.slice();
    let results = [];

    for (let attempt = 0; attempt <= patches.length; attempt++) {
      const doc = await readDocument(documentId, headers, fetchImpl);
      const plan = planRequests(doc, remaining, tabId);
      results = plan.results;
      if (plan.requests.length === 0) {
        break;
      }

      const updateRes = await fetchImpl(`${DOCS_API}/${encodeURIComponent(documentId)}:batchUpdate`, {
        method: "POST",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify({ requests: plan.requests, writeControl: { requiredRevisionId: doc.revisionId } })
      });
      if (updateRes.status === 401) throw new AuthError("Google sign-in expired.");
      if (updateRes.ok) {
        break;
      }

      const detail = await readError(updateRes);
      if (updateRes.status === 400 && /revision/i.test(detail)) {
        throw new RevisionConflictError(detail);
      }
      const match = detail.match(REJECTED_REQUEST_PATTERN);
      const owners = match ? plan.owners[Number(match[1])] : null;
      if (!owners || owners.length === 0) {
        throw new Error(`Google Docs rejected the changes (${detail}). Nothing was changed.`);
      }
      const reason = detail.replace(/^Invalid requests\[\d+\]\.\w+:\s*/, "");
      for (const id of owners) {
        rejected.set(id, `Google Docs could not make this change automatically (${reason}). Apply it manually.`);
      }
      remaining = remaining.filter((p) => !rejected.has(p.patchId));
    }

    const byId = new Map(results.map((r) => [r.patchId, r]));
    return patches.map((p) => rejected.has(p.patchId)
      ? { patchId: p.patchId, ok: false, message: rejected.get(p.patchId) }
      : byId.get(p.patchId) || { patchId: p.patchId, ok: false, message: "Not applied." });
  }

  /** The document's tabs for choosing one: [{ tabId, title, depth }]. */
  async function listTabs({ token, documentId, fetchImpl = fetch }) {
    const doc = await readDocument(documentId, { Authorization: `Bearer ${token}` }, fetchImpl);
    return { documentTitle: doc.title || "", tabs: flattenTabs(doc).map(({ tabId, title, depth }) => ({ tabId, title, depth })) };
  }

  /**
   * One tab's content as paragraphs for the desktop app, read without pending suggestions so the snapshot
   * is the text as it currently stands.
   */
  async function readTabBlocks({ token, documentId, tabId = null, fetchImpl = fetch }) {
    const doc = await readDocument(documentId, { Authorization: `Bearer ${token}` }, fetchImpl, "&suggestionsViewMode=PREVIEW_WITHOUT_SUGGESTIONS");
    const tab = findTab(doc, tabId);
    if (!tab) {
      throw new Error("The selected document tab no longer exists. Choose a tab and try again.");
    }
    const blocks = await downloadImages(tabToBlocks(tab), token, fetchImpl);
    return { documentTitle: doc.title || "", tabId: tab.tabId, tabTitle: tab.title, blocks };
  }

  root.DocsApply = {
    applyPatches,
    listTabs,
    readTabBlocks,
    flattenTabs,
    tabToBlocks,
    downloadImages,
    planRequests,
    computeHunks,
    collectParagraphs,
    matchKey,
    AuthError,
    RevisionConflictError
  };
})(typeof self !== "undefined" ? self : globalThis);
