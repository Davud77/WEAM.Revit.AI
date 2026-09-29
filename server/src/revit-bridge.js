import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import path from 'node:path';

const HOST = '127.0.0.1';
const PORT = 37651;
const TIMEOUT_MS = 310_000;
const MAX_RESPONSE_BYTES = 8 * 1024 * 1024;

async function loadToken() {
  if (process.env.WEAM_REVIT_AI_TOKEN) return process.env.WEAM_REVIT_AI_TOKEN.trim();
  const localAppData = process.env.LOCALAPPDATA;
  if (!localAppData) throw new Error('LOCALAPPDATA is not set; cannot locate the Revit bridge token.');
  const tokenPath = path.join(localAppData, 'WEAM.Revit.AI', 'bridge.token');
  try {
    return (await readFile(tokenPath, 'utf8')).trim();
  } catch {
    throw new Error(`Revit bridge token was not found at ${tokenPath}. Start Revit with the add-in loaded first.`);
  }
}

// The factory allows isolated TCP tests; MCP callers cannot override the endpoint.
export function createRevitClient({ port = PORT, timeoutMs = TIMEOUT_MS, tokenLoader = loadToken,
  maxResponseBytes = MAX_RESPONSE_BYTES, maxRequestBytes = 1024 * 1024 } = {}) {
 return async function call(tool, args = {}, { signal } = {}) {
  signal?.throwIfAborted();
  const token = await tokenLoader();
  signal?.throwIfAborted();
  if (typeof token !== 'string' || token.length < 32) throw new Error('Invalid bridge token. Restart the Revit add-in.');
  const id = randomUUID();
  const payload = `${JSON.stringify({ id, token, tool, arguments: args })}\n`;
  if (Buffer.byteLength(payload, 'utf8') > maxRequestBytes) throw new Error('Revit request exceeds the 1 MB limit. Reduce the batch.');

  return await new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: HOST, port });
    let data = '';
    let settled = false;
    let bytes = 0;
    const abort = () => finish(new Error('MCP client cancelled the Revit request.'));
    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(deadline);
      signal?.removeEventListener('abort', abort);
      socket.destroy();
      error ? reject(error) : resolve(value);
    };

    const deadline = setTimeout(() => finish(new Error('Timed out waiting for Revit. Check the Revit confirmation dialog.')), timeoutMs);
    socket.setEncoding('utf8');
    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) { abort(); return; }
    socket.on('connect', () => socket.write(payload));
    socket.on('data', chunk => {
      bytes += Buffer.byteLength(chunk, 'utf8');
      if (bytes > maxResponseBytes) {
        finish(new Error('Revit response exceeded the 8 MB limit. Narrow the request and retry.'));
        return;
      }
      data += chunk;
      const newline = data.indexOf('\n');
      if (newline < 0) return;
      try {
        const response = JSON.parse(data.slice(0, newline));
        if (!response || response.id !== id) throw new Error('Response request ID does not match.');
        if (typeof response.ok !== 'boolean') throw new Error('Response has no boolean ok field.');
        if (!response.ok) finish(new Error(response.error ?? 'Revit returned an unspecified error.'));
        else finish(null, response.result);
      } catch (error) {
        finish(new Error(`Invalid response from Revit: ${error.message}`));
      }
    });
    socket.on('error', error => finish(new Error(`Cannot connect to Revit at ${HOST}:${port}. Open Revit and confirm the WEAM AI add-in loaded. ${error.message}`)));
    socket.on('end', () => {
      if (!settled) finish(new Error('Revit closed the bridge connection before returning a response.'));
    });
    socket.on('close', () => finish(new Error('Revit bridge connection closed.')));
  });
 };
}

export const callRevit = createRevitClient();
