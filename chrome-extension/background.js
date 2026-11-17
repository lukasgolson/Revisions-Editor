// Service worker: performs every network request for the content script.
//
// Requests from the extension (rather than from the docs.google.com page) are covered by the extension's
// host permissions, so they are not blocked by CORS, by Chrome's local-network-access rules for public
// pages calling 127.0.0.1, or by the export redirect to googleusercontent.com.

const BRIDGE_URL = "http://127.0.0.1:17420";
const BRIDGE_TIMEOUT_MS = 120000; // A pull commits to Git and signs a block, which can take a while.

async function bridgeRequest(path, method = "GET", body = undefined) {
  let res;
  try {
    res = await fetch(`${BRIDGE_URL}${path}`, {
      method,
      headers: body !== undefined ? { "Content-Type": "application/json" } : undefined,
      body: body !== undefined ? JSON.stringify(body) : undefined,
      signal: AbortSignal.timeout(BRIDGE_TIMEOUT_MS)
    });
  } catch (err) {
    const timedOut = err && err.name === "TimeoutError";
    return {
      ok: false,
      offline: !timedOut,
      message: timedOut
        ? "Revisions Editor did not respond in time."
        : "Revisions Editor is not running, or its Google Docs bridge is offline (port 17420)."
    };
  }

  let data = null;
  try {
    data = await res.json();
  } catch {
    // Non-JSON body; fall through with data = null.
  }

  if (!res.ok) {
    const message = res.status === 503
      ? "Open a workspace in Revisions Editor first."
      : (data && data.error) || `Revisions Editor returned HTTP ${res.status}.`;
    return { ok: false, status: res.status, message };
  }
  return { ok: true, data };
}

function bytesToBase64(bytes) {
  let binary = "";
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
  }
  return btoa(binary);
}

// A .docx file is a ZIP archive, which starts with "PK".
function isDocx(bytes) {
  return bytes.length > 4 && bytes[0] === 0x50 && bytes[1] === 0x4b;
}

async function exportDocx(exportUrl) {
  let res;
  try {
    res = await fetch(exportUrl, { credentials: "include", redirect: "follow" });
  } catch (err) {
    return { ok: false, message: `Could not download the document from Google Docs (${err.message}).` };
  }
  if (!res.ok) {
    return { ok: false, message: `Google Docs refused the Word export (HTTP ${res.status}).` };
  }

  const bytes = new Uint8Array(await res.arrayBuffer());
  if (!isDocx(bytes)) {
    return {
      ok: false,
      message: "Google Docs did not return a Word document. Check that you are signed in and allowed to download this document."
    };
  }
  return { ok: true, docxBase64: bytesToBase64(bytes) };
}

// ---- Automatic apply through the Google Docs API ---------------------------------------------------

importScripts("docs-apply.js");

// Docs to read and edit the document; drive.file to put figure images in Drive for a moment while they are inserted
// (only files this extension creates: nothing else in Drive is visible to it).
const DOCS_SCOPE = "https://www.googleapis.com/auth/documents https://www.googleapis.com/auth/drive.file";
const TOKEN_KEY = "googleAccessToken";

async function getClientId() {
  const { googleClientId } = await chrome.storage.sync.get("googleClientId");
  return (googleClientId || "").trim();
}

/**
 * OAuth access token for the Docs API. Uses launchWebAuthFlow (works in Chrome, Edge and other Chromium
 * browsers) with a "Web application" OAuth client whose redirect URI is chrome.identity.getRedirectURL().
 * Tokens are kept in session storage only, so they are discarded when the browser closes.
 */
class SignInNeededError extends Error {}

/** `interactive: false` never opens the Google sign-in window (used when just listing tabs). */
async function getAccessToken({ forceNew = false, interactive = true } = {}) {
  if (!forceNew) {
    const { [TOKEN_KEY]: cached } = await chrome.storage.session.get(TOKEN_KEY);
    if (cached && cached.expiresAt > Date.now() + 60000) {
      return cached.token;
    }
  }

  const clientId = await getClientId();
  const params = new URLSearchParams({
    client_id: clientId,
    response_type: "token",
    redirect_uri: chrome.identity.getRedirectURL(),
    scope: DOCS_SCOPE,
    include_granted_scopes: "true"
  });
  const authUrl = `https://accounts.google.com/o/oauth2/v2/auth?${params}`;

  // Silent first: works once you have approved access and are signed in to Google in this browser.
  // Google reports "needs your input" (interaction_required, consent_required, login_required, ...) as an
  // error inside the redirect rather than as a failure, so any result without a token means: ask the user.
  let result;
  try {
    result = parseAuthRedirect(await chrome.identity.launchWebAuthFlow({ url: `${authUrl}&prompt=none`, interactive: false }));
  } catch {
    result = { error: "silent_sign_in_unavailable" };
  }

  if (!result.token && !interactive) {
    throw new SignInNeededError("Sign in to Google Docs (send or apply once) to list this document's tabs.");
  }
  if (!result.token) {
    try {
      result = parseAuthRedirect(await chrome.identity.launchWebAuthFlow({ url: authUrl, interactive: true }));
    } catch (err) {
      // Thrown when the sign-in window is closed or cannot be shown.
      throw new Error(`Google sign-in did not complete (${err && err.message || err}).`);
    }
  }

  if (!result.token) {
    throw new Error(describeAuthError(result.error));
  }
  await chrome.storage.session.set({ [TOKEN_KEY]: { token: result.token, expiresAt: Date.now() + result.expiresIn * 1000 } });
  return result.token;
}

