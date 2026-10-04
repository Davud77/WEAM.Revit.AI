import { spawn } from 'node:child_process';
const [nodePath, entry, tool] = process.argv.slice(2);
if (!nodePath || !entry) throw new Error('Usage: smoke.mjs node.exe server/src/index.js [read-only tool]');
const child = spawn(nodePath, [entry], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
let buffer = '', stderr = '', nextId = 1;
const pending = new Map();
child.stderr.on('data', data => stderr += data);
child.stdout.on('data', data => {
  buffer += data.toString();
  while (buffer.includes('\n')) {
    const end = buffer.indexOf('\n'), response = JSON.parse(buffer.slice(0, end)); buffer = buffer.slice(end + 1);
    pending.get(response.id)?.(response);
  }
});
async function rpc(method, params) {
  return await new Promise((resolve, reject) => {
    const id = nextId++, timeout = setTimeout(() => reject(new Error(`Timeout: ${method}: ${stderr}`)), 30000);
    pending.set(id, result => { clearTimeout(timeout); pending.delete(id); resolve(result); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}
try {
  const hello = await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'installer-smoke', version: '1' } });
  if (hello.error) throw new Error(JSON.stringify(hello.error));
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const listing = await rpc('tools/list', {});
  if (listing.error || !listing.result?.tools?.length) throw new Error('Missing MCP tools');
  const result = { server: hello.result.serverInfo, tools: listing.result.tools.length };
  if (tool) {
    if (!['revit_ping', 'get_project_info'].includes(tool)) throw new Error('Smoke probe permits read-only tools only');
    const call = await rpc('tools/call', { name: tool, arguments: {} });
    if (call.error || call.result?.isError) throw new Error(JSON.stringify(call.error ?? call.result));
    result[tool] = JSON.parse(call.result.content.find(c => c.type === 'text').text);
  }
  process.stdout.write(JSON.stringify(result, null, 2) + '\n');
} finally { child.kill(); }
