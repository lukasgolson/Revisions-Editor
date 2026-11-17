// Offline tests of sending figures to Google Docs (node --test chrome-extension/tests).
// Google Docs and Drive are replaced by a recording fake; the requests sent are checked one by one.
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
require("../docs-apply.js");
const { applyPatches, imagesOf, AuthError } = globalThis.DocsApply;

/** A document: a paragraph, an image, its caption, and a last paragraph (Docs API shape and indices). */
function sampleDocument() {
  const run = (text, start) => ({ startIndex: start, endIndex: start + text.length, textRun: { content: text, textStyle: {} } });
  return {
    revisionId: "rev-1",
    title: "Paper",
    tabs: [{
      tabProperties: { tabId: "t.0", title: "Tab 1" },
      documentTab: {
        body: {
          content: [
            { startIndex: 1, endIndex: 32, paragraph: { elements: [run("The study site is shown below.\n", 1)], paragraphStyle: { namedStyleType: "NORMAL_TEXT" } } },
            {
              startIndex: 32, endIndex: 34, paragraph: {
                elements: [{ startIndex: 32, endIndex: 33, inlineObjectElement: { inlineObjectId: "kix.image1" } }, run("\n", 33)],
                paragraphStyle: { namedStyleType: "NORMAL_TEXT" }
              }
            },
            { startIndex: 34, endIndex: 54, paragraph: { elements: [run("Figure 1. Site map.\n", 34)], paragraphStyle: { namedStyleType: "NORMAL_TEXT" } } },
            { startIndex: 54, endIndex: 70, paragraph: { elements: [run("Results follow.\n", 54)], paragraphStyle: { namedStyleType: "NORMAL_TEXT" } } }
          ]
        },
        inlineObjects: {
          "kix.image1": {
            inlineObjectProperties: {
              embeddedObject: { imageProperties: { contentUri: "https://lh3.googleusercontent.com/x" }, size: { width: { magnitude: 300, unit: "PT" }, height: { magnitude: 200, unit: "PT" } } }
            }
          }
        }
      }
    }]
  };
}

/** A fake Google: answers like Docs and Drive, and records what was asked. */
function fakeGoogle({ uploadStatus = 200 } = {}) {
  const calls = [];
  let files = 0;
  const json = (status, body) => ({ status, ok: status < 300, json: async () => body, text: async () => JSON.stringify(body) });
  const fetchImpl = async (url, init = {}) => {
    const method = init.method || "GET";
    calls.push({ url, method, init });
    if (url.startsWith("https://docs.googleapis.com/v1/documents/doc1?")) return json(200, sampleDocument());
    if (url.includes(":batchUpdate")) return json(200, { replies: [] });
    if (url.startsWith("https://www.googleapis.com/upload/drive/v3/files")) {
      return uploadStatus === 200 ? json(200, { id: `file${++files}` }) : json(uploadStatus, { error: { message: "insufficient permissions" } });
    }
    if (url.endsWith("/permissions")) return json(200, { id: "anyoneWithLink" });
    if (method === "DELETE") return json(204, {});
    throw new Error(`Unexpected request ${method} ${url}`);
  };
  return { calls, fetchImpl };
}

const replacePatch = { patchId: "img-r", kind: "ReplaceImage", imageOrdinal: 0, imagePath: "figures/site.png", imageWidthPt: 400, afterText: "Figure 1. Site map." };
const insertPatch = { patchId: "img-i", kind: "InsertImage", anchorText: "Results follow.", imagePath: "figures/pipeline.svg", imageWidthPt: 420, afterText: "Figure 2. The pipeline." };
const png = { bytes: new Uint8Array([0x89, 0x50, 0x4e, 0x47, 1, 2, 3]), contentType: "image/png" };

test("images are found in document order, as the app numbers figures", () => {
  const doc = sampleDocument();
  const tab = globalThis.DocsApply.flattenTabs(doc)[0];
  assert.deepEqual(imagesOf(tab), [{ objectId: "kix.image1", index: 32, widthPt: 300, positioned: false }]);
});

