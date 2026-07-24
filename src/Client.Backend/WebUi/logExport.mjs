function singleLine(value) {
  return String(value ?? "").replace(/\r?\n/g, " ");
}

function markdownTableCell(value) {
  return singleLine(value)
    .replace(/\\/g, "\\\\")
    .replace(/\|/g, "\\|");
}

function markdownText(value) {
  return String(value ?? "")
    .replace(/\\/g, "\\\\")
    .replace(/([`*_[\]<>#])/g, "\\$1");
}

function messageLabel(message) {
  return message.method || message.summary || "";
}

function payloadFence(payload) {
  const text = typeof payload === "string" ? payload : JSON.stringify(payload, null, 2);
  const longestRun = Math.max(0, ...Array.from(text.matchAll(/`+/g), match => match[0].length));
  const fence = "`".repeat(Math.max(3, longestRun + 1));
  const language = typeof payload === "string" ? "text" : "json";
  return `${fence}${language}\n${text}\n${fence}`;
}

export function selectedMessages(messages, selectedSeqs) {
  return messages.filter(message => selectedSeqs.has(message.seq));
}

export function formatMarkdownTable(messages) {
  const lines = [
    "| Seq | Time | Direction | Kind | Method | Detail |",
    "| ---: | --- | --- | --- | --- | --- |",
  ];

  for (const message of messages) {
    lines.push(
      `| ${message.seq} | ${markdownTableCell(message.time)} | ` +
      `${markdownTableCell(message.direction)} | ${markdownTableCell(message.kind)} | ` +
      `${markdownTableCell(messageLabel(message))} | ${markdownTableCell(message.detail)} |`);
  }

  return lines.join("\n");
}

export function formatDetailedMarkdown(messages) {
  const lines = [
    "# lspeek log export",
    "",
    `${messages.length} message${messages.length === 1 ? "" : "s"}`,
  ];

  for (const message of messages) {
    const label = messageLabel(message);
    lines.push(
      "",
      `## #${message.seq} ${markdownText(message.direction)} ${markdownText(message.kind)}` +
        (label ? `: ${markdownText(label)}` : ""),
      "",
      `- **Time:** ${markdownText(message.time)}`,
    );
    if (message.id !== undefined) {
      lines.push(`- **ID:** \`${String(JSON.stringify(message.id)).replace(/`/g, "\\`")}\``);
    }
    if (message.detail) {
      lines.push(`- **Detail:** ${markdownText(message.detail)}`);
    }
    if (message.payload !== undefined) {
      lines.push("", payloadFence(message.payload));
    }
  }

  return lines.join("\n");
}

export function formatPlainText(messages) {
  return messages.map(message => {
    const label = messageLabel(message);
    const detail = message.detail ? `: ${singleLine(message.detail)}` : "";
    return `[${singleLine(message.time)}] #${message.seq} ${singleLine(message.direction)} ` +
      `${singleLine(message.kind)}${label ? ` ${singleLine(label)}` : ""}${detail}`;
  }).join("\n");
}

export function formatJson(messages) {
  return JSON.stringify(messages, null, 2);
}

export function formatExport(messages, format) {
  switch (format) {
    case "markdown-table": return formatMarkdownTable(messages);
    case "markdown-detailed": return formatDetailedMarkdown(messages);
    case "text": return formatPlainText(messages);
    case "json": return formatJson(messages);
    default: throw new Error(`Unsupported export format: ${format}`);
  }
}

export function exportFileName(format, date = new Date()) {
  const extension = format === "text" ? "txt" : format === "json" ? "json" : "md";
  const timestamp = date.toISOString().replace(/[-:]/g, "").replace(/\.\d{3}Z$/, "Z");
  return `lspeek-${timestamp}.${extension}`;
}