/** Reads the token or error from the OAuth redirect (fragment for the token flow; query string as a fallback). */
function parseAuthRedirect(redirectUrl) {
  const url = new URL(redirectUrl);
  const params = new URLSearchParams(url.hash.slice(1));
  for (const [key, value] of url.searchParams) {
    if (!params.has(key)) params.set(key, value);
  }
  return {
    token: params.get("access_token"),
    expiresIn: Number(params.get("expires_in") || 3600),
    error: params.get("error")
  };
}

function describeAuthError(error) {
  switch (error) {
    case "access_denied":
      return "Google access was not granted. Click Apply again and allow access to Google Docs. If you are not listed as a test user on the OAuth consent screen, add your account there first.";
    case "redirect_uri_mismatch":
      return "Google rejected the redirect URI. Copy the one shown in the extension popup into the OAuth client's authorized redirect URIs.";
    case "invalid_client":
    case "unauthorized_client":
      return "Google did not accept the client ID. Check that it is a 'Web application' OAuth client and was copied correctly.";
    case null:
    case undefined:
    case "":
      return "Google sign-in was cancelled.";
    default:
      return `Google sign-in failed (${error}).`;
  }
}

/**
 * Runs a Docs API call with a valid token. Retries once with a fresh token if it expired, or with a fresh
 * copy of the document if it changed meanwhile. Returns { ok: true, value } or { ok: false, message }.
 */
async function withDocsApi(call, { interactive = true } = {}) {
  if (!(await getClientId())) {
    return {
      ok: false,
      needsSetup: true,
      message: "Google Docs access is not set up yet. Open the extension popup and follow 'Set up Google Docs access'."
    };
  }

  for (let attempt = 1; ; attempt++) {
    try {
      const token = await getAccessToken({ forceNew: attempt > 1, interactive });
      return { ok: true, value: await call(token) };
    } catch (err) {
      if (err instanceof SignInNeededError) {
        return { ok: false, needsSignIn: true, message: err.message };
      }
      const retryable = err instanceof DocsApply.AuthError || err instanceof DocsApply.RevisionConflictError;
      if (retryable && attempt < 2) {
        if (err instanceof DocsApply.AuthError) await chrome.storage.session.remove(TOKEN_KEY);
        continue;
      }
      return { ok: false, message: err instanceof DocsApply.RevisionConflictError
        ? "The document changed while the edits were being applied. Try again."
        : err.message };
    }
  }
}

async function applyPatchesToDocument(documentId, tabId, patches) {
  // Figure images come from the desktop app.
  const imageSource = async (patch) => {
    const res = await fetch(`${BRIDGE_URL}/api/google-docs/figure?path=${encodeURIComponent(patch.imagePath)}`, { signal: AbortSignal.timeout(BRIDGE_TIMEOUT_MS) });
    if (!res.ok) throw new Error("Revisions Editor did not send this figure's image.");
    return { bytes: new Uint8Array(await res.arrayBuffer()), contentType: res.headers.get("Content-Type") || "image/png" };
  };
  const outcome = await withDocsApi((token) => DocsApply.applyPatches({ token, documentId, tabId, patches, imageSource }));
  if (!outcome.ok) {
    return outcome;
  }

  const results = outcome.value;
  const appliedIds = results.filter((r) => r.ok).map((r) => r.patchId);
  if (appliedIds.length > 0) {
    await bridgeRequest("/api/google-docs/ack-patch", "POST", { patchIds: appliedIds });
  }
  return { ok: true, results, appliedCount: appliedIds.length };
}

async function listDocumentTabs(documentId) {
  const outcome = await withDocsApi((token) => DocsApply.listTabs({ token, documentId }), { interactive: false });
  return outcome.ok ? { ok: true, ...outcome.value } : outcome;
}

async function readDocumentTab(documentId, tabId) {
  const outcome = await withDocsApi((token) => DocsApply.readTabBlocks({ token, documentId, tabId }));
  return outcome.ok ? { ok: true, ...outcome.value } : outcome;
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  let work;
  if (msg && msg.type === "bridge") {
    work = bridgeRequest(msg.path, msg.method, msg.body);
  } else if (msg && msg.type === "exportDocx") {
    work = exportDocx(msg.url);
  } else if (msg && msg.type === "applyPatches") {
    work = applyPatchesToDocument(msg.documentId, msg.tabId || null, msg.patches);
  } else if (msg && msg.type === "listTabs") {
    work = listDocumentTabs(msg.documentId);
  } else if (msg && msg.type === "readTab") {
    work = readDocumentTab(msg.documentId, msg.tabId || null);
  } else if (msg && msg.type === "authSetup") {
    work = getClientId().then((clientId) => ({ ok: true, clientId, redirectUrl: chrome.identity.getRedirectURL() }));
  } else {
    return false;
  }

  // Always answer, even on unexpected errors, so the caller never sees a closed message port.
  work.then(sendResponse, (err) => sendResponse({ ok: false, message: String(err && err.message || err) }));
  return true;
});
