const BRIDGE_URL = "http://127.0.0.1:17420";
// Matches /document/d/<id>/... and multi-account URLs such as /document/u/1/d/<id>/...
const DOC_URL_PATTERN = /^https:\/\/docs\.google\.com\/document\/(?:u\/\d+\/)?d\/[a-zA-Z0-9_-]+/;

const statusBox = document.getElementById("statusBox");
const pullButton = document.getElementById("pullDocBtn");
const panelButton = document.getElementById("toggleDrawerBtn");
const messageBox = document.getElementById("message");
const applyButton = document.getElementById("applyBtn");
const setupSection = document.getElementById("setup");
const redirectUrlInput = document.getElementById("redirectUrl");
const clientIdInput = document.getElementById("clientId");
const tabSelect = document.getElementById("tabSelect");

function docIdFromUrl(url) {
  const match = (url || "").match(/\/document\/(?:u\/\d+\/)?d\/([a-zA-Z0-9_-]+)/);
  return match ? match[1] : null;
}

/** Lists the document's tabs (never opens a sign-in window) and selects the saved choice. */
async function loadTabs(tab) {
  const docId = docIdFromUrl(tab.url);
  const key = `tabChoice:${docId}`;
  const [{ [key]: chosen }, listing] = await Promise.all([
    chrome.storage.local.get(key),
    chrome.runtime.sendMessage({ type: "listTabs", documentId: docId }).catch(() => ({ ok: false }))
  ]);

  const viewed = new URL(tab.url).searchParams.get("tab");
  tabSelect.replaceChildren(new Option("Current tab", ""));
  if (listing && listing.ok) {
    const viewedTab = listing.tabs.find((t) => t.tabId === viewed) || (!viewed ? listing.tabs[0] : null);
    if (viewedTab) tabSelect.options[0].text = `Current tab (${viewedTab.title})`;
    for (const t of listing.tabs) {
      tabSelect.append(new Option(`${"\u2003".repeat(t.depth)}${t.title}`, t.tabId || ""));
    }
    tabSelect.title = "";
  } else {
    tabSelect.title = (listing && listing.message) || "Set up Google Docs access to choose a tab.";
  }
  tabSelect.value = chosen || "";
  if (tabSelect.value !== (chosen || "")) tabSelect.value = ""; // saved tab no longer exists
  tabSelect.disabled = false;

  tabSelect.onchange = async () => {
    if (tabSelect.value) await chrome.storage.local.set({ [key]: tabSelect.value });
    else await chrome.storage.local.remove(key);
    const name = tabSelect.options[tabSelect.selectedIndex].text.trim();
    showMessage(tabSelect.value ? `Sending and applying will use tab '${name}'.` : "Sending and applying will use the tab you are viewing.");
  };
}

function showMessage(text, isError = false) {
  messageBox.textContent = text;
  messageBox.classList.toggle("error", isError);
}

function renderStatus(title, cssClass, rows) {
  const heading = document.createElement("div");
  heading.className = `status-title ${cssClass}`;
  heading.textContent = title;
  statusBox.replaceChildren(heading);
  for (const row of rows) {
    const line = document.createElement("div");
    line.className = "status-row";
    line.textContent = row;
    statusBox.append(line);
  }
}

async function checkBridgeStatus() {
  try {
    const res = await fetch(`${BRIDGE_URL}/api/status`, { signal: AbortSignal.timeout(5000) });
    if (res.status === 503) {
      renderStatus("Revisions Editor is running", "connected", ["Open a workspace in the app to send documents."]);
      return false;
    }
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    renderStatus("Connected to Revisions Editor", "connected", [
      `Branch: ${data.branch}`,
      `Merged changes to apply: ${data.pendingPatchCount}`
    ]);
    return true;
  } catch {
    renderStatus("Revisions Editor is not running", "offline", ["Start Revisions Editor to send documents (port 17420)."]);
    return false;
  }
}

async function getActiveDocTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab && tab.id !== undefined && DOC_URL_PATTERN.test(tab.url || "") ? tab : null;
}

/**
 * Sends a message to the content script. Tabs that were open before the extension was installed or
 * reloaded have no content script, so inject it and try once more.
 */
async function sendToDocTab(tab, message) {
  try {
    return await chrome.tabs.sendMessage(tab.id, message);
  } catch {
    await chrome.scripting.insertCSS({ target: { tabId: tab.id }, files: ["styles.css"] });
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ["content.js"] });
    return await chrome.tabs.sendMessage(tab.id, message);
  }
}

async function runOnDocTab(message, busyText) {
  const tab = await getActiveDocTab();
  if (!tab) {
    showMessage("Switch to a Google Docs document, then open this panel again.", true);
    return null;
  }

  showMessage(busyText);
  try {
    const response = await sendToDocTab(tab, message);
    if (!response) {
      showMessage("The document did not respond. Reload the Google Docs tab and try again.", true);
      return null;
    }
    if (response.message) {
      showMessage(response.message, !response.ok);
    }
    return response;
  } catch (err) {
    showMessage(`Could not reach the document: ${err.message}. Reload the Google Docs tab and try again.`, true);
    return null;
  }
}

pullButton.addEventListener("click", async () => {
  pullButton.disabled = true;
  await runOnDocTab({ action: "PULL_GOOGLE_DOC_TO_WPF" }, "Exporting the document and sending it to Revisions Editor...");
  await checkBridgeStatus();
  pullButton.disabled = false;
});

panelButton.addEventListener("click", async () => {
  const response = await runOnDocTab({ action: "TOGGLE_SYNC_DRAWER" }, "");
  if (response && response.ok) {
    window.close();
  }
});

applyButton.addEventListener("click", async () => {
  const { googleClientId } = await chrome.storage.sync.get("googleClientId");
  if (!googleClientId) {
    setupSection.open = true;
    showMessage("Set up Google Docs access first (one time).", true);
    return;
  }
  applyButton.disabled = true;
  // Progress and per-change results are also shown in the in-page panel, which stays open if this popup
  // closes (for example when the Google sign-in window appears the first time).
  await runOnDocTab({ action: "APPLY_ALL_PATCHES" }, "Applying merged changes to the document...");
  await checkBridgeStatus();
  applyButton.disabled = false;
});

document.getElementById("copyRedirect").addEventListener("click", async () => {
  await navigator.clipboard.writeText(redirectUrlInput.value);
  showMessage("Redirect URI copied.");
});

document.getElementById("saveClientId").addEventListener("click", async () => {
  const value = clientIdInput.value.trim();
  if (value && !/\.apps\.googleusercontent\.com$/.test(value)) {
    showMessage("That does not look like an OAuth client ID (it ends in .apps.googleusercontent.com).", true);
    return;
  }
  await chrome.storage.sync.set({ googleClientId: value });
  await chrome.storage.session.remove("googleAccessToken");
  showMessage(value ? "Saved. Automatic apply is ready." : "Automatic apply turned off.");
});

(async () => {
  redirectUrlInput.value = chrome.identity.getRedirectURL();
  const { googleClientId } = await chrome.storage.sync.get("googleClientId");
  clientIdInput.value = googleClientId || "";
  setupSection.open = !googleClientId;

  const [tab, bridgeReady] = await Promise.all([getActiveDocTab(), checkBridgeStatus()]);
  pullButton.disabled = !tab;
  panelButton.disabled = !tab;
  applyButton.disabled = !tab || !bridgeReady;
  if (tab) {
    loadTabs(tab);
  }
  if (!tab) {
    showMessage("Switch to a Google Docs document to send it to Revisions Editor.");
  } else if (!bridgeReady) {
    pullButton.disabled = true;
  }
})();
