import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { registerHostedTools } from '../src/hosted-tools.js';
import { registerAnnotationTools } from '../src/annotation-tools.js';
import { registerGraphicsTools } from '../src/graphics-tools.js';
import { registerSheetTools } from '../src/sheet-tools.js';
import { registerRoofTools } from '../src/roof-tools.js';
import { registerMepTools } from '../src/mep-tools.js';
import { registerStructureTools } from '../src/structure-tools.js';

test('MCP initialize/list and C# dispatcher expose matching tool contracts', async t => {
  const child = spawn(process.execPath, [fileURLToPath(new URL('../src/index.js', import.meta.url))], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  t.after(() => child.kill());
  let buffer = '';
  const pending = new Map();
  let stderr = '';
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', chunk => { stderr += chunk; });
  child.stdout.setEncoding('utf8');
  child.stdout.on('data', chunk => {
    buffer += chunk;
    while (buffer.includes('\n')) {
      const newline = buffer.indexOf('\n');
      const message = JSON.parse(buffer.slice(0, newline));
      buffer = buffer.slice(newline + 1);
      pending.get(message.id)?.(message);
    }
  });
  let nextId = 1;
  const rpc = (method, params) => new Promise((resolve, reject) => {
    const id = nextId++;
    const timeout = setTimeout(() => reject(new Error(`MCP ${method} timed out: ${stderr}`)), 5000);
    pending.set(id, response => { clearTimeout(timeout); pending.delete(id); resolve(response); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
  const initialized = await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'weam-tests', version: '1' } });
  assert.equal(initialized.result.serverInfo.version, '0.9.9');
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const listed = await rpc('tools/list', {});
  const tools = listed.result.tools;
  const names = tools.map(tool => tool.name);
  assert.equal(new Set(names).size, names.length);
  const source = await readFile(new URL('../../plugin/Bridge/RevitTools.cs', import.meta.url), 'utf8');
  const routed = [...source.slice(0, source.indexOf('private static object Ping')).matchAll(/"([a-z_]+)"\s*=>/g)].map(match => match[1]);
  const local = ['store_project_data', 'store_room_data', 'query_stored_data'];
  assert.deepEqual(names.filter(name => !local.includes(name)).sort(), [...new Set(routed)].sort());
  for (const name of ['preview_place_hosted_families', 'apply_create_dimensions', 'preview_tag_rooms', 'apply_color_by_parameter'])
    assert.ok(names.includes(name), `Missing tool ${name}`);
  const deletion = tools.find(tool => tool.name === 'apply_delete_plan');
  const documentation = tools.find(tool => tool.name === 'read_documentation');
  assert.ok(documentation, 'Reference capture needs a native documentation reader');
  assert.equal(documentation.annotations.readOnlyHint, true);
  assert.equal(documentation.inputSchema.properties.limit.maximum, 10);
  const oversized = await rpc('tools/call', { name: 'read_documentation', arguments: { offset: 0, limit: 11 } });
  assert.ok(oversized.error || oversized.result?.isError, 'Oversized capture must be rejected before bridge access');
  assert.equal(deletion.inputSchema.properties.allowDependentDeletion.default, false);
  assert.equal(deletion.annotations.destructiveHint, true);
  const invalid = await rpc('tools/call', { name: 'preview_place_hosted_families', arguments: { instances: [] } });
  assert.ok(invalid.error || invalid.result?.isError, 'Invalid batch must be rejected before bridge access');
});

const contracts = new Map();
for (const registrar of [registerHostedTools, registerAnnotationTools, registerGraphicsTools, registerSheetTools, registerRoofTools, registerMepTools, registerStructureTools])
  registrar((name, description, schema, annotations) => contracts.set(name, { schema, annotations }));
const parse = (name, args) => contracts.get(name).schema.safeParse(args).success;

test('MEP tags require explicit types, unique targets and valid leader geometry', () => {
  const target = { elementId: 10, tagTypeId: 20 };
  const point = { xMeters: 1, yMeters: 2, zMeters: 3 };
  assert.ok(parse('preview_tag_mep', { tags: [target] }));
  assert.ok(parse('preview_tag_mep', { viewId: 30, tags: [{ elementId: 10 }],
    categoryTypes: [{ category: 'OST_DuctTerminal', tagTypeId: 20 }] }));
  assert.ok(parse('preview_tag_mep', { tags: [{ ...target, headPosition: point,
    leaderEndPosition: point, elbowPosition: { ...point, xMeters: 2 } }] }));
  for (const input of [
    { tags: [] }, { tags: Array(51).fill(target) }, { tags: [target, target] },
    { tags: [{ elementId: 10 }] }, { tags: [{ ...target, leader: false, elbowPosition: point }] },
    { tags: [{ ...target, leader: false, leaderEndPosition: point }] },
    { tags: [{ ...target, headPosition: point, offsetRightPaperMillimeters: 15 }] },
    { tags: [{ ...target, headPosition: { ...point, zMeters: Infinity } }] },
    { tags: [{ ...target, offsetUpPaperMillimeters: 101 }] },
    { tags: [{ ...target, orientation: 'diagonal' }] },
    { tags: [target], categoryTypes: [{ category: 'OST_Walls', tagTypeId: 20 }] },
    { tags: [target], categoryTypes: Array(2).fill({ category: 'OST_DuctCurves', tagTypeId: 20 }) }
  ]) assert.equal(parse('preview_tag_mep', input), false, JSON.stringify(input));
  assert.ok(parse('list_annotation_types', { kind: 'mepTags' }));
  assert.equal(contracts.get('apply_tag_mep').annotations.readOnlyHint, false);
  assert.equal(parse('apply_tag_mep', {}), false);
});

test('roofs and sections reject degenerate geometry before bridge access', () => {
  const roof={roofTypeId:1,levelId:2,minX:0,maxX:12,minY:0,maxY:8,offsetMeters:3.15,slopeDegrees:30,ridgeAxis:'x',attachWallIds:[3,4]};
  assert.ok(parse('preview_create_roof',roof));
  for(const changes of [{maxX:0},{slopeDegrees:0},{slopeDegrees:90},{ridgeAxis:'z'},{attachWallIds:[3,3]},{offsetMeters:Infinity}])
    assert.equal(parse('preview_create_roof',{...roof,...changes}),false);
  const section={name:'Section',xMeters:0,yMeters:0,bottomMeters:-1,topMeters:7,widthMeters:15,depthMeters:10,directionX:0,directionY:1};
  assert.ok(parse('preview_create_sections',{views:[section]}));
  for(const changes of [{topMeters:-2},{directionY:0},{depthMeters:0}])
    assert.equal(parse('preview_create_sections',{views:[{...section,...changes}]}),false);
});

test('MEP contracts require a valid segment and matching size shape', () => {
  const point = (x) => ({ xMeters: x, yMeters: 0, zMeters: 2 });
  const pipe = { kind: 'pipe', typeId: 1, systemTypeId: 2, levelId: 3,
    start: point(0), end: point(2), diameterMeters: 0.05 };
  assert.ok(parse('preview_create_mep', { segments: [pipe] }));
  assert.ok(parse('preview_create_mep', { segments: [{ ...pipe, kind: 'duct',
    diameterMeters: undefined, widthMeters: 0.4, heightMeters: 0.2 }] }));
  for (const bad of [{ end: point(0) }, { diameterMeters: 0 },
    { widthMeters: 0.2 }, { kind: 'duct', widthMeters: 0.2, heightMeters: 0.2 }])
    assert.equal(parse('preview_create_mep', { segments: [{ ...pipe, ...bad }] }), false);
});

test('beam system and phase contracts reject invalid layouts and duplicate views', () => {
  const system = { levelId: 1, beamTypeId: 2, minX: 0, maxX: 10, minY: 0, maxY: 8,
    beamDirection: 'x', spacingMeters: 0.6 };
  assert.ok(parse('preview_create_beam_systems', { systems: [system] }));
  assert.equal(parse('preview_create_beam_systems', { systems: [{ ...system, maxX: 0 }] }), false);
  assert.equal(parse('preview_create_beam_systems', { systems: [{ ...system, spacingMeters: 0 }] }), false);
  assert.ok(parse('preview_set_view_phase', { phaseId: 1, viewIds: [2, 3] }));
  assert.equal(parse('preview_set_view_phase', { phaseId: 1, viewIds: [2, 2] }), false);
});

test('sheet layout rejects incomplete views and reversed crop boxes', () => {
  const view={sourceViewId:1,viewName:'Plan A3',scale:50,centerXmm:200,centerYmm:165};
  const sheet={number:'АР-01',name:'План этажа',views:[view]};
  assert.ok(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[sheet]}));
  assert.equal(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[{...sheet,views:[]}]}),false);
  assert.equal(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[{...sheet,views:[{...view,scale:0}]}]}),false);
  assert.equal(parse('preview_create_sheets',{titleBlockTypeId:2,sheets:[{...sheet,views:[{...view,crop:{minX:1,maxX:0,minY:0,maxY:1,minZ:0,maxZ:1}}]}]}),false);
  assert.equal(parse('export_sheets_pdf',{sheetIds:[],path:'C:/out.pdf'}),false);
});

