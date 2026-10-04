import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { editCodex, editClaude } from './configure-mcp.mjs';

const root = path.resolve('C:/Users/Test User/Данные/Application/versions/0.9.8');
test('Codex preserves unrelated tables and own preferences; rewrites multiline args', () => {
  const original = '# keep\r\nmodel = "x"\r\n[mcp_servers.weam_revit_ai]\r\ncommand = "node"\r\nargs = [\r\n "old.js", # legacy\r\n]\r\ntool_timeout_sec = 400\r\nenabled = false\r\n[mcp_servers.other]\r\ncommand = "other"\r\n';
  const edited = editCodex(original, root);
  assert.ok(edited.startsWith('# keep\r\nmodel = "x"'));
  assert.ok(edited.includes('tool_timeout_sec = 400\r\nenabled = false'));
  assert.ok(edited.endsWith('[mcp_servers.other]\r\ncommand = "other"\r\n'));
  assert.ok(edited.includes(JSON.stringify(path.join(root, 'server', 'src', 'index.js'))));
  assert.equal(editCodex(edited, root), edited);
});
test('new Codex profile has approval and deadline settings', () => {
  const text = editCodex('', root);
  assert.match(text, /default_tools_approval_mode = "writes"/);
  assert.match(text, /tool_timeout_sec = 320/);
});
test('uninstall removes own version but keeps a separately configured server', () => {
  const text = editCodex('[mcp_servers.other]\ncommand="keep"\n', root);
  assert.equal(editCodex(text, root, true), '[mcp_servers.other]\ncommand="keep"\n\n');
  const external = '[mcp_servers.weam_revit_ai]\ncommand="node"\nargs=["external.js"]\n';
  assert.equal(editCodex(external, root, true), external);
});
test('Claude preserves other connections and fields; repair is idempotent', () => {
  const original = JSON.stringify({ setting: true, mcpServers: { other: { command: 'other' }, weam_revit_ai: { env: { LOCAL: 'keep' } } } });
  const edited = editClaude(original, root);
  const parsed = JSON.parse(edited);
  assert.deepEqual(parsed.mcpServers.other, { command: 'other' });
  assert.deepEqual(parsed.mcpServers.weam_revit_ai.env, { LOCAL: 'keep' });
  assert.equal(parsed.setting, true);
  assert.equal(editClaude(edited, root), edited);
  const removed = JSON.parse(editClaude(edited, root, true));
  assert.equal(removed.mcpServers.weam_revit_ai, undefined);
  assert.equal(removed.mcpServers.other.command, 'other');
});
test('invalid client data is rejected; foreign Claude registration is preserved', () => {
  assert.throws(() => editClaude('{bad', root));
  assert.throws(() => editClaude('{"mcpServers":[]}', root));
  const foreign = '{"mcpServers":{"weam_revit_ai":{"command":"elsewhere"}}}';
  assert.equal(editClaude(foreign, root, true), foreign);
  assert.throws(() => editCodex('[mcp_servers.weam_revit_ai]\nargs = ["unfinished"', root));
});
