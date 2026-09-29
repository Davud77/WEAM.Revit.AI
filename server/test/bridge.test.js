import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import { once } from 'node:events';
import { createRevitClient } from '../src/revit-bridge.js';

const token = 'test-token-that-is-not-a-real-secret-123456789';
async function withBridge(handler, check, options = {}) {
  const sockets = new Set();
  const bridge = net.createServer(socket => {
    sockets.add(socket);
    socket.on('error', () => {});
    socket.on('close', () => sockets.delete(socket));
    let incoming = '';
    socket.on('data', chunk => {
      incoming += chunk;
      if (!incoming.includes('\n')) return;
      const request = JSON.parse(incoming.slice(0, incoming.indexOf('\n')));
      socket.removeAllListeners('data');
      handler(socket, request);
    });
  });
  bridge.listen(0, '127.0.0.1');
  await once(bridge, 'listening');
  const call = createRevitClient({ port: bridge.address().port, timeoutMs: 500,
    tokenLoader: async () => token, ...options });
  try { await check(call); }
  finally {
    for (const socket of sockets) socket.destroy();
    await new Promise(resolve => bridge.close(resolve));
  }
}

test('fragmented UTF-8 response and request payload round trip', async () => {
  await withBridge((socket, request) => {
    assert.equal(request.tool, 'get_project_info');
    assert.equal(request.token, token);
    assert.deepEqual(request.arguments, { query: 'Стена' });
    const data = Buffer.from(JSON.stringify({ id: request.id, ok: true, result: { name: 'Проект' } }) + '\n');
    for (const byte of data) socket.write(Buffer.from([byte]));
  }, async call => assert.deepEqual(await call('get_project_info', { query: 'Стена' }), { name: 'Проект' }));
});

test('wrong response ID is rejected', async () => {
  await withBridge(socket => socket.end('{"id":"unrelated","ok":true,"result":1}\n'),
    call => assert.rejects(call('read'), /ID does not match/));
});

test('malformed response and bridge error are not successful results', async () => {
  await withBridge(socket => socket.end('broken JSON\n'), call => assert.rejects(call('read'), /Invalid response/));
  await withBridge((socket, request) => socket.end(JSON.stringify({ id: request.id, ok: false, error: 'Rejected in Revit' }) + '\n'),
    call => assert.rejects(call('read'), /Rejected in Revit/));
});

test('oversized responses and early disconnects fail', async () => {
  await withBridge(socket => socket.end('x'.repeat(300)), call => assert.rejects(call('read'), /8 MB limit/), { maxResponseBytes: 100 });
  await withBridge(socket => socket.end(), call => assert.rejects(call('read'), /closed/));
});

test('deadline is absolute even while peer sends partial response', async () => {
  await withBridge(socket => {
    const timer = setInterval(() => socket.write(' '), 5);
    socket.once('close', () => clearInterval(timer));
  }, call => assert.rejects(call('read'), /Timed out/), { timeoutMs: 45 });
});

test('cancellation closes live socket and propagates error', async () => {
  const controller = new AbortController();
  await withBridge(() => controller.abort(), call => assert.rejects(call('apply_example', {}, { signal: controller.signal }), /cancelled/));
});

test('pre-cancelled calls and oversized requests do not open a socket', async () => {
  let tokenRead = false;
  const controller = new AbortController();
  controller.abort();
  const call = createRevitClient({ tokenLoader: async () => { tokenRead = true; return token; }, maxRequestBytes: 64 });
  await assert.rejects(call('read', {}, { signal: controller.signal }));
  assert.equal(tokenRead, false);
  await assert.rejects(call('read', { text: 'x'.repeat(100) }), /1 MB limit/);
});

test('invalid token is rejected before connecting', async () => {
  await assert.rejects(createRevitClient({ tokenLoader: async () => 'short' })('read'), /Invalid bridge token/);
});