test('hosted placement requires one level selector and one sill selector', () => {
  const instance = { hostWallId: 1, familySymbolId: 2, levelId: 3, position: { xMeters: 1, yMeters: 2 } };
  assert.ok(parse('preview_place_hosted_families', { instances: [instance] }));
  assert.equal(parse('preview_place_hosted_families', { instances: [{ ...instance, levelName: 'L1' }] }), false);
  assert.equal(parse('preview_place_hosted_families', { instances: [{ ...instance, sillHeightMeters: 1, offsetMeters: 2 }] }), false);
  assert.equal(parse('preview_place_hosted_families', { instances: [{ ...instance, hostWallId: Number.MAX_SAFE_INTEGER + 1 }] }), false);
});

test('graphics contracts reject incompatible fields, duplicates and missing targets', () => {
  assert.ok(parse('preview_element_graphics', { viewId: 1, action: 'reset_temporary' }));
  for (const args of [
    { viewId: 1, action: 'hide' },
    { viewId: 1, action: 'set_color', elementIds: [2] },
    { viewId: 1, action: 'hide', elementIds: [2, 2] },
    { viewId: 1, action: 'reset_temporary', elementIds: [2] },
    { viewId: 1, action: 'set_transparency', elementIds: [2], transparency: 101 }
  ]) assert.equal(parse('preview_element_graphics', args), false);
});

test('dimensions require complete line and at least two explicit references', () => {
  const dimension = { dimensionTypeId: 2, line: { start: { xMeters: 0, yMeters: 0, zMeters: 0 }, end: { xMeters: 2, yMeters: 0, zMeters: 0 } },
    references: [{ elementId: 3, stableReference: 'a' }, { elementId: 4, stableReference: 'b' }] };
  assert.ok(parse('preview_create_dimensions', { dimensions: [dimension] }));
  assert.equal(parse('preview_create_dimensions', { dimensions: [{ ...dimension, references: [dimension.references[0]] }] }), false);
  assert.equal(parse('preview_tag_rooms', { tagTypeId: 2, offsetUpPaperMillimeters: 101 }), false);
});