test("a changed figure replaces its image at the same width, a new one is inserted with its caption, and Drive is cleaned up", async () => {
  const google = fakeGoogle();
  const results = await applyPatches({ token: "tok", documentId: "doc1", tabId: "t.0", patches: [replacePatch, insertPatch], fetchImpl: google.fetchImpl, imageSource: async () => png });
  assert.deepEqual(results.map((r) => [r.patchId, r.ok]), [["img-r", true], ["img-i", true]]);

  // Each image is uploaded to Drive and shared by link, for Google Docs to fetch.
  const uploads = google.calls.filter((c) => c.url.startsWith("https://www.googleapis.com/upload/"));
  assert.equal(uploads.length, 2);
  assert.match(uploads[0].init.headers["Content-Type"], /^multipart\/related; boundary=/);
  const sharing = google.calls.filter((c) => c.url.endsWith("/permissions"));
  assert.deepEqual(sharing.map((c) => JSON.parse(c.init.body)), [{ role: "reader", type: "anyone" }, { role: "reader", type: "anyone" }]);

  // One batch: from the end of the document backwards.
  const batch = JSON.parse(google.calls.find((c) => c.url.includes(":batchUpdate")).init.body);
  assert.equal(batch.writeControl.requiredRevisionId, "rev-1");
  assert.deepEqual(batch.requests, [
    // After the last paragraph: a paragraph for the image and one for the caption.
    { insertText: { location: { index: 69, tabId: "t.0" }, text: "\n\nFigure 2. The pipeline." } },
    { insertInlineImage: { location: { index: 70, tabId: "t.0" }, uri: "https://drive.google.com/uc?export=download&id=file2", objectSize: { width: { magnitude: 420, unit: "PT" } } } },
    // The new image where the old one is, at the old one's width; then the old one goes.
    { insertInlineImage: { location: { index: 32, tabId: "t.0" }, uri: "https://drive.google.com/uc?export=download&id=file1", objectSize: { width: { magnitude: 300, unit: "PT" } } } },
    { deleteContentRange: { range: { startIndex: 33, endIndex: 34, tabId: "t.0" } } }
  ]);

  // The Drive copies are deleted afterwards (the document keeps its own).
  const deletes = google.calls.filter((c) => c.method === "DELETE").map((c) => c.url.split("/").pop());
  assert.deepEqual(deletes.sort(), ["file1", "file2"]);
});

test("a figure that cannot be sent is reported, and the others still go", async () => {
  const google = fakeGoogle();
  const imageSource = async (patch) => {
    if (patch.patchId === "img-i") throw new Error("Revisions Editor did not send this figure's image.");
    return png;
  };
  const results = await applyPatches({ token: "tok", documentId: "doc1", tabId: "t.0", patches: [replacePatch, insertPatch], fetchImpl: google.fetchImpl, imageSource });
  assert.equal(results.find((r) => r.patchId === "img-r").ok, true);
  const failed = results.find((r) => r.patchId === "img-i");
  assert.equal(failed.ok, false);
  assert.match(failed.message, /did not send/);
});

test("without permission to use Drive, sign-in is asked for (and nothing is left in Drive)", async () => {
  const google = fakeGoogle({ uploadStatus: 403 });
  await assert.rejects(
    applyPatches({ token: "tok", documentId: "doc1", tabId: "t.0", patches: [replacePatch], fetchImpl: google.fetchImpl, imageSource: async () => png }),
    (err) => err instanceof AuthError && /Google Drive/.test(err.message));
  assert.equal(google.calls.filter((c) => c.url.includes(":batchUpdate")).length, 0);
});

test("an image that is no longer in the document is reported rather than guessed", async () => {
  const google = fakeGoogle();
  const results = await applyPatches({ token: "tok", documentId: "doc1", tabId: "t.0", patches: [{ ...replacePatch, imageOrdinal: 3 }], fetchImpl: google.fetchImpl, imageSource: async () => png });
  assert.equal(results[0].ok, false);
  assert.match(results[0].message, /not found/);
  // Its upload is still removed from Drive.
  assert.equal(google.calls.filter((c) => c.method === "DELETE").length, 1);
});
