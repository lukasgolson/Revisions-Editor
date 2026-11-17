// Content script for Google Docs. Wrapped in a function so the popup can inject it again into tabs that
// were open before the extension was installed or reloaded without redeclaring top-level names.
(() => {
  // Matches /document/d/<id>/... and multi-account URLs such as /document/u/1/d/<id>/...
  const DOC_PATH_PATTERN = /^\/document\/(?:u\/(\d+)\/)?d\/([a-zA-Z0-9_-]+)/;

  // ---- Talking to the extension ---------------------------------------------------------------------

  // Network requests go through the service worker (see background.js).
  function sendToBackground(message) {
    return new Promise((resolve) => {
      try {
        chrome.runtime.sendMessage(message, (response) => {
          if (chrome.runtime.lastError || !response) {
            resolve({ ok: false, message: "The extension was updated. Reload this Google Docs tab and try again." });
          } else {
            resolve(response);
          }
        });
      } catch {
        // "Extension context invalidated": this script belongs to a previous version of the extension.
        resolve({ ok: false, message: "The extension was updated. Reload this Google Docs tab and try again." });
      }
    });
  }

  function getDocInfo() {
    const match = window.location.pathname.match(DOC_PATH_PATTERN);
    if (!match) {
      return null;
    }
    const accountPrefix = match[1] !== undefined ? `u/${match[1]}/` : "";
    const docId = match[2];
    return {
      docId,
      // The tab being viewed: Docs keeps it in the URL (?tab=t.0); absent means the first tab.
      viewedTabId: new URLSearchParams(window.location.search).get("tab")
        || new URLSearchParams(window.location.hash.slice(1)).get("tab"),
      exportUrl: `https://docs.google.com/document/${accountPrefix}d/${docId}/export?format=docx`
    };
  }

  // ---- Document tab to work with --------------------------------------------------------------------

  const tabChoiceKey = (docId) => `tabChoice:${docId}`;

  /**
   * The document tab to send and apply to: the tab chosen in the popup or panel, otherwise the tab being
   * viewed. Returns { tabId, chosen } where tabId null means the first tab.
   */
  async function getTargetTab(info) {
    try {
      const stored = await chrome.storage.local.get(tabChoiceKey(info.docId));
      const chosen = stored[tabChoiceKey(info.docId)];
      if (chosen) return { tabId: chosen, chosen: true };
    } catch {
      // Storage unavailable (extension updated): fall back to the viewed tab.
    }
    return { tabId: info.viewedTabId || null, chosen: false };
  }

  async function setTabChoice(docId, tabId) {
    if (tabId) await chrome.storage.local.set({ [tabChoiceKey(docId)]: tabId });
    else await chrome.storage.local.remove(tabChoiceKey(docId));
  }

  async function hasDocsAccess() {
    const setup = await sendToBackground({ type: "authSetup" });
    return !!(setup.ok && setup.clientId);
  }

  function getDocTitle() {
    const titleInput = document.querySelector(".docs-title-input");
    if (titleInput && titleInput.value) {
      return titleInput.value.trim();
    }
    return document.title.replace(/\s*-\s*Google Docs\s*$/, "").trim();
  }

  function bytesToBase64(bytes) {
    let binary = "";
    const chunk = 0x8000;
    for (let i = 0; i < bytes.length; i += chunk) {
      binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
    }
    return btoa(binary);
  }

  async function exportDocx(exportUrl) {
    // Same-origin first: it always carries the signed-in session. If Docs redirects the download to
    // googleusercontent.com the page's CORS rules block it, so fall back to the service worker.
    try {
      const res = await fetch(exportUrl, { credentials: "include" });
      if (res.ok) {
        const bytes = new Uint8Array(await res.arrayBuffer());
        if (bytes.length > 4 && bytes[0] === 0x50 && bytes[1] === 0x4b) {
          return { ok: true, docxBase64: bytesToBase64(bytes) };
        }
      }
    } catch {
      // Fall through to the service worker.
    }
    return sendToBackground({ type: "exportDocx", url: exportUrl });
  }

  const REPLACE_CONFIRMATION = "Replace the manuscript in Revisions Editor with this document?\n\nIts text, headings, captions and tables become exactly what this document has; edits made only in Revisions Editor are not merged in. Figures and equations are kept as they are in Revisions Editor (an image here stands for the figure drawn there). The current version is committed first, so it stays in the history.";

  /**
   * Sends the target tab to Revisions Editor: merged three ways with the edits made there ("merge"), or replacing
   * the manuscript with this document while keeping the app's figures ("replace").
   */
  async function pullDocToEditor(mode = "merge") {
    const info = getDocInfo();
    if (!info) {
      return { ok: false, message: "This page is not a Google Docs document." };
    }

    const target = await getTargetTab(info);
    const content = await readTargetTabContent(info, target);
    if (!content.ok) {
      return content;
    }

    const title = content.documentTitle || getDocTitle();
    const result = await sendToBackground({
      type: "bridge",
      path: "/api/google-docs/pull",
      method: "POST",
      body: {
        googleDocId: info.docId,
        title,
        tabId: content.tabId,
        tabTitle: content.tabTitle,
        blocks: content.blocks,
        docxBase64: content.docxBase64,
        mode
      }
    });

    if (!result.ok) {
      return result;
    }

    const sha = (result.data && result.data.commitSha || "").substring(0, 7);
    const what = content.tabTitle ? `tab '${content.tabTitle}' of '${title}'` : `'${title}'`;
    return {
      ok: true,
      message: (mode === "replace"
        ? `Replaced the manuscript with ${what} (commit ${sha}): ${result.data.summary || "figures kept."}`
        : `Sent ${what} to Revisions Editor (commit ${sha}, signed block #${result.data.blockIndex}).`)
        + (content.note ? " " + content.note : "")
    };
  }

  /**
   * The content of the target tab. With Google Docs access set up it is read through the Docs API, which
   * returns exactly that tab, in the same form automatic apply later compares against. Otherwise the Word
   * export is used with Google's (undocumented) tab parameter, which may still include every tab.
   */
  async function readTargetTabContent(info, target) {
    if (await hasDocsAccess()) {
      const tab = await sendToBackground({ type: "readTab", documentId: info.docId, tabId: target.tabId });
      if (!tab.ok) return tab;
      return { ok: true, documentTitle: tab.documentTitle, tabId: tab.tabId, tabTitle: tab.tabTitle, blocks: tab.blocks };
    }

    const url = target.tabId ? `${info.exportUrl}&tab=${encodeURIComponent(target.tabId)}` : info.exportUrl;
    const exported = await exportDocx(url);
    if (!exported.ok) return exported;
    return {
      ok: true,
      tabId: target.tabId,
      docxBase64: exported.docxBase64,
      note: "The Word export may include every document tab. Set up Google Docs access in the extension popup to send only the selected tab."
    };
  }

  // ---- In-page panel for applying merged review edits ---------------------------------------------

  const PANEL_ID = "revisions-editor-sync-panel";
  const lastFailures = new Map(); // patchId -> reason from the last automatic apply
  let pendingPatches = [];
  let snapshotTab = { tabId: null, tabTitle: null }; // the tab the pending changes were prepared from
  let applying = false;

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text; // Document text is never interpreted as HTML.
    return node;
  }

  function option(text, value) {
    const node = el("option", undefined, text);
    node.value = value;
    return node;
  }

  // The panel lives in the page, so it outlives the script that built it. After the extension is updated
  // the tab can still hold a panel from the previous version (with different buttons); replace it.
  const PANEL_VERSION = chrome.runtime.getManifest().version;

  function getPanel() {
    const panel = document.getElementById(PANEL_ID);
    if (panel && panel.dataset.version !== PANEL_VERSION) {
      panel.remove();
      return null;
    }
    return panel;
  }

  function setPanelStatus(text) {
    const status = getPanel() && getPanel().querySelector(".re-status");
    if (status) status.textContent = text;
  }

  function buildPanel() {
    const panel = el("div");
    panel.id = PANEL_ID;
    panel.dataset.version = PANEL_VERSION;
    panel.setAttribute("role", "dialog");
    panel.setAttribute("aria-label", "Revisions Editor");

    const header = el("div", "re-header");
    header.append(el("span", "re-title", "Revisions Editor"));
    const close = el("button", "re-close", "\u00D7");
    close.title = "Close";
    close.setAttribute("aria-label", "Close");
    close.addEventListener("click", () => { panel.hidden = true; });
    header.append(close);

    const toolbar = el("div", "re-toolbar");
    const pull = el("button", "re-button re-primary", "Send to Revisions Editor");
    pull.addEventListener("click", async () => {
      pull.disabled = true;
      setPanelStatus("Sending document...");
      const r = await pullDocToEditor();
      setPanelStatus(r.message);
      pull.disabled = false;
      await refreshPatches();
    });
    const replace = el("button", "re-button", "Replace manuscript");
    replace.title = "Make the manuscript in Revisions Editor exactly this document, keeping its figures";
    replace.addEventListener("click", async () => {
      if (!window.confirm(REPLACE_CONFIRMATION)) return;
      replace.disabled = true;
      setPanelStatus("Replacing the manuscript with this document...");
      const r = await pullDocToEditor("replace");
      setPanelStatus(r.message);
      replace.disabled = false;
      await refreshPatches();
    });
    const applyAll = el("button", "re-button re-apply-all", "Apply all");
    applyAll.title = "Apply every merged change to this document";
    applyAll.disabled = true;
    applyAll.addEventListener("click", () => applyToDocument(pendingPatches));
    const refresh = el("button", "re-button", "Refresh");
    refresh.addEventListener("click", refreshPatches);
    toolbar.append(pull, replace, applyAll, refresh);

    // Which document tab to send and apply to.
    const tabRow = el("label", "re-tab-row");
    tabRow.append(el("span", "re-label-inline", "Document tab"));
    const tabSelect = el("select", "re-tab-select");
    tabSelect.append(option("Current tab", ""));
    tabSelect.addEventListener("change", async () => {
      const info = getDocInfo();
      if (info) await setTabChoice(info.docId, tabSelect.value || null);
      await refreshPatches();
    });
    tabRow.append(tabSelect);

    panel.append(header, toolbar, tabRow, el("div", "re-status", "Ready"), el("div", "re-patches"));
    document.body.appendChild(panel);
    loadTabChoices(panel);
    return panel;
  }

  async function togglePanel() {
    let panel = getPanel();
    if (panel) {
      panel.hidden = !panel.hidden;
      if (!panel.hidden) loadTabChoices(panel);
    } else {
      panel = buildPanel();
    }
    if (!panel.hidden) {
      await refreshPatches();
    }
    return { ok: true };
  }

  /** Fills the panel's tab list (without opening a Google sign-in window) and selects the saved choice. */
  async function loadTabChoices(panel) {
    const select = panel.querySelector(".re-tab-select");
    const info = getDocInfo();
    if (!select || !info) return;

    const [listing, target] = await Promise.all([
      sendToBackground({ type: "listTabs", documentId: info.docId }),
      getTargetTab(info)
    ]);
    select.replaceChildren(option("Current tab", ""));
    if (listing.ok && Array.isArray(listing.tabs)) {
      for (const tab of listing.tabs) {
        select.append(option(`${"\u2003".repeat(tab.depth)}${tab.title}`, tab.tabId || ""));
      }
      select.title = "";
    } else {
      select.title = listing.message || "";
    }
    select.value = target.chosen ? target.tabId : "";
    if (select.value !== (target.chosen ? target.tabId : "")) {
      select.value = ""; // the saved tab no longer exists
    }
  }

  async function refreshPatches() {
    const panel = getPanel();
    const container = panel && panel.querySelector(".re-patches");
    if (!container) return;

    const result = await sendToBackground({ type: "bridge", path: "/api/google-docs/pending-patches" });
    if (!container.isConnected) return; // the panel was replaced or removed while waiting
    container.replaceChildren();
    const applyAll = panel.querySelector(".re-apply-all");
    if (!result.ok) {
      pendingPatches = [];
      if (applyAll) applyAll.disabled = true;
      setPanelStatus(result.message);
      return;
    }

    const patches = (result.data && result.data.patches) || [];
    pendingPatches = patches.filter((p) => !p.appliedInGoogleDocs);
    snapshotTab = { tabId: (result.data && result.data.tabId) || null, tabTitle: (result.data && result.data.tabTitle) || null };
    if (applyAll) applyAll.disabled = applying || pendingPatches.length === 0;
    if (!applying) {
      const fromTab = snapshotTab.tabTitle ? ` (prepared from tab '${snapshotTab.tabTitle}')` : "";
      setPanelStatus(pendingPatches.length === 0
        ? "No merged changes are waiting to be applied."
        : `${pendingPatches.length} merged change${pendingPatches.length === 1 ? "" : "s"} to apply${fromTab}.`);
    }

    for (const patch of pendingPatches) {
      const kind = String(patch.kind || "Modify");
      const item = el("div", "re-patch");
      item.append(
        el("div", "re-patch-title", patch.sectionHeading || patch.paragraphId),
        el("div", "re-patch-summary", patch.changeSummary));
      const isImage = kind === "ReplaceImage" || kind === "InsertImage";
      if (isImage) {
        item.append(el("div", "re-label", kind === "ReplaceImage" ? "Figure image to update" : "New figure"), el("div", "re-text re-after", patch.afterText));
      } else {
        if (kind !== "Insert") {
          item.append(el("div", "re-label", kind === "Delete" ? "Paragraph to remove" : "Current text"), el("div", "re-text re-before", patch.beforeText));
        }
        if (kind !== "Delete") {
          item.append(el("div", "re-label", kind === "Insert" ? "New paragraph" : "Merged text"), el("div", "re-text re-after", patch.afterText));
        }
      }
      if (lastFailures.has(patch.patchId)) {
        item.append(el("div", "re-failure", lastFailures.get(patch.patchId)));
      }

      const actions = el("div", "re-actions");
      const apply = el("button", "re-button re-primary", "Apply");
      apply.disabled = applying;
      apply.addEventListener("click", () => applyToDocument([patch]));
      const copyAfter = el("button", "re-button", "Copy merged");
      copyAfter.disabled = kind === "Delete" || isImage;
      copyAfter.addEventListener("click", async () => {
        await navigator.clipboard.writeText(patch.afterText);
        setPanelStatus("Copied the merged text. Select the paragraph and paste (Ctrl+V).");
      });
      const done = el("button", "re-button", "Mark applied");
      done.addEventListener("click", async () => {
        const r = await sendToBackground({
          type: "bridge",
          path: "/api/google-docs/ack-patch",
          method: "POST",
          body: { patchId: patch.patchId }
        });
        if (!r.ok) {
          setPanelStatus(r.message);
          return;
        }
        await refreshPatches();
      });
      actions.append(apply, copyAfter, done);
      item.append(actions);
      container.append(item);
    }
  }

  /**
   * The tab to apply to. Changes are prepared against the snapshot of one tab, so applying them anywhere
   * else would rewrite the wrong text: refuse when the target differs from the snapshot's tab.
   */
  function resolveApplyTab(target) {
    if (!snapshotTab.tabId) {
      return { tabId: target.tabId }; // older snapshot without tab information
    }
    // With no tab in the URL and none chosen, Docs is showing the first tab; trust the snapshot's tab id.
    if (!target.tabId || target.tabId === snapshotTab.tabId) {
      return { tabId: snapshotTab.tabId };
    }
    const name = snapshotTab.tabTitle ? `'${snapshotTab.tabTitle}'` : "another tab";
    return {
      error: `These changes were prepared from tab ${name}. Switch to that tab or choose it under Document tab, or send this tab to Revisions Editor first.`
    };
  }

  /**
   * Applies merged changes to this document through the Google Docs API (see docs-apply.js), then sends a
   * fresh export to Revisions Editor so its Google Docs snapshot matches the document again.
   */
  async function applyToDocument(patches) {
    const info = getDocInfo();
    if (!info) {
      return { ok: false, message: "This page is not a Google Docs document." };
    }
    if (applying) {
      return { ok: false, message: "Changes are already being applied." };
    }
    if (!patches || patches.length === 0) {
      setPanelStatus("No merged changes are waiting to be applied.");
      return { ok: true, message: "No merged changes are waiting to be applied." };
    }

    applying = true;
    getPanel()?.querySelectorAll("button.re-primary, .re-apply-all").forEach((b) => { b.disabled = true; });
    setPanelStatus(`Applying ${patches.length} change${patches.length === 1 ? "" : "s"} to this document...`);

    let outcome;
    try {
      // The changes were prepared from one tab; apply them to that tab only.
      const target = await getTargetTab(info);
      const resolved = resolveApplyTab(target);
      const tabId = resolved.tabId;
      const result = resolved.error
        ? { ok: false, message: resolved.error }
        : await sendToBackground({ type: "applyPatches", documentId: info.docId, tabId, patches });
      if (!result.ok) {
        outcome = result;
      } else {
        for (const r of result.results) {
          if (r.ok) lastFailures.delete(r.patchId);
          else lastFailures.set(r.patchId, r.message);
        }

        if (result.appliedCount > 0) {
          // Keep the app's snapshot of this tab current, so later merges produce correct changes.
          const tab = await sendToBackground({ type: "readTab", documentId: info.docId, tabId });
          if (tab.ok) {
            await sendToBackground({
              type: "bridge",
              path: "/api/google-docs/baseline",
              method: "POST",
              body: { googleDocId: info.docId, title: tab.documentTitle || getDocTitle(), tabId: tab.tabId, tabTitle: tab.tabTitle, blocks: tab.blocks }
            });
          }
        }

        const failed = result.results.length - result.appliedCount;
        outcome = {
          ok: failed === 0,
          message: failed === 0
            ? `Applied ${result.appliedCount} change${result.appliedCount === 1 ? "" : "s"}. Use File > Version history to review or undo them.`
            : `Applied ${result.appliedCount} of ${result.results.length} changes. The others are listed with the reason.`
        };
      }
    } finally {
      applying = false;
    }

    await refreshPatches();
    setPanelStatus(outcome.message);
    return outcome;
  }

  // ---- Messages from the popup ----------------------------------------------------------------------

  async function handleMessage(msg) {
    switch (msg && msg.action) {
      case "PING":
        return { ok: true, isDocument: getDocInfo() !== null };
      case "PULL_GOOGLE_DOC_TO_WPF":
        return pullDocToEditor();
      case "REPLACE_WITH_GOOGLE_DOC":
        // The popup has asked for confirmation.
        return pullDocToEditor("replace");
      case "TOGGLE_SYNC_DRAWER":
        return togglePanel();
      case "APPLY_ALL_PATCHES": {
        // Show the panel so progress and per-change results stay visible after the popup closes.
        const panel = getPanel() || buildPanel();
        panel.hidden = false;
        await refreshPatches();
        return applyToDocument(pendingPatches);
      }
      default:
        return { ok: false, message: "Unknown request." };
    }
  }

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    // Always respond, even on unexpected errors, so the popup can show what went wrong.
    handleMessage(msg).then(sendResponse, (err) => sendResponse({ ok: false, message: String(err && err.message || err) }));
    return true;
  });
})();
