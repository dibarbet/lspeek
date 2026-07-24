import assert from "node:assert/strict";
import test from "node:test";
import {
  exportFileName,
  formatDetailedMarkdown,
  formatJson,
  formatMarkdownTable,
  formatPlainText,
  selectedMessages,
} from "../../src/Client.Backend/WebUi/logExport.mjs";

const messages = [
  {
    seq: 1,
    time: "2026-07-24T20:35:10.0000000+00:00",
    direction: "send",
    kind: "request",
    method: "textDocument/hover",
    detail: "At A|B.cs:1",
    payload: { jsonrpc: "2.0", id: 1 },
  },
  {
    seq: 2,
    time: "2026-07-24T20:35:11.0000000+00:00",
    direction: "recv",
    kind: "stderr",
    summary: "line one\nline two",
    payload: "contains ``` fence",
  },
];

test("selectedMessages preserves chronological order", () => {
  assert.deepEqual(selectedMessages(messages, new Set([2, 1])), messages);
});

test("Markdown table escapes table delimiters and newlines", () => {
  const output = formatMarkdownTable(messages);
  assert.match(output, /At A\\\|B\.cs:1/);
  assert.match(output, /line one line two/);
  assert.equal(output.split("\n").length, 4);
});

test("detailed Markdown includes complete payloads with safe fences", () => {
  const output = formatDetailedMarkdown(messages);
  assert.match(output, /"jsonrpc": "2\.0"/);
  assert.match(output, /````text\ncontains ``` fence\n````/);
});

test("plain text emits one line per message", () => {
  assert.equal(formatPlainText(messages).split("\n").length, 2);
});

test("JSON export preserves complete records", () => {
  assert.deepEqual(JSON.parse(formatJson(messages)), messages);
});

test("export filename uses a UTC timestamp and matching extension", () => {
  const date = new Date("2026-07-24T20:35:10.123Z");
  assert.equal(exportFileName("markdown-table", date), "lspeek-20260724T203510Z.md");
  assert.equal(exportFileName("text", date), "lspeek-20260724T203510Z.txt");
});
